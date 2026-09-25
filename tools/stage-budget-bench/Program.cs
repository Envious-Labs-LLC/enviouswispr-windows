// THE STAGE BUDGET BENCH (#239). Each deterministic text stage runs under a deadline; a first call that pays
// just-in-time compilation and first-use construction once crossed a 50 ms deadline on a loaded CI runner, the
// stage stood down, and the output lost the emoji the person said. The deadlines are set from what this prints.
//
// ONE FRESH PROCESS PER MEASUREMENT. Stages share code (the regular-expression runtime, the collections), so a
// stage measured after another in the same process inherits the other's compilation and reads as cheaper than
// it is on a person's first dictation. The driver starts itself once per stage, per condition, per run.
//
// TWO CONDITIONS. "normal" is the process as Windows starts it. "throttled" is the slowest a process can be
// made to run without other load: Idle priority, EcoQoS (execution-speed throttling), and affinity to the
// efficiency cores where the processor has them. A child that cannot apply a condition exits non-zero, so a
// half-applied throttle can never print a number that looks like a throttled one.
//
// Each child builds the stages the way the app does (the bundled emoji table loaded, inverse text normalisation
// warmed at construction) and then times the stage's own Process call with no deadline, so a slow first call is
// measured whole rather than cut off at the budget. The "pipeline" child runs the real pipeline with its real
// deadlines instead and prints each stage's receipt: the check that a budget holds, not the measurement that set it.
//
//   dotnet run -c Release --project tools/stage-budget-bench                  every stage, both conditions, 7 runs
//   dotnet run -c Release --project tools/stage-budget-bench -- --runs 15
//   dotnet run -c Release --project tools/stage-budget-bench -- --warmed      the same after the startup warm-up
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.PostProcessing;

const int WarmIterations = 200;

if (ArgumentValue(args, "--child") is { } child)
{
    var throttled = args.Contains("--throttled", StringComparer.Ordinal);
    var warmed = args.Contains("--warmed", StringComparer.Ordinal);
    var condition = throttled ? Throttle.Apply() : "normal";
    Console.WriteLine(child == "pipeline"
        ? await Child.MeasurePipelineAsync(condition, warmed)
        : Child.MeasureStage(Enum.Parse<DeterministicTextStage>(child), condition, warmed, WarmIterations));
    return 0;
}

var runs = int.Parse(ArgumentValue(args, "--runs") ?? "7", CultureInfo.InvariantCulture);
var warmedRun = args.Contains("--warmed", StringComparer.Ordinal);
var self = Environment.ProcessPath ?? throw new InvalidOperationException("The bench cannot find its own executable.");
DeterministicTextStage[] stages =
[
    DeterministicTextStage.CustomWords,
    DeterministicTextStage.FillerAndFalseStarts,
    DeterministicTextStage.SpokenEmoji,
    DeterministicTextStage.InverseTextNormalization,
    DeterministicTextStage.EnglishSpelling,
    DeterministicTextStage.EnglishSpellingAfterPolish,
    DeterministicTextStage.EmojiRestoration,
];

Console.WriteLine($"{runs} fresh processes per stage and condition; {WarmIterations} warm calls each; " +
    (warmedRun ? "after the startup warm-up." : "no warm-up (a first dictation before this fix)."));
Console.WriteLine();
Console.WriteLine("stage                        condition   budget ms  cold median  cold max  warm median  warm max  worst GC pause in a call");
foreach (var stage in stages)
{
    foreach (var throttledRun in new[] { false, true })
    {
        var samples = new List<StageSample>();
        for (var run = 0; run < runs; run++)
        {
            samples.Add(JsonSerializer.Deserialize<StageSample>(RunChild(self, stage.ToString(), throttledRun, warmedRun))
                ?? throw new InvalidOperationException("A child printed no measurement."));
        }

        var cold = samples.Select(sample => sample.ColdMilliseconds).Order().ToArray();
        var warmMedian = samples.Select(sample => sample.WarmMedianMilliseconds).Order().ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{stage,-28} {samples[0].Condition,-11} {samples[0].BudgetMilliseconds,9:0} {Median(cold),12:0.00} {cold[^1],9:0.00} " +
            $"{Median(warmMedian),12:0.000} {samples.Max(sample => sample.WarmMaxMilliseconds),9:0.000} " +
            $"{samples.Max(sample => sample.WorstGcPauseInOneCallMilliseconds),10:0.0}"));
    }
}

Console.WriteLine();
Console.WriteLine("The real pipeline, real deadlines, first call in a fresh process (status and elapsed ms per stage):");
foreach (var throttledRun in new[] { false, true })
{
    for (var run = 0; run < runs; run++)
    {
        Console.WriteLine(RunChild(self, "pipeline", throttledRun, warmedRun));
    }
}

return 0;

static string RunChild(string self, string child, bool throttled, bool warmed)
{
    var start = new ProcessStartInfo(self)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    start.ArgumentList.Add("--child");
    start.ArgumentList.Add(child);
    if (throttled)
    {
        start.ArgumentList.Add("--throttled");
    }

    if (warmed)
    {
        start.ArgumentList.Add("--warmed");
    }

    using var process = Process.Start(start) ?? throw new InvalidOperationException("A child did not start.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
    {
        throw new InvalidOperationException($"The {child} child failed (exit {process.ExitCode}): {error.Trim()}");
    }

    return output.Trim();
}

static double Median(double[] sorted) =>
    sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;

static string? ArgumentValue(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

internal sealed record StageSample(
    string Stage,
    string Condition,
    double BudgetMilliseconds,
    double ColdMilliseconds,
    double WarmMedianMilliseconds,
    double WarmMaxMilliseconds,
    double WorstGcPauseInOneCallMilliseconds);

internal static class Child
{
    // A made-up dictation of ordinary length that gives every stage real work: a custom word, fillers, a spoken
    // emoji, numbers, spoken punctuation and American spellings. Not the warm-up sentence, so a warmed run is
    // measured on text the warm-up never saw.
    private const string Spoken =
        "um so i think we should ship the windows build this week comma thumbs up emoji and see what people say " +
        "about the color of it period uh i counted fourteen things left on the list and three of them need review " +
        "from envy wisper before friday at two thirty";

    private const string Deterministic =
        "So I think we should ship the Windows build this week, 👍 and see what people say about the colour of it. " +
        "I counted 14 things left on the list and 3 of them need review from EnviousWispr before Friday at 2:30";

    private const string Polished =
        "So I think we should ship the Windows build this week and see what people say about the color of it. " +
        "I counted 14 things left on the list, and 3 of them need review from EnviousWispr before Friday at 2:30.";

    public static string MeasureStage(DeterministicTextStage stage, string condition, bool warmed, int warmIterations)
    {
        var steps = DeterministicTextPipeline.DefaultSteps();
        InverseTextNormalizer.Warm();
        if (warmed)
        {
            RequireWarm(new DeterministicTextPipeline(steps).WarmStages());
        }

        var step = steps.Single(candidate => candidate.Stage == stage);
        var context = ContextFor(stage);
        if (!step.IsEnabled(context))
        {
            throw new InvalidOperationException($"{stage} is not enabled by the bench's context, so it would time nothing.");
        }

        // A GARBAGE COLLECTION LANDS INSIDE WHICHEVER CALL IS RUNNING WHEN IT STARTS, and the deadline cannot
        // tell the difference, so the pause inside each call is measured beside the call and reported too.
        var pauseBefore = GC.GetTotalPauseDuration();
        var timer = Stopwatch.StartNew();
        _ = step.Process(context, CancellationToken.None);
        var cold = timer.Elapsed.TotalMilliseconds;
        var worstPause = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;

        var warm = new double[warmIterations];
        for (var iteration = 0; iteration < warmIterations; iteration++)
        {
            pauseBefore = GC.GetTotalPauseDuration();
            timer.Restart();
            _ = step.Process(context, CancellationToken.None);
            warm[iteration] = timer.Elapsed.TotalMilliseconds;
            worstPause = Math.Max(worstPause, (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds);
        }

        Array.Sort(warm);
        return JsonSerializer.Serialize(new StageSample(
            stage.ToString(),
            condition,
            step.Timeout.TotalMilliseconds,
            cold,
            warm[warm.Length / 2],
            warm[^1],
            worstPause));
    }

    public static async Task<string> MeasurePipelineAsync(string condition, bool warmed)
    {
        var pipeline = new DeterministicTextPipeline();
        if (warmed)
        {
            RequireWarm(pipeline.WarmStages());
        }

        var request = new DeterministicTextRequest(
            Transcript(),
            [new CustomWordEntry("envy wisper", "EnviousWispr")],
            Options());
        var deterministic = await pipeline.ProcessAsync(request).ConfigureAwait(false);
        var completed = await pipeline.ApplyPolishedTextAsync(request, deterministic, Polished).ConfigureAwait(false);
        return $"{condition,-9} " + string.Join(
            "  ",
            completed.Receipts.Select(receipt => string.Create(
                CultureInfo.InvariantCulture,
                $"{receipt.Stage}={receipt.Status}/{receipt.ElapsedMilliseconds}")));
    }

    private static void RequireWarm(IReadOnlyList<DeterministicTextStage> failed)
    {
        if (failed.Count > 0)
        {
            throw new InvalidOperationException($"The warm-up failed for {string.Join(", ", failed)}.");
        }
    }

    private static DeterministicTextContext ContextFor(DeterministicTextStage stage)
    {
        var afterPolish = stage is DeterministicTextStage.EnglishSpellingAfterPolish or DeterministicTextStage.EmojiRestoration;
        return new DeterministicTextContext(
            Transcript(),
            afterPolish ? Deterministic : Spoken,
            [new CustomWordEntry("envy wisper", "EnviousWispr")],
            Options(),
            afterPolish ? Polished : null);
    }

    private static Transcript Transcript() =>
        new(DictationSessionId.Create(), Spoken, "bench", DetectedLanguage: "en");

    private static DeterministicTextOptions Options() =>
        new(
            WordCorrectionEnabled: true,
            FillerRemovalEnabled: true,
            EmojiFormatterEnabled: true,
            SpokenPunctuationEnabled: true,
            EnglishSpelling: EnglishSpelling.British);
}

internal static class Throttle
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 1;
    private const int CpuSetInformation = 0;

    /// <summary>Idle priority, EcoQoS, and the efficiency cores; throws if any of them does not take.</summary>
    public static string Apply()
    {
        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed,
            StateMask = PowerThrottlingExecutionSpeed,
        };
        if (!SetProcessInformation(
                Process.GetCurrentProcess().Handle,
                ProcessPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<PowerThrottlingState>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "EcoQoS could not be applied.");
        }

        using var self = Process.GetCurrentProcess();
        self.PriorityClass = ProcessPriorityClass.Idle;
        self.Refresh();
        if (self.PriorityClass != ProcessPriorityClass.Idle)
        {
            throw new InvalidOperationException("Idle priority did not take.");
        }

        var efficiencyMask = EfficiencyCoreMask();
        if (efficiencyMask == 0)
        {
            return "throt-noE";
        }

        self.ProcessorAffinity = (nint)efficiencyMask;
        self.Refresh();
        if ((ulong)self.ProcessorAffinity != efficiencyMask)
        {
            throw new InvalidOperationException("Efficiency-core affinity did not take.");
        }

        return "throttled";
    }

    /// <summary>
    /// The logical processors in the lowest efficiency class, as a group-0 affinity mask, or 0 when every
    /// processor is in the same class (no hybrid cores to choose between).
    /// </summary>
    private static ulong EfficiencyCoreMask()
    {
        _ = GetSystemCpuSetInformation(null, 0, out var length, IntPtr.Zero, 0);
        var buffer = new byte[length];
        if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The processor sets could not be read.");
        }

        var processors = new List<(ushort Group, byte Index, byte Class)>();
        for (var offset = 0; offset < length;)
        {
            var size = BitConverter.ToInt32(buffer, offset);
            if (BitConverter.ToInt32(buffer, offset + 4) == CpuSetInformation)
            {
                processors.Add((
                    BitConverter.ToUInt16(buffer, offset + 12),
                    buffer[offset + 14],
                    buffer[offset + 18]));
            }

            offset += size;
        }

        var lowest = processors.Min(processor => processor.Class);
        if (processors.All(processor => processor.Class == lowest))
        {
            return 0;
        }

        return processors
            .Where(processor => processor.Group == 0 && processor.Class == lowest && processor.Index < 64)
            .Aggregate(0UL, (mask, processor) => mask | (1UL << processor.Index));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref PowerThrottlingState information,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        byte[]? information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);
}
