using System.Runtime.InteropServices;
using EnviousWispr.Core.Distribution;
using EnviousWispr.Services.Distribution;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace EnviousWispr.App;

/// <summary>Checks the Microsoft Store for a newer package and asks it to install one.</summary>
/// <remarks>
/// THE STORE SIGNS, DELIVERS AND APPLIES; THIS ONLY ASKS. There is nothing here to download, stage or
/// verify, because Microsoft re-signs every package and Windows admits it. What stays the app's job is
/// the product promise: it asks for an install only when the session is idle, which the caller owns.
///
/// A COPY WITHOUT PACKAGE IDENTITY MAKES NO STORE CALL. A development build runs unpackaged, and the
/// Store would answer for no package at all; it is told plainly that it was not installed from the Store.
/// </remarks>
internal sealed class StoreUpdateService
{
    private readonly Func<nint> _ownerWindow;
    private StoreContext? _context;
    private IReadOnlyList<StorePackageUpdate>? _pending;

    /// <param name="ownerWindow">The main window's handle, read when the Store is first asked.</param>
    public StoreUpdateService(Func<nint> ownerWindow)
    {
        ArgumentNullException.ThrowIfNull(ownerWindow);
        _ownerWindow = ownerWindow;
        InstalledVersion = TryReadInstalledVersion();
    }

    /// <summary>Whether this copy has package identity, and so came from the Store (or a sideloaded test package).</summary>
    public bool IsStoreInstalled => InstalledVersion is not null;

    /// <summary>The installed package's version, or null for an unpackaged copy.</summary>
    public string? InstalledVersion { get; }

    /// <summary>Asks the Store whether a newer package exists; keeps any it offers for <see cref="RequestInstallAsync"/>.</summary>
    /// <remarks>READ-ONLY, SO IT NEEDS NO SESSION HOLD: nothing is downloaded or applied by asking.</remarks>
    public async Task<UpdateOperationResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStoreInstalled)
        {
            return new UpdateOperationResult(UpdateOperationStatus.NotPackaged);
        }

        try
        {
            var updates = await Context().GetAppAndOptionalStorePackageUpdatesAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(true);
            var offered = updates.ToArray();
            var result = StoreUpdateOutcome.FromCheck(
                offered.Select(update => update.Package.Id.Version).ToArray(),
                InstalledVersion);
            Volatile.Write(ref _pending, result.CanApply ? offered : null);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            Volatile.Write(ref _pending, null);
            return new UpdateOperationResult(UpdateOperationStatus.Failed);
        }
    }

    /// <summary>Asks the Store to download and install what the last check found. The caller holds the session.</summary>
    /// <remarks>
    /// TAKEN, NOT READ. The pending set is consumed by the request, so a second press cannot ask twice;
    /// a request that did not complete needs a fresh check, which also means it installs what the Store
    /// offers then rather than what it offered before.
    /// </remarks>
    public async Task<UpdateOperationStatus> RequestInstallAsync(CancellationToken cancellationToken = default)
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        if (!IsStoreInstalled || pending is null || pending.Count == 0)
        {
            return UpdateOperationStatus.Failed;
        }

        try
        {
            // UI-THREAD CONTINUATIONS ON PURPOSE. The Store shows its own prompt owned by the main window.
            var result = await Context().RequestDownloadAndInstallStorePackageUpdatesAsync(pending)
                .AsTask(cancellationToken)
                .ConfigureAwait(true);
            return StoreUpdateOutcome.FromInstall(result.OverallState);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return UpdateOperationStatus.Failed;
        }
    }

    /// <summary>The Store context, parented to the main window.</summary>
    /// <remarks>
    /// A DESKTOP APP MUST NAME ITS WINDOW. Without InitializeWithWindow the Store has no owner for its
    /// install prompt and the request fails rather than showing it. Created on first use, because the
    /// window does not exist when this service is constructed.
    /// </remarks>
    private StoreContext Context()
    {
        if (_context is { } existing)
        {
            return existing;
        }

        var handle = _ownerWindow();
        if (handle == 0)
        {
            throw new InvalidOperationException("The main window is not available to own the Store prompt.");
        }

        var context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(context, handle);
        _context = context;
        return context;
    }

    /// <summary>This copy's package version, or null when it has no package identity.</summary>
    /// <remarks>
    /// ASKED ONCE, AT CONSTRUCTION. Identity cannot change while a process runs. An unpackaged process
    /// throws on reading Package.Current (APPMODEL_ERROR_NO_PACKAGE), and any failure to establish
    /// identity reads as unpackaged: the safe side, because it makes no Store call.
    /// </remarks>
    private static string? TryReadInstalledVersion()
    {
        try
        {
            return StoreUpdateOutcome.Format(Package.Current.Id.Version);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return null;
        }
    }
}
