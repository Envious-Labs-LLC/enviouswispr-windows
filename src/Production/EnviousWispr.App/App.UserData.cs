using System.ComponentModel;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.UserData;

namespace EnviousWispr.App;

/// <summary>"Export my data" and "Delete all EnviousWispr data", the app's half. Ref: #42.</summary>
/// <remarks>
/// WHY THE APP OWNS THIS AT ALL. The Store package gets no uninstall hook, and the data folder is
/// excluded from package virtualization so it survives an uninstall by design (product contract,
/// founder decision A1). The only place a person can remove it, or take it with them, is here.
/// </remarks>
public partial class App
{
    private const int DeletionNotRequested = 0;
    private const int DeletionPending = 1;
    private const int DeletionRan = 2;

    // A FEW WALKS, A MOMENT APART, BECAUSE THE LAST HANDLES CLOSE JUST AFTER THE EXIT DOES. The runtime
    // worker is a separate process the exit ends, and Windows lets go of its files a moment later. A
    // file still held after the last walk is reported, never waited on for ever.
    private const int ErasureWalks = 3;
    private static readonly TimeSpan ErasureWalkPause = TimeSpan.FromMilliseconds(500);

    /// <summary>The session hold the deletion keeps until the app has left; given back by the exit's admission close.</summary>
    private IDisposable? _dataDeletionHold;

    /// <summary>Whether the data folder is to be emptied as the process ends, and whether that has happened.</summary>
    private int _dataDeletion;

    /// <summary>Whether Credential Manager kept any of this app's keys; read by the erasure's note for the next launch.</summary>
    private volatile bool _credentialsRemaining;

    /// <summary>Writes the export through the stores the app owns, inside the presentation lease so the exit cannot dispose them under it.</summary>
    private async Task<UserDataExportResult> ExportUserDataAsync(PortableProfile profile, int retentionDays, string path)
    {
        if (Leaving || _presentation is not { } presentation || !presentation.TryEnter(out var lease))
        {
            return new UserDataExportResult(Succeeded: false);
        }

        using (lease)
        {
            try
            {
                return await UserDataExportService.ExportAsync(
                    profile,
                    _historyStore,
                    retentionDays,
                    _recoveryTextStore,
                    path,
                    DateTimeOffset.UtcNow,
                    lease.Closing).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (lease.Closing.IsCancellationRequested)
            {
                // THE APP IS LEAVING. The temporary file is gone and nothing reached the chosen path.
                return new UserDataExportResult(Succeeded: false);
            }
        }
    }

    /// <summary>Takes the session for the deletion, or says what has it; on success the app removes the keys and leaves.</summary>
    /// <remarks>
    /// REFUSED WHILE ANYTHING ELSE HAS THE SESSION. A dictation, a file transcription, an update install
    /// or a paste of the last dictation all hold files this is about to remove; the person is told which,
    /// and nothing is deleted. Once held, a press is answered Busy until the process ends.
    /// </remarks>
    private SessionHoldAttempt? DeleteAllData()
    {
        if (Leaving)
        {
            return new SessionHoldAttempt(null, SessionHoldRefusal.Closed);
        }

        var attempt = _sessionCoordinator?.TryHold(SessionHolder.DataDeletion) ?? new SessionHoldAttempt(NoScope.Instance);
        if (attempt.Hold is not { } hold)
        {
            _logger.Write(new AppLogEntry(DateTimeOffset.UtcNow, AppEventCode.DataDeletionRefused));
            return attempt;
        }

        // PUBLISHED BEFORE ANYTHING IS AWAITED, as the update install's hold is: the exit's admission close
        // gives it back, so the session's shutdown never waits its budget for a hold nobody will release.
        Volatile.Write(ref _dataDeletionHold, hold);
        _ = DeleteAllDataAndLeaveAsync();
        return null;
    }

    /// <summary>The keys first, while the app is whole; then the orderly exit; then the folder; then the process ends.</summary>
    /// <remarks>
    /// THE FOLDER IS EMPTIED ONLY AFTER THE EXIT, because until then the log, the stores, the run state and
    /// the runtime worker hold its files. The exit is the one every path out uses, so it releases them in
    /// the same order the tray's Exit does. An exit that cannot finish ends the process from inside the
    /// lifetime; the terminator empties the folder first on that path too, so the person's request is
    /// carried out whichever way the process ends.
    /// </remarks>
    private async Task DeleteAllDataAndLeaveAsync()
    {
        try
        {
            _credentialsRemaining = !DeleteStoredCredentials();
            if (!DataDirectoryEraser.CanErase(_dataDirectory))
            {
                // A DATA FOLDER THAT IS A LINK, A FILE OR A DRIVE ROOT IS NEVER EMPTIED OR WRITTEN INTO, so no
                // note can be left for the next launch. Said here instead, while the log is still open.
                _logger.Write(new AppLogEntry(
                    DateTimeOffset.UtcNow,
                    AppEventCode.DataDeletionRefused,
                    AppFailureCategory.AccessDenied));
            }

            Volatile.Write(ref _dataDeletion, DeletionPending);
            _exitRequested = true;
            await PrepareForExitAsync().ConfigureAwait(true);
            await Task.Run(EraseDataDirectory).ConfigureAwait(true);
        }
        finally
        {
            // THE PERSON WAS TOLD THE APP CLOSES, so it closes whatever happened above.
            Exit();
        }
    }

    /// <summary>Removes every credential this app's namespace owns; false when any could not be removed or listed.</summary>
    private bool DeleteStoredCredentials()
    {
        try
        {
            return _credentialStore.DeleteAll() == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Empties the data folder once, when a deletion is pending; leaves the next launch a count of anything it could not remove.</summary>
    /// <remarks>
    /// RUN ONCE, from whichever of the two endings reaches it first: the deletion's own continuation after a
    /// finished exit, or the terminator when the exit could not finish. Nothing may write to the folder after
    /// this, so it runs only once the logger and every store are closed or the process is about to end.
    /// </remarks>
    private void EraseDataDirectory()
    {
        if (Interlocked.CompareExchange(ref _dataDeletion, DeletionRan, DeletionPending) != DeletionPending)
        {
            return;
        }

        var report = DataDirectoryEraser.Erase(_dataDirectory);
        for (var walk = 1; walk < ErasureWalks && !report.Complete; walk++)
        {
            Thread.Sleep(ErasureWalkPause);
            report = DataDirectoryEraser.Erase(_dataDirectory);
        }

        if (!report.Complete || _credentialsRemaining)
        {
            // NOT A SUCCESS THAT WAS NOT. The log is closed and was itself being deleted, so the next launch
            // is told instead: a count and a flag, written where it will look, read once and removed.
            DataDirectoryEraser.WriteLeftover(
                _dataDirectory,
                new DataDeletionLeftover(Math.Max(report.Remaining, report.Refused), _credentialsRemaining));
        }
    }

    /// <summary>At launch: whether the last deletion left anything behind; logged content-free and shown to the person.</summary>
    private void ReportDataDeletionLeftover(MainWindow window)
    {
        if (DataDirectoryEraser.TakeLeftover(_dataDirectory) is not { } leftover)
        {
            return;
        }

        _logger.Write(new AppLogEntry(
            DateTimeOffset.UtcNow,
            AppEventCode.DataDeletionIncomplete,
            AppFailureCategory.StorageUnavailable));
        window.SetDataDeletionLeftoverNotice(leftover);
    }
}
