using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

public sealed class StreamingShortTailTests
{
    [Fact]
    public async Task AShortInputRefusalUsesExactlyOneWholeRecordingResultAndItsMetadata()
    {
        var world = await World.ReadyAsync();
        var expected = new Transcript(world.Whole.SessionId, "whole result", "result engine", [],
            UsedFallback: true, DetectedLanguage: "de", RecognitionLanguage: "auto");
        world.Engine.Responses.Enqueue((_, _) => throw Short());
        world.Engine.Responses.Enqueue((audio, token) =>
        {
            Assert.Same(world.Whole, audio);
            Assert.Equal(CancellationToken.None, token);
            return expected;
        });

        var result = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, world.Whole, CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal([world.PrefixLength, 160, world.Whole.Samples.Length],
            world.Engine.Requests.Select(audio => audio.Samples.Length));
        Assert.True(world.Engine.Requests[1].Samples.Span.SequenceEqual(world.Whole.Samples.Span[^160..]));
        Assert.Contains(world.Log.Entries, entry => entry.Event == AppEventCode.StreamingAbandoned &&
            entry.ErrorCode == AppErrorCode.TranscriptionInputTooShort);
        Assert.DoesNotContain(world.Log.Entries, entry => entry.Event == AppEventCode.StreamingHeadStartUsed);
    }

    [Theory]
    [InlineData(AppErrorCode.TranscriptionFailed, AppErrorStage.FinalAsr)]
    [InlineData(AppErrorCode.RuntimeResourceBusy, AppErrorStage.FinalAsr)]
    [InlineData(AppErrorCode.RuntimeProviderUnavailable, AppErrorStage.FinalAsr)]
    [InlineData(AppErrorCode.TranscriptionInputTooShort, AppErrorStage.RuntimeWorker)]
    public async Task OtherFailuresNeverTriggerWholeRecovery(AppErrorCode code, AppErrorStage stage)
    {
        var world = await World.ReadyAsync();
        var expected = new TranscriptionEngineException(new AppError(code, stage, CanRetry: true));
        world.Engine.Responses.Enqueue((_, _) => throw expected);

        var actual = await Assert.ThrowsAsync<TranscriptionEngineException>(() =>
            world.Controller.TranscribeUsingAnyHeadStartAsync(world.Engine, world.Whole, CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(2, world.Engine.Requests.Count);
    }

    [Fact]
    public async Task CancellationAfterTheRefusalPreventsRecovery()
    {
        var world = await World.ReadyAsync();
        using var cancellation = new CancellationTokenSource();
        world.Engine.Responses.Enqueue((_, _) =>
        {
            cancellation.Cancel();
            throw Short();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            world.Controller.TranscribeUsingAnyHeadStartAsync(world.Engine, world.Whole, cancellation.Token));

        Assert.Equal(2, world.Engine.Requests.Count);
    }

    [Fact]
    public async Task AnOwnedLoopPreventsRecoveryEvenWhenNoNativeRequestIsCurrentlyActive()
    {
        var world = await World.ReadyAsync(stop: false);
        try
        {
            var actual = await Assert.ThrowsAsync<TranscriptionEngineException>(() =>
                world.Controller.TranscribeUsingAnyHeadStartAsync(world.Engine, world.Whole, CancellationToken.None));
            Assert.Equal(AppErrorCode.RuntimeResourceBusy, actual.Error.Code);
            Assert.Equal(AppErrorStage.RuntimeWorker, actual.Error.Stage);
            Assert.True(actual.Error.CanRetry);
            Assert.True(world.Controller.IsRunning);
            Assert.Equal(world.PrefixLength, Assert.Single(world.Engine.Requests).Samples.Length);
            Assert.DoesNotContain(world.Log.Entries, entry => entry.Event is
                AppEventCode.StreamingAbandoned or AppEventCode.StreamingHeadStartUsed);
        }
        finally
        {
            await world.Controller.StopAsync();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptyWholeRecoveryNeverRestoresTheOldHead(string text)
    {
        var world = await World.ReadyAsync();
        var expected = new Transcript(world.Whole.SessionId, text, "whole");
        world.Engine.Responses.Enqueue((_, _) => throw Short());
        world.Engine.Responses.Enqueue((_, _) => expected);

        var result = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, world.Whole, CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal(text, result.Text);
        Assert.Equal(3, world.Engine.Requests.Count);
    }

    [Fact]
    public async Task FailedWholeRecoveryDoesNotRecurse()
    {
        var world = await World.ReadyAsync();
        var expected = Short();
        world.Engine.Responses.Enqueue((_, _) => throw Short());
        world.Engine.Responses.Enqueue((_, _) => throw expected);

        var actual = await Assert.ThrowsAsync<TranscriptionEngineException>(() =>
            world.Controller.TranscribeUsingAnyHeadStartAsync(world.Engine, world.Whole, CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(3, world.Engine.Requests.Count);
    }

    [Fact]
    public async Task CancellationDuringWholeRecoveryPropagatesWithoutAnotherRequest()
    {
        var world = await World.ReadyAsync();
        using var cancellation = new CancellationTokenSource();
        world.Engine.Responses.Enqueue((_, _) => throw Short());
        world.Engine.Responses.Enqueue((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            world.Controller.TranscribeUsingAnyHeadStartAsync(world.Engine, world.Whole, cancellation.Token));

        Assert.Equal(3, world.Engine.Requests.Count);
    }

    [Fact]
    public async Task ASuccessfulEmptyTailKeepsTheExistingJoinBehavior()
    {
        var world = await World.ReadyAsync();
        world.Engine.Responses.Enqueue((audio, _) => new Transcript(audio.SessionId, "", "tail"));

        var result = await world.Controller.TranscribeUsingAnyHeadStartAsync(
            world.Engine, world.Whole, CancellationToken.None);

        Assert.Equal("committed head", result.Text);
        Assert.Equal(2, world.Engine.Requests.Count);
    }

    [Fact]
    public async Task AnAlreadyWholeRequestIsNeverRetried()
    {
        var world = World.Create();
        var expected = Short();
        world.Engine.Responses.Enqueue((_, _) => throw expected);

        var actual = await Assert.ThrowsAsync<TranscriptionEngineException>(() =>
            world.Controller.TranscribeUsingAnyHeadStartAsync(world.Engine, world.Whole, CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Single(world.Engine.Requests);
    }

    private static TranscriptionEngineException Short() => new(new AppError(
        AppErrorCode.TranscriptionInputTooShort, AppErrorStage.FinalAsr, CanRetry: false));

    private sealed class World
    {
        public required StreamingTranscriptionController Controller { get; init; }
        public required Engine Engine { get; init; }
        public required CapturedAudio Whole { get; init; }
        public required Log Log { get; init; }
        public required Deterministic.ManualClock Clock { get; init; }
        public required int PrefixLength { get; init; }

        public static World Create()
        {
            var session = DictationSessionId.Create();
            var prefix = new float[70_400];
            Array.Fill(prefix, 0.2f, 3_200, 48_000);
            var whole = new float[prefix.Length + 160];
            prefix.CopyTo(whole, 0);
            var engine = new Engine();
            var log = new Log();
            var clock = new Deterministic.ManualClock();
            return new World
            {
                Engine = engine, Log = log, Clock = clock, PrefixLength = prefix.Length,
                Whole = new CapturedAudio(session, whole, 16_000, 1),
                Controller = new StreamingTranscriptionController(
                    new Effects(engine, new Snapshot(session, prefix)), log, clock),
            };
        }

        public static async Task<World> ReadyAsync(bool stop = true)
        {
            var world = Create();
            world.Engine.Responses.Enqueue((audio, _) => new Transcript(audio.SessionId, "committed head", "head"));
            world.Controller.Start(world.Whole.SessionId);
            world.Clock.Advance(TimeSpan.FromMilliseconds(500));
            await world.Clock.WhenRegistered(2).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(world.PrefixLength, Assert.Single(world.Engine.Requests).Samples.Length);
            Assert.Contains(world.Log.Entries, entry => entry.Event == AppEventCode.StreamingSegmentCommitted);
            if (stop) await world.Controller.StopAsync();
            return world;
        }
    }

    private sealed class Engine : ITranscriptionEngine
    {
        public string EngineId => "synthetic";
        public Queue<Func<CapturedAudio, CancellationToken, Transcript>> Responses { get; } = new();
        public List<CapturedAudio> Requests { get; } = [];

        public async Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(audio);
            cancellationToken.ThrowIfCancellationRequested();
            return Responses.Dequeue()(audio, cancellationToken);
        }
    }

    private sealed class Snapshot(DictationSessionId session, float[] samples) : IAudioSnapshotSource
    {
        public AudioSnapshot? GetSnapshot(TimeSpan maximumDuration) => new(session, samples, 16_000, 1);
    }

    private sealed class Effects(ITranscriptionEngine engine, IAudioSnapshotSource audio) : IStreamingTranscriptionEffects
    {
        public bool LivePreviewEnabled => false;
        public ITranscriptionEngine? Engine => engine;
        public IAudioSnapshotSource? Audio => audio;
    }

    private sealed class Log : IAppLogger
    {
        public List<AppLogEntry> Entries { get; } = [];
        public void Write(AppLogEntry entry) => Entries.Add(entry);
    }
}
