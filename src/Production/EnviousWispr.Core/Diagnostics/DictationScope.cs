namespace EnviousWispr.Core.Diagnostics;

/// <summary>Marks every log line written while one dictation is in flight.</summary>
/// <remarks>
/// THE STAGES WERE ALL MEASURED AND NOTHING JOINED THEM. Capture, transcription, cleanup, polish and
/// delivery each logged an elapsed time, and the only thing making them one dictation was the order
/// they appeared in the file. Ordering is not a join: two dictations that overlap interleave, a
/// dropped line is invisible, and whoever asks "why did THAT one take four seconds" is guessing which
/// lines belong to it. macOS records the breakdown as one thing per dictation.
///
/// AMBIENT RATHER THAN THREADED, AND A REVIEW IS WHY. The first attempt passed the id to nine log
/// writes by hand and added a gate to catch a tenth. A reviewer then enumerated the writes that
/// belong to a dictation and live somewhere else - polish, capture, the streaming commit, auto-stop,
/// the watchdog, recovery, live preview - and the honest count was more than twenty across a dozen
/// helpers. A gate that catches a forgotten argument is worth less than a design where there is no
/// argument to forget.
///
/// SO THE ONLY THING A CALLER DOES IS SAY WHEN A DICTATION STARTS AND ENDS. Everything written inside
/// that scope is joined, including from helpers that have never heard of it, and a helper added next
/// month is joined on arrival.
///
/// IT FLOWS WITH THE WORK, NOT WITH THE THREAD. `AsyncLocal` follows an await onto whichever thread
/// resumes it, which is what a dictation actually does - and a `[ThreadStatic]` would have joined the
/// first half of a dictation and lost the rest at the first await.
///
/// **LIMIT, STATED: work STARTED inside a dictation and outliving it stays joined to it.** A
/// fire-and-forget task spawned mid-dictation inherits the id and keeps it after the dictation ends.
/// That is the right answer for the streaming worker and the recovery write, which genuinely belong
/// to that dictation, and the wrong one for anything long-lived. Nothing long-lived is started from
/// inside the scope today.
/// </remarks>
public static class DictationScope
{
    private static readonly AsyncLocal<Guid?> Ambient = new();

    /// <summary>The dictation the current work belongs to, if any.</summary>
    public static Guid? Current => Ambient.Value;

    /// <summary>Marks everything written until the returned value is disposed.</summary>
    public static IDisposable Begin(Guid dictationId)
    {
        var previous = Ambient.Value;
        Ambient.Value = dictationId;
        return new Restore(previous);
    }

    /// <summary>
    /// Puts back whatever was there before, rather than clearing.
    /// </summary>
    /// <remarks>
    /// RESTORING RATHER THAN CLEARING COSTS NOTHING AND SURVIVES A NESTED SCOPE. Nothing nests one
    /// today; a recovery replay that re-enters the dictation path would, and clearing would then
    /// unjoin the rest of the outer dictation - a defect that only appears on the path nobody runs
    /// often.
    /// </remarks>
    private sealed class Restore(Guid? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = previous;
        }
    }
}

/// <summary>A scope that marks nothing, for a path where there is no dictation yet.</summary>
/// <remarks>
/// A SHARED INSTANCE RATHER THAN A NULLABLE, so a call site reads as one `using` either way. The
/// alternative is a nullable disposable and a null-conditional dispose, which is three places for a
/// path to differ instead of one.
/// </remarks>
public sealed class NoScope : IDisposable
{
    public static NoScope Instance { get; } = new();

    private NoScope()
    {
    }

    public void Dispose()
    {
        // Nothing was set, so nothing is restored.
    }
}
