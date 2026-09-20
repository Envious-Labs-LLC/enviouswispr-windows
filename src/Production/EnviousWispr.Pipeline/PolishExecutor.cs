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
    private readonly Func<IReadOnlyList<CustomWordEntry>> _currentCustomWords;

    /// <param name="admission">Who hands out the runtime resource a local provider needs.</param>
    /// <param name="effects">Who writes the attempt's log lines.</param>
    /// <param name="currentCustomWords">
    /// The person's words AS THEY ARE at the moment the provider is asked - not as they were when the
    /// recording ended. A word taught or removed while transcription was running reaches the very
    /// next polish, which is what the shell always did by reading its settings at the call.
    /// </param>
    public PolishExecutor(
        IRuntimeResourceAdmission admission,
        IPolishAttemptEffects effects,
        Func<IReadOnlyList<CustomWordEntry>> currentCustomWords)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(currentCustomWords);
        _admission = admission;
        _effects = effects;
        _currentCustomWords = currentCustomWords;
    }

    /// <summary>Returns null when there is no provider or nothing to polish; otherwise the attempt's result.</summary>
    public async Task<PolishResult?> TryPolishAsync(
        PolishSetup? setup,
        ProcessedText input,
        string? detectedLanguage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (setup is null || string.IsNullOrWhiteSpace(input.Text))
        {
            return null;
        }

        var provider = setup.Provider;
        _effects.RecordPolishStarted(provider.ProviderId);
        var timer = Stopwatch.StartNew();
        PolishResult result;
        if (!setup.UsesLocalRuntime)
        {
            result = await provider.TryPolishAsync(Request(input, detectedLanguage), cancellationToken)
                .ConfigureAwait(false);
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
                    // BUILT INSIDE THE LEASE, NOT BEFORE THE WAIT. The vocabulary is read at the last
                    // moment, and a refused admission never pays for scanning the dictionary.
                    result = await provider.TryPolishAsync(Request(input, detectedLanguage), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        timer.Stop();
        _effects.RecordPolishFinished(provider.ProviderId, result, setup.UsesLocalRuntime, timer.ElapsedMilliseconds);
        return result;
    }

    private PolishRequest Request(ProcessedText input, string? detectedLanguage) => new(
        input,
        detectedLanguage,
        PolishVocabulary.Eligible(input.Text, _currentCustomWords()));
}
