using System.Diagnostics;

namespace EnviousWispr.Core.Presentation;

/// <summary>Expiry and countdown rail behavior owned by <see cref="OverlayExpiryClock"/>.</summary>
public enum OverlayExpiryMode
{
    None,
    FixedDwell,
    CountdownResetOnHover,
    CountdownAccumulateWithFloor,
}

/// <summary>Snapshot returned when evaluating the single active overlay expiry arm.</summary>
public readonly record struct OverlayExpiryTickResult(
    bool IsActive,
    double RailFraction,
    bool Expired);

/// <summary>
/// Single authoritative dismissal and countdown clock for the overlay window.
/// Only one expiry arm exists at a time; arming or cancelling invalidates all previous arms.
/// </summary>
public sealed class OverlayExpiryClock
{
    private static readonly TimeSpan VisualFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan MinimumTimerInterval = TimeSpan.FromMilliseconds(15);

    private readonly Func<TimeSpan> _monotonicClock;
    private long _currentArmId;
    private OverlayExpiryMode _mode = OverlayExpiryMode.None;
    private TimeSpan _duration;
    private TimeSpan _accumulatedElapsed;
    private TimeSpan? _segmentStartedAt;
    private bool _autoExpire;
    private bool _isHoverPaused;
    private bool _isHoldOpen;

    public OverlayExpiryClock(Func<TimeSpan>? monotonicClock = null)
    {
        _monotonicClock = monotonicClock ?? DefaultClock;
    }

    public long CurrentArmId => _currentArmId;

    public OverlayExpiryMode Mode => _mode;

    public bool IsActive => _mode != OverlayExpiryMode.None;

    public bool IsHoverPaused => _isHoverPaused;

    public bool IsHoldOpen => _isHoldOpen;

    public TimeSpan Duration => _duration;

    public long ArmFixedDwell(TimeSpan duration, bool startPaused = false, bool holdOpen = false)
    {
        _currentArmId++;
        _isHoldOpen = holdOpen;
        _isHoverPaused = startPaused;
        _accumulatedElapsed = TimeSpan.Zero;
        if (duration <= TimeSpan.Zero)
        {
            _mode = OverlayExpiryMode.None;
            _duration = TimeSpan.Zero;
            _segmentStartedAt = null;
            _autoExpire = false;
            return _currentArmId;
        }

        _mode = OverlayExpiryMode.FixedDwell;
        _duration = duration;
        _autoExpire = true;
        _segmentStartedAt = startPaused || holdOpen ? null : _monotonicClock();
        return _currentArmId;
    }

    public long ArmEscapeCountdown(TimeSpan duration, bool startPaused = false, bool holdOpen = false)
    {
        _currentArmId++;
        _mode = OverlayExpiryMode.CountdownResetOnHover;
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(OverlayPillCatalog.EscapeRecoveryDwellSeconds);
        _accumulatedElapsed = TimeSpan.Zero;
        _autoExpire = true;
        _isHoverPaused = startPaused;
        _isHoldOpen = holdOpen;
        _segmentStartedAt = startPaused || holdOpen ? null : _monotonicClock();
        return _currentArmId;
    }

    public long ArmLearnedCountdown(
        TimeSpan duration,
        bool autoExpire = true,
        bool startPaused = false,
        bool holdOpen = false)
    {
        _currentArmId++;
        _mode = OverlayExpiryMode.CountdownAccumulateWithFloor;
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(OverlayPillCatalog.LearnedDwellSeconds);
        _accumulatedElapsed = TimeSpan.Zero;
        _autoExpire = autoExpire;
        _isHoverPaused = startPaused;
        _isHoldOpen = holdOpen;
        _segmentStartedAt = startPaused || holdOpen ? null : _monotonicClock();
        return _currentArmId;
    }

    public long EnterUndoPending(TimeSpan minimumRemaining)
    {
        if (_mode != OverlayExpiryMode.CountdownAccumulateWithFloor)
        {
            return _currentArmId;
        }

        var remaining = _duration - CurrentElapsed();
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var pendingDuration = remaining >= minimumRemaining ? remaining : minimumRemaining;
        _currentArmId++;
        _duration = pendingDuration > TimeSpan.Zero ? pendingDuration : minimumRemaining;
        _accumulatedElapsed = TimeSpan.Zero;
        _autoExpire = false;
        _segmentStartedAt = _isHoverPaused || _isHoldOpen ? null : _monotonicClock();
        return _currentArmId;
    }

    public void PauseForHover()
    {
        _isHoverPaused = true;
        if (_mode == OverlayExpiryMode.None)
        {
            return;
        }

        if (_mode == OverlayExpiryMode.CountdownResetOnHover)
        {
            _accumulatedElapsed = TimeSpan.Zero;
            _segmentStartedAt = null;
            return;
        }

        if (_mode == OverlayExpiryMode.CountdownAccumulateWithFloor)
        {
            _accumulatedElapsed = CurrentElapsed();
            _segmentStartedAt = null;
            return;
        }

        _accumulatedElapsed = TimeSpan.Zero;
        _segmentStartedAt = null;
    }

    public void ResumeFromHover(TimeSpan hoverResumeFloor)
    {
        _isHoverPaused = false;
        if (_mode == OverlayExpiryMode.None || _isHoldOpen)
        {
            return;
        }

        if (_mode == OverlayExpiryMode.CountdownResetOnHover || _mode == OverlayExpiryMode.FixedDwell)
        {
            _accumulatedElapsed = TimeSpan.Zero;
            _segmentStartedAt = _monotonicClock();
            return;
        }

        if (_mode == OverlayExpiryMode.CountdownAccumulateWithFloor)
        {
            var maxElapsed = _duration - hoverResumeFloor;
            if (maxElapsed > TimeSpan.Zero && _accumulatedElapsed > maxElapsed)
            {
                _accumulatedElapsed = maxElapsed;
            }

            _segmentStartedAt = _monotonicClock();
        }
    }

    public void SetHoldOpen(bool holdOpen)
    {
        if (_isHoldOpen == holdOpen)
        {
            return;
        }

        _isHoldOpen = holdOpen;
        if (_mode == OverlayExpiryMode.None)
        {
            return;
        }

        if (holdOpen)
        {
            _accumulatedElapsed = _mode == OverlayExpiryMode.CountdownAccumulateWithFloor
                ? CurrentElapsed()
                : TimeSpan.Zero;
            _segmentStartedAt = null;
            return;
        }

        if (!_isHoverPaused)
        {
            _segmentStartedAt = _monotonicClock();
        }
    }

    public void Cancel()
    {
        _currentArmId++;
        _mode = OverlayExpiryMode.None;
        _duration = TimeSpan.Zero;
        _accumulatedElapsed = TimeSpan.Zero;
        _segmentStartedAt = null;
        _autoExpire = false;
        _isHoverPaused = false;
        _isHoldOpen = false;
    }

    public OverlayExpiryTickResult Evaluate(long expectedArmId)
    {
        if (expectedArmId != _currentArmId || _mode == OverlayExpiryMode.None)
        {
            return new OverlayExpiryTickResult(IsActive: false, RailFraction: 0d, Expired: false);
        }

        var elapsed = CurrentElapsed();
        var totalMs = _duration.TotalMilliseconds;
        var elapsedMs = elapsed.TotalMilliseconds;

        var fraction = _mode switch
        {
            OverlayExpiryMode.CountdownResetOnHover =>
                _isHoverPaused || _isHoldOpen
                    ? 1.0d
                    : (totalMs > 0 ? Math.Clamp(1.0d - elapsedMs / totalMs, 0d, 1d) : 0d),
            OverlayExpiryMode.CountdownAccumulateWithFloor =>
                totalMs > 0 ? Math.Clamp(elapsedMs / totalMs, 0d, 1d) : 1d,
            _ => 0d,
        };

        var expired = _autoExpire && !_isHoverPaused && !_isHoldOpen && elapsed >= _duration;
        if (expired)
        {
            _currentArmId++;
            _mode = OverlayExpiryMode.None;
            _segmentStartedAt = null;
            _autoExpire = false;
        }

        return new OverlayExpiryTickResult(
            IsActive: !expired,
            RailFraction: fraction,
            Expired: expired);
    }

    public TimeSpan? RecommendedNextInterval(bool animationsEnabled)
    {
        if (_mode == OverlayExpiryMode.None || _isHoverPaused || _isHoldOpen)
        {
            return null;
        }

        if (_mode is OverlayExpiryMode.CountdownResetOnHover or OverlayExpiryMode.CountdownAccumulateWithFloor
            && animationsEnabled)
        {
            return VisualFrameInterval;
        }

        if (!_autoExpire)
        {
            return null;
        }

        var remaining = _duration - CurrentElapsed();
        if (remaining < MinimumTimerInterval)
        {
            return MinimumTimerInterval;
        }

        return remaining;
    }

    private TimeSpan CurrentElapsed()
    {
        if (_segmentStartedAt is not { } startedAt)
        {
            return _accumulatedElapsed;
        }

        var delta = _monotonicClock() - startedAt;
        return delta > TimeSpan.Zero ? _accumulatedElapsed + delta : _accumulatedElapsed;
    }

    private static TimeSpan DefaultClock() => Stopwatch.GetElapsedTime(0);
}
