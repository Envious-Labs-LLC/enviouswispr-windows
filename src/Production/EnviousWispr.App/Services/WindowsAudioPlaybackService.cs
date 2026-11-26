using EnviousWispr.Presentation;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace EnviousWispr.App.Services;

/// <summary>
/// Single-active local WAV player for History dictation cards, backed by <see cref="MediaPlayer"/>
/// over an in-memory copy of the WAV bytes so files on disk are never locked during deletion.
/// </summary>
internal sealed class WindowsAudioPlaybackService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _positionTimer;
    private readonly SingleActivePlaybackTracker _tracker = new();
    private MediaPlayer? _player;
    private MediaSource? _currentSource;
    private InMemoryRandomAccessStream? _currentStream;
    private long _loadGeneration;
    private bool _disposed;

    public WindowsAudioPlaybackService(DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _dispatcherQueue = dispatcherQueue;
        _positionTimer = _dispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _positionTimer.Tick += OnPositionTimerTick;
    }

    public event Action<HistoryPlaybackSnapshot>? StateChanged;

    public event Action<Guid, string>? PlaybackFailed;

    public HistoryPlaybackSnapshot CurrentState => _tracker.Snapshot;

    public void TogglePlayPause(Guid entryId, string wavFilePath, long expectedDurationMs)
    {
        if (_disposed || entryId == Guid.Empty || string.IsNullOrWhiteSpace(wavFilePath))
        {
            return;
        }

        var outcome = _tracker.Toggle(entryId, expectedDurationMs);
        switch (outcome)
        {
            case HistoryPlaybackToggleOutcome.Pause:
                _player?.Pause();
                _positionTimer.Stop();
                PublishState();
                break;

            case HistoryPlaybackToggleOutcome.Resume:
                if (_tracker.LoadingEntryId == entryId)
                {
                    // Load is already in flight; CompleteLoad will resume at latest requested position.
                    PublishState();
                    return;
                }

                if (_player is not null && _currentSource is not null)
                {
                    if (_tracker.PositionMs == 0)
                    {
                        _player.PlaybackSession.Position = TimeSpan.Zero;
                    }

                    _player.Play();
                    _positionTimer.Start();
                    PublishState();
                    return;
                }

                _ = LoadAndStartAsync(entryId, wavFilePath, expectedDurationMs);
                break;

            case HistoryPlaybackToggleOutcome.StartNew:
                StopMediaCore();
                PublishState();
                _ = LoadAndStartAsync(entryId, wavFilePath, expectedDurationMs);
                break;
        }
    }

    public void SeekFraction(Guid entryId, string wavFilePath, double fraction, long expectedDurationMs)
    {
        if (_disposed || entryId == Guid.Empty || string.IsNullOrWhiteSpace(wavFilePath))
        {
            return;
        }

        var hasLoadedSource = _player is not null && _currentSource is not null && _tracker.LoadingEntryId != entryId;
        var plan = _tracker.Seek(entryId, fraction, expectedDurationMs, hasLoadedSource);
        if (plan.StartNewLoad)
        {
            StopMediaCore();
            PublishState();
            _ = LoadAndStartAsync(entryId, wavFilePath, expectedDurationMs);
            return;
        }

        if (_tracker.LoadingEntryId == entryId)
        {
            // Preserve IsPlaying and latest PendingSeekMs without toggling or restarting async load.
            PublishState();
            return;
        }

        if (_player is not null && _currentSource is not null)
        {
            _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(plan.TargetPositionMs);
        }

        PublishState();
    }

    public void Stop(Guid? onlyEntryId = null)
    {
        if (_disposed)
        {
            return;
        }

        if (onlyEntryId is { } target && _tracker.ActiveEntryId != target)
        {
            return;
        }

        Interlocked.Increment(ref _loadGeneration);
        _tracker.Stop(onlyEntryId);
        _positionTimer.Stop();
        StopMediaCore();
        PublishState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _loadGeneration);
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        StopMediaCore();
        if (_player is not null)
        {
            _player.MediaEnded -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
            _player.Dispose();
            _player = null;
        }

        _tracker.Stop();
    }

    private async Task LoadAndStartAsync(
        Guid entryId,
        string wavFilePath,
        long expectedDurationMs)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => File.ReadAllBytes(wavFilePath)).ConfigureAwait(true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_disposed && generation == Volatile.Read(ref _loadGeneration))
            {
                HandleFailure(entryId, "This local recording could not be opened.");
            }

            return;
        }

        if (_disposed || generation != Volatile.Read(ref _loadGeneration) || _tracker.ActiveEntryId != entryId)
        {
            return;
        }

        InMemoryRandomAccessStream? stream = null;
        MediaSource? source = null;
        try
        {
            stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask().ConfigureAwait(true);
                writer.DetachStream();
            }

            if (_disposed || generation != Volatile.Read(ref _loadGeneration) || _tracker.ActiveEntryId != entryId)
            {
                stream.Dispose();
                return;
            }

            stream.Seek(0);
            source = MediaSource.CreateFromStream(stream, "audio/wav");
            var player = EnsurePlayer();
            ReplaceMediaResources(source, stream);
            stream = null;
            source = null;

            player.Source = _currentSource;
            var naturalMs = (long)player.PlaybackSession.NaturalDuration.TotalMilliseconds;
            var durationMs = naturalMs > 0 ? naturalMs : expectedDurationMs;
            var startPositionMs = _tracker.CompleteLoad(entryId, durationMs);
            if (startPositionMs > 0)
            {
                player.PlaybackSession.Position = TimeSpan.FromMilliseconds(startPositionMs);
            }

            if (_tracker.IsPlaying)
            {
                player.Play();
                _positionTimer.Start();
            }
            else
            {
                player.Pause();
                _positionTimer.Stop();
            }

            PublishState();
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or StackOverflowException))
        {
            source?.Dispose();
            stream?.Dispose();
            if (!_disposed && generation == Volatile.Read(ref _loadGeneration))
            {
                HandleFailure(entryId, "Audio playback could not start on this device.");
            }
        }
    }

    private MediaPlayer EnsurePlayer()
    {
        if (_player is not null)
        {
            return _player;
        }

        var player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = false,
        };
        player.MediaEnded += OnMediaEnded;
        player.MediaFailed += OnMediaFailed;
        _player = player;
        return player;
    }

    private void ReplaceMediaResources(MediaSource nextSource, InMemoryRandomAccessStream nextStream)
    {
        if (_player is not null)
        {
            _player.Pause();
            _player.Source = null;
        }

        _currentSource?.Dispose();
        _currentStream?.Dispose();
        _currentSource = nextSource;
        _currentStream = nextStream;
    }

    private void StopMediaCore()
    {
        if (_player is not null)
        {
            _player.Pause();
            _player.Source = null;
        }

        _currentSource?.Dispose();
        _currentSource = null;
        _currentStream?.Dispose();
        _currentStream = null;
    }

    private void OnPositionTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _player is null || _tracker.ActiveEntryId is not { } activeId)
        {
            _positionTimer.Stop();
            return;
        }

        var session = _player.PlaybackSession;
        var posMs = (long)Math.Max(0, session.Position.TotalMilliseconds);
        var naturalMs = (long)Math.Max(0, session.NaturalDuration.TotalMilliseconds);
        _tracker.UpdateProgress(activeId, posMs, naturalMs);
        PublishState();
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || _tracker.ActiveEntryId is not { } activeId)
            {
                return;
            }

            _positionTimer.Stop();
            _tracker.MarkCompleted(activeId);
            if (_player is not null)
            {
                _player.Pause();
                _player.PlaybackSession.Position = TimeSpan.Zero;
            }

            PublishState();
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || _tracker.ActiveEntryId is not { } activeId)
            {
                return;
            }

            HandleFailure(activeId, "This local recording could not be played.");
        });
    }

    private void HandleFailure(Guid entryId, string message)
    {
        _positionTimer.Stop();
        _tracker.Stop(entryId);
        StopMediaCore();
        PublishState();
        PlaybackFailed?.Invoke(entryId, message);
    }

    private void PublishState() => StateChanged?.Invoke(_tracker.Snapshot);
}
