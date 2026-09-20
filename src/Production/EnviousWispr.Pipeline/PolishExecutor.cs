using System.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;

namespace EnviousWispr.Pipeline;

/// <summary>Which polish provider is in force and how it is hosted.</summary>
/// <param name="Provider">The provider to ask.</param>
/// <param name="UsesLocalRuntime">True when the provider runs on this machine and must hold a runtime resource.</param>
/// <param name="Resource">The resource a local provider needs - CPU or the accelerator.</param>
public sealed record PolishSetup(
    IPolishProvider Provider,
    bool UsesLocalRuntime,
    RuntimeResourceKind Resource);

/// <summary>The content-free lines a polish attempt writes, owned by whoever owns the log.</summary>
public interface IPolishAttemptEffects
{
    void RecordPolishStarted(string providerId);

    void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds);
}

/// <summary>
/// One polish attempt: the provider is asked, and a local provider is asked only while it holds its
/// runtime resource, so it never competes with final transcription for the same processor or card.
/// </summary>
/// <remarks>
/// POLISH IS A LIMB. Nothing here can lose the words: a provider that is busy, absent or refused
/// returns the input with a status that says so, and the caller carries on with the deterministic
/// text it already had. The two-second wait for the resource is the same one the shell always used.
/// </remarks>
public sealed class PolishExecutor
{
    private static readonly TimeSpan ResourceWait = TimeSpan.FromSeconds(2);
    private readonly IRuntimeResourceAdmission _admission;
    private readonly IPolishAttemptEffects _effects;

    public PolishExecutor(IRuntimeResourceAdmission admission, IPolishAttemptEffects effects)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(effects);
        _admission = admission;
        _effects = effects;
    }

    /// <summary>Returns null when there is no provider or nothing to polish; otherwise the attempt's result.</summary>
    public async Task<PolishResult?> TryPolishAsync(
        PolishSetup? setup,
        ProcessedText input,
        string? detectedLanguage,
        IReadOnlyList<CustomWordEntry> customWords,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(customWords);
        if (setup is null || string.IsNullOrWhiteSpace(input.Text))
        {
            return null;
        }

        var provider = setup.Provider;
        _effects.RecordPolishStarted(provider.ProviderId);
        var timer = Stopwatch.StartNew();
        PolishResult result;
        var request = new PolishRequest(
            input,
            detectedLanguage,
            PolishVocabulary.Eligible(input.Text, customWords));
        if (!setup.UsesLocalRuntime)
        {
            result = await provider.TryPolishAsync(request, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var acquired = await _admission.AcquireAsync(
                setup.Resource,
                RuntimeWorkloadKind.LocalPolish,
                ResourceWait,
                cancellationToken).ConfigureAwait(false);
            if (!acquired.Succeeded || acquired.Lease is null)
            {
                timer.Stop();
                result = new PolishResult(
                    input,
                    PolishAttemptStatus.Unavailable,
                    acquired.Error ?? new AppError(
                        AppErrorCode.RuntimeResourceBusy,
                        AppErrorStage.RuntimeResource,
                        CanRetry: true),
                    timer.ElapsedMilliseconds);
            }
            else
            {
                await using (acquired.Lease.ConfigureAwait(false))
                {
                    result = await provider.TryPolishAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        timer.Stop();
        _effects.RecordPolishFinished(provider.ProviderId, result, setup.UsesLocalRuntime, timer.ElapsedMilliseconds);
        return result;
    }
}
