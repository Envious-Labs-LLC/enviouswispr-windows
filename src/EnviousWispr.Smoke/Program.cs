using System.Diagnostics;
using System.IO;
using System.Text.Json;
using EnviousWispr;
using EnviousWispr.Asr;
using EnviousWispr.Polish;

// Headless full-stack smoke test (no mic, no GUI):
//   1. Parakeet ASR on the S1 spike clips (clip10 / clip20 / clip94)
//   2. EG-1 server start + activation probe (GREEN requires real transform)
//   3. Full polish pipeline on a real ASR transcript
// Exit code 0 = all steps passed.
// --skip-eg1 : verify only the ASR leg (while model downloads are in flight)

var skipEg1 = args.Contains("--skip-eg1");

// ---------- A/B mode: identical real transcripts through multiple EG-1 builds ----------
//   EnviousWispr.Smoke.exe --ab <model.gguf> [<model.gguf> ...]
// ASR runs once (deterministic int8) and every build polishes the same raw
// transcripts, so the outputs are directly comparable. Exit 0 = every server
// started and every probe GREEN.
if (args.Length > 0 && args[0] == "--ab")
{
    var cfgAb = ConfigLoader.Load();
    var spikeDir = Path.GetFullPath(Path.Combine(cfgAb.BaseDir, "spikes", "s1", "audio"));
    var asrAb = new ParakeetEngine(cfgAb.Resolve(cfgAb.Asr.ModelDir), cfgAb.Asr.IntraOpThreads,
        cfgAb.Asr.InterOpThreads, cfgAb.Asr.MaxTokensPerStep, cfgAb.Asr.Pack, useCuda: false);
    var corpus = new (string Label, string Text)[]
    {
        ("clip10 10s", asrAb.Recognize(ReadWav16kMono(Path.Combine(spikeDir, "clip10.wav"))).Text),
        ("clip20 20s", asrAb.Recognize(ReadWav16kMono(Path.Combine(spikeDir, "clip20.wav"))).Text),
        ("clip94 91.5s", asrAb.Recognize(ReadWav16kMono(Path.Combine(spikeDir, "clip94.wav"))).Text),
    };
    asrAb.Dispose();

    var abFailures = 0;
    foreach (var model in args[1..])
    {
        Console.WriteLine($"=== AB {Path.GetFileName(model)} ===");
        var srv = new EgOneServer();
        var t0 = Stopwatch.StartNew();
        var ok = await srv.StartAsync(cfgAb.Resolve(cfgAb.Eg1.ServerExe), model,
            cfgAb.Eg1.ContextTokens, cfgAb.Eg1.StartTimeoutSeconds);
        Console.WriteLine($"start: {(ok ? "ok" : "FAIL")} in {t0.ElapsedMilliseconds} ms");
        if (!ok || srv.Endpoint is null) { abFailures++; await srv.DisposeAsync(); continue; }
        var pol = new EgOnePolisher(srv.Endpoint, cfgAb.Eg1.RequestTimeoutSeconds);
        var probeSw = Stopwatch.StartNew();
        var (green, probeOut) = EgOneProbe.Evaluate(await pol.PolishAsync(EgOneProbe.ProbeTranscript));
        Console.WriteLine($"probe: {(green ? "GREEN" : "FAIL")} ({probeSw.ElapsedMilliseconds} ms)\u2192 {probeOut}");
        if (!green) abFailures++;
        foreach (var (label, text) in corpus)
        {
            var pw = Stopwatch.StartNew();
            var outText = await pol.PolishAsync(text);
            Console.WriteLine($"[{label}] {pw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  in : {text}");
            Console.WriteLine($"  out: {outText ?? "<skipped>"}");
        }
        await srv.DisposeAsync();
    }
    Console.WriteLine(abFailures == 0 ? "AB PASS" : $"AB FAIL ({abFailures})");
    return abFailures == 0 ? 0 : 1;
}

var cfg = ConfigLoader.Load();
Console.WriteLine($"[cfg] base={cfg.BaseDir}");

// ---------- 1. ASR ----------
var modelDir = cfg.Resolve(cfg.Asr.ModelDir);
var sw = Stopwatch.StartNew();
var asr = new ParakeetEngine(modelDir, cfg.Asr.IntraOpThreads, cfg.Asr.InterOpThreads,
    cfg.Asr.MaxTokensPerStep, cfg.Asr.Pack, useCuda: cfg.Asr.Provider == "cuda");
Console.WriteLine($"[asr] engine loaded in {sw.ElapsedMilliseconds} ms");

var spikeAudio = Path.GetFullPath(Path.Combine(cfg.BaseDir, "spikes", "s1", "audio"));
var failures = 0;
foreach (var clip in new[] { "clip10.wav", "clip20.wav", "clip94.wav" })
{
    var path = Path.Combine(spikeAudio, clip);
    if (!File.Exists(path)) { Console.WriteLine($"[asr] SKIP {clip} (missing)"); continue; }
    var samples = ReadWav16kMono(path);
    var t0 = Stopwatch.StartNew();
    var result = asr.Recognize(samples);
    var seconds = samples.Length / 16000.0;
    Console.WriteLine($"[asr] {clip} ({seconds:0.#} s): {t0.ElapsedMilliseconds} ms  RTFx={seconds / t0.ElapsedMilliseconds * 1000.0:0.#}x");
    Console.WriteLine($"      text: {result.Text}");
    if (string.IsNullOrWhiteSpace(result.Text)) { failures++; Console.WriteLine("      FAIL: empty text"); }
}

// ---------- 2. EG-1 ----------
EgOneServer? server = null;
EgOnePolisher? polisher = null;
if (skipEg1)
{
    Console.WriteLine("[eg1] skipped (--skip-eg1)");
}
else if (cfg.Eg1.Enabled)
{
    server = new EgOneServer();
    server.Log += s => Console.WriteLine($"[eg1] {s}");
    var shard = Path.Combine(cfg.Resolve(cfg.Eg1.ModelDir), cfg.Eg1.EntrypointShard);
    var loadSw = Stopwatch.StartNew();
    var ok = await server.StartAsync(cfg.Resolve(cfg.Eg1.ServerExe), shard, cfg.Eg1.ContextTokens,
        cfg.Eg1.StartTimeoutSeconds);
    Console.WriteLine($"[eg1] server start: {ok} in {loadSw.ElapsedMilliseconds} ms");
    if (ok && server.Endpoint is not null)
    {
        polisher = new EgOnePolisher(server.Endpoint, cfg.Eg1.RequestTimeoutSeconds);
        var probeSw = Stopwatch.StartNew();
        var probeText = await polisher.PolishAsync(EgOneProbe.ProbeTranscript);
        var (green, output) = EgOneProbe.Evaluate(probeText);
        Console.WriteLine($"[eg1] probe ({probeSw.ElapsedMilliseconds} ms): {(green ? "GREEN" : "FAIL")}");
        Console.WriteLine($"      in : {EgOneProbe.ProbeTranscript}");
        Console.WriteLine($"      out: {output}");
        if (!green) failures++;
    }
    else
    {
        failures++;
    }
}
else
{
    Console.WriteLine("[eg1] disabled in config");
}

// ---------- 3. Full pipeline on a real clip ----------
var clip10 = Path.Combine(spikeAudio, "clip10.wav");
if (skipEg1)
{
    Console.WriteLine("[pipeline] skipped (--skip-eg1)");
}
else if (File.Exists(clip10) && polisher is not null)
{
    var samples = ReadWav16kMono(clip10);
    var raw = asr.Recognize(samples);
    var t0 = Stopwatch.StartNew();
    var polished = await polisher.PolishAsync(raw.Text);
    Console.WriteLine($"[pipeline] asr ({raw.ElapsedMs} ms): {raw.Text}");
    Console.WriteLine($"[pipeline] polish ({t0.ElapsedMilliseconds} ms): {polished ?? "<skipped — raw used>"}");
    Console.WriteLine($"[pipeline] total {(raw.ElapsedMs + t0.ElapsedMilliseconds)} ms");
}

if (server is not null) await server.DisposeAsync();
asr.Dispose();

Console.WriteLine(failures == 0 ? "SMOKE PASS" : $"SMOKE FAIL ({failures})");
return failures == 0 ? 0 : 1;

/// Reads a 16 kHz mono 16-bit PCM WAV (the capture spec; all corpus clips are PCM_16/16000/1).
static float[] ReadWav16kMono(string path)
{
    var b = File.ReadAllBytes(path);
    var p = 0;
    void Chk(ReadOnlySpan<byte> sig)
    {
        if (p + sig.Length > b.Length || !b.AsSpan(p, sig.Length).SequenceEqual(sig))
            throw new InvalidDataException($"bad wav header at {p} in {path}");
        p += sig.Length;
    }
    short S16() { var v = BitConverter.ToInt16(b, p); p += 2; return v; }
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
            var audioFormat = BitConverter.ToInt16(fmt, 0);
            var channels = BitConverter.ToInt16(fmt, 2);
            var sr = BitConverter.ToInt32(fmt, 4);
            var bits = BitConverter.ToInt16(fmt, 14);
            if (audioFormat != 1 || bits != 16)
                throw new InvalidDataException($"unsupported wav {audioFormat}/{bits} in {path}");
            if (sr != 16000) throw new InvalidDataException($"sr {sr} != 16000 in {path}");
            var n = Math.Min(size / 2 / channels, (b.Length - p) / 2 / channels);
            var s = new float[n];
            for (var i = 0; i < n; i++)
                s[i] = BitConverter.ToInt16(b, p + i * 2 * channels) / 32768f;
            return s;
        }
        else p += size + (size % 2);
    }
}
