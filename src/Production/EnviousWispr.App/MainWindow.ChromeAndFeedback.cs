using EnviousWispr.App.Services;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace EnviousWispr.App;

public sealed partial class MainWindow
{
    private void SetOnboardingReliabilityNotice(string title, string message)
    {
        _pendingOnboardingReliabilityNotice = (title, message);
        if (OnboardingReliabilityInfoBar is not null)
        {
            OnboardingReliabilityInfoBar.Title = title;
            OnboardingReliabilityInfoBar.Message = message;
            OnboardingReliabilityInfoBar.IsOpen = true;
        }
    }

    public void SetReliabilityNotice(string title, string message, bool isError = false) =>
        ShowMessage(
            title,
            message,
            isError ? InfoBarSeverity.Error : InfoBarSeverity.Warning);

    private static void ApplySegmentButtonState(Button? button, bool selected)
    {
        if (button is null)
        {
            return;
        }

        button.Style = selected
            ? (Style)Application.Current.Resources["BrandSegmentedActiveButtonStyle"]
            : (Style)Application.Current.Resources["BrandSegmentedButtonStyle"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(
            button,
            selected ? "Selected" : string.Empty);
    }

    /// <summary>Asks before something that cannot be undone.</summary>
    private async Task<bool> ConfirmAsync(string title, string body, string confirmLabel)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = body,
            PrimaryButtonText = confirmLabel,
            CloseButtonText = "Keep them",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Hides a section's eyebrow when it merely repeats the page title above it.
    /// </summary>
    /// <remarks>
    /// On a page showing ONE section the small-caps eyebrow sits a few pixels under the page
    /// title saying the same word: "Sounds" over "SOUNDS". Splitting the pages made this worse,
    /// because most pages now show exactly one section. Where the eyebrow does real work -
    /// Transcription shows an engine section and a cleanup section - it stays.
    ///
    /// Shared by BOTH pages. The first version lived inside the settings page only, so Open Source
    /// Licenses kept showing "OPEN SOURCE LICENSES" under "Open Source Licenses" - the exact case
    /// the rule was written for, on the one page the rule never ran. Only an EXACT match collapses:
    /// Permissions shows "PERMISSIONS AND PRIVACY" and Updates shows "UPDATES" under "Check for
    /// Updates", and neither is a repeat.
    /// </remarks>
    private static void CollapseEyebrowThatRepeatsTheTitle(
        Border[] allSections, Border[] visibleSections, string title)
    {
        foreach (var candidate in allSections)
        {
            var eyebrow = EyebrowOf(candidate);
            var row = EyebrowRowOf(candidate);
            if (eyebrow is null || row is null)
            {
                continue;
            }

            var redundant = visibleSections.Length == 1
                && ReferenceEquals(candidate, visibleSections[0])
                && string.Equals(eyebrow.Text, title, StringComparison.OrdinalIgnoreCase);
            row.Visibility = redundant ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// The eyebrow of a section card: by construction the first TextBlock inside it.
    /// </summary>
    /// <remarks>
    /// Read from the STRUCTURE rather than from a name on each of the fifteen eyebrows, so a
    /// section added later behaves the same without anyone wiring it up. Returns null rather than
    /// throwing if a card is ever built differently - a missing eyebrow is not worth a crash.
    /// </remarks>
    private static TextBlock? EyebrowOf(Border section) =>
        EyebrowRowOf(section)?.Children.OfType<TextBlock>().FirstOrDefault();

    /// <summary>
    /// The whole eyebrow row of a section card: its accent glyph and its label together.
    /// </summary>
    /// <remarks>
    /// COLLAPSING THE LABEL ALONE WOULD LEAVE THE GLYPH BEHIND. An eyebrow that repeats the page
    /// title is hidden, and once the eyebrow became two things rather than one, hiding the text
    /// would have left a small accent mark floating above the card with nothing beside it.
    /// </remarks>
    private static StackPanel? EyebrowRowOf(Border section) =>
        section.Child is StackPanel panel
            ? panel.Children.OfType<StackPanel>().FirstOrDefault(row =>
                row.Orientation == Orientation.Horizontal &&
                row.Children.OfType<TextBlock>().Any())
            : null;

    /// <summary>The vertical card-based choice lists.</summary>
    private ItemsControl[] ChoiceLists() =>
    [
        PolishProviderComboBox,
    ];

    /// <summary>
    /// Moves the choice with the arrow keys, the way a Windows radio group does.
    /// </summary>
    /// <remarks>
    /// Written by hand because the framework does not supply it here. Replacing RadioButtons with
    /// an ItemsControl fixed card widths and took the group's keyboard behaviour with it, and two
    /// attempts to get it back from a property failed: TabFocusNavigation="Once" gave the single
    /// tab stop and nothing to move within the group, then XYFocusKeyboardNavigation="Enabled"
    /// changed nothing at all - it governs directional/gamepad navigation, not arrow keys inside a
    /// radio group, which RadioButtons supplies through its own key handling.
    ///
    /// Both were verified INERT on the running app rather than assumed: with focus resting on a
    /// non-selected card, Down and Up moved nothing, while the same synthetic arrow injection
    /// moved the navigation list in the same binary. So focus can sit on these cards; the keys
    /// were simply never routed between them.
    ///
    /// Arrowing CHANGES the selection, which is what a Windows radio group does and what
    /// RadioButtons did here before. Left and Right are included because a user reaching for
    /// either is reaching for the same thing.
    /// </remarks>

    private void ChoiceListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not ItemsControl list
            || list.ItemsSource is not SelectableChoiceOption[] choices
            || choices.Length == 0)
        {
            return;
        }

        var delta = e.Key switch
        {
            VirtualKey.Down or VirtualKey.Right => 1,
            VirtualKey.Up or VirtualKey.Left => -1,
            _ => 0,
        };
        if (delta == 0)
        {
            return;
        }

        // Handled either way once an arrow reaches a choice list: at the ends the key does
        // nothing here, and letting it bubble would move focus out of the group, which is the
        // one thing a radio group's arrow keys must never do.
        e.Handled = true;

        var next = Math.Clamp(SelectedIndexOf(choices) + delta, 0, choices.Length - 1);
        if (choices[next].IsSelected)
        {
            return;
        }

        SelectChoice(choices, next);
        FindDescendant<RadioButton>(list.ContainerFromIndex(next) as DependencyObject)
            ?.Focus(FocusState.Keyboard);
    }

    /// <summary>First descendant of the requested type, or null.</summary>
    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    internal void ListSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionDependentButtons();

    private static void UpdateListAndEmptyStateVisibility(
        ListView list,
        FrameworkElement emptyState,
        int itemCount)
    {
        list.Visibility = itemCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        emptyState.Visibility = itemCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Sets a live region's text and visibility, and announces once if anything changed.</summary>
    /// <remarks>
    /// ONE CALL, BECAUSE TWO CALLS ANNOUNCE TWICE. Separate text and visibility helpers each raised,
    /// so showing a region and then filling it announced the OLD or empty text first and the new text
    /// second - the alias suggestion status and the speed check both did exactly that. Reassigning
    /// the same visibility announced again for no change at all.
    ///
    /// RAISED ONLY WHEN THE FINAL STATE IS VISIBLE AND SOMETHING ACTUALLY MOVED. Announcing text
    /// nobody can see is noise, and announcing an unchanged region is noise that repeats.
    ///
    /// IT HAS TO BE A CONTROL WITH A PEER. WinUI creates no automation peer for a Border or a Panel,
    /// so a live region declared on one has nothing to raise through and the raise silently does
    /// nothing. TextBlock has one.
    /// </remarks>
    private static void SetLiveRegion(TextBlock region, string text, Visibility visibility)
    {
        var textChanged = !string.Equals(region.Text, text, StringComparison.Ordinal);
        var becameVisible = region.Visibility != visibility && visibility == Visibility.Visible;

        region.Text = text;
        region.Visibility = visibility;

        if (visibility == Visibility.Visible && (textChanged || becameVisible) && IsOnAVisiblePage(region))
        {
            var peer = FrameworkElementAutomationPeer.FromElement(region)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(region);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>Shows or hides one of the history cards.</summary>
    /// <remarks>
    /// A HELPER SO THE LIVE REGIONS ARE NOT WRITTEN DIRECTLY. HistoryList is a live region and its
    /// visibility changes on every refresh; assigning it in place forced the gate to grant that
    /// region a blanket exemption, which then excused any other direct write to it anywhere in the
    /// file. Going through here means the gate needs no exemption at all.
    /// </remarks>
    private static void ShowCard(FrameworkElement card, bool show) =>
        card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Announces a region that carries its own words, rather than being assigned them.</summary>
    /// <remarks>
    /// FOR THE CARDS THAT ALREADY SAY IT. A history card's title is fixed in markup - "No dictations
    /// yet" never changes - so there is nothing to assign and nothing for the atomic setter to
    /// compare. What changes is which card is showing, and that is the news.
    /// </remarks>
    /// <returns>True when the announcement was actually raised.</returns>
    private static bool AnnounceLiveRegion(FrameworkElement region)
    {
        if (!IsOnAVisiblePage(region))
        {
            return false;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(region)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(region);
        if (peer is null)
        {
            return false;
        }

        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return true;
    }

    /// <summary>Whether every ancestor of a region is showing, not just the region itself.</summary>
    /// <remarks>
    /// A VISIBLE CONTROL ON A COLLAPSED PAGE IS NOT VISIBLE. Every settings page in this window is
    /// built once and collapsed until it is chosen, so history finishing its load while the user is
    /// on Home announced "History loaded" from a page nobody was looking at. The control's own
    /// Visibility says nothing about that.
    ///
    /// PARENTS, NOT IsOffscreen. A status scrolled out of its own viewport is still on the page the
    /// user is reading and should still announce; a status on a collapsed page should not. Those are
    /// different questions and only the first one is about ancestry.
    /// </remarks>
    private static bool IsOnAVisiblePage(DependencyObject region)
    {
        for (var node = region; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Sets a live region's text, leaving its visibility alone.</summary>
    private static void SetLiveText(TextBlock region, string text) =>
        SetLiveRegion(region, text, region.Visibility);

    /// <summary>Shows or hides a live region without changing its words.</summary>
    /// <remarks>
    /// A REGION THAT DISAPPEARS IS NOT ANNOUNCED, which is why this defers to the same setter: the
    /// raise happens only when the final state is visible. Hiding something is not news.
    /// </remarks>
    private static void SetLiveVisibility(TextBlock region, Visibility visibility) =>
        SetLiveRegion(region, region.Text, visibility);

    /// <summary>What every surface says about settings that freeze when a recording starts.</summary>
    /// <remarks>
    /// ONE STRING, LIKE macOS'S OWN `SettingsCopy`, because it appeared in two places and the two
    /// were already different - a banner and an inline helper line saying the same thing in
    /// different words is how a user decides one of them means something else.
    /// </remarks>
    private const string FrozenPerRecordingCopy =
        "Changes made during a recording apply to the next recording.";

    private void ShowMessage(string title, string message, InfoBarSeverity severity) =>
        ShowMessage(title, message, severity, offer: null);

    /// <summary>Shows one message, optionally carrying the one thing the user can do about it.</summary>
    /// <remarks>
    /// THE BUTTON IS CLEARED ON EVERY MESSAGE, INCLUDING THE ONES THAT DO NOT WANT ONE. An action
    /// left on the bar belongs to a message that has gone, and this app has already been bitten by
    /// exactly that shape - a "Snippet removed" confirmation that stayed visible across four other
    /// pages, reading as a message about wherever the user now was. A stale BUTTON is worse than a
    /// stale sentence, because pressing it does something.
    /// </remarks>
    private void ShowMessage(
        string title, string message, InfoBarSeverity severity, ImportConflictOffer? offer)
    {
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.Severity = severity;
        OperationInfoBar.ActionButton = offer is null ? null : BuildOfferButton(offer);
        var wasAlreadyOpen = OperationInfoBar.IsOpen;
        OperationInfoBar.IsOpen = true;
        if (!wasAlreadyOpen)
        {
            PlayNotificationEntrance();
        }
    }

    /// <summary>
    /// Opens the notification bar by growing it, instead of shoving the page down in one frame.
    /// </summary>
    /// <remarks>
    /// Measured on the running app: the bar arrived in a single frame and the page content below
    /// jumped down 76 pixels with it. The bar lives in an Auto row above the pages - which is
    /// deliberate, and fixed a worse defect where it painted OVER the page title - so its arrival
    /// is a layout change and a layout change is what has to be animated.
    ///
    /// MaxHeight IS THE PROPERTY TO ANIMATE, and the choice is not arbitrary. An Auto row takes the
    /// smaller of its content's natural height and any maximum, so growing the maximum from zero
    /// grows the row smoothly without anyone needing to know how tall the bar will be. Measuring
    /// its natural height first would need a layout pass, and a forced layout pass inside a queued
    /// callback is what crashed this app earlier in this branch.
    ///
    /// AND BECAUSE IT IS A LAYOUT PROPERTY IT NEEDS EnableDependentAnimation. The first version of
    /// this method did not set it, so the grow was silently skipped while everything around it
    /// worked - see the comment at the animation itself. The reasoning above was correct about
    /// WHICH property and silent about what animating it requires, which is the more dangerous
    /// shape: a justification that reads as settled and is only half the story.
    ///
    /// The ceiling is deliberately far above any real bar. The row stops at the content's natural
    /// height and the animation runs on past it with no visible effect, so a long message is not
    /// clipped by a number someone guessed. MaxHeight is released entirely when the animation ends,
    /// so nothing is left capped - a bar whose message grows later must still be able to grow.
    ///
    /// Only the OPEN is animated. Closing is driven by navigation and by the bar's own dismiss
    /// button, and a page that has already changed underneath a shrinking bar reads as a glitch
    /// rather than as motion. Stated rather than left as an omission someone will read as a bug.
    /// </remarks>
    private void PlayNotificationEntrance()
    {
        if (!_animationsEnabled)
        {
            return;
        }

        OperationInfoBar.MaxHeight = 0;
        OperationInfoBar.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(NotificationEntranceMilliseconds));

        var grow = new DoubleAnimation
        {
            To = NotificationEntranceCeilingPixels,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // WITHOUT THIS THE ANIMATION IS SILENTLY SKIPPED AND THE FIRST VERSION WAS.
            // MaxHeight is a LAYOUT property, which makes this a dependent animation, and WinUI
            // refuses to run those unless asked - with no error, no exception and no log line.
            // Measured on the running app: MaxHeight was set to 0, the grow never ran, the fade
            // animated opacity on an element clamped to zero height, and 220ms later the Completed
            // handler released MaxHeight and the bar appeared in ONE frame. A dead pause followed
            // by the exact snap the animation existed to remove.
            //
            // The same build animated a page entrance correctly through the same guard, the same
            // API and the same file, which is what ruled out every other suspect: that one targets
            // Opacity and a transform, both independent.
            //
            // A ScaleTransform would stay off the layout path and is the usual advice, and it is
            // WRONG HERE: scaling the bar would not move the page below it, because the page's
            // position comes from this row's height. The bar would grow smoothly while the page
            // still jumped - worse than either, and it would pass a check on the bar alone.
            // So the layout animation is the point rather than an oversight, and its cost is one
            // property for 220ms.
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(grow, OperationInfoBar);
        Storyboard.SetTargetProperty(grow, "MaxHeight");

        var fade = new DoubleAnimation { To = 1, Duration = duration };
        Storyboard.SetTarget(fade, OperationInfoBar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(grow);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => OperationInfoBar.MaxHeight = double.PositiveInfinity;
        storyboard.Begin();
    }

    /// <summary>
    /// Colours the Minimize, Maximize and Close glyphs for the theme actually on screen.
    /// </summary>
    /// <remarks>
    /// WINDOWS DRAWS THESE, NOT XAML, so setting RequestedTheme on the content does not reach them.
    /// They follow the MACHINE's theme unless told otherwise, which is invisible while the two
    /// agree and produces white glyphs on a near-white titlebar the moment a user picks Light on a
    /// machine set to Dark: 1.11:1, against the 3:1 a control glyph needs. The window offers no
    /// visible way to close it.
    ///
    /// The colour comes from the same token the window's text uses, read off a zero-size swatch
    /// bound to it, so there is exactly one definition of "the primary text colour in this theme".
    ///
    /// Backgrounds stay transparent so the buttons sit on the titlebar rather than on plates of
    /// their own; only hover and pressed take a wash, which is the Windows behaviour. Inactive
    /// glyphs are dimmed rather than recoloured, because a dimmed caption button is how Windows
    /// says the window is not focused, and that signal is worth keeping.
    /// </remarks>
    private void ApplyCaptionButtonColors()
    {
        if (ThemeColorProbe.Background is not SolidColorBrush probe)
        {
            return;
        }

        var glyph = probe.Color;
        var wash = WindowRoot.ActualTheme == ElementTheme.Dark ? (byte)0x20 : (byte)0x14;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = glyph;
        titleBar.ButtonHoverForegroundColor = glyph;
        titleBar.ButtonPressedForegroundColor = glyph;
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x9B, glyph.R, glyph.G, glyph.B);
        titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(wash, glyph.R, glyph.G, glyph.B);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(
            (byte)(wash + 0x10),
            glyph.R,
            glyph.G,
            glyph.B);
    }

    /// <summary>
    /// Puts the Windows 11 material behind the two cards, in place of a flat painted canvas.
    /// </summary>
    /// <remarks>
    /// Mica is the single loudest "this is a Windows 11 app" signal there is - Settings, Photos and
    /// Terminal all sit on it - and its absence is most of why this window read as a form.
    ///
    /// THE ORDER IS THE WHOLE THING. The canvas brush paints OVER the material, so it has to be
    /// cleared; but clearing it on a machine where Mica is unavailable leaves a window with no
    /// background at all. So the brush is cleared only AFTER the backdrop is actually assigned, and
    /// the unsupported path changes nothing and needs no fallback of its own.
    ///
    /// Mica follows the window content's theme, and ApplyTheme sets RequestedTheme on exactly that
    /// element, so the light and dark materials follow the user's choice without a second switch.
    /// </remarks>
    private void TryUseMicaBackdrop()
    {
        if (!MicaController.IsSupported())
        {
            SystemBackdrop = null;
            return;
        }

        if (SystemBackdrop is not MicaBackdrop)
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }

        WindowRoot.Background = null;
    }

    /// <summary>
    /// The short settle a Windows 11 page makes when it arrives, instead of a hard cut.
    /// </summary>
    /// <remarks>
    /// Windows 11 apps MOVE. A page that simply appears is one of the loudest not-native signals
    /// after a missing backdrop, and every page here was a Visibility flip.
    ///
    /// Hand-rolled rather than EntranceThemeTransition, and the reason is not preference: a theme
    /// transition fires when an element is ADDED to the tree, and these pages are always in the
    /// tree and only change Visibility, so the sanctioned transition would attach cleanly and never
    /// play. That is the "ships and does nothing" shape, so it is written down here rather than
    /// discovered by measuring an unchanged screen.
    ///
    /// Honours the system's animation setting. A user who has turned animations off has said so
    /// once, for every app, and Fluent motion is not exempt from that.
    /// </remarks>
    private void PlayPageEntrance(UIElement page)
    {
        if (!_animationsEnabled)
        {
            page.Opacity = 1;
            return;
        }

        var offset = new TranslateTransform { Y = PageEntranceOffsetPixels };
        page.RenderTransform = offset;
        page.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(PageEntranceMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation { To = 1, Duration = duration };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var settle = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(settle, offset);
        Storyboard.SetTargetProperty(settle, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(settle);
        storyboard.Begin();
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
