using EnviousWispr.Core.Diagnostics;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// A debug tool that fills someone's disk is a worse bug than the one it was helping to find.
/// </summary>
public sealed class AudioArchiveRetentionTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static (string, DateTimeOffset)[] Recordings(int count) =>
        Enumerable.Range(0, count)
            .Select(i => ($"clip-{i:D3}.wav", Base + TimeSpan.FromMinutes(i)))
            .ToArray();

    [Fact]
    public void AnArchiveInsideItsBoundDeletesNothing()
    {
        Assert.Empty(AudioArchiveRetention.ToDelete(Recordings(AudioArchiveRetention.MaximumRecordings)));
    }

    /// <summary>
    /// The control for the test above. An archive over the bound must delete, or a policy that
    /// never deleted would pass every case here and the archive would grow without limit.
    /// </summary>
    [Fact]
    public void AnArchiveOverItsBoundDeletesTheExcess()
    {
        var deleted = AudioArchiveRetention.ToDelete(Recordings(AudioArchiveRetention.MaximumRecordings + 5));

        Assert.Equal(5, deleted.Count);
    }

    /// <summary>
    /// Oldest first. Deleting the newest would throw away the recording someone is most likely to
    /// be trying to reproduce - the one they just made.
    /// </summary>
    [Fact]
    public void TheOldestRecordingsGoFirst()
    {
        var deleted = AudioArchiveRetention.ToDelete(Recordings(AudioArchiveRetention.MaximumRecordings + 3));

        Assert.Equal(["clip-000.wav", "clip-001.wav", "clip-002.wav"], deleted.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Input order must not change the answer. The caller lists a directory, and a directory
    /// listing's order is the filesystem's business rather than a promise.
    /// </summary>
    [Fact]
    public void TheInputOrderDoesNotChangeWhatIsDeleted()
    {
        var recordings = Recordings(AudioArchiveRetention.MaximumRecordings + 4);

        var forwards = AudioArchiveRetention.ToDelete(recordings).Order(StringComparer.Ordinal);
        var backwards = AudioArchiveRetention.ToDelete(recordings.Reverse().ToArray()).Order(StringComparer.Ordinal);

        Assert.Equal(forwards, backwards);
    }

    /// <summary>
    /// Two recordings can share a write time on a machine with a coarse clock. An unstable order
    /// would make the same archive delete different files on different runs, which is the shape of
    /// bug nobody reproduces.
    /// </summary>
    [Fact]
    public void RecordingsSharingATimeAreStillOrderedTheSameWayEveryTime()
    {
        var tied = Enumerable.Range(0, AudioArchiveRetention.MaximumRecordings + 6)
            .Select(i => ($"clip-{i:D3}.wav", Base))
            .ToArray();

        var first = AudioArchiveRetention.ToDelete(tied);
        var second = AudioArchiveRetention.ToDelete(tied.Reverse().ToArray());

        Assert.Equal(first, second);
        Assert.Equal(6, first.Count);
    }

    [Fact]
    public void AnEmptyArchiveDeletesNothingRatherThanCrashing()
    {
        Assert.Empty(AudioArchiveRetention.ToDelete([]));
    }

    /// <summary>
    /// The policy returns what to DELETE rather than what to keep, and this is why. A caller handed
    /// the keepers has to work out the complement, and the obvious way - delete everything not in
    /// the list - destroys anything that appeared between the two operations.
    /// </summary>
    [Fact]
    public void WhatIsDeletedAndWhatSurvivesTogetherAccountForEveryFile()
    {
        var recordings = Recordings(AudioArchiveRetention.MaximumRecordings + 7);

        var deleted = AudioArchiveRetention.ToDelete(recordings).ToHashSet(StringComparer.Ordinal);
        var survivors = recordings.Where(file => !deleted.Contains(file.Item1)).ToArray();

        Assert.Equal(AudioArchiveRetention.MaximumRecordings, survivors.Length);
        Assert.Equal(recordings.Length, survivors.Length + deleted.Count);
    }
}
