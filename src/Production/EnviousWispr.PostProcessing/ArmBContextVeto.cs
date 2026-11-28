using System.Globalization;
using System.Text;

namespace EnviousWispr.PostProcessing;

/// <summary>
/// Pure-C# 12-language FNV-1a signed INT8 context veto for seam continuation capitalization
/// (<c>arm_b_v2.bin.br</c>). Matches the quantized EWB1 artifact and returns <c>true</c>
/// when surrounding context indicates the leading capital should be kept.
/// </summary>
public sealed class ArmBContextVeto
{
    private const string MagicHeader = "EWB1";

    private sealed record LanguageModel(
        int NumBuckets,
        float Bias,
        float Scale,
        float Tau,
        sbyte[] Weights);

    private static readonly Lazy<ArmBContextVeto?> Bundled = new(TryLoadBundledCore, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, LanguageModel> _models;

    private ArmBContextVeto(Dictionary<string, LanguageModel> models)
    {
        _models = models;
    }

    public static ArmBContextVeto? Shared => Bundled.Value;

    public static bool Warm() => Shared is not null;

    public static ArmBContextVeto Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var magicBytes = reader.ReadBytes(4);
        var magic = Encoding.ASCII.GetString(magicBytes);
        if (!string.Equals(magic, MagicHeader, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid ArmB context veto header '{magic}'.");
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > 256)
        {
            throw new InvalidDataException($"Invalid ArmB language count {count}.");
        }

        var models = new Dictionary<string, LanguageModel>(count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var langLength = reader.ReadByte();
            var lang = Encoding.ASCII.GetString(reader.ReadBytes(langLength));
            var numBuckets = reader.ReadInt32();
            if (numBuckets <= 0)
            {
                throw new InvalidDataException($"Invalid ArmB bucket count {numBuckets} for '{lang}'.");
            }

            var bias = reader.ReadSingle();
            var scale = reader.ReadSingle();
            var tau = reader.ReadSingle();
            var raw = reader.ReadBytes(numBuckets);
            if (raw.Length != numBuckets)
            {
                throw new EndOfStreamException($"Truncated ArmB weights for '{lang}'.");
            }

            var weights = new sbyte[numBuckets];
            Buffer.BlockCopy(raw, 0, weights, 0, numBuckets);
            var baseLang = SeamCasingPolicies.NormalizeBaseCode(lang) ?? lang;
            models[baseLang] = new LanguageModel(numBuckets, bias, scale, tau, weights);
        }

        return new ArmBContextVeto(models);
    }

    public bool SupportsLanguage(string? languageCode)
    {
        var baseLang = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        return baseLang is not null && _models.ContainsKey(baseLang);
    }

    public bool ShouldKeepCapital(string? languageCode, string leftContext, string payload, float? overrideTau = null)
    {
        var baseLang = SeamCasingPolicies.NormalizeBaseCode(languageCode);
        if (baseLang is null || !_models.TryGetValue(baseLang, out var model))
        {
            return true;
        }

        var prob = PredictKeepProbability(baseLang, model, leftContext ?? string.Empty, payload ?? string.Empty);
        return prob >= (overrideTau ?? model.Tau);
    }

    internal double PredictKeepProbability(string baseLang, string leftContext, string payload)
    {
        if (!_models.TryGetValue(baseLang, out var model))
        {
            return 1.0;
        }

        return PredictKeepProbability(baseLang, model, leftContext, payload);
    }

    private static double PredictKeepProbability(string lang, LanguageModel model, string leftContext, string payload)
    {
        var cleanLeft = StripFormatCharacters(leftContext);
        var cleanPayload = StripFormatCharacters(payload);
        var (p2Raw, p1Raw, rawTarget, n1Raw, n2Raw) = SplitContext(cleanLeft, cleanPayload);
        var bare = rawTarget.Trim(SeamCasingPolicies.EdgeTrimChars);
        var w = SeamCasingPolicies.ToLowerLocale(bare, lang);
        var hasTrailDot = rawTarget.EndsWith('.') && bare.Length <= 2;

        var p1Clean = p1Raw == "<BOS>" ? "<BOS>" : p1Raw.Trim(SeamCasingPolicies.EdgeTrimChars);
        var p2Clean = p2Raw == "<BOS>" ? "<BOS>" : p2Raw.Trim(SeamCasingPolicies.EdgeTrimChars);
        var p1Low = p1Clean == "<BOS>" ? "<BOS>" : SeamCasingPolicies.ToLowerLocale(p1Clean, lang);
        var p2Low = p2Clean == "<BOS>" ? "<BOS>" : SeamCasingPolicies.ToLowerLocale(p2Clean, lang);
        var p1IsCap = p1Clean.Length > 0 && p1Clean != "<BOS>" && char.IsUpper(p1Clean[0]) ? 1 : 0;
        var p1IsAllCap = p1Clean.Length > 1 && p1Clean != "<BOS>" &&
            AllLettersUppercase(p1Clean) ? 1 : 0;
        var p1EndsPunct = p1Raw.Length > 0 && p1Raw != "<BOS>" && !char.IsLetterOrDigit(p1Raw[^1])
            ? p1Raw[^1].ToString()
            : string.Empty;

        var n1Clean = n1Raw == "<EOS>" ? "<EOS>" : n1Raw.Trim(SeamCasingPolicies.EdgeTrimChars);
        var n2Clean = n2Raw == "<EOS>" ? "<EOS>" : n2Raw.Trim(SeamCasingPolicies.EdgeTrimChars);
        var n1Low = n1Clean == "<EOS>" ? "<EOS>" : SeamCasingPolicies.ToLowerLocale(n1Clean, lang);
        var n2Low = n2Clean == "<EOS>" ? "<EOS>" : SeamCasingPolicies.ToLowerLocale(n2Clean, lang);
        var n1IsCap = n1Clean.Length > 0 && n1Clean != "<EOS>" && char.IsUpper(n1Clean[0]) ? 1 : 0;
        var n1IsPunct = n1Raw != "<EOS>" && !ContainsLetterOrDigit(n1Raw) ? 1 : 0;

        var sumQ = 0;
        var nb = model.NumBuckets;
        var weights = model.Weights;

        void AddFeat(string feat)
        {
            var h = Fnv1a32(feat);
            var idx = (int)(h % (uint)nb);
            var sign = (h >> 31) == 0 ? 1 : -1;
            sumQ += sign * weights[idx];
        }

        AddFeat("b");
        AddFeat($"w={w}");
        AddFeat($"len={Math.Min(w.Length, 12)}");
        AddFeat($"dot={(hasTrailDot ? 1 : 0)}");
        AddFeat($"p1={p1Low}");
        AddFeat($"p2={p2Low}");
        AddFeat($"p12={p2Low}|{p1Low}");
        AddFeat($"p1c={p1IsCap}");
        AddFeat($"p1ac={p1IsAllCap}");
        AddFeat($"p1p={p1EndsPunct}");
        AddFeat($"n1={n1Low}");
        AddFeat($"n2={n2Low}");
        AddFeat($"n12={n1Low}|{n2Low}");
        AddFeat($"n1c={n1IsCap}");
        AddFeat($"n1p={n1IsPunct}");
        AddFeat($"pc_nc={p1IsCap}|{n1IsCap}");
        AddFeat($"p1_w={p1Low}|{w}");
        AddFeat($"w_n1={w}|{n1Low}");
        AddFeat($"p1_n1={p1Low}|{n1Low}");
        AddFeat($"p1c_w={p1IsCap}|{w}");
        AddFeat($"w_n1c={w}|{n1IsCap}");
        AddFeat($"p2_p1c={p2Low}|{p1IsCap}");

        var wl = w.Length;
        for (var k = 2; k <= 5; k++)
        {
            if (wl >= k)
            {
                var suf = w[(wl - k)..];
                AddFeat($"s{k}={suf}");
                AddFeat($"p1_s{k}={p1Low}|{suf}");
            }

            if (wl >= k && k <= 4)
            {
                AddFeat($"pre{k}={w[..k]}");
            }
        }

        if (wl == 1)
        {
            AddFeat("is1=1");
            AddFeat($"is1_p1={p1Low}");
            AddFeat($"is1_n1={n1Low}");
        }

        var logit = model.Bias + sumQ * (double)model.Scale;
        if (logit > 30)
        {
            return 1.0;
        }

        if (logit < -30)
        {
            return 0.0;
        }

        return 1.0 / (1.0 + Math.Exp(-logit));
    }

    internal static (string P2, string P1, string RawTarget, string N1, string N2) SplitContext(string left, string payload)
    {
        var p1 = "<BOS>";
        var p2 = "<BOS>";
        var lSpan = left.AsSpan().TrimEnd();
        if (!lSpan.IsEmpty)
        {
            var e1 = lSpan.Length;
            var s1 = e1;
            while (s1 > 0 && !char.IsWhiteSpace(lSpan[s1 - 1]))
            {
                s1--;
            }

            p1 = lSpan[s1..e1].ToString();
            var rem = lSpan[..s1].TrimEnd();
            if (!rem.IsEmpty)
            {
                var e2 = rem.Length;
                var s2 = e2;
                while (s2 > 0 && !char.IsWhiteSpace(rem[s2 - 1]))
                {
                    s2--;
                }

                p2 = rem[s2..e2].ToString();
            }
        }

        var rawTarget = string.Empty;
        var n1 = "<EOS>";
        var n2 = "<EOS>";
        var rSpan = payload.AsSpan().TrimStart();
        if (!rSpan.IsEmpty)
        {
            var e0 = 0;
            while (e0 < rSpan.Length && !char.IsWhiteSpace(rSpan[e0]))
            {
                e0++;
            }

            rawTarget = rSpan[..e0].ToString();
            var rem1 = rSpan[e0..].TrimStart();
            if (!rem1.IsEmpty)
            {
                var e1 = 0;
                while (e1 < rem1.Length && !char.IsWhiteSpace(rem1[e1]))
                {
                    e1++;
                }

                n1 = rem1[..e1].ToString();
                var rem2 = rem1[e1..].TrimStart();
                if (!rem2.IsEmpty)
                {
                    var e2 = 0;
                    while (e2 < rem2.Length && !char.IsWhiteSpace(rem2[e2]))
                    {
                        e2++;
                    }

                    n2 = rem2[..e2].ToString();
                }
            }
        }

        return (p2, p1, rawTarget, n1, n2);
    }

    internal static string StripFormatCharacters(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var hasFormat = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                hasFormat = true;
                break;
            }
        }

        if (!hasFormat)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                builder.Append(rune);
            }
        }

        return builder.ToString();
    }

    private static bool AllLettersUppercase(string value)
    {
        var hasLetter = false;
        foreach (var c in value)
        {
            if (char.IsLetter(c))
            {
                hasLetter = true;
                if (!char.IsUpper(c))
                {
                    return false;
                }
            }
        }

        return hasLetter;
    }

    private static bool ContainsLetterOrDigit(string value)
    {
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    internal static uint Fnv1a32(string s)
    {
        var h = 0x811c9dc5u;
        var byteCount = Encoding.UTF8.GetByteCount(s);
        Span<byte> buf = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(s, buf);
        foreach (var b in buf)
        {
            h ^= b;
            h *= 0x01000193u;
        }

        return h;
    }

    private static ArmBContextVeto? TryLoadBundledCore()
    {
        try
        {
            using var stream = CasingResources.TryOpenBrotliDecompressedStream("arm_b_v2.bin.br");
            return stream is null ? null : Load(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
