using System.Globalization;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.UserData;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace EnviousWispr.App;

/// <summary>The Backup page's "Your data" card: export everything the person owns, or delete all of it. Ref: #42.</summary>
/// <remarks>
/// THE WINDOW ASKS; THE APP DOES. Export reads the stores and deletion takes the session, stops the app and empties
/// its folder, and all of that is the app's. The window chooses the file, confirms, and draws the answer.
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>Writes the export to the chosen path from the profile and history retention the window holds; null until the app supplies it.</summary>
    public Func<PortableProfile, int, string, Task<UserDataExportResult>>? ExportUserData { get; set; }

    /// <summary>Starts "Delete all EnviousWispr data": null when it began and the app is leaving, or the refusal when the session was in use.</summary>
    public Func<SessionHoldAttempt?>? DeleteAllData { get; set; }

    /// <summary>What the confirmation lists, exactly: every kind of thing the deletion removes.</summary>
    internal const string DeleteAllDataConfirmation =
        "This permanently removes, from this PC:\n\n"
        + "• Your dictation history and any recovered text\n"
        + "• Your settings, your words, and your snippets\n"
        + "• Downloaded speech and polish models (models kept by Ollama stay in Ollama)\n"
        + "• Diagnostic logs\n"
        + "• API keys stored for AI Polish\n\n"
        + "EnviousWispr then closes, and starts fresh the next time you open it. Export your data first if you want a copy.";

    private async void ExportMyDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportUserData is not { } export)
        {
            ShowMessage("Not ready yet", "EnviousWispr is still starting. Try again in a moment.", InfoBarSeverity.Informational);
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = "EnviousWispr-my-data",
            DefaultFileExtension = ".zip",
        };
        picker.FileTypeChoices.Add("EnviousWispr data", [".zip"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var result = await export(
            _settings.ToPortableProfile(),
            _settings.Preferences.History.RetentionDays,
            file.Path).ConfigureAwait(true);
        if (_session.Closing)
        {
            return;
        }

        var dictations = result.HistoryEntries == 1
            ? "1 dictation"
            : $"{result.HistoryEntries.ToString(CultureInfo.CurrentCulture)} dictations";
        ShowMessage(
            result.Succeeded ? "Your data was exported" : "Your data could not be exported",
            result.Succeeded
                ? $"Settings, words, snippets, and {dictations} were saved. No API keys, models, diagnostic logs, or recordings are in the file."
                : "The export failed. Any file already at the place you chose was left unchanged. Check there is space and that you can save there, then try again.",
            result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void DeleteAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeleteAllData is not { } delete)
        {
            ShowMessage("Not ready yet", "EnviousWispr is still starting. Try again in a moment.", InfoBarSeverity.Informational);
            return;
        }

        // CANCEL IS THE DEFAULT, as on every other dialog here that removes something for good: Enter
        // or Escape keeps the data. The design system has no destructive button treatment of its own,
        // so the primary button says exactly what it does instead.
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Delete all EnviousWispr data?",
            Content = new TextBlock { Text = DeleteAllDataConfirmation, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Delete everything and close",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _session.Closing)
        {
            return;
        }

        // ASKED ONLY AFTER THE CONFIRMATION, so a dictation that started while the dialog was open is
        // what the answer is about.
        if (delete() is { } refusal)
        {
            ShowMessage("Nothing was deleted", DeletionRefusalSentence(refusal), InfoBarSeverity.Warning);
            return;
        }

        // THE BUTTONS STAY LIVE. The app is on its way out, and a second press is answered by the hold
        // it already has - "already deleting", or "closing" - rather than by a control nothing re-enables.
        ShowMessage("Deleting your data", "EnviousWispr is closing to remove everything it keeps.", InfoBarSeverity.Informational);
    }

    /// <summary>Why the deletion could not start, in the words the other refusals use.</summary>
    private static string DeletionRefusalSentence(SessionHoldAttempt refusal) => refusal switch
    {
        { Refusal: SessionHoldRefusal.Dictation } => "A dictation is running. Try again when it finishes.",
        { HeldBy: SessionHolder.UpdateApply } => "EnviousWispr is updating. Try again when it finishes.",
        { HeldBy: SessionHolder.LastDictationReuse } => "EnviousWispr is pasting your last dictation. Try again in a moment.",
        { HeldBy: SessionHolder.FileTranscription } => "A file is being transcribed. Stop it or let it finish, then try again.",
        { HeldBy: SessionHolder.DataDeletion } => "EnviousWispr is already deleting your data.",
        { HeldBy: SessionHolder.GraphicsRuntimeSwitch } => "EnviousWispr is moving dictation to your graphics card. Try again in a moment.",
        { HeldBy: SessionHolder.SavedDictationPaste } => "EnviousWispr is pasting your dictation. Try again in a moment.",
        _ => "EnviousWispr is closing.",
    };

    /// <summary>Says, once, that the last deletion could not remove everything, and what to do about it.</summary>
    /// <remarks>
    /// NOT A SUCCESS THAT WAS NOT. A file still in use when the app left stays where it was, and the person
    /// asked for it gone. The count is all that is known and all that is said: never which file, never where.
    /// Shown where a person lands after a deletion, which is the first-run screen, and on Home.
    /// </remarks>
    public void SetDataDeletionLeftoverNotice(DataDeletionLeftover leftover)
    {
        ArgumentNullException.ThrowIfNull(leftover);
        const string title = "Some EnviousWispr data was not removed";
        var parts = new List<string>();
        if (leftover.Remaining > 0)
        {
            parts.Add(leftover.Remaining == 1
                ? "1 item was still in use"
                : $"{leftover.Remaining.ToString(CultureInfo.CurrentCulture)} items were still in use");
        }

        if (leftover.CredentialsRemaining)
        {
            parts.Add("your saved API keys could not be removed");
        }

        var what = parts.Count == 0 ? "Something could not be removed" : string.Join(", and ", parts);
        var message = char.ToUpper(what[0], CultureInfo.CurrentCulture) + what[1..]
            + " when EnviousWispr last deleted your data. To finish, open Backup and choose Delete all EnviousWispr data again.";
        FoundationInfoBar.Title = title;
        FoundationInfoBar.Message = message;
        FoundationInfoBar.Severity = InfoBarSeverity.Warning;
        SetOnboardingReliabilityNotice(title, message);
    }
}
