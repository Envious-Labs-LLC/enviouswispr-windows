using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Pipeline;

namespace EnviousWispr.Architecture.Tests;

/// <summary>Transcribe a File: where a long file is cut, and how a job shares the engine with dictation. Ref: #211.</summary>
public sealed class FileTranscriptionTests
{
    private const int Rate = AudioPieceCutter.SampleRate;

    [Fact]
    public void AShortFileIsOnePiece()
    {
        var audio = Tone(seconds: 20);

        Assert.Equal(audio.Length, AudioPieceCutter.NextPieceLength(audio, atEnd: true));
        Assert.Equal(0, AudioPieceCutter.NextPieceLength(audio, atEnd: false));
    }

    /// <summary>A pause inside the last ten seconds of the minute is where the cut lands, not at the minute.</summary>
    [Fact]
    public void ALongFileIsCutInsideThePauseNearTheMinute()
    {
        // Speech to 55.0 s, half a second of silence, speech again.
        var audio = Concat(Tone(55.0), Silence(0.5), Tone(20));

        var cut = AudioPieceCutter.NextPieceLength(audio, atEnd: true);

        Assert.InRange(cut, (int)(55.0 * Rate), (int)(55.5 * Rate));
    }

    /// <summary>With no pause at all, the piece is still bounded: one minute, no more.</summary>
    [Fact]
    public void AFileWithNoPauseIsStillCutByTheMinute()
    {
        var audio = Tone(90);

        var cut = AudioPieceCutter.NextPieceLength(audio, atEnd: true);

        Assert.InRange(cut, (int)(50 * Rate), AudioPieceCutter.MaximumPieceSamples);
    }

    [Fact]
    public void SilenceIsNeverSentToTheEngine()
    {
        Assert.True(AudioPieceCutter.IsSilent(Silence(5)));
        Assert.False(AudioPieceCutter.IsSilent(Tone(1)));
    }

    [Fact]
    public async Task AFileIsTranscribedPieceByPieceAndJoinedThroughThePipeline()
    {
        var engine = new FakeEngine();
        var audio = new ArraySource(Concat(Tone(55), Silence(0.5), Tone(40)));
        var seen = new List<FileTranscriptionProgress>();

        var result = await FileTranscriptionJob.RunAsync(
            audio,
            new FileTranscriptionEnvironment(() => new Hold(), engine.TranscribeAsync, (transcript, _) => Task.FromResult(transcript.Text.ToUpperInvariant())),
            new SynchronousProgress(seen.Add),
            CancellationToken.None);

        Assert.Equal(FileTranscriptionOutcome.Completed, result.Outcome);
        Assert.Equal(2, engine.Pieces.Count);
        Assert.Equal("PIECE 1 PIECE 2", result.Text);
        Assert.Equal(2, seen.Count);
        Assert.Equal(TimeSpan.FromSeconds(95.5), result.AudioDone);
    }

    /// <summary>A dictation holds the session: the job waits its turn rather than taking the engine from it.</summary>
    [Fact]
    public async Task APieceWaitsWhileADictationHoldsTheEngine()
    {
        var engine = new FakeEngine();
        var refusals = 3;

        var result = await FileTranscriptionJob.RunAsync(
            new ArraySource(Tone(5)),
            new FileTranscriptionEnvironment(
                () => refusals-- > 0 ? null : new Hold(),
                engine.TranscribeAsync,
                (transcript, _) => Task.FromResult(transcript.Text),
                RetryDelay: TimeSpan.FromMilliseconds(1)),
            progress: null,
            CancellationToken.None);

        Assert.Equal(FileTranscriptionOutcome.Completed, result.Outcome);
        Assert.Equal(-1, refusals);
        Assert.Single(engine.Pieces);
    }

    /// <summary>The engine is held only while a piece is transcribed.</summary>
    [Fact]
    public async Task TheHoldIsGivenBackAfterEveryPiece()
    {
        var engine = new FakeEngine();
        var holds = new List<Hold>();

        await FileTranscriptionJob.RunAsync(
            new ArraySource(Concat(Tone(55), Silence(0.5), Tone(40))),
            new FileTranscriptionEnvironment(
                () =>
                {
                    Assert.All(holds, hold => Assert.True(hold.Released));
                    var hold = new Hold();
                    holds.Add(hold);
                    return hold;
                },
                engine.TranscribeAsync,
                (transcript, _) => Task.FromResult(transcript.Text)),
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, holds.Count);
        Assert.All(holds, hold => Assert.True(hold.Released));
    }

    [Fact]
    public async Task AFailedPieceKeepsTheWordsSoFar()
    {
        var engine = new FakeEngine { FailOnPiece = 2 };

        var result = await FileTranscriptionJob.RunAsync(
            new ArraySource(Concat(Tone(55), Silence(0.5), Tone(40))),
            new FileTranscriptionEnvironment(() => new Hold(), engine.TranscribeAsync, (transcript, _) => Task.FromResult(transcript.Text)),
            progress: null,
            CancellationToken.None);

        Assert.Equal(FileTranscriptionOutcome.Failed, result.Outcome);
        Assert.Equal("piece 1", result.Text);
        Assert.Equal(AppErrorCode.TranscriptionFailed, result.Error?.Code);
    }

    [Fact]
    public async Task ACancelKeepsTheWordsSoFar()
    {
        using var cancel = new CancellationTokenSource();
        var engine = new FakeEngine { AfterPiece = count => { if (count == 1) { cancel.Cancel(); } } };

        var result = await FileTranscriptionJob.RunAsync(
            new ArraySource(Concat(Tone(55), Silence(0.5), Tone(40))),
            new FileTranscriptionEnvironment(() => new Hold(), engine.TranscribeAsync, (transcript, _) => Task.FromResult(transcript.Text)),
            progress: null,
            cancel.Token);

        Assert.Equal(FileTranscriptionOutcome.Cancelled, result.Outcome);
        Assert.Equal("piece 1", result.Text);
    }

    [Fact]
    public async Task ASilentFileIsEmptyAndNeverReachesTheEngine()
    {
        var engine = new FakeEngine();

        var result = await FileTranscriptionJob.RunAsync(
            new ArraySource(Silence(30)),
            new FileTranscriptionEnvironment(() => new Hold(), engine.TranscribeAsync, (transcript, _) => Task.FromResult(transcript.Text)),
            progress: null,
            CancellationToken.None);

        Assert.Equal(FileTranscriptionOutcome.Empty, result.Outcome);
        Assert.Empty(engine.Pieces);
    }

    private static float[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(seconds * Rate)).Select(i => 0.3f * MathF.Sin(i * 0.05f)).ToArray();

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static float[] Concat(params float[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class ArraySource(float[] samples) : IAudioSampleSource
    {
        private int _position;

        public TimeSpan? Duration { get; } = TimeSpan.FromSeconds(samples.Length / (double)Rate);

        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private sealed class FakeEngine
    {
        public List<int> Pieces { get; } = [];

        public int FailOnPiece { get; init; }

        public Action<int>? AfterPiece { get; init; }

        public Task<Transcript> TranscribeAsync(CapturedAudio audio, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Pieces.Add(audio.Samples.Length);
            if (Pieces.Count == FailOnPiece)
            {
                throw new TranscriptionEngineException(new AppError(AppErrorCode.TranscriptionFailed, AppErrorStage.FinalAsr, CanRetry: true));
            }

            AfterPiece?.Invoke(Pieces.Count);
            return Task.FromResult(new Transcript(audio.SessionId, $"piece {Pieces.Count}", "engine"));
        }
    }

    private sealed class Hold : IDisposable
    {
        public bool Released { get; private set; }

        public void Dispose() => Released = true;
    }

    private sealed class SynchronousProgress(Action<FileTranscriptionProgress> report) : IProgress<FileTranscriptionProgress>
    {
        public void Report(FileTranscriptionProgress value) => report(value);
    }
}

/// <summary>The decoder, against a real file Windows' own decoders read.</summary>
public sealed class AudioFileSourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"EnviousWisprAudioFile-{Guid.NewGuid():N}");

    /// <summary>A stereo 44.1 kHz WAV comes out 16 kHz mono, the length it was, the tone still there.</summary>
    [Fact]
    public void AStereoFileAtAnotherRateComesOutSixteenKilohertzMono()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "tone.wav");
        const int sourceRate = 44_100;
        using (var writer = new NAudio.Wave.WaveFileWriter(path, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 2)))
        {
            for (var i = 0; i < sourceRate * 3; i++)
            {
                var sample = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / sourceRate);
                writer.WriteSample(sample);
                writer.WriteSample(sample);
            }
        }

        using var source = EnviousWispr.Audio.AudioFileSource.TryOpen(path, out var failure);

        Assert.Equal(EnviousWispr.Audio.AudioFileOpenFailure.None, failure);
        Assert.NotNull(source);
        var all = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = source!.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        Assert.InRange(all.Count, (int)(2.95 * 16_000), (int)(3.05 * 16_000));
        Assert.InRange(all.Max(), 0.4f, 0.6f);
    }

    [Fact]
    public void AFileThatIsNotAudioIsRefusedByName()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "notes.wav");
        File.WriteAllText(path, "this is text, not audio");

        using var source = EnviousWispr.Audio.AudioFileSource.TryOpen(path, out var failure);

        Assert.Null(source);
        Assert.Equal(EnviousWispr.Audio.AudioFileOpenFailure.NotAudio, failure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.GetFiles(_directory))
            {
                File.Delete(file);
            }

            Directory.Delete(_directory);
        }
    }
}
