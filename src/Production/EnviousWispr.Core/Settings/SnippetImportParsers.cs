using System.Text;

namespace EnviousWispr.Core.Settings;

// Pasted text and chosen files into snippet candidates, ported from macOS SnippetImportParsers.swift (#2997).
// One grammar per paste; a line with no separator or an empty side is COUNTED as skipped, never guessed at.

/// <summary>What a list or a CSV yielded: the snippets, and how many lines could not become one.</summary>
public sealed record SnippetParseResult(IReadOnlyList<SnippetImportCandidate> Candidates, int SkippedLines);

/// <summary>One snippet per line: the trigger, a separator, the text.</summary>
/// <remarks>
/// THE APPROVED DESIGN'S GRAMMAR. Explicit separators (a tab, <c>=&gt;</c>, <c>-&gt;</c>, an arrow, <c>=</c>) are
/// tried first and the earliest in the line wins, the longest on a tie, so <c>signature = Hello, world</c> keeps
/// its comma; a bare comma, then a colon not followed by <c>//</c>, are last resorts. A line that STARTS with a
/// quote is a quoted trigger, split by whatever follows its closing quote, and nothing inside the quotes is
/// searched. A blank line is nothing at all. The typed two characters <c>\n</c> become a line break in the text.
/// </remarks>
public static class SnippetLineListParser
{
    internal static readonly string[] ExplicitSeparators = ["\t", "=>", "->", "\u2192", "="];

    internal static readonly (char Open, char Close)[] QuotePairs =
        [('"', '"'), ('\'', '\''), ('\u201C', '\u201D'), ('\u2018', '\u2019')];

    private static readonly string[] SeparatorsLongestFirst =
        ExplicitSeparators.OrderByDescending(separator => separator.Length).ToArray();

    /// <summary>Lines by any line break: LF, CR, CRLF as one break, and the other Unicode line breaks.</summary>
    internal static IReadOnlyList<string> Lines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029')
            {
                lines.Add(text[start..index]);
                if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                start = index + 1;
            }
        }

        lines.Add(text[start..]);
        return lines;
    }

    /// <summary>The first non-blank line is a header when its left side names the trigger and its right side the text.</summary>
    internal static bool IsHeader(string line)
    {
        if (SplitOnFirstSeparator(line) is not var (rawLeft, rawRight))
        {
            return false;
        }

        var left = StrippingOneQuotePair(TrimHorizontal(rawLeft)).ToLowerInvariant();
        var right = StrippingOneQuotePair(TrimHorizontal(rawRight)).ToLowerInvariant();
        return left is "trigger" or "name" or "snippet" &&
            (right.Contains("text", StringComparison.Ordinal) || right.Contains("expansion", StringComparison.Ordinal));
    }

    /// <summary>One matching pair of surrounding quotes removed: <c>""hello""</c> becomes <c>"hello"</c>; <c>'hello</c> keeps its apostrophe.</summary>
    internal static string StrippingOneQuotePair(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var first = value[0];
        var last = value[^1];
        return QuotePairs.Any(pair => pair.Open == first && pair.Close == last) ? value[1..^1] : value;
    }

    /// <summary>The index of the closing quote of a field that opens the line with a quote; a doubled closer inside is skipped.</summary>
    internal static int? ClosingQuoteOfLeadingField(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        var pair = QuotePairs.FirstOrDefault(candidate => candidate.Open == line[0]);
        if (pair == default)
        {
            return null;
        }

        var from = 1;
        while (from < line.Length)
        {
            var close = line.IndexOf(pair.Close, from);
            if (close < 0)
            {
                return null;
            }

            if (close + 1 < line.Length && line[close + 1] == pair.Close)
            {
                from = close + 2;
                continue;
            }

            return close;
        }

        return null;
    }

    private static (string Left, string Right)? SplitOnFirstSeparator(string line)
    {
        if (ClosingQuoteOfLeadingField(line) is { } close)
        {
            var after = line[(close + 1)..].TrimStart(' ');
            var left = line[..(close + 1)];
            foreach (var separator in SeparatorsLongestFirst)
            {
                if (after.StartsWith(separator, StringComparison.Ordinal))
                {
                    return (left, after[separator.Length..]);
                }
            }

            if (after.StartsWith(',')) { return (left, after[1..]); }
            if (after.StartsWith(':') && !after.StartsWith("://", StringComparison.Ordinal)) { return (left, after[1..]); }

            // A quoted trigger with no joiner after it falls through, so `"quoted words" and more = x` still
            // reads by its explicit separator.
        }

        var best = -1;
        var bestLength = 0;
        foreach (var separator in ExplicitSeparators)
        {
            var at = line.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best || (at == best && separator.Length > bestLength)))
            {
                best = at;
                bestLength = separator.Length;
            }
        }

        if (best >= 0)
        {
            return (line[..best], line[(best + bestLength)..]);
        }

        var comma = line.IndexOf(',');
        if (comma >= 0)
        {
            return (line[..comma], line[(comma + 1)..]);
        }

        // The first colon NOT followed by `//`, so `https://example.com: homepage` splits at the second colon.
        var searchFrom = 0;
        while (searchFrom < line.Length && line.IndexOf(':', searchFrom) is var colon && colon >= 0)
        {
            if (!line.AsSpan(colon + 1).StartsWith("//", StringComparison.Ordinal))
            {
                return (line[..colon], line[(colon + 1)..]);
            }

            searchFrom = colon + 1;
        }

        return null;
    }

    /// <summary>Reads a list. Refuses more than <paramref name="limit"/> snippets as soon as it knows, without reading the rest.</summary>
    public static SnippetParseResult Parse(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        var candidates = new List<SnippetImportCandidate>();
        var skipped = 0;
        var sawFirstLine = false;
        foreach (var rawLine in Lines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!sawFirstLine)
            {
                sawFirstLine = true;
                if (IsHeader(line))
                {
                    continue;
                }
            }

            if (SplitOnFirstSeparator(line) is not var (rawLeft, rawRight))
            {
                skipped++;
                continue;
            }

            var trigger = StrippingOneQuotePair(TrimHorizontal(rawLeft));
            var expansion = StrippingOneQuotePair(TrimHorizontal(rawRight)).Replace("\\n", "\n", StringComparison.Ordinal);
            if (trigger.Length == 0 || expansion.Length == 0)
            {
                skipped++;
                continue;
            }

            candidates.Add(new SnippetImportCandidate(trigger, expansion));
            if (candidates.Count > limit)
            {
                throw new SnippetImportException(SnippetImportMessages.TooManySnippets(limit));
            }
        }

        return new SnippetParseResult(candidates, skipped);
    }

    /// <summary>Trims spaces and tabs but not line breaks (Foundation's <c>.whitespaces</c>).</summary>
    internal static string TrimHorizontal(string value) =>
        value.Trim().Length == value.Length
            ? value
            : TrimWhere(value, c => char.IsWhiteSpace(c) && c is not ('\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029'));

    private static string TrimWhere(string value, Func<char, bool> trim)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && trim(value[start])) { start++; }
        while (end > start && trim(value[end - 1])) { end--; }
        return value[start..end];
    }
}

/// <summary>RFC 4180 with two columns that matter: trigger, then text.</summary>
/// <remarks>
/// A QUOTED FIELD may hold commas, doubled quotes and line breaks - the only way a multi-line snippet travels
/// through a spreadsheet. Records end at CR, LF or CRLF outside quotes. Two deliberate departures from a strict
/// reading, both macOS's: a quote INSIDE an unquoted field is kept literally (<c>5" screen</c> has one honest
/// reading), while text after a CLOSING quote is refused (<c>"hello"x</c> has two).
/// </remarks>
public static class SnippetCsvParser
{
    private enum State { FieldStart, Unquoted, Quoted, QuoteInQuoted }

    /// <summary>Every record in order, with whether it was written explicitly (a quote or a comma appeared).</summary>
    internal static IEnumerable<(IReadOnlyList<string> Fields, bool Explicit)> Scan(string text)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var isExplicit = false;
        var state = State.FieldStart;
        var line = 1;
        var recordStartLine = 1;

        (IReadOnlyList<string>, bool) EndRecord()
        {
            record.Add(field.ToString());
            field.Clear();
            var finished = record.ToArray();
            var wasExplicit = isExplicit;
            record.Clear();
            isExplicit = false;
            recordStartLine = line;
            return (finished, wasExplicit);
        }

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            var lfFollows = index + 1 < text.Length && text[index + 1] == '\n';
            switch (state)
            {
                case State.FieldStart or State.Unquoted:
                    if (c == '"' && state == State.FieldStart)
                    {
                        isExplicit = true;
                        state = State.Quoted;
                    }
                    else if (c == ',')
                    {
                        isExplicit = true;
                        record.Add(field.ToString());
                        field.Clear();
                        state = State.FieldStart;
                    }
                    else if (c is '\r' or '\n')
                    {
                        if (c == '\r' && lfFollows) { index++; }
                        line++;
                        yield return EndRecord();
                        state = State.FieldStart;
                    }
                    else
                    {
                        field.Append(c);
                        state = State.Unquoted;
                    }

                    break;
                case State.Quoted:
                    if (c == '"')
                    {
                        state = State.QuoteInQuoted;
                    }
                    else
                    {
                        // Line breaks inside a quoted field are content, kept as written, and counted so a later
                        // error names the right line. CRLF is one line.
                        field.Append(c);
                        if (c == '\r')
                        {
                            line++;
                            if (lfFollows)
                            {
                                field.Append('\n');
                                index++;
                            }
                        }
                        else if (c == '\n')
                        {
                            line++;
                        }
                    }

                    break;
                case State.QuoteInQuoted:
                    if (c == '"')
                    {
                        field.Append('"');
                        state = State.Quoted;
                    }
                    else if (c == ',')
                    {
                        record.Add(field.ToString());
                        field.Clear();
                        state = State.FieldStart;
                    }
                    else if (c is '\r' or '\n')
                    {
                        if (c == '\r' && lfFollows) { index++; }
                        line++;
                        yield return EndRecord();
                        state = State.FieldStart;
                    }
                    else
                    {
                        throw new SnippetImportException(SnippetImportMessages.MalformedCsv(recordStartLine));
                    }

                    break;
            }
        }

        if (state == State.Quoted)
        {
            throw new SnippetImportException(SnippetImportMessages.MalformedCsv(recordStartLine));
        }

        // A final terminator already ended the last record; anything else still open is one.
        if (state != State.FieldStart || record.Count > 0 || field.Length > 0 || isExplicit)
        {
            yield return EndRecord();
        }
    }

    /// <summary>A header record: first column exactly trigger, name or snippet; second exactly expansion or text.</summary>
    internal static bool IsHeader(IReadOnlyList<string> record) =>
        record.Count >= 2 &&
        SnippetLineListParser.TrimHorizontal(record[0]).ToLowerInvariant() is "trigger" or "name" or "snippet" &&
        SnippetLineListParser.TrimHorizontal(record[1]).ToLowerInvariant() is "expansion" or "text";

    /// <summary>Reads a CSV. A physically blank line is nothing; a record written explicitly with nothing in it is counted.</summary>
    public static SnippetParseResult Parse(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        var candidates = new List<SnippetImportCandidate>();
        var skipped = 0;
        var first = true;
        foreach (var (record, isExplicit) in Scan(text))
        {
            if (!isExplicit && record.Count == 1 && SnippetLineListParser.TrimHorizontal(record[0]).Length == 0)
            {
                continue;
            }

            if (first)
            {
                first = false;
                if (IsHeader(record))
                {
                    continue;
                }
            }

            var trigger = record.Count > 0 ? SnippetLineListParser.TrimHorizontal(record[0]) : string.Empty;
            var expansion = record.Count > 1 ? record[1] : string.Empty;
            if (trigger.Length == 0 || expansion.Trim().Length == 0)
            {
                skipped++;
                continue;
            }

            candidates.Add(new SnippetImportCandidate(trigger, expansion));
            if (candidates.Count > limit)
            {
                throw new SnippetImportException(SnippetImportMessages.TooManySnippets(limit));
            }
        }

        return new SnippetParseResult(candidates, skipped);
    }
}

/// <summary>How a paste should be read. Auto sniffs; the person chooses only when the sniff says it is ambiguous.</summary>
public enum SnippetPasteFormat { Auto, List, Csv }

/// <summary>What a paste looks like.</summary>
public enum SnippetPasteSniff
{
    TransferDocument,
    Csv,
    List,

    /// <summary>A line carries both a comma and an explicit separator, so CSV and the list grammar would disagree. The screen offers "Read as".</summary>
    Ambiguous,
}

/// <summary>Reads pasted text: sniff, then the one grammar, then the same validation every source gets.</summary>
public static class SnippetPasteImport
{
    public static SnippetPasteSniff Sniff(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();

        // A brace that opens VALID JSON goes to the export reader, whose sentences then say "not ours" or
        // "damaged" truthfully. A brace that does not (`{date} = September 16`) is a list line.
        if (trimmed.StartsWith('{') && SnippetsTransferDocument.IsJson(trimmed))
        {
            return SnippetPasteSniff.TransferDocument;
        }

        var lines = SnippetLineListParser.Lines(trimmed).Select(SnippetLineListParser.TrimHorizontal).ToArray();
        var firstLine = lines.FirstOrDefault(line => line.Length > 0);
        if (firstLine is null)
        {
            return SnippetPasteSniff.List;
        }

        if (firstLine.StartsWith('"') && LeadingQuotedFieldIsAListSide(firstLine))
        {
            return SnippetPasteSniff.List;
        }

        // EVERY QUOTE SIGNAL ON EVERY LINE, never the first only: a headerless CSV whose first row is unquoted and
        // whose later row is quoted is still CSV.
        foreach (var line in lines.Where(line => line.Length > 0))
        {
            if (line.StartsWith('"') && !LeadingQuotedFieldIsAListSide(line)) { return SnippetPasteSniff.Csv; }
            if (HasQuotedFieldAfterComma(line)) { return SnippetPasteSniff.Csv; }
        }

        try
        {
            foreach (var (fields, _) in SnippetCsvParser.Scan(firstLine))
            {
                if (SnippetCsvParser.IsHeader(fields))
                {
                    return SnippetPasteSniff.Csv;
                }

                break;
            }
        }
        catch (SnippetImportException)
        {
            // Not readable as CSV, so not a CSV header.
        }

        var ambiguous = lines.Any(line =>
            line.Contains(',') &&
            SnippetLineListParser.ExplicitSeparators.Any(separator => line.Contains(separator, StringComparison.Ordinal)));
        return ambiguous ? SnippetPasteSniff.Ambiguous : SnippetPasteSniff.List;
    }

    /// <summary>The person's choice matters only for an ambiguous paste; otherwise the sniff decides.</summary>
    public static SnippetPasteSniff Resolve(SnippetPasteSniff sniff, SnippetPasteFormat choice) =>
        sniff != SnippetPasteSniff.Ambiguous
            ? sniff
            : choice == SnippetPasteFormat.Csv ? SnippetPasteSniff.Csv : SnippetPasteSniff.List;

    /// <summary>Bound, sniff, resolve, parse, validate - the same reading the live count and Continue both use.</summary>
    public static (SnippetPasteSniff Sniff, SnippetImportBatch Batch) Preview(string text, SnippetPasteFormat choice)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > SnippetImportLimits.MaximumImportFileBytes)
        {
            throw new SnippetImportException(SnippetImportMessages.TooLarge);
        }

        var sniff = Sniff(text);
        var limit = SnippetImportLimits.MaximumCandidates;
        var batch = Resolve(sniff, choice) switch
        {
            SnippetPasteSniff.TransferDocument => new SnippetImportBatch(
                "paste", "Pasted export", SnippetsTransferDocument.Read(text).Snippets, []),
            SnippetPasteSniff.Csv => FromResult("paste", "Pasted CSV", SnippetCsvParser.Parse(text, limit)),
            _ => FromResult("paste", "Pasted list", SnippetLineListParser.Parse(text, limit)),
        };
        return (sniff, batch.Validated());
    }

    internal static SnippetImportBatch FromResult(string sourceId, string displayName, SnippetParseResult result) =>
        new(sourceId, displayName, result.Candidates,
            result.SkippedLines > 0 ? [new SnippetImportNotice.LinesSkipped(result.SkippedLines)] : []);

    private static bool LeadingQuotedFieldIsAListSide(string line)
    {
        if (SnippetLineListParser.ClosingQuoteOfLeadingField(line) is not { } close)
        {
            return false;
        }

        var suffix = line[(close + 1)..].TrimStart(' ');
        return SnippetLineListParser.ExplicitSeparators.Append(":").Any(separator => suffix.StartsWith(separator, StringComparison.Ordinal));
    }

    private static bool HasQuotedFieldAfterComma(string line)
    {
        var quotedComma = line.IndexOf(",\"", StringComparison.Ordinal);
        if (quotedComma < 0)
        {
            return false;
        }

        return !SnippetLineListParser.ExplicitSeparators.Any(separator =>
            line.IndexOf(separator, StringComparison.Ordinal) is var at && at >= 0 && at < quotedComma);
    }
}

/// <summary>A chosen file into a batch: dispatch by exact extension, a bounded read, then the same parsers as a paste.</summary>
public static class SnippetFileImport
{
    /// <summary>Every extension the picker offers, in the order it lists them.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".json", ".csv", ".txt", ".text", ".md", ".list"];

    /// <summary>The byte ceiling for a file with this extension: our own export gets the higher one.</summary>
    public static int MaximumBytes(string extension) =>
        string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
            ? SnippetImportLimits.MaximumExportedFileBytes
            : SnippetImportLimits.MaximumImportFileBytes;

    /// <summary>Reads the bytes of a file the person chose. The caller has already bounded the read by <see cref="MaximumBytes"/>.</summary>
    public static SnippetImportBatch Read(string extension, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(bytes);
        var ext = extension.ToLowerInvariant();
        if (!Extensions.Contains(ext))
        {
            throw new SnippetImportException(SnippetImportMessages.UnsupportedType(ext.Length == 0 ? "those" : ext));
        }

        if (bytes.Length > MaximumBytes(ext))
        {
            throw new SnippetImportException(SnippetImportMessages.TooLarge);
        }

        var text = Decode(bytes) ?? throw new SnippetImportException(SnippetImportMessages.Unreadable);
        var limit = SnippetImportLimits.MaximumCandidates;
        var batch = ext switch
        {
            ".json" => new SnippetImportBatch("file_json", "EnviousWispr snippets file", SnippetsTransferDocument.Read(text).Snippets, []),
            ".csv" => SnippetPasteImport.FromResult("file_csv", "CSV file", SnippetCsvParser.Parse(text, limit)),
            _ => SnippetPasteImport.FromResult("file_text", "Plain list", SnippetLineListParser.Parse(text, limit)),
        };
        return batch.Validated();
    }

    /// <summary>UTF-8, or UTF-8 / UTF-16 with a byte-order mark; anything else is refused rather than guessed at.</summary>
    /// <remarks>A recognised mark is AUTHORITATIVE: a file whose mark says UTF-16 and whose bytes then fail is broken, not secretly Latin-1.</remarks>
    public static string? Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string text;
        try
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                text = DecodeStrict(new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), bytes, 2);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                text = DecodeStrict(new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), bytes, 2);
            }
            else
            {
                var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                text = DecodeStrict(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), bytes, skip);
            }
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        return SnippetImportTextPolicy.IsPlausiblyText(text) ? text : null;
    }

    private static string DecodeStrict(Encoding encoding, byte[] bytes, int skip)
    {
        if (encoding is UnicodeEncoding && (bytes.Length - skip) % 2 != 0)
        {
            // A dangling byte is a truncated file, not a shorter one.
            throw new DecoderFallbackException("Odd byte count for UTF-16.");
        }

        return encoding.GetString(bytes, skip, bytes.Length - skip);
    }
}
