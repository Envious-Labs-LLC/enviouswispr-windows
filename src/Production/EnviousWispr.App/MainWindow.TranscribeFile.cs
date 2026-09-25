using EnviousWispr.Pipeline;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>Runs one file through the app's transcription; null until the app has an engine to offer.</summary>
    public Func<string, IProgress<FileTranscriptionProgress>, CancellationToken, Task<FileTranscriptionResult>>? TranscribeFile { get; set; }

    private async void TranscribeFileChooseButton_Click(object sender, RoutedEventArgs e)
    {
        if (TranscribeFile is not { } transcribe || _transcribeFileRun is not null)
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

        using var run = new CancellationTokenSource();
        _transcribeFileRun = run;
        TranscribeFileChooseButton.IsEnabled = false;
        TranscribeFileCancelButton.Visibility = Visibility.Visible;
        TranscribeFileResultPanel.Visibility = Visibility.Collapsed;
        TranscribeFileProgress.Value = 0;
        TranscribeFileProgress.IsIndeterminate = true;
        TranscribeFileProgress.Visibility = Visibility.Visible;
        ShowTranscribeFileStatus("Reading the file...");
        try
        {
            var result = await transcribe(file.Path, new Progress<FileTranscriptionProgress>(ShowTranscribeFileProgress), run.Token)
                .ConfigureAwait(true);
            ShowTranscribeFileResult(result);
        }
        finally
        {
            _transcribeFileRun = null;
            TranscribeFileChooseButton.IsEnabled = true;
            TranscribeFileCancelButton.Visibility = Visibility.Collapsed;
            TranscribeFileProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void TranscribeFileCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _transcribeFileRun?.Cancel();
        ShowTranscribeFileStatus("Stopping after the piece in progress...");
    }

    private void ShowTranscribeFileProgress(FileTranscriptionProgress progress)
    {
        if (progress.AudioTotal is { TotalSeconds: > 0 } total)
        {
            TranscribeFileProgress.IsIndeterminate = false;
            TranscribeFileProgress.Value = Math.Clamp(progress.AudioDone / total, 0, 1);
            ShowTranscribeFileStatus($"Transcribed {Minutes(progress.AudioDone)} of {Minutes(total)}");
        }
        else
        {
            ShowTranscribeFileStatus($"Transcribed {Minutes(progress.AudioDone)}");
        }
    }

    private void ShowTranscribeFileResult(FileTranscriptionResult result)
    {
        var (status, showText) = result.Outcome switch
        {
            FileTranscriptionOutcome.Completed => ($"Done: {Minutes(result.AudioDone)} of audio.", true),
            FileTranscriptionOutcome.Empty => ("No speech was found in that file.", false),
            FileTranscriptionOutcome.Cancelled => ("Stopped. The words so far are below.", !string.IsNullOrWhiteSpace(result.Text)),
            FileTranscriptionOutcome.Failed when string.IsNullOrWhiteSpace(result.Text) =>
                ("That file could not be transcribed. Choose Transcription in the sidebar to check the engine.", false),
            FileTranscriptionOutcome.Failed => ("Transcription stopped part way. The words so far are below.", true),
            _ => ("That file could not be transcribed.", false),
        };
        ShowTranscribeFileStatus(status);
        TranscribeFileResultText.Text = showText ? result.Text : string.Empty;
        TranscribeFileResultPanel.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTranscribeFileStatus(string text)
    {
        TranscribeFileStatusText.Text = text;
        TranscribeFileStatusText.Visibility = Visibility.Visible;
    }

    private void TranscribeFileCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(TranscribeFileResultText.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowMessage("Copied", "The transcript is on your clipboard.", InfoBarSeverity.Success);
    }

    private async void TranscribeFileSaveButton_Click(object sender, RoutedEventArgs e)
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

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s" : $"{Math.Max(0, (int)Math.Round(span.TotalSeconds))} s";
}
