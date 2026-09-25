using EnviousWispr.Core.Distribution;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace EnviousWispr.Services.Distribution;

/// <summary>What the Microsoft Store's answers mean to the window, decided without a Store.</summary>
/// <remarks>
/// PURE, BECAUSE A StoreContext CANNOT RUN IN A TEST. The service in the app makes the calls; every
/// decision about what an answer means lives here, over the Store's own types, so the suite can sweep
/// every state the Store can report rather than the two a hand-built fake happens to return.
/// </remarks>
public static class StoreUpdateOutcome
{
    /// <summary>What a check found: nothing newer, or the newest version on offer.</summary>
    /// <param name="offered">The version of each package update the Store returned.</param>
    /// <param name="installedVersion">This copy's own version, shown when nothing is newer.</param>
    public static UpdateOperationResult FromCheck(IReadOnlyCollection<PackageVersion> offered, string? installedVersion)
    {
        ArgumentNullException.ThrowIfNull(offered);
        if (offered.Count == 0)
        {
            return new UpdateOperationResult(UpdateOperationStatus.NoUpdate, installedVersion);
        }

        // THE NEWEST, NOT THE FIRST. The call returns the app's package and any optional packages in an
        // order it does not promise, and the version the person is offered is the one they will end on.
        var newest = offered.MaxBy(Comparable);
        return new UpdateOperationResult(UpdateOperationStatus.UpdateAvailable, Format(newest));
    }

    /// <summary>What an install request ended as.</summary>
    /// <remarks>
    /// ONLY COMPLETED IS AN INSTALL. Every other state the Store can end on - an error, a low battery,
    /// a Wi-Fi requirement, or a request still pending or deploying when the call returned - left this
    /// copy as it was, so it reads as a failure the person can retry, never as progress.
    /// </remarks>
    public static UpdateOperationStatus FromInstall(StorePackageUpdateState overallState) => overallState switch
    {
        StorePackageUpdateState.Completed => UpdateOperationStatus.Installing,
        StorePackageUpdateState.Canceled => UpdateOperationStatus.Cancelled,
        _ => UpdateOperationStatus.Failed,
    };

    /// <summary>A package version as the Store and Windows Settings print it: four parts.</summary>
    public static string Format(PackageVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

    private static ulong Comparable(PackageVersion version) =>
        ((ulong)version.Major << 48) | ((ulong)version.Minor << 32) | ((ulong)version.Build << 16) | version.Revision;
}
