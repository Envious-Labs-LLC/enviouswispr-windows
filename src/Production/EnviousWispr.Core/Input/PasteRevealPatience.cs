namespace EnviousWispr.Core.Input;

/// <summary>
/// In-memory per-application clipboard settle estimator ("grow fast, shrink slowly", ported in
/// spirit from macOS <c>PasteRevealPatience</c>). Adapts clipboard settle timing upward
/// immediately when an Electron/Chromium/Office host is slow to reveal pasted text, while
/// requiring warm-up samples and capped 10% decay before shortening wait times. Never persisted.
/// </summary>
public sealed class PasteRevealPatience
{
    public const int CapMilliseconds = 2_500;
    public const int WarmUpSamples = 10;
    public const double Margin = 1.5;
    public const double ShrinkFactor = 0.9;

    public sealed record DispatchToken(string ProcessKey, ulong Sequence);

    private struct AppState(double patienceMs)
    {
        public double PatienceMs = patienceMs;
        public int Admitted;
    }

    private readonly Lock _sync = new();
    private readonly Dictionary<string, AppState> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _newestDispatch = new(StringComparer.Ordinal);
    private ulong _nextSequence;

    public DispatchToken? RecordDispatch(string? processName)
    {
        var key = WindowsPastePolicy.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        key = key.ToLowerInvariant();
        lock (_sync)
        {
            _nextSequence++;
            _newestDispatch[key] = _nextSequence;
            return new DispatchToken(key, _nextSequence);
        }
    }

    public TimeSpan GetSettleDelay(string? processName, TimeSpan floorDelay)
    {
        var floorMs = Math.Max(0, floorDelay.TotalMilliseconds);
        var key = WindowsPastePolicy.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(key))
        {
            return TimeSpan.FromMilliseconds(floorMs);
        }

        key = key.ToLowerInvariant();
        lock (_sync)
        {
            if (!_apps.TryGetValue(key, out var state))
            {
                return TimeSpan.FromMilliseconds(floorMs);
            }

            var effectiveMs = Math.Clamp(state.PatienceMs, floorMs, Math.Max(floorMs, CapMilliseconds));
            return TimeSpan.FromMilliseconds(Math.Round(effectiveMs));
        }
    }

    public bool RecordSighting(DispatchToken? token, int revealMilliseconds, TimeSpan floorDelay)
    {
        if (token is null || revealMilliseconds < 0)
        {
            return false;
        }

        var floorMs = Math.Max(0, floorDelay.TotalMilliseconds);
        var capMs = Math.Max(floorMs, CapMilliseconds);
        var target = Math.Clamp(Margin * revealMilliseconds, floorMs, capMs);

        lock (_sync)
        {
            if (!_newestDispatch.TryGetValue(token.ProcessKey, out var newest) ||
                newest != token.Sequence)
            {
                return false;
            }

            var state = _apps.TryGetValue(token.ProcessKey, out var existing)
                ? existing
                : new AppState(floorMs);

            state.Admitted++;
            if (target > state.PatienceMs)
            {
                state.PatienceMs = target;
            }
            else if (state.Admitted >= WarmUpSamples)
            {
                state.PatienceMs = Math.Max(target, ShrinkFactor * state.PatienceMs);
            }

            _apps[token.ProcessKey] = state;
            return true;
        }
    }
}
