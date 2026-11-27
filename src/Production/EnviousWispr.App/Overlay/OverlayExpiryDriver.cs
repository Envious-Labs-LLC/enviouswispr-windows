using EnviousWispr.Core.Presentation;
using Microsoft.UI.Xaml;

namespace EnviousWispr.App.Overlay;

/// <summary>
/// Wraps <see cref="OverlayExpiryClock"/> with one <see cref="DispatcherTimer"/> that handles
/// standard dwell dismissal, Escape Recovery countdowns, and Learned Correction countdowns.
/// </summary>
internal sealed class OverlayExpiryDriver : IDisposable
{
    private readonly DispatcherTimer _timer = new();
    private readonly OverlayExpiryClock _clock = new();
    private readonly Func<bool> _getAnimationsEnabled;
    private long _armedTimerId;
    private bool _disposed;

    public OverlayExpiryDriver(Func<bool> getAnimationsEnabled)
    {
        ArgumentNullException.ThrowIfNull(getAnimationsEnabled);
        _getAnimationsEnabled = getAnimationsEnabled;
        _timer.Tick += OnTick;
    }

    public OverlayExpiryMode Mode => _clock.Mode;

    public bool IsActive => _clock.IsActive;

    public event Action<OverlayExpiryMode, double>? RailProgressChanged;

    public event Action? Expired;

    public void ArmFixedDwell(TimeSpan duration, bool startPaused = false, bool holdOpen = false)
    {
        _clock.ArmFixedDwell(duration, startPaused, holdOpen);
        Reschedule();
    }

    public void ArmEscapeCountdown(TimeSpan duration, bool startPaused = false, bool holdOpen = false)
    {
        var armId = _clock.ArmEscapeCountdown(duration, startPaused, holdOpen);
        var snapshot = _clock.Evaluate(armId);
        RailProgressChanged?.Invoke(OverlayExpiryMode.CountdownResetOnHover, snapshot.RailFraction);
        Reschedule();
    }

    public void ArmLearnedCountdown(
        TimeSpan duration,
        bool autoExpire = true,
        bool startPaused = false,
        bool holdOpen = false)
    {
        _clock.ArmLearnedCountdown(duration, autoExpire, startPaused, holdOpen);
        RailProgressChanged?.Invoke(OverlayExpiryMode.CountdownAccumulateWithFloor, 0d);
        Reschedule();
    }

    public void EnterUndoPending(TimeSpan minimumRemaining)
    {
        _clock.EnterUndoPending(minimumRemaining);
        Reschedule();
    }

    public void PauseForHover()
    {
        var mode = _clock.Mode;
        _clock.PauseForHover();
        if (mode == OverlayExpiryMode.CountdownResetOnHover)
        {
            RailProgressChanged?.Invoke(mode, 1.0d);
        }

        Reschedule();
    }

    public void ResumeFromHover(TimeSpan hoverResumeFloor)
    {
        _clock.ResumeFromHover(hoverResumeFloor);
        Reschedule();
    }

    public void SetHoldOpen(bool holdOpen)
    {
        _clock.SetHoldOpen(holdOpen);
        Reschedule();
    }

    public void Cancel()
    {
        _clock.Cancel();
        _timer.Stop();
        _armedTimerId = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _clock.Cancel();
    }

    private void Reschedule()
    {
        _timer.Stop();
        if (_disposed)
        {
            return;
        }

        var interval = _clock.RecommendedNextInterval(_getAnimationsEnabled());
        if (interval is not { } next)
        {
            return;
        }

        _armedTimerId = _clock.CurrentArmId;
        _timer.Interval = next;
        _timer.Start();
    }

    private void OnTick(object? sender, object e)
    {
        if (_disposed)
        {
            _timer.Stop();
            return;
        }

        var mode = _clock.Mode;
        var result = _clock.Evaluate(_armedTimerId);
        if (!result.IsActive && !result.Expired)
        {
            _timer.Stop();
            return;
        }

        if (mode is OverlayExpiryMode.CountdownResetOnHover or OverlayExpiryMode.CountdownAccumulateWithFloor)
        {
            RailProgressChanged?.Invoke(mode, result.RailFraction);
        }

        if (result.Expired)
        {
            _timer.Stop();
            Expired?.Invoke();
            return;
        }

        var nextInterval = _clock.RecommendedNextInterval(_getAnimationsEnabled());
        if (nextInterval is not { } next)
        {
            _timer.Stop();
            return;
        }

        _armedTimerId = _clock.CurrentArmId;
        if (_timer.Interval != next)
        {
            _timer.Interval = next;
        }
    }
}
