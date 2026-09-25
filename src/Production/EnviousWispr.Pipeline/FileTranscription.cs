using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

namespace EnviousWispr.Pipeline;

/// <summary>Where a long recording is cut: at the quietest moment near the piece's limit, never mid-word.</summary>
/// <remarks>
/// THE ENGINES TAKE ONE PIECE AT A TIME AND NONE TAKES AN HOUR. Parakeet runs a clip through one encoder pass, so
/// memory grows with its length, and the worker's request has a two-minute deadline. So a file is transcribed in
/// pieces of at most a minute, and each piece ends at the lowest-energy tenth of a second in its last ten seconds -
/// between words, where a cut loses nothing. Ref: #211.
/// </remarks>
public static class AudioPieceCutter
{
    public const int SampleRate = 16_000;
    public static readonly int MaximumPieceSamples = SampleRate * 60;
    public static readonly int SearchSamples = SampleRate * 10;
    private static readonly int FrameSamples = SampleRate / 10;

    /// <summary>How many samples of <paramref name="audio"/> the next piece takes.</summary>
    /// <param name="audio">The audio not yet transcribed, from its start.</param>
    /// <param name="atEnd">True when nothing follows <paramref name="audio"/>: a short remainder is the last piece.</param>
    public static int NextPieceLength(ReadOnlySpan<float> audio, bool atEnd)
    {
        if (audio.Length <= MaximumPieceSamples)
        {
            return atEnd ? audio.Length : 0;
        }

        var searchStart = MaximumPieceSamples - SearchSamples;
        var quietest = MaximumPieceSamples;
        var quietestEnergy = double.MaxValue;
        for (var frameStart = searchStart; frameStart + FrameSamples <= MaximumPieceSamples; frameStart += FrameSamples)
        {
            var energy = 0.0;
            foreach (var sample in audio.Slice(frameStart, FrameSamples))
            {
                energy += sample * (double)sample;
            }

            // The LAST of equally quiet frames, so a silence is cut at its far end and the piece keeps its words whole.
            if (energy <= quietestEnergy)
            {
                quietestEnergy = energy;
                quietest = frameStart + (FrameSamples / 2);
            }
        }

        return quietest;
    }

    /// <summary>A piece with nothing in it: whisper.cpp writes words into silence, so it is not sent at all.</summary>
    public static bool IsSilent(ReadOnlySpan<float> piece)
    {
        var peak = 0f;
        foreach (var sample in piece)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak < 0.005f;
    }
}

public enum FileTranscriptionOutcome
{
    /// <summary>Every piece was transcribed.</summary>
    Completed,

    /// <summary>The file held no speech, or nothing the engine turned into words.</summary>
    Empty,

    /// <summary>The person stopped it; the words so far are kept.</summary>
    Cancelled,

    /// <summary>A piece failed; the words so far are kept.</summary>
    Failed,
}

/// <summary>How far a job has got, for the page's bar.</summary>
public sealed record FileTranscriptionProgress(int PiecesDone, TimeSpan AudioDone, TimeSpan? AudioTotal);

public sealed record FileTranscriptionResult(
    FileTranscriptionOutcome Outcome,
    string Text,
    int Pieces,
    TimeSpan AudioDone,
    AppError? Error = null);

/// <summary>Everything a job touches, handed in so a test drives it without an engine or a file.</summary>
/// <param name="AcquireEngine">
/// The session hold, or null while a dictation holds the session. The job asks again until it gets it: the engine
/// is the dictation's, and a dictation always goes first.
/// </param>
/// <param name="Transcribe">One piece through the final engine.</param>
/// <param name="Finish">The deterministic text pipeline over the joined words, with the person's settings.</param>
public sealed record FileTranscriptionEnvironment(
    Func<IDisposable?> AcquireEngine,
    Func<CapturedAudio, CancellationToken, Task<Transcript>> Transcribe,
    Func<Transcript, CancellationToken, Task<string>> Finish,
    TimeSpan? RetryDelay = null);

/// <summary>Transcribes a decoded file piece by piece, a dictation first whenever one starts. Ref: #211, macOS #2648.</summary>
/// <remarks>
/// THE ENGINE IS HELD FOR ONE PIECE AT A TIME, never for the whole file. A job an hour long that held the session
/// would lock out dictation for its whole run; held per piece, a person can dictate between pieces and the job
/// waits its turn. A FAILURE OR A CANCEL KEEPS THE WORDS SO FAR, the product's rule that a failure returns the last
/// good text rather than nothing.
/// </remarks>
public static class FileTranscriptionJob
{
    public static async Task<FileTranscriptionResult> RunAsync(
        IAudioSampleSource audio,
        FileTranscriptionEnvironment environment,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(environment);
        var sessionId = DictationSessionId.Create();
        // ONE FILE IS ONE SESSION: every line its pieces and its clean-up write is joined to it.
        using var scope = DictationScope.Begin(sessionId.Value);
        var texts = new List<string>();
        Transcript? first = null;
        var pieces = 0;
        var samplesDone = 0L;
        var pending = new List<float>(AudioPieceCutter.MaximumPieceSamples * 2);
        var buffer = new float[AudioPieceCutter.SampleRate];
        var atEnd = false;

        FileTranscriptionResult Ended(FileTranscriptionOutcome outcome, AppError? error = null) =>
            new(outcome, string.Join(' ', texts), pieces, Seconds(samplesDone), error);

        try
        {
            while (true)
            {
                while (!atEnd && pending.Count <= AudioPieceCutter.MaximumPieceSamples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = audio.Read(buffer);
                    if (read == 0)
                    {
                        atEnd = true;
                        break;
                    }

                    pending.AddRange(buffer.AsSpan(0, read));
                }

                var length = AudioPieceCutter.NextPieceLength(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending), atEnd);
                if (length == 0)
                {
                    break;
                }

                var piece = pending.GetRange(0, length).ToArray();
                pending.RemoveRange(0, length);
                pieces++;
                if (!AudioPieceCutter.IsSilent(piece))
                {
                    var transcript = await TranscribeWhenFreeAsync(
                        new CapturedAudio(sessionId, piece, AudioPieceCutter.SampleRate, Channels: 1),
                        environment,
                        cancellationToken).ConfigureAwait(false);
                    first ??= transcript;
                    if (!string.IsNullOrWhiteSpace(transcript.Text))
                    {
                        texts.Add(transcript.Text.Trim());
                    }
                }

                samplesDone += length;
                progress?.Report(new FileTranscriptionProgress(pieces, Seconds(samplesDone), audio.Duration));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Ended(FileTranscriptionOutcome.Cancelled);
        }
        catch (TranscriptionEngineException exception)
        {
            return Ended(FileTranscriptionOutcome.Failed, exception.Error);
        }

        if (texts.Count == 0 || first is null)
        {
            return Ended(FileTranscriptionOutcome.Empty);
        }

        var joined = new Transcript(
            sessionId,
            string.Join(' ', texts),
            first.EngineId,
            DetectedLanguage: first.DetectedLanguage);
        var finished = await environment.Finish(joined, cancellationToken).ConfigureAwait(false);
        return new FileTranscriptionResult(FileTranscriptionOutcome.Completed, finished, pieces, Seconds(samplesDone));
    }

    private static async Task<Transcript> TranscribeWhenFreeAsync(
        CapturedAudio piece,
        FileTranscriptionEnvironment environment,
        CancellationToken cancellationToken)
    {
        using var scope = DictationScope.Begin(piece.SessionId.Value);
        var retry = environment.RetryDelay ?? TimeSpan.FromMilliseconds(500);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hold = environment.AcquireEngine();
            if (hold is not null)
            {
                return await environment.Transcribe(piece, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan Seconds(long samples) => TimeSpan.FromSeconds(samples / (double)AudioPieceCutter.SampleRate);
}
