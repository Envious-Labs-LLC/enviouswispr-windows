using System.Text;

namespace EnviousWispr.Core.Learning;

/// <summary>
/// Pure state machine and anchor/edit-distance locator for tracking a pasted region after delivery.
/// Driven on Windows by <c>WindowsPastedRegionObserver</c> and directly testable without UI Automation.
/// </summary>
public sealed class PastedRegionTracker
{
    public readonly record struct ReadSnapshot(
        bool Readable,
        string? FieldText,
        int? CaretOffset,
        bool SameApplicationFocus,
        bool SameElementFocus,
        bool ElementAlive,
        bool TooLong = false);

    public enum LocateKind
    {
        Located,
        TextboxEmptied,
        RegionRemoved,
        AnchorAmbiguous,
        EditDistanceExceeded,
    }

    public readonly record struct LocateResult(LocateKind Kind, string? Region);

    private readonly PastedRegionTarget _target;
    private readonly int _editDistanceLimit;
    private readonly int _startedAtMs;

    private string _lastRegion;
    private bool _hasUnsettledEdit;
    private int _lastChangeAtMs = -1;
    private int _firstDeferralAtMs = -1;
    private int _focusAwaySinceMs = -1;
    private int _consecutiveFailures;
    private bool _ended;

    public PastedRegionTracker(PastedRegionTarget target, int startedAtMs)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _startedAtMs = startedAtMs;
        _lastRegion = target.EffectiveRenderedText;
        _editDistanceLimit = Math.Max(
            PastedRegionTiming.EditDistanceLimitFloor,
            (int)Math.Floor(target.EffectiveRenderedText.Length * PastedRegionTiming.EditDistanceLimitFraction));
    }

    public bool HasEnded => _ended;

    public static bool TryCaptureTarget(
        string fieldText,
        string pastedText,
        int processId,
        long windowHandle,
        string? processName,
        out PastedRegionTarget? target,
        out PastedRegionEndReason? endReason)
    {
        target = null;
        endReason = null;

        if (string.IsNullOrEmpty(pastedText))
        {
            endReason = PastedRegionEndReason.DictatedTextNotFound;
            return false;
        }

        if (fieldText.Length > PastedRegionTiming.MaxValueUtf16)
        {
            endReason = PastedRegionEndReason.CaptureUnsupported;
            return false;
        }

        // Hosts such as Word and OneNote strip a trailing space or normalize spaces to NBSP;
        // match the exact string first at each site, then fall back to trailing-horizontal-whitespace
        // trimmed and NBSP-folded forms, while scanning every position so an exact hit never hides
        // a duplicate normalized hit.
        var foldedField = FoldSpaces(fieldText);
        var foldedFull = FoldSpaces(pastedText);
        var foldedTrimmed = TrimTrailingHorizontalWhitespace(foldedFull);
        if (string.IsNullOrEmpty(foldedTrimmed) || foldedTrimmed.Length == foldedFull.Length)
        {
            foldedTrimmed = null;
        }

        var hitStart = -1;
        var hitLength = 0;
        var hitCount = 0;
        for (var i = 0; i < foldedField.Length; i++)
        {
            if (MatchesAt(foldedField, i, foldedFull))
            {
                hitStart = i;
                hitLength = foldedFull.Length;
                hitCount++;
            }
            else if (foldedTrimmed is not null &&
                     MatchesAt(foldedField, i, foldedTrimmed) &&
                     EndsAtTrimmedBoundary(foldedField, i + foldedTrimmed.Length, foldedTrimmed))
            {
                hitStart = i;
                hitLength = foldedTrimmed.Length;
                hitCount++;
            }

            if (hitCount > 1)
            {
                endReason = PastedRegionEndReason.AnchorAmbiguous;
                return false;
            }
        }

        if (hitCount == 0)
        {
            endReason = PastedRegionEndReason.DictatedTextNotFound;
            return false;
        }

        var renderedText = fieldText.Substring(hitStart, hitLength);
        var anchors = ExtractAnchors(fieldText, hitStart, hitLength);
        target = new PastedRegionTarget(pastedText, anchors, processId, windowHandle, processName, renderedText);
        return true;
    }

    private static string FoldSpaces(string value) =>
        value.Contains('\u00A0', StringComparison.Ordinal) ? value.Replace('\u00A0', ' ') : value;

    private static string TrimTrailingHorizontalWhitespace(string value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] is ' ' or '\t')
        {
            end--;
        }

        return end == value.Length ? value : value[..end];
    }

    private static bool MatchesAt(string haystack, int index, string needle) =>
        index + needle.Length <= haystack.Length &&
        string.CompareOrdinal(haystack, index, needle, 0, needle.Length) == 0;

    private static bool EndsAtTrimmedBoundary(string haystack, int endIndex, string trimmed)
    {
        if (endIndex >= haystack.Length)
        {
            return true;
        }

        // Trimming removed trailing whitespace; avoid matching a prefix of a longer or
        // connector-joined word (e.g. "cat" in "catalog", "cat's", "cat-like", or "cat_1").
        if (Rune.DecodeLastFromUtf16(trimmed.AsSpan(), out var lastTrimmed, out _) != System.Buffers.OperationStatus.Done ||
            !IsWordContinuationRune(lastTrimmed))
        {
            return true;
        }

        return Rune.DecodeFromUtf16(haystack.AsSpan(endIndex), out var nextRune, out _) != System.Buffers.OperationStatus.Done ||
            !IsWordContinuationRune(nextRune);
    }

    private static bool IsWordContinuationRune(Rune rune) =>
        Rune.IsLetterOrDigit(rune) ||
        rune.Value is '\'' or 0x2019 or '-' or 0x2010 or 0x2011 or '_';

    public static PastedRegionAnchors ExtractAnchors(string fullText, int startIndex, int length)
    {
        var beforeStart = Math.Max(0, startIndex - PastedRegionTiming.AnchorUtf16);
        // Never split inside a UTF-16 surrogate pair.
        if (beforeStart > 0 && beforeStart < startIndex && char.IsLowSurrogate(fullText[beforeStart]))
        {
            beforeStart++;
        }

        var before = fullText.Substring(beforeStart, startIndex - beforeStart);

        var afterStart = startIndex + length;
        var afterEnd = Math.Min(fullText.Length, afterStart + PastedRegionTiming.AnchorUtf16);
        if (afterEnd > afterStart && afterEnd < fullText.Length && char.IsHighSurrogate(fullText[afterEnd - 1]))
        {
            afterEnd--;
        }

        var after = fullText.Substring(afterStart, afterEnd - afterStart);
        return new PastedRegionAnchors(before, after);
    }

    public static LocateResult LocateRegion(string fieldText, PastedRegionTarget target, int editDistanceLimit)
    {
        if (string.IsNullOrWhiteSpace(fieldText))
        {
            return new LocateResult(LocateKind.TextboxEmptied, null);
        }

        var anchors = target.Anchors;
        int regionStart;
        int regionEnd;

        if (anchors.Before.Length == 0 && anchors.After.Length == 0)
        {
            regionStart = 0;
            regionEnd = fieldText.Length;
        }
        else if (anchors.Before.Length == 0)
        {
            var afterIndex = FindUniqueIndex(fieldText, anchors.After, out var ambiguous);
            if (ambiguous)
            {
                return new LocateResult(LocateKind.AnchorAmbiguous, null);
            }

            if (afterIndex < 0)
            {
                return new LocateResult(LocateKind.RegionRemoved, null);
            }

            regionStart = 0;
            regionEnd = afterIndex;
        }
        else if (anchors.After.Length == 0)
        {
            var beforeIndex = FindUniqueIndex(fieldText, anchors.Before, out var ambiguous);
            if (ambiguous)
            {
                return new LocateResult(LocateKind.AnchorAmbiguous, null);
            }

            if (beforeIndex < 0)
            {
                return new LocateResult(LocateKind.RegionRemoved, null);
            }

            regionStart = beforeIndex + anchors.Before.Length;
            regionEnd = fieldText.Length;
        }
        else
        {
            var beforeIndex = FindUniqueIndex(fieldText, anchors.Before, out var beforeAmbiguous);
            if (beforeAmbiguous)
            {
                return new LocateResult(LocateKind.AnchorAmbiguous, null);
            }

            if (beforeIndex < 0)
            {
                return new LocateResult(LocateKind.RegionRemoved, null);
            }

            var afterSearchStart = beforeIndex + anchors.Before.Length;
            var afterIndex = FindUniqueIndex(fieldText, anchors.After, afterSearchStart, out var afterAmbiguous);
            if (afterAmbiguous)
            {
                return new LocateResult(LocateKind.AnchorAmbiguous, null);
            }

            if (afterIndex < 0)
            {
                return new LocateResult(LocateKind.RegionRemoved, null);
            }

            regionStart = afterSearchStart;
            regionEnd = afterIndex;
        }

        if (regionEnd < regionStart)
        {
            return new LocateResult(LocateKind.RegionRemoved, null);
        }

        var region = fieldText.Substring(regionStart, regionEnd - regionStart);
        if (string.IsNullOrEmpty(region))
        {
            return new LocateResult(LocateKind.RegionRemoved, null);
        }

        var baseline = target.EffectiveRenderedText;
        if (string.Equals(region, baseline, StringComparison.Ordinal))
        {
            return new LocateResult(LocateKind.Located, region);
        }

        if (Math.Abs(region.Length - baseline.Length) > editDistanceLimit ||
            BandedEditDistance(baseline, region, editDistanceLimit) > editDistanceLimit)
        {
            return new LocateResult(LocateKind.EditDistanceExceeded, null);
        }

        return new LocateResult(LocateKind.Located, region);
    }

    public IReadOnlyList<PastedRegionEvent> Poll(ReadSnapshot snapshot, int nowMs)
    {
        if (_ended)
        {
            return [];
        }

        var events = new List<PastedRegionEvent>();
        if (nowMs - _startedAtMs >= PastedRegionTiming.CeilingMs)
        {
            FlushAndEnd(PastedRegionEndReason.CeilingElapsed, nowMs, events);
            return events;
        }

        if (!snapshot.ElementAlive)
        {
            FlushAndEnd(PastedRegionEndReason.ElementDestroyed, nowMs, events);
            return events;
        }

        if (!snapshot.SameApplicationFocus)
        {
            FlushAndEnd(PastedRegionEndReason.FocusChanged, nowMs, events);
            return events;
        }

        if (snapshot.SameElementFocus)
        {
            _focusAwaySinceMs = -1;
        }
        else
        {
            if (_focusAwaySinceMs < 0)
            {
                _focusAwaySinceMs = nowMs;
            }
            else if (nowMs - _focusAwaySinceMs >= PastedRegionTiming.FocusGraceMs)
            {
                FlushAndEnd(PastedRegionEndReason.FocusChanged, nowMs, events);
                return events;
            }
        }

        if (snapshot.TooLong)
        {
            FlushAndEnd(PastedRegionEndReason.CaptureUnsupported, nowMs, events);
            return events;
        }

        if (!snapshot.SameElementFocus && !snapshot.Readable)
        {
            return events;
        }

        if (!snapshot.Readable || snapshot.FieldText is null)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= PastedRegionTiming.MaxConsecutiveReadFailures)
            {
                // Lost box after seeing a valid edit with enough quiet age may still flush as weak evidence.
                if (_hasUnsettledEdit &&
                    nowMs - _lastChangeAtMs >= PastedRegionTiming.FlushMinQuietMs)
                {
                    events.Add(new PastedRegionEvent.Settled(_lastRegion, SettleEvidence.Weak));
                    _hasUnsettledEdit = false;
                }

                _ended = true;
                events.Add(new PastedRegionEvent.Ended(PastedRegionEndReason.CaptureUnsupported));
            }

            return events;
        }

        _consecutiveFailures = 0;
        var located = LocateRegion(snapshot.FieldText, _target, _editDistanceLimit);
        switch (located.Kind)
        {
            case LocateKind.TextboxEmptied:
                FlushAndEnd(PastedRegionEndReason.TextboxEmptied, nowMs, events);
                return events;
            case LocateKind.RegionRemoved:
                FlushAndEnd(PastedRegionEndReason.RegionRemoved, nowMs, events);
                return events;
            case LocateKind.AnchorAmbiguous:
                FlushAndEnd(PastedRegionEndReason.AnchorAmbiguous, nowMs, events);
                return events;
            case LocateKind.EditDistanceExceeded:
                FlushAndEnd(PastedRegionEndReason.EditDistanceExceeded, nowMs, events);
                return events;
        }

        var region = located.Region!;
        if (!string.Equals(region, _lastRegion, StringComparison.Ordinal))
        {
            _lastRegion = region;
            _lastChangeAtMs = nowMs;
            _firstDeferralAtMs = -1;
            _hasUnsettledEdit = !string.Equals(region, _target.EffectiveRenderedText, StringComparison.Ordinal);
            events.Add(new PastedRegionEvent.Changed(region));
            return events;
        }

        if (_hasUnsettledEdit && _lastChangeAtMs >= 0 && nowMs - _lastChangeAtMs >= PastedRegionTiming.SettleMs)
        {
            var evidence = EvaluateSettleEvidence(snapshot, region, nowMs);
            if (evidence is null)
            {
                // Cursor-aware deferral: wait until the caret leaves the edited word or CaretCapMs elapses.
                if (_firstDeferralAtMs < 0)
                {
                    _firstDeferralAtMs = nowMs;
                }

                if (nowMs - _firstDeferralAtMs < PastedRegionTiming.CaretCapMs)
                {
                    return events;
                }

                evidence = SettleEvidence.Weak;
            }

            _hasUnsettledEdit = false;
            _firstDeferralAtMs = -1;
            events.Add(new PastedRegionEvent.Settled(region, evidence.Value));
        }

        return events;
    }

    public IReadOnlyList<PastedRegionEvent> Finish(PastedRegionEndReason reason, int nowMs)
    {
        if (_ended)
        {
            return [];
        }

        var events = new List<PastedRegionEvent>();
        FlushAndEnd(reason, nowMs, events);
        return events;
    }

    private SettleEvidence? EvaluateSettleEvidence(ReadSnapshot snapshot, string region, int nowMs)
    {
        if (!snapshot.SameElementFocus)
        {
            return SettleEvidence.Weak;
        }

        if (snapshot.CaretOffset is not { } caret || snapshot.FieldText is null)
        {
            return SettleEvidence.Weak;
        }

        var regionStart = _target.Anchors.Before.Length == 0
            ? 0
            : snapshot.FieldText.IndexOf(_target.Anchors.Before, StringComparison.Ordinal) is int bIdx && bIdx >= 0
                ? bIdx + _target.Anchors.Before.Length
                : -1;
        if (regionStart < 0)
        {
            return SettleEvidence.Weak;
        }

        var regionEnd = regionStart + region.Length;
        if (caret < regionStart || caret > regionEnd)
        {
            return SettleEvidence.Strong;
        }

        // Caret sits inside one of the changed character spans -> user may still be typing that word.
        var (diffStart, diffEnd) = ChangedSpanInEdited(_target.EffectiveRenderedText, region);
        var absDiffStart = regionStart + diffStart;
        var absDiffEnd = regionStart + diffEnd;
        if (caret >= absDiffStart && caret <= absDiffEnd)
        {
            return null;
        }

        return SettleEvidence.Strong;
    }

    private static (int Start, int End) ChangedSpanInEdited(string original, string edited)
    {
        var prefix = 0;
        var minLen = Math.Min(original.Length, edited.Length);
        while (prefix < minLen && original[prefix] == edited[prefix])
        {
            prefix++;
        }

        var origSuffix = original.Length;
        var editSuffix = edited.Length;
        while (origSuffix > prefix && editSuffix > prefix && original[origSuffix - 1] == edited[editSuffix - 1])
        {
            origSuffix--;
            editSuffix--;
        }

        return (prefix, editSuffix);
    }

    private void FlushAndEnd(PastedRegionEndReason reason, int nowMs, List<PastedRegionEvent> events)
    {
        if (_hasUnsettledEdit && reason.FlushesPendingEdit())
        {
            var age = _lastChangeAtMs >= 0 ? nowMs - _lastChangeAtMs : 0;
            if (age >= reason.MinimumPendingEditAgeMs() &&
                !string.Equals(_lastRegion, _target.EffectiveRenderedText, StringComparison.Ordinal))
            {
                events.Add(new PastedRegionEvent.Settled(_lastRegion, reason.PendingEvidence()));
            }

            _hasUnsettledEdit = false;
        }

        _ended = true;
        events.Add(new PastedRegionEvent.Ended(reason));
    }

    private static int FindUniqueIndex(string text, string pattern, out bool ambiguous) =>
        FindUniqueIndex(text, pattern, startIndex: 0, out ambiguous);

    private static int FindUniqueIndex(string text, string pattern, int startIndex, out bool ambiguous)
    {
        ambiguous = false;
        var first = text.IndexOf(pattern, startIndex, StringComparison.Ordinal);
        if (first < 0)
        {
            return -1;
        }

        if (text.IndexOf(pattern, first + 1, StringComparison.Ordinal) >= 0)
        {
            ambiguous = true;
            return -1;
        }

        return first;
    }

    public static int BandedEditDistance(string source, string target, int limit)
    {
        var n = source.Length;
        var m = target.Length;
        if (Math.Abs(n - m) > limit)
        {
            return limit + 1;
        }

        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var j = 0; j <= m; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            var jStart = Math.Max(1, i - limit);
            var jEnd = Math.Min(m, i + limit);
            if (jStart > 1)
            {
                curr[jStart - 1] = limit + 1;
            }

            var rowMin = limit + 1;
            for (var j = jStart; j <= jEnd; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                var val = Math.Min(
                    Math.Min(prev[j] + 1, curr[j - 1] + 1),
                    prev[j - 1] + cost);
                curr[j] = val;
                if (val < rowMin)
                {
                    rowMin = val;
                }
            }

            if (jEnd < m)
            {
                curr[jEnd + 1] = limit + 1;
            }

            if (rowMin > limit)
            {
                return limit + 1;
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }
}
