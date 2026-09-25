namespace EnviousWispr.Core.Distribution;

/// <summary>What a Microsoft Store update check or install came to.</summary>
/// <remarks>
/// ONLY WHAT THE STORE FLOW CAN PRODUCE. Microsoft signs and delivers the package, so there is no
/// download to stage, no hash or publisher to admit and no endpoint to configure here; a status for
/// any of those would be a word the window could show and nothing could ever cause.
/// </remarks>
public enum UpdateOperationStatus
{
    /// <summary>This copy has no package identity, so it was not installed from the Store and makes no Store call.</summary>
    NotPackaged,

    /// <summary>A dictation is recording or processing; the install was not requested.</summary>
    BusyDictating,

    /// <summary>A file is being transcribed; it holds the session until it finishes or is stopped. Ref: #211.</summary>
    BusyTranscribingFile,

    /// <summary>The Store has nothing newer for this copy.</summary>
    NoUpdate,

    /// <summary>The Store offers a newer package; it is kept pending until the person asks to install it.</summary>
    UpdateAvailable,

    /// <summary>The Store accepted the install; Windows closes the app and applies the package.</summary>
    Installing,

    /// <summary>The person declined the Store's install prompt.</summary>
    Cancelled,

    /// <summary>The Store call failed or ended in any state other than completed or cancelled.</summary>
    Failed,
}

/// <param name="Status">What happened.</param>
/// <param name="Version">The newest version the Store offers for <see cref="UpdateOperationStatus.UpdateAvailable"/>, the installed version for <see cref="UpdateOperationStatus.NoUpdate"/>; otherwise null.</param>
public sealed record UpdateOperationResult(
    UpdateOperationStatus Status,
    string? Version = null)
{
    public bool CanApply => Status == UpdateOperationStatus.UpdateAvailable;
}
