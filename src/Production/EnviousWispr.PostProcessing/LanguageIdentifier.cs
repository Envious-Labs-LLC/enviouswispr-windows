using System.Text;
using EnviousWispr.Core.Dictation;

namespace EnviousWispr.PostProcessing;

public readonly record struct LanguageIdentificationResult(
    string? AcceptedLanguage,
    double AcceptedConfidence,
    string? TopHypothesis,
    double TopConfidence,
    bool UsedOsAgreement = false)
{
    public bool EnglishVeto =>
        AcceptedLanguage is null &&
        !string.IsNullOrEmpty(TopHypothesis) &&
        !string.Equals(TopHypothesis, "en", StringComparison.OrdinalIgnoreCase) &&
        double.IsFinite(TopConfidence) &&
        TopConfidence >= LanguageIdentifier.EnglishVetoThreshold;
}

/// <summary>
/// Unicode script-block detector for unsegmented / non-Latin scripts where script distribution is authoritative.
/// </summary>
public static class ScriptLanguageGate
{
    public static (string Language, double Confidence)? DetectByScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int kana = 0, han = 0, hangul = 0, thai = 0, lao = 0, khmer = 0, myanmar = 0, greek = 0, arabic = 0, hebrew = 0, letters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            letters++;
            var v = rune.Value;
            if (v is >= 0x3040 and <= 0x30FF) kana++;
            else if (v is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF) han++;
            else if (v is >= 0xAC00 and <= 0xD7AF) hangul++;
            else if (v is >= 0x0E00 and <= 0x0E7F) thai++;
            else if (v is >= 0x0E80 and <= 0x0EFF) lao++;
            else if (v is >= 0x1780 and <= 0x17FF) khmer++;
            else if (v is >= 0x1000 and <= 0x109F) myanmar++;
            else if (v is >= 0x0370 and <= 0x03FF) greek++;
            else if (v is >= 0x0600 and <= 0x06FF) arabic++;
            else if (v is >= 0x0590 and <= 0x05FF) hebrew++;
        }

        if (letters == 0)
        {
            return null;
        }

        if (kana > 0 && (kana + han) * 2 >= letters) return ("ja", 1.0);
        if (han * 2 >= letters) return ("zh", 1.0);
        if (hangul * 2 >= letters) return ("ko", 1.0);
        if (thai * 2 >= letters) return ("th", 1.0);
        if (lao * 2 >= letters) return ("lo", 1.0);
        if (khmer * 2 >= letters) return ("km", 1.0);
        if (myanmar * 2 >= letters) return ("my", 1.0);
        if (greek * 2 >= letters) return ("el", 1.0);
        if (arabic * 2 >= letters) return ("ar", 1.0);
        if (hebrew * 2 >= letters) return ("he", 1.0);
        return null;
    }
}

/// <summary>
/// Pure-C# 21-language character n-gram &amp; word-feature classifier (<c>langid_v1.bin.br</c>)
/// combined with <see cref="ScriptLanguageGate"/> and optional OS detector agreement (L4 Hybrid).
/// </summary>
public sealed class LanguageIdentifier
{
    public const double SoloAcceptThreshold = 0.85;
    public const double HybridAgreementThreshold = 0.55;
    public const double EnglishVetoThreshold = 0.50;

    private const string MagicHeader = "EWL1";

    private static readonly char[] WordTrimChars =
        ['.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}'];

    private static readonly Lazy<LanguageIdentifier?> Bundled =
        new(TryLoadBundledCore, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string[] _langs;
    private readonly int _numBuckets;
    private readonly float[] _biases;
    private readonly float[] _scales;
    private readonly sbyte[][] _weights;

    private LanguageIdentifier(string[] langs, int numBuckets, float[] biases, float[] scales, sbyte[][] weights)
    {
        _langs = langs;
        _numBuckets = numBuckets;
        _biases = biases;
        _scales = scales;
        _weights = weights;
    }

    public static LanguageIdentifier? Shared => Bundled.Value;

    public static bool Warm() => Shared is not null;

    public static LanguageIdentifier Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (!string.Equals(magic, MagicHeader, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid LangID header '{magic}'.");
        }

        var nLangs = reader.ReadInt32();
        var numBuckets = reader.ReadInt32();
        if (nLangs <= 0 || numBuckets <= 0)
        {
            throw new InvalidDataException($"Invalid LangID dimensions: langs={nLangs}, buckets={numBuckets}.");
        }

        var langs = new string[nLangs];
        var biases = new float[nLangs];
        var scales = new float[nLangs];
        var weights = new sbyte[nLangs][];
        for (var i = 0; i < nLangs; i++)
        {
            var langLength = reader.ReadByte();
            langs[i] = Encoding.ASCII.GetString(reader.ReadBytes(langLength));
            biases[i] = reader.ReadSingle();
            scales[i] = reader.ReadSingle();
            var raw = reader.ReadBytes(numBuckets);
            if (raw.Length != numBuckets)
            {
                throw new EndOfStreamException($"Truncated LangID weights for '{langs[i]}'.");
            }

            weights[i] = new sbyte[numBuckets];
            Buffer.BlockCopy(raw, 0, weights[i], 0, numBuckets);
        }

        return new LanguageIdentifier(langs, numBuckets, biases, scales, weights);
    }

    public (string Language, double Confidence)? IdentifyRaw(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var script = ScriptLanguageGate.DetectByScript(text);
        if (script is not null)
        {
            return script;
        }

        var cleanText = ArmBContextVeto.StripFormatCharacters(text);
        var parts = cleanText.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var joined = string.Concat(" ", string.Join(' ', parts), " ");
        var counts = new Dictionary<int, float>(128);
        var len = joined.Length;
        for (var k = 2; k <= 4; k++)
        {
            for (var i = 0; i <= len - k; i++)
            {
                var h = Fnv1a32Prefixed($"c{k}|", joined.AsSpan(i, k));
                var idx = (int)(h % (uint)_numBuckets);
                var sign = (h >> 31) == 0 ? 1f : -1f;
                counts[idx] = counts.TryGetValue(idx, out var cur) ? cur + sign : sign;
            }
        }

        foreach (var word in parts)
        {
            var trimmed = word.Trim(WordTrimChars);
            if (trimmed.Length < 2)
            {
                continue;
            }

            var h = ArmBContextVeto.Fnv1a32($"w|{trimmed}");
            var idx = (int)(h % (uint)_numBuckets);
            var sign = (h >> 31) == 0 ? 1.5f : -1.5f;
            counts[idx] = counts.TryGetValue(idx, out var cur) ? cur + sign : sign;
        }

        double normSq = 0;
        foreach (var value in counts.Values)
        {
            normSq += value * value;
        }

        var invNorm = normSq > 0 ? (float)(1.0 / Math.Sqrt(normSq)) : 1f;
        var classCount = _langs.Length;
        Span<double> logits = classCount <= 64 ? stackalloc double[classCount] : new double[classCount];
        var maxLogit = double.NegativeInfinity;
        for (var c = 0; c < classCount; c++)
        {
            var wc = _weights[c];
            double dot = 0;
            foreach (var kv in counts)
            {
                dot += kv.Value * wc[kv.Key];
            }

            var logit = _biases[c] + dot * invNorm * _scales[c];
            logits[c] = logit;
            if (logit > maxLogit)
            {
                maxLogit = logit;
            }
        }

        double sumExp = 0;
        var bestIndex = 0;
        var bestExp = -1.0;
        for (var c = 0; c < classCount; c++)
        {
            var exp = Math.Exp(logits[c] - maxLogit);
            sumExp += exp;
            if (exp > bestExp)
            {
                bestExp = exp;
                bestIndex = c;
            }
        }

        var confidence = sumExp > 0 ? bestExp / sumExp : 0.0;
        return (_langs[bestIndex], confidence);
    }

    public LanguageIdentificationResult IdentifyHybrid(
        string? text,
        IOsLanguageDetector? osDetector = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        var script = ScriptLanguageGate.DetectByScript(text);
        if (script is { } matchedScript)
        {
            return new LanguageIdentificationResult(
                matchedScript.Language,
                matchedScript.Confidence,
                matchedScript.Language,
                matchedScript.Confidence);
        }

        var raw = IdentifyRaw(text);
        if (raw is not { } top)
        {
            return default;
        }

        if (top.Confidence >= SoloAcceptThreshold)
        {
            return new LanguageIdentificationResult(
                top.Language,
                top.Confidence,
                top.Language,
                top.Confidence);
        }

        if (top.Confidence >= HybridAgreementThreshold && osDetector is not null)
        {
            var tags = osDetector.DetectBaseTags(text);
            if (tags.Count > 0)
            {
                var osTop = SeamCasingPolicies.NormalizeBaseCode(tags[0]);
                if (string.Equals(osTop, top.Language, StringComparison.OrdinalIgnoreCase))
                {
                    return new LanguageIdentificationResult(
                        top.Language,
                        top.Confidence,
                        top.Language,
                        top.Confidence,
                        UsedOsAgreement: true);
                }
            }
        }

        return new LanguageIdentificationResult(
            AcceptedLanguage: null,
            AcceptedConfidence: top.Confidence,
            TopHypothesis: top.Language,
            TopConfidence: top.Confidence);
    }

    private static uint Fnv1a32Prefixed(string prefix, ReadOnlySpan<char> slice)
    {
        var h = 0x811c9dc5u;
        var prefixCount = Encoding.UTF8.GetByteCount(prefix);
        var sliceCount = Encoding.UTF8.GetByteCount(slice);
        var total = prefixCount + sliceCount;
        Span<byte> buf = total <= 256 ? stackalloc byte[total] : new byte[total];
        Encoding.UTF8.GetBytes(prefix, buf[..prefixCount]);
        Encoding.UTF8.GetBytes(slice, buf[prefixCount..]);
        foreach (var b in buf)
        {
            h ^= b;
            h *= 0x01000193u;
        }

        return h;
    }

    private static LanguageIdentifier? TryLoadBundledCore()
    {
        try
        {
            using var stream = CasingResources.TryOpenBrotliDecompressedStream("langid_v1.bin.br");
            return stream is null ? null : Load(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
