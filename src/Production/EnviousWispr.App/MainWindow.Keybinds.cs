using System.ComponentModel;
using System.Globalization;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EnviousWispr.App;

/// <summary>Keybind capture, conflict detection, recording mode toggles, and auto-stop settings.</summary>
public sealed partial class MainWindow
{
    // The lone modifier a keybind field has seen go down with nothing else after it. Null the
    // moment any ordinary key arrives, because from then on the modifier is qualifying that key.
    private string? _keybindModifierCandidate;

    // Every modifier held together since the candidate began, so two held at once can be offered as a set
    // (Ctrl+Win). Cleared with the candidate. Ref: #66.
    private HotkeyModifiers _keybindModifierSet;
    private WindowsKeybindCaptureSession? _keybindCaptureSession;
    private KeybindCaptureSlot? _capturingSlot;
    private TextBox? _capturingBox;
    private Button? _capturingButton;
    private string _capturingPreviousValue = string.Empty;
    private bool _isUpdatingCaptureDisplay;

    private enum KeybindCaptureSlot
    {
        Recording,
        Cancel,
        QuickAdd,
        PasteLast,
        CopyLast,
    }
    private int _recordingModeIndex;

    /// <summary>
    /// Raised true while a keybind field is waiting for a keystroke, false the moment it is not.
    /// </summary>
    /// <remarks>
    /// Pressing the recording key inside its own capture field started a real recording, because
    /// the system-wide hook sees the key before this window does and marking the routed event
    /// handled has no bearing on it. So the fix cannot live in the field's key handler at all -
    /// the hook has to be told to stand down, and the only thing this window knows is focus.
    ///
    /// Closing the window raises false as well. A capture field that still held focus when the
    /// window went away would otherwise leave the hook standing down with no field on screen to
    /// clear it, and dictation would stop working with nothing on screen to explain it.
    /// </remarks>
    public event Action<bool>? KeybindCaptureActiveChanged;

    public void SetHotkeyReady(string gesture, DictationRecordingMode recordingMode)
    {
        // One shortcut phrase, used on screen and in the tray, so the two cannot drift apart.
        var shortcut = recordingMode == DictationRecordingMode.PushToTalk
            ? $"Hold {gesture}"
            : $"Toggle with {gesture}";
        ObserveRecordingKeyForOnboarding(gesture, recordingMode);
        // A full stop rather than a middle dot. The tray tooltip is a SENTENCE - it has no
        // layout to separate, and a screen reader either announces a middle dot literally or
        // drops it. Text that is laid out on screen may still use one; this is not.
        SessionStatusChanged?.Invoke(DictationStatus.Quiet($"ready. {shortcut}"));
    }

    public void SetHotkeyUnavailable(string status)
    {
        ObserveRecordingKeyProblemForOnboarding(status);
        // AN ERROR FOR THE TRAY, WHICH IS THE ONLY PLACE IT SHOWS. The recording key not being
        // available means the app cannot be used at all, and unlike a pill the icon stays. It
        // raises no pill, because SetSessionStatus is not on this path.
        SessionStatusChanged?.Invoke(DictationStatus.Error("shortcut unavailable"));
    }

    /// <summary>Fills the recording keybind with the binding that unlocks the four gestures.</summary>
    /// <remarks>
    /// IT FILLS THE FIELD RATHER THAN SAVING. Everything a typed keybind goes through - the parse,
    /// the conflict check against the other two binds, the Save button - happens the same way, so
    /// this cannot be the one route into the setting that skips the checks. It also leaves the person
    /// looking at what they are about to agree to.
    /// </remarks>
    private void HandsFreeGestureButton_Click(object sender, RoutedEventArgs e)
    {
        CancelKeybindCapture();
        HotkeyTextBox.Focus(FocusState.Programmatic);
        HotkeyTextBox.Text = HandsFreeRecordBinding.Suggested;
        _ = TriggerAutoSaveAsync();
    }

    private void PushToTalkSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        SetRecordingMode(0, triggerSave: true);
    }

    private void ToggleSegmentButton_Click(object sender, RoutedEventArgs e)
    {
        SetRecordingMode(1, triggerSave: true);
    }

    private void SetRecordingMode(int modeIndex, bool triggerSave = true)
    {
        _recordingModeIndex = modeIndex;
        UpdateRecordingModeButtons();

        if (AutoStopToggle is not null)
        {
            UpdateAutoStopAvailability();
        }

        if (triggerSave && !_isApplyingSettings && _settings is not null)
        {
            _ = TriggerAutoSaveAsync();
        }
    }

    private void UpdateRecordingModeButtons()
    {
        var pttSelected = _recordingModeIndex == 0;
        var accentBrush = (Brush)Application.Current.Resources["BrandAccentBrush"];
        var accentFgBrush = (Brush)Application.Current.Resources["BrandOnAccentBrush"];

        if (PushToTalkSegmentButton is not null)
        {
            if (pttSelected)
            {
                PushToTalkSegmentButton.Background = accentBrush;
                PushToTalkSegmentButton.Foreground = accentFgBrush;
                PushToTalkSegmentButton.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                PushToTalkSegmentButton.ClearValue(Button.BackgroundProperty);
                PushToTalkSegmentButton.ClearValue(Button.ForegroundProperty);
                PushToTalkSegmentButton.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        if (ToggleSegmentButton is not null)
        {
            if (!pttSelected)
            {
                ToggleSegmentButton.Background = accentBrush;
                ToggleSegmentButton.Foreground = accentFgBrush;
                ToggleSegmentButton.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                ToggleSegmentButton.ClearValue(Button.BackgroundProperty);
                ToggleSegmentButton.ClearValue(Button.ForegroundProperty);
                ToggleSegmentButton.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }

    private void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            TryResolveCaptureSlot(tag, out var slot, out var box, out var button) is false)
        {
            return;
        }

        if (_capturingSlot == slot)
        {
            CancelKeybindCapture();
            return;
        }

        StartKeybindCapture(slot, box, button);
    }

    private void HotkeyBoxTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not TextBox box ||
            TryResolveCaptureSlot(box, out var slot, out var button) is false)
        {
            return;
        }

        if (_capturingSlot == slot)
        {
            return;
        }

        StartKeybindCapture(slot, box, button);
    }

    private bool TryResolveCaptureSlot(string tag, out KeybindCaptureSlot slot, out TextBox box, out Button button)
    {
        switch (tag)
        {
            case "Hotkey":
                slot = KeybindCaptureSlot.Recording;
                box = HotkeyTextBox;
                button = ChangeHotkeyButton;
                return true;
            case "CancelHotkey":
                slot = KeybindCaptureSlot.Cancel;
                box = CancelHotkeyTextBox;
                button = ChangeCancelHotkeyButton;
                return true;
            case "QuickAdd":
                slot = KeybindCaptureSlot.QuickAdd;
                box = QuickAddHotkeyTextBox;
                button = ChangeQuickAddHotkeyButton;
                return true;
            case "PasteLast":
                slot = KeybindCaptureSlot.PasteLast;
                box = PasteLastHotkeyTextBox;
                button = ChangePasteLastHotkeyButton;
                return true;
            case "CopyLast":
                slot = KeybindCaptureSlot.CopyLast;
                box = CopyLastHotkeyTextBox;
                button = ChangeCopyLastHotkeyButton;
                return true;
            default:
                slot = default;
                box = null!;
                button = null!;
                return false;
        }
    }

    private bool TryResolveCaptureSlot(TextBox box, out KeybindCaptureSlot slot, out Button button)
    {
        if (ReferenceEquals(box, HotkeyTextBox))
        {
            slot = KeybindCaptureSlot.Recording;
            button = ChangeHotkeyButton;
            return true;
        }

        if (ReferenceEquals(box, CancelHotkeyTextBox))
        {
            slot = KeybindCaptureSlot.Cancel;
            button = ChangeCancelHotkeyButton;
            return true;
        }

        if (ReferenceEquals(box, QuickAddHotkeyTextBox))
        {
            slot = KeybindCaptureSlot.QuickAdd;
            button = ChangeQuickAddHotkeyButton;
            return true;
        }

        if (ReferenceEquals(box, PasteLastHotkeyTextBox))
        {
            slot = KeybindCaptureSlot.PasteLast;
            button = ChangePasteLastHotkeyButton;
            return true;
        }

        if (ReferenceEquals(box, CopyLastHotkeyTextBox))
        {
            slot = KeybindCaptureSlot.CopyLast;
            button = ChangeCopyLastHotkeyButton;
            return true;
        }

        slot = default;
        button = null!;
        return false;
    }

    private void StartKeybindCapture(KeybindCaptureSlot slot, TextBox box, Button button)
    {
        if (_currentOverlayState is DictationOverlayState.Recording or DictationOverlayState.Processing)
        {
            ShowKeybindCaptureHint("Stop the current recording before changing shortcuts.");
            return;
        }

        CancelKeybindCapture();

        _capturingSlot = slot;
        _capturingBox = box;
        _capturingButton = button;
        _capturingPreviousValue = box.Text;
        KeybindCaptureActiveChanged?.Invoke(true);

        _isUpdatingCaptureDisplay = true;
        try
        {
            box.Text = "Listening...";
            box.SelectionStart = box.Text.Length;
        }
        finally
        {
            _isUpdatingCaptureDisplay = false;
        }

        box.BorderBrush = KeybindListeningProbe.Background;
        box.Background = KeybindListeningProbe.BorderBrush;
        button.Content = "Cancel";
        SetLiveRegion(KeybindCaptureHintText, string.Empty, Visibility.Collapsed);
        box.Focus(FocusState.Programmatic);

        try
        {
            _keybindCaptureSession = new WindowsKeybindCaptureSession(
                allowModifierOnly: slot == KeybindCaptureSlot.Recording,
                allowEscapeAsBinding: slot == KeybindCaptureSlot.Cancel,
                allowClear: slot is KeybindCaptureSlot.PasteLast or KeybindCaptureSlot.CopyLast,
                onCommitted: gestureText => CompleteKeybindCapture(slot, gestureText),
                onCancelled: CancelKeybindCapture,
                onInvalidAttempt: ShowKeybindCaptureHint,
                dispatchToUi: action => DispatcherQueue.TryEnqueue(() => action()));
        }
        catch (Win32Exception)
        {
            CancelKeybindCapture();
            ShowKeybindCaptureHint("Could not start keyboard capture. Try again.");
        }
    }

    private void CompleteKeybindCapture(KeybindCaptureSlot slot, string gestureText)
    {
        if (_capturingSlot != slot || _capturingBox is not { } box)
        {
            return;
        }

        StopKeybindCaptureSession();
        box.Text = gestureText;
        box.SelectionStart = box.Text.Length;
        RefreshKeybindConflicts();
        KeybindCaptureActiveChanged?.Invoke(false);
        _ = TriggerAutoSaveAsync();
    }

    private void CancelKeybindCapture()
    {
        var box = _capturingBox;
        var hasSession = _keybindCaptureSession is not null || box is not null;
        if (!hasSession)
        {
            return;
        }

        var previous = _capturingPreviousValue;
        StopKeybindCaptureSession();

        if (box is not null)
        {
            _isUpdatingCaptureDisplay = true;
            try
            {
                box.Text = previous;
                box.SelectionStart = box.Text.Length;
            }
            finally
            {
                _isUpdatingCaptureDisplay = false;
            }
        }

        RefreshKeybindConflicts();
        KeybindCaptureActiveChanged?.Invoke(false);
    }

    private void StopKeybindCaptureSession()
    {
        _keybindCaptureSession?.Dispose();
        _keybindCaptureSession = null;

        if (_capturingBox is { } box)
        {
            box.ClearValue(Control.BorderBrushProperty);
            box.Background = KeybindDefaultBgProbe.Background;
        }

        if (_capturingButton is { } button)
        {
            button.Content = "Change";
        }

        _capturingSlot = null;
        _capturingBox = null;
        _capturingButton = null;
        _capturingPreviousValue = string.Empty;
        SetLiveRegion(KeybindCaptureHintText, string.Empty, Visibility.Collapsed);
    }

    private void ShowKeybindCaptureHint(string hint) =>
        SetLiveRegion(KeybindCaptureHintText, hint, Visibility.Visible);

    /// <summary>
    /// Captures a pressed key combination into a keybind field.
    /// </summary>
    /// <remarks>
    /// These were plain text boxes. A field labelled "Recording keybind" showing "F8" reads
    /// unmistakably as a capture control, so the overwhelmingly likely user action is to click it
    /// and press the key they want. Measured on the running app: pressing F9 did nothing at all -
    /// no value change, no feedback - and pressing Q produced "qF8", because the character was
    /// inserted at a caret sitting at position 0. Silent in both directions, and the corrupted
    /// value still LOOKS like a keybind.
    ///
    /// Only the combinations HotkeyGestureParser can express are accepted, so anything captured
    /// here round-trips: letters, digits, F1-F24 and the named keys it normalises. An unsupported
    /// key leaves the field untouched rather than writing something that cannot be parsed.
    /// </remarks>
    /// <summary>
    /// The threshold field is only useful when the switch above it is on, and only in Toggle mode.
    /// </summary>
    /// <remarks>
    /// Disabled rather than hidden. A control that vanishes leaves the user wondering whether they
    /// imagined it; a disabled one with the switch beside it says what turns it back on.
    /// </remarks>
    private void AutoStopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateAutoStopAvailability();
        _ = TriggerAutoSaveAsync();
    }

    private void UpdateAutoStopAvailability()
    {
        var visibility = AutoStopToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (AutoStopSecondsDivider is not null)
        {
            AutoStopSecondsDivider.Visibility = visibility;
        }

        if (AutoStopSecondsRow is not null)
        {
            AutoStopSecondsRow.Visibility = visibility;
        }

        AutoStopSecondsBox.IsEnabled = AutoStopToggle.IsOn;
    }

    /// <summary>The five shortcut fields with the roles the presenter's Save checks them under: the live warning and the refusal share one detector and one set of names.</summary>
    private (TextBox Box, string Role)[] KeybindFields()
    {
        var roles = SettingsPresenter.ShortcutRoles(
            HotkeyTextBox.Text,
            CancelHotkeyTextBox.Text,
            QuickAddHotkeyTextBox.Text,
            PasteLastHotkeyTextBox.Text,
            CopyLastHotkeyTextBox.Text);
        return
        [
            (HotkeyTextBox, roles[0].Role),
            (CancelHotkeyTextBox, roles[1].Role),
            (QuickAddHotkeyTextBox, roles[2].Role),
            (PasteLastHotkeyTextBox, roles[3].Role),
            (CopyLastHotkeyTextBox, roles[4].Role),
        ];
    }

    private void HotkeyBoxTextChanged(object sender, TextChangedEventArgs e) => RefreshKeybindConflicts();

    /// <summary>Names a clash while it is being made, not when Save refuses it.</summary>
    /// <remarks>
    /// SAVE ALREADY REFUSED A CLASH, so nothing broken could reach the settings file, and that is
    /// why this reads as polish rather than a fix. It is not: a rule enforced only at the last
    /// possible moment lets someone set a shortcut, watch two fields sit there agreeing with each
    /// other, and be told no after they commit. Both paths now ask the same detector, so the
    /// warning and the refusal cannot come to different conclusions.
    /// </remarks>
    private void RefreshKeybindConflicts()
    {
        if (_isUpdatingCaptureDisplay)
        {
            return;
        }

        // No existence guard: every field here is built by InitializeComponent, and the only two
        // callers are a change event and the Save button, both of which run after it. Settings
        // load fills the three fields one at a time, which is harmless - a field still holding
        // its empty starting value does not collide with anything.
        var fields = KeybindFields();
        var clashes = HotkeyConflictDetector.Find(fields.Select(field => (field.Role, field.Box.Text)).ToArray());
        var guilty = clashes
            .SelectMany(clash => new[] { clash.FirstRole, clash.SecondRole })
            .ToHashSet(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            if (ReferenceEquals(field.Box, _capturingBox))
            {
                field.Box.BorderBrush = KeybindListeningProbe.Background;
            }
            else if (guilty.Contains(field.Role))
            {
                // Read off a probe element rather than Application.Current.Resources, because the
                // error brush lives in a theme dictionary: indexing the app resources for one
                // throws, and a brush captured once would keep the old theme's colour after a
                // switch. The probe carries a ThemeResource, so it follows the theme for free.
                field.Box.BorderBrush = KeybindErrorProbe.Background;
            }
            else
            {
                // ClearValue rather than null: a null brush is an invisible border, not the
                // theme's border, so the field would silently lose its outline once fixed.
                field.Box.ClearValue(Control.BorderBrushProperty);
            }
        }

        SetLiveRegion(
            KeybindConflictText,
            HotkeyConflictDetector.Describe(clashes),
            clashes.Count == 0 ? Visibility.Collapsed : Visibility.Visible);
        HandsFreeGestureButton.Visibility = string.Equals(
            HotkeyTextBox.Text.Trim(),
            HandsFreeRecordBinding.Suggested,
            StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void HotkeyBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (_keybindCaptureSession is not null)
        {
            e.Handled = true;
            return;
        }

        // A MODIFIER IS NOT A GESTURE YET, WHICH IS NOT THE SAME AS NOT BEING ONE. On the way down
        // it is indistinguishable from the start of a combination, so it is still swallowed here -
        // it must neither edit the field nor move focus while the user is mid-chord. What decides
        // it is the RELEASE: coming back up with nothing else pressed is a deliberate tap, and that
        // is handled in HotkeyBoxKeyUp.
        if (e.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            _keybindModifierCandidate ??= SidedModifierName();
            _keybindModifierSet |= HeldModifierSet() | ModifierFlagOf(e.Key);
            e.Handled = true;
            return;
        }

        // THE LAST-DICTATION SHORTCUTS CAN BE REMOVED, the others cannot: Backspace with nothing held empties one of
        // those two fields (Delete stays a key that can be bound), which Save stores as "no shortcut". Every other field captures the key as before.
        // Ref: #206.
        if (e.Key == VirtualKey.Back &&
            (ReferenceEquals(box, PasteLastHotkeyTextBox) || ReferenceEquals(box, CopyLastHotkeyTextBox)) &&
            !IsHeld(VirtualKey.Control) && !IsHeld(VirtualKey.Menu) && !IsHeld(VirtualKey.Shift) &&
            !IsHeld(VirtualKey.LeftWindows) && !IsHeld(VirtualKey.RightWindows))
        {
            _keybindModifierCandidate = null;
            _keybindModifierSet = HotkeyModifiers.None;
            e.Handled = true;
            box.Text = string.Empty;
            return;
        }

        // Any ordinary key means the modifier was qualifying it. The candidate is dropped here
        // rather than on the modifier's release, because by then the field already holds the
        // combination and overwriting it would undo what the user just chose.
        _keybindModifierCandidate = null;
        _keybindModifierSet = HotkeyModifiers.None;

        // Handled regardless of whether the key is usable: the field is capture-driven, so a
        // keystroke must never fall through and be inserted as text. That fall-through is the
        // defect being fixed.
        e.Handled = true;

        if (!TryDescribeKey(e.Key, out var key))
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (IsHeld(VirtualKey.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsHeld(VirtualKey.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsHeld(VirtualKey.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsHeld(VirtualKey.LeftWindows) || IsHeld(VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        box.Text = new HotkeyGesture(modifiers, key).ToString();
        box.SelectionStart = box.Text.Length;
    }

    /// <summary>Offers a lone modifier as a binding, once it comes back up untouched.</summary>
    /// <remarks>
    /// THE RELEASE IS WHERE A TAP BECOMES DISTINGUISHABLE from the start of a shortcut, which is the
    /// same reason the running app decides it there. Deciding on the way down would put a binding in
    /// the field the moment a user reached for Control, before they had pressed the letter they were
    /// aiming at.
    ///
    /// ALT IS NOT OFFERED, matching the engine and the parser. A lone Alt tap already opens a
    /// window's menu bar, so a user who set it would have a binding that reads correctly, saves
    /// correctly, and loses every race with the shell.
    ///
    /// WITHOUT THIS THE WHOLE MODIFIER BINDING IS UNREACHABLE. The engine accepts one and the
    /// settings file can carry one; if nothing on screen can produce one, that is a working feature
    /// wired to nothing - which had already happened once on this project and was caught by reading
    /// rather than by shipping.
    /// </remarks>
    private void HotkeyBoxKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        var candidate = _keybindModifierCandidate;
        var set = _keybindModifierSet | ModifierFlagOf(e.Key);
        _keybindModifierCandidate = null;
        _keybindModifierSet = HotkeyModifiers.None;

        if (candidate is null ||
            e.Key is not (VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
                or VirtualKey.LeftWindows or VirtualKey.RightWindows))
        {
            return;
        }

        // TWO OR MORE MODIFIERS HELD TOGETHER ARE A SET, AND THE SET IS THE BINDING - Ctrl+Win, the default. This field
        // only ever offered the first modifier down, so pressing Ctrl+Win here produced "LeftCtrl": the engine could
        // run the set and nobody could choose it. Offered on the first release, while the set is still whole. Alt is
        // refused in a set as it is alone, matching the parser. Ref: #66.
        var isSet = System.Numerics.BitOperations.PopCount((uint)set) >= 2;
        if (isSet && set.HasFlag(HotkeyModifiers.Alt))
        {
            e.Handled = true;
            return;
        }

        // A LONE MODIFIER IS A RECORDING KEY'S SHAPE, not a last-dictation shortcut's: those fire once per press and
        // need an ordinary key, so the tap is not offered to their fields (Save would refuse it). Ref: #206.
        // ONLY THE RECORDING KEY MAY BE A SET: the cancel and Add-a-word keys need a key of their own, and one without
        // leaves the whole hook refused (HotkeyGestureParser.ParseKeyed). Ref: #66 review.
        if (ReferenceEquals(box, PasteLastHotkeyTextBox) ||
            ReferenceEquals(box, CopyLastHotkeyTextBox) ||
            (isSet && !ReferenceEquals(box, HotkeyTextBox)))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        box.Text = isSet
            ? new HotkeyGesture(set, string.Empty).ToString()
            : new HotkeyGesture(HotkeyModifiers.None, candidate).ToString();
        box.SelectionStart = box.Text.Length;
    }

    private void ForgetKeybindChord()
    {
        _keybindModifierCandidate = null;
        _keybindModifierSet = HotkeyModifiers.None;
    }

    /// <summary>The modifiers the keyboard state says are down right now, sides folded together.</summary>
    private static HotkeyModifiers HeldModifierSet()
    {
        var held = HotkeyModifiers.None;
        if (IsHeld(VirtualKey.Control))
        {
            held |= HotkeyModifiers.Control;
        }

        if (IsHeld(VirtualKey.Menu))
        {
            held |= HotkeyModifiers.Alt;
        }

        if (IsHeld(VirtualKey.Shift))
        {
            held |= HotkeyModifiers.Shift;
        }

        if (IsHeld(VirtualKey.LeftWindows) || IsHeld(VirtualKey.RightWindows))
        {
            held |= HotkeyModifiers.Windows;
        }

        return held;
    }

    /// <summary>The modifier a key is, for the key this event is about, whatever the state has caught up with.</summary>
    private static HotkeyModifiers ModifierFlagOf(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => HotkeyModifiers.Control,
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => HotkeyModifiers.Alt,
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => HotkeyModifiers.Shift,
        VirtualKey.LeftWindows or VirtualKey.RightWindows => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };

    /// <summary>Which physical modifier is down, by side.</summary>
    /// <remarks>
    /// A key event reports "Control", not "the right one". A binding has to name ONE PHYSICAL KEY,
    /// so the side is read from the keyboard state instead. Left is checked first only to make the
    /// answer deterministic when someone is holding both; either answer would be defensible and an
    /// undefined one would not.
    /// </remarks>
    private static string? SidedModifierName()
    {
        if (IsHeld(VirtualKey.LeftControl))
        {
            return "LeftCtrl";
        }

        if (IsHeld(VirtualKey.RightControl))
        {
            return "RightCtrl";
        }

        if (IsHeld(VirtualKey.LeftMenu))
        {
            return "LeftAlt";
        }

        if (IsHeld(VirtualKey.RightMenu))
        {
            return "RightAlt";
        }

        if (IsHeld(VirtualKey.LeftShift))
        {
            return "LeftShift";
        }

        if (IsHeld(VirtualKey.RightShift))
        {
            return "RightShift";
        }

        if (IsHeld(VirtualKey.LeftWindows))
        {
            return "LeftWin";
        }

        return IsHeld(VirtualKey.RightWindows) ? "RightWin" : null;
    }

    private static bool IsHeld(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// The parser's own vocabulary, and nothing outside it.
    /// </summary>
    /// <remarks>
    /// Deliberately mirrors HotkeyGestureParser.TryNormalizeKey rather than accepting every key
    /// Windows can report. Capturing something the parser cannot read back would put a value in
    /// the field that looks valid and fails at save - which is the failure this whole change
    /// exists to remove.
    /// </remarks>
    private static bool TryDescribeKey(VirtualKey key, out string described)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            described = key.ToString();
            return true;
        }

        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            described = ((int)key - (int)VirtualKey.Number0).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (key is >= VirtualKey.F1 and <= VirtualKey.F24)
        {
            described = $"F{(int)key - (int)VirtualKey.F1 + 1}";
            return true;
        }

        described = key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Pause => "Pause",
            VirtualKey.Scroll => "ScrollLock",
            VirtualKey.Escape => "Escape",
            _ => string.Empty,
        };
        return described.Length > 0;
    }
}
