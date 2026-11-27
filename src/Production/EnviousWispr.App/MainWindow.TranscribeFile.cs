using EnviousWispr.Pipeline;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace EnviousWispr.App;

/// <summary>The Transcribe a File page: choose, watch it go, stop, copy or save. Ref: #211, macOS #2648.</summary>
/// <remarks>
/// THE WINDOW HOLDS NO ENGINE. It hands the chosen path to <see cref="TranscribeFile"/>, which the app supplies, and
/// draws what comes back; the job, the engine and the dictation's claim on it are the app's. The words stay on this
/// page until the person copies or saves them - nothing about the file reaches History in this version.
/// </remarks>
public sealed partial class MainWindow
{
    private CancellationTokenSource? _transcribeFileRun;
    private int _transcribeFileCopyVersion;
    private bool _transcribeFileDropZoneHovering;
    private bool _transcribeFileDropZoneDragActive;

    /// <summary>Runs one file through the app's transcription; null until the app has an engine to offer.</summary>
    public Func<string, IProgress<FileTranscriptionProgress>, CancellationToken, Task<FileTranscriptionResult>>? TranscribeFile { get; set; }

    internal async void TranscribeFileChooseButton_Click(object sender, RoutedEventArgs e)
    {
        await PickAndStartFileTranscriptionAsync().ConfigureAwait(true);
    }

    internal async void TranscribeFileDropZone_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideInteractiveChildButton(source))
        {
            return;
        }

        e.Handled = true;
        await PickAndStartFileTranscriptionAsync().ConfigureAwait(true);
    }

    internal void TranscribeFileDropZone_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _transcribeFileDropZoneHovering = true;
        UpdateTranscribeDropZoneVisualState();
    }

    internal void TranscribeFileDropZone_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _transcribeFileDropZoneHovering = false;
        UpdateTranscribeDropZoneVisualState();
    }

    private async Task PickAndStartFileTranscriptionAsync()
    {
        if (TranscribeFile is not { } || _transcribeFileRun is not null)
        {
            ShowMessage("Not ready yet", "Transcription is still starting. Try again in a moment.", InfoBarSeverity.Informational);
            return;
        }

        var picker = new FileOpenPicker();
        foreach (var type in new[] { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac" })
        {
            picker.FileTypeFilter.Add(type);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await StartFileTranscriptionAsync(file.Path).ConfigureAwait(true);
    }

    internal void TranscribeFileDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            _transcribeFileDropZoneDragActive = true;
            UpdateTranscribeDropZoneVisualState();
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Drop audio file to transcribe";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    internal void TranscribeFileDropZone_DragLeave(object sender, DragEventArgs e)
    {
        _transcribeFileDropZoneDragActive = false;
        UpdateTranscribeDropZoneVisualState();
    }

    internal async void TranscribeFileDropZone_Drop(object sender, DragEventArgs e)
    {
        _transcribeFileDropZoneDragActive = false;
        UpdateTranscribeDropZoneVisualState();

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0)
        {
            return;
        }

        if (items[0] is Windows.Storage.StorageFile file)
        {
            var ext = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
            var supported = new[] { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac" };
            if (!supported.Contains(ext))
            {
                ShowMessage("Unsupported file type", $"'{ext}' is not a supported audio format. Choose WAV, MP3, M4A, AAC, WMA, or FLAC.", InfoBarSeverity.Warning);
                return;
            }

            await StartFileTranscriptionAsync(file.Path).ConfigureAwait(true);
        }
    }

    private async Task StartFileTranscriptionAsync(string filePath)
    {
        if (TranscribeFile is not { } transcribe || _transcribeFileRun is not null)
        {
            ShowMessage("Not ready yet", "Transcription is still starting. Try again in a moment.", InfoBarSeverity.Informational);
            return;
        }

        using var run = new CancellationTokenSource();
        _transcribeFileRun = run;
        TranscribeFileChooseButton.IsEnabled = false;
        TranscribeFileCancelButton.Visibility = Visibility.Visible;
        TranscribeFileProgress.Value = 0;
        TranscribeFileProgress.IsIndeterminate = true;
        TranscribeFileProgress.Visibility = Visibility.Visible;
        SetTranscribeFileStep(FileTranscriptionStepState.StartStep);
        ShowTranscribeFileStatus($"Working on {System.IO.Path.GetFileName(filePath)}...");
        try
        {
            var result = await transcribe(filePath, new Progress<FileTranscriptionProgress>(ShowTranscribeFileProgress), run.Token)
                .ConfigureAwait(true);
            ShowTranscribeFileResult(result);
        }
        finally
        {
            _transcribeFileRun = null;
            TranscribeFileChooseButton.IsEnabled = true;
            TranscribeFileCancelButton.Visibility = Visibility.Collapsed;
            TranscribeFileProgress.Visibility = Visibility.Collapsed;
            UpdateTranscribeDropZoneVisualState();
        }
    }

    internal void TranscribeFileCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _transcribeFileRun?.Cancel();
        SetTranscribeFileStep(FileTranscriptionStepState.ChooseFileStep);
        ShowTranscribeFileStatus("Stopping after the piece in progress...");
    }

    private void ShowTranscribeFileProgress(FileTranscriptionProgress progress)
    {
        // THE JOB HAS THE SESSION: the last file's words go now, and not before - a refused start keeps them.
        TranscribeFileResultPanel.Visibility = Visibility.Collapsed;
        SetTranscribeFileStep(FileTranscriptionStepState.StepForProgress(progress));
        if (progress.PiecesDone == 0)
        {
            return;
        }

        if (progress.AudioTotal is { TotalSeconds: > 0 } total)
        {
            var fraction = Math.Clamp(progress.AudioDone / total, 0, 1);
            TranscribeFileProgress.IsIndeterminate = false;
            TranscribeFileProgress.Value = fraction;
            ShowTranscribeFileStatus($"Transcribed {Minutes(progress.AudioDone)} of {Minutes(total)}");
        }
        else
        {
            ShowTranscribeFileStatus($"Transcribed {Minutes(progress.AudioDone)}");
        }
    }

    private void ShowTranscribeFileResult(FileTranscriptionResult result)
    {
        // A START THAT WAS REFUSED CHANGES NOTHING ELSE ON THE PAGE: the words from the last file stay where they were.
        if (result.Outcome == FileTranscriptionOutcome.Refused)
        {
            SetTranscribeFileStep(FileTranscriptionStepState.StepForOutcome(result.Outcome));
            ShowTranscribeFileStatus(RefusalSentence(result.Refusal));
            return;
        }

        var completedCleanly = result.Outcome == FileTranscriptionOutcome.Completed && !string.IsNullOrWhiteSpace(result.Text);
        SetTranscribeFileStep(FileTranscriptionStepState.StepForOutcome(result.Outcome), isDone: completedCleanly);

        var (status, showText) = result.Outcome switch
        {
            FileTranscriptionOutcome.Completed => ($"Done: {Minutes(result.AudioDone)} of audio.", true),
            FileTranscriptionOutcome.Empty => ("No speech was found in that file.", false),
            FileTranscriptionOutcome.Cancelled => ("Stopped. The words so far are below.", !string.IsNullOrWhiteSpace(result.Text)),
            FileTranscriptionOutcome.Failed when string.IsNullOrWhiteSpace(result.Text) =>
                ("That file could not be transcribed. Choose Dictation Settings -> Engine to check the engine.", false),
            FileTranscriptionOutcome.Failed => ("Transcription stopped part way. The words so far are below.", true),
            _ => ("That file could not be transcribed.", false),
        };
        ShowTranscribeFileStatus(status);
        TranscribeFileResultText.Text = showText ? result.Text : string.Empty;
        TranscribeFileResultPanel.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        ResetTranscribeFileCopyVisual();
    }

    private void SetTranscribeFileStep(int step, bool isDone = false)
    {
        if (TranscribeStep1Badge is null)
        {
            return;
        }

        var accentSolidBrush = (Brush)Application.Current.Resources["BrandAccentSolidBrush"];
        var accentBrush = (Brush)Application.Current.Resources["BrandAccentBrush"];
        var onAccentBrush = (Brush)Application.Current.Resources["BrandOnAccentBrush"];
        var dividerBrush = (Brush)Application.Current.Resources["BrandDividerBrush"];
        var tertiaryBrush = (Brush)Application.Current.Resources["BrandTextTertiaryBrush"];
        var successBrush = (Brush)Application.Current.Resources["BrandSuccessBrush"];
        var secondaryBrush = (Brush)Application.Current.Resources["BrandTextSecondaryBrush"];
        var transparentBrush = (Brush)Application.Current.Resources["BrandHitTestBrush"];
        var textFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var symbolFont = new FontFamily("Segoe Fluent Icons");

        var activeStep = FileTranscriptionStepState.ClampStep(step);
        var steps = new (Border? Container, Border? Badge, TextBlock? Num, TextBlock? Label, string NumberText)[]
        {
            (TranscribeStep1Container, TranscribeStep1Badge, TranscribeStep1Num, TranscribeStep1Label, "1"),
            (TranscribeStep2Container, TranscribeStep2Badge, TranscribeStep2Num, TranscribeStep2Label, "2"),
            (TranscribeStep3Container, TranscribeStep3Badge, TranscribeStep3Num, TranscribeStep3Label, "3"),
        };

        for (var i = 0; i < steps.Length; i++)
        {
            var (container, badge, num, label, numberText) = steps[i];
            if (badge is null || num is null || label is null)
            {
                continue;
            }

            var stepNum = i + 1;
            var isStepCompleted = stepNum < activeStep || (isDone && stepNum == 3);
            var isStepActive = stepNum == activeStep;

            if (container is not null)
            {
                container.BorderBrush = isStepActive ? accentSolidBrush : transparentBrush;
            }

            if (isStepCompleted)
            {
                badge.Background = successBrush;
                num.FontFamily = symbolFont;
                num.FontSize = 14;
                num.FontWeight = FontWeights.Bold;
                num.Text = "\uE73E";
                num.Foreground = onAccentBrush;
                label.Foreground = isStepActive ? accentBrush : secondaryBrush;
                label.FontWeight = isStepActive ? FontWeights.SemiBold : FontWeights.Normal;
            }
            else if (isStepActive)
            {
                badge.Background = accentSolidBrush;
                num.FontFamily = textFont;
                num.FontSize = 14;
                num.FontWeight = FontWeights.Bold;
                num.Text = numberText;
                num.Foreground = onAccentBrush;
                label.Foreground = accentBrush;
                label.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                badge.Background = dividerBrush;
                num.FontFamily = textFont;
                num.FontSize = 14;
                num.FontWeight = FontWeights.Bold;
                num.Text = numberText;
                num.Foreground = tertiaryBrush;
                label.Foreground = tertiaryBrush;
                label.FontWeight = FontWeights.Normal;
            }
        }
    }

    private void UpdateTranscribeDropZoneVisualState()
    {
        if (TranscribeFileDropZone is null || TranscribeFileDashedBorder is null)
        {
            return;
        }

        var highlighted = (_transcribeFileDropZoneHovering || _transcribeFileDropZoneDragActive) && _transcribeFileRun is null;
        if (highlighted)
        {
            TranscribeFileDashedBorder.Stroke = (Brush)Application.Current.Resources["BrandAccentBrush"];
            TranscribeFileDropZone.Background = (Brush)Application.Current.Resources["BrandAccentLightBrush"];
        }
        else
        {
            TranscribeFileDashedBorder.ClearValue(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty);
            TranscribeFileDropZone.ClearValue(Panel.BackgroundProperty);
        }
    }

    private static bool IsInsideInteractiveChildButton(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Button or TextBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>Why a file could not start, in macOS's words where macOS has them. Ref: #211.</summary>
    private static string RefusalSentence(SessionHoldAttempt? refusal) => refusal switch
    {
        { Refusal: SessionHoldRefusal.Dictation } => "A dictation is running. Try again when it finishes.",
        { HeldBy: SessionHolder.UpdateApply } => "EnviousWispr is updating. Try again when it finishes.",
        { HeldBy: SessionHolder.LastDictationReuse } => "EnviousWispr is pasting your last dictation. Try again in a moment.",
        { HeldBy: SessionHolder.FileTranscription } => "Another file is being transcribed right now.",
        { HeldBy: SessionHolder.DataDeletion } => "EnviousWispr is deleting your data and will close.",
        { HeldBy: SessionHolder.GraphicsRuntimeSwitch } => "EnviousWispr is moving dictation to your graphics card. Try again in a moment.",
        { HeldBy: SessionHolder.SpeechEngineSwitch } => "EnviousWispr is updating its speech engine. Try again in a moment.",
        { HeldBy: SessionHolder.SavedDictationPaste } => "EnviousWispr is pasting your dictation. Try again in a moment.",
        _ => "EnviousWispr is closing.",
    };

    private void ShowTranscribeFileStatus(string text)
    {
        SetLiveRegion(TranscribeFileStatusText, text, Visibility.Visible);
    }

    internal void TranscribeFileCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(TranscribeFileResultText.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();

        var version = ++_transcribeFileCopyVersion;
        if (TranscribeFileCopyIcon is not null && TranscribeFileCopyText is not null)
        {
            TranscribeFileCopyIcon.Glyph = "\uE73E";
            TranscribeFileCopyText.Text = "Copied";
        }

        _ = ResetTranscribeFileCopyBadgeAfterDelayAsync(version);
    }

    private async Task ResetTranscribeFileCopyBadgeAfterDelayAsync(int expectedVersion)
    {
        await Task.Delay(2000).ConfigureAwait(true);
        if (_shuttingDownProductWindows || _transcribeFileCopyVersion != expectedVersion)
        {
            return;
        }

        ResetTranscribeFileCopyVisual();
    }

    private void ResetTranscribeFileCopyVisual()
    {
        _transcribeFileCopyVersion++;
        if (TranscribeFileCopyIcon is not null && TranscribeFileCopyText is not null)
        {
            TranscribeFileCopyIcon.Glyph = "\uE8C8";
            TranscribeFileCopyText.Text = "Copy";
        }
    }

    internal async void TranscribeFileSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = "Transcript",
            DefaultFileExtension = ".txt",
        };
        picker.FileTypeChoices.Add("Text", [".txt"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(file.Path, TranscribeFileResultText.Text).ConfigureAwait(true);
            ShowMessage("Saved", "The transcript was saved.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Not saved", "The transcript could not be written there. Choose another place.", InfoBarSeverity.Error);
        }
    }

    internal void TranscribeFileNewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transcribeFileRun is not null)
        {
            return;
        }

        ResetTranscribeFileCopyVisual();
        TranscribeFileResultText.Text = string.Empty;
        TranscribeFileResultPanel.Visibility = Visibility.Collapsed;
        SetLiveRegion(TranscribeFileStatusText, string.Empty, Visibility.Collapsed);
        SetTranscribeFileStep(FileTranscriptionStepState.ChooseFileStep);
        UpdateTranscribeDropZoneVisualState();
    }

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s" : $"{Math.Max(0, (int)Math.Round(span.TotalSeconds))} s";
}
