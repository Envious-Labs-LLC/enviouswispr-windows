using System.Diagnostics;

namespace EnviousWispr.Core.Input;

/// <summary>
/// Single cumulative wall-clock budget shared across every UI Automation probe while preparing a
/// target window for delivery, preventing stacked per-call timeouts from stalling paste readiness.
/// </summary>
public sealed class UiaPreparationBudget
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Func<TimeSpan> _readElapsed;
    private bool _exhausted;

    public UiaPreparationBudget(TimeSpan totalBudget, Func<TimeSpan>? readElapsed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalBudget, TimeSpan.Zero);
        TotalBudget = totalBudget;
        if (readElapsed is null)
        {
            var stopwatch = Stopwatch.StartNew();
            _readElapsed = () => stopwatch.Elapsed;
        }
        else
        {
            _readElapsed = readElapsed;
        }
    }

    public TimeSpan TotalBudget { get; }

    public TimeSpan Elapsed
    {
        get
        {
            var elapsed = _readElapsed();
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            if (_exhausted)
            {
                return TimeSpan.Zero;
            }

            var remaining = TotalBudget - Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool IsExhausted
    {
        get
        {
            if (!_exhausted && Elapsed >= TotalBudget)
            {
                _exhausted = true;
            }

            return _exhausted;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> only while cumulative preparation time remains below
    /// <see cref="TotalBudget"/>; latches exhausted once elapsed time reaches the budget.
    /// </summary>
    public bool TryAdmit()
    {
        if (IsExhausted)
        {
            return false;
        }

        return true;
    }
}
