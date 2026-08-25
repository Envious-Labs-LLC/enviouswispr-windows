using EnviousWispr.Asr;
using Xunit;

namespace EnviousWispr.Tests;

/// Runtime tests for the REAL ASR engine on REAL corpus clips (spikes/s1/audio).
/// Local-only runtime tests on the S1 corpus (the founder's own dictation).
/// The model packs are gitignored by design, so CI excludes this whole file
/// via -p:ExcludeLocalOnlyTests=true (.github/workflows/ci.yml); locally a
/// missing model fails the tests with a clear message.
public class AsrEngineRuntimeTests : IClassFixture<AsrEngineRuntimeTests.EngineFixture>
{
    private readonly EngineFixture _fx;

    public AsrEngineRuntimeTests(EngineFixture fx) => _fx = fx;

    public sealed class EngineFixture : IDisposable
    {
        public ParakeetEngine? Engine { get; }
        public string? ClipDir { get; }
        public string Reason { get; } = "not initialized";

        public EngineFixture()
        {
            var root = FindRepoRoot();
            if (root == null)
            {
                Reason = "repo root with models/parakeet-tdt-0.6b-v3 not found";
                return;
            }
            var modelDir = Path.Combine(root, "models", "parakeet-tdt-0.6b-v3");
            var clipDir = Path.Combine(root, "spikes", "s1", "audio");
            if (!File.Exists(Path.Combine(modelDir, "encoder-model.int8.onnx"))
                || !File.Exists(Path.Combine(clipDir, "clip10.wav")))
            {
                Reason = $"models or clips missing under {root}";
                return;
            }
            ClipDir = clipDir;
            // Same tier as the app default (int8, intra_op=8 per S1).
            Engine = new ParakeetEngine(modelDir, 8, 1, 10, "int8", useCuda: false);
        }

        /// Walks up from the test bin looking for the repo root (models/ dir).
        internal static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null
                   && !Directory.Exists(Path.Combine(dir.FullName, "models", "parakeet-tdt-0.6b-v3")))
                dir = dir.Parent;
            return dir?.FullName;
        }

        /// xUnit v2 has no runtime skip (Assert.Skip is v3-only; SkipException
        /// has no public ctor — verified 2026-08-25), so locally a missing
        /// model is a loud failure with a reason, and CI excludes the file.
        public void Require(ParakeetEngine? e) { if (e is null) Assert.Fail(Reason); }

        public void Dispose() => Engine?.Dispose();
    }

    /// Ported from EnviousWispr.Smoke (verified against the S1 clips): walks
    /// arbitrary chunk orders instead of assuming "fmt " comes first.
    internal static float[] ReadWav16kMono(string path)
    {
        var b = File.ReadAllBytes(path);
        var p = 0;
        void Chk(ReadOnlySpan<byte> sig)
        {
            if (p + sig.Length > b.Length || !b.AsSpan(p, sig.Length).SequenceEqual(sig))
                throw new InvalidDataException($"bad wav header at {p} in {path}");
            p += sig.Length;
        }
        int S32() { var v = BitConverter.ToInt32(b, p); p += 4; return v; }

        Chk("RIFF"u8); S32();
        Chk("WAVE"u8);
        var fmt = default(byte[]);
        while (true)
        {
            var id = b.AsSpan(p, 4); p += 4;
            var size = S32();
            if (id.SequenceEqual("fmt "u8)) { fmt = b.AsSpan(p, size).ToArray(); p += size; }
            else if (id.SequenceEqual("data"u8))
            {
                if (fmt is null || fmt.Length < 16)
                    throw new InvalidDataException($"missing or incomplete fmt chunk in {path}");
                var audioFormat = BitConverter.ToInt16(fmt, 0);
                var channels = BitConverter.ToInt16(fmt, 2);
                var sr = BitConverter.ToInt32(fmt, 4);
                var bits = BitConverter.ToInt16(fmt, 14);
                Assert.Equal(1, audioFormat); // PCM
                Assert.Equal(1, channels);
                Assert.Equal(16000, sr);
                Assert.Equal(16, bits);
                var n = Math.Min(size / 2, (b.Length - p) / 2);
                var s = new float[n];
                for (var i = 0; i < n; i++)
                    s[i] = BitConverter.ToInt16(b, p + i * 2) / 32768f;
                return s;
            }
            else p += size + (size % 2);
        }
    }

    [Fact]
    public void Clip10_TranscribesGroundTruth()
    {
        _fx.Require(_fx.Engine);
        var r = _fx.Engine!.Recognize(ReadWav16kMono(Path.Combine(_fx.ClipDir!, "clip10.wav")));
        Assert.Contains("telemetry", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(r.Text.Length > 30, $"text too short: '{r.Text}'");
    }

    [Fact]
    public void Clip20_TranscribesGroundTruth()
    {
        _fx.Require(_fx.Engine);
        var r = _fx.Engine!.Recognize(ReadWav16kMono(Path.Combine(_fx.ClipDir!, "clip20.wav")));
                // Ground truth (S1 corpus, founder's dictation): the clip ends with
        // "...no mention of sentiment analysis anywhere in this document."
        Assert.Contains("sentiment analysis", r.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clip10_Latency_StaysWithinCpuTierBudget()
    {
        _fx.Require(_fx.Engine);
        var r = _fx.Engine!.Recognize(ReadWav16kMono(Path.Combine(_fx.ClipDir!, "clip10.wav")));
        // S1 bar: CPU int8 intra_op=8 ≈ 0.32-0.35 s per 10 s clip on the dev rig.
        // Loose bound for slower machines; a regression of several seconds is the
        // failure this exists to catch.
        Assert.True(r.ElapsedMs < 3000, $"clip10 took {r.ElapsedMs} ms");
    }

    [Fact]
    public void SilentInput_ReturnsQuickly_WithoutThrowing()
    {
        _fx.Require(_fx.Engine);
        var silence = new float[16000 / 2]; // 0.5 s
        var r = _fx.Engine!.Recognize(silence);
        Assert.True(r.ElapsedMs < 1000, $"silence took {r.ElapsedMs} ms");
        Assert.True(r.Text.Length < 20, $"silence produced '{r.Text}'");
    }
}

/// The fp32 QDQ-free pack is the GPU-tier graph (f58204f) — runtime-verify the
/// pack switch end-to-end. Loads 2.5 GB, so it lives in its own fixture.
public class Fp32PackRuntimeTests
{
    private readonly Lazy<ParakeetEngine?> _engine;
    private readonly string? _clipDir;
    private readonly string _skip = "not initialized";

    public Fp32PackRuntimeTests()
    {
        var root = AsrEngineRuntimeTests.EngineFixture.FindRepoRoot();
        if (root is null ||
            !File.Exists(Path.Combine(root, "models", "parakeet-tdt-0.6b-v3", "encoder-model.onnx")) ||
            !File.Exists(Path.Combine(root, "spikes", "s1", "audio", "clip10.wav")))
        {
            _skip = "fp32 pack or clips missing (gitignored assets)";
            _engine = new(() => null);
            return;
        }
        _clipDir = Path.Combine(root, "spikes", "s1", "audio");
        _engine = new(() => new ParakeetEngine(
            Path.Combine(root, "models", "parakeet-tdt-0.6b-v3"), 8, 1, 10, "fp32", useCuda: false));
    }

    private void Require()
    {
        if (_clipDir is null) Assert.Fail(_skip);
    }

    [Fact]
    public void Fp32Pack_TranscribesGroundTruth()
    {
        Require();
        var e = _engine.Value!;
        using (e)
        {
            var r = e.Recognize(AsrEngineRuntimeTests.ReadWav16kMono(Path.Combine(_clipDir!, "clip10.wav")));
            Assert.Contains("telemetry", r.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
