using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnviousWispr.Core.Diagnostics;

public enum UatRuntimePhase
{
    OrdinaryReleaseEntered,
    CaptureStopEntered,
    CaptureStopReturned,
    StopEntered,
    StopCompleted,
    BackgroundCleanupEntered,
    BackgroundCleanupCompleted,
    StreamingSegmentAdmitted,
    StreamingSegmentReturned,
    StreamingSegmentCancelled,
    StreamingSegmentFailed,
    FinalTailSkipped,
    FinalTailDispatched,
    FinalTailReturned,
    FinalWholeAudioDispatched,
    FinalWholeAudioReturned,
    DeliveryEntered,
    DeliveryReturned,
    ParentRequestIssued,
    RealRequestAdmitted,
    PreprocessorEntered,
    PreprocessorReturned,
    PreprocessorFailed,
    EncoderEntered,
    EncoderReturned,
    EncoderFailed,
    DecoderEntered,
    DecoderReturned,
    DecoderFailed,
    WhisperProcessorEntered,
    WhisperProcessorReturned,
    WhisperProcessorFailed,
    ResponsePublished,
    ResponseFailedPublished,
    ResponsePublicationFailed,
    ResponseConsumed,
    ResponseFailureConsumed,
    CallerCancellationObserved,
    RetirementRequested,
    WorkerExitObserved,
    CancellationRequested,
}

public enum UatNativeOperation
{
    Preprocessor,
    Encoder,
    Decoder,
    WhisperProcessor,
}

/// <summary>Opt-in test evidence, separate from product diagnostics and their exporter/transport.</summary>
/// <remarks>
/// A finished segment and a surviving PID do not prove native work was active at key release.
/// This journal records the real boundaries in a deliberately isolated journey profile. It cannot
/// accept text, audio, paths, map names or exception messages as record fields. Missing evidence
/// invalidates a test conclusion; a diagnostic write never changes transcription or stop behavior.
/// Normal launches, and the headline benchmark arms, do not set the destination and stay disabled.
/// </remarks>
public sealed class UatRuntimePhaseJournal
{
    public const string DirectoryVariable = "ENVIOUSWISPR_UAT_PHASE_DIRECTORY";
    public const string GenerationVariable = "ENVIOUSWISPR_UAT_PHASE_GENERATION";
    public const string ParentVariable = "ENVIOUSWISPR_UAT_PHASE_PARENT_PID";
    private const string DataVariable = "ENVIOUSWISPR_DATA_DIRECTORY";
    private const string CredentialVariable = "ENVIOUSWISPR_UAT_CREDENTIAL_SUFFIX";
    private const long MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly object WriteGate = new();
    private static readonly AsyncLocal<RequestContext?> Ambient = new();
    private static readonly AsyncLocal<CorrelatedRequestBox?> AmbientParentRequest = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<UatRuntimePhase>(JsonNamingPolicy.CamelCase) },
    };
    private readonly string _directory;
    private readonly string _profile;
    private readonly string _runRoot;
    private readonly string _credentialSuffix;
    private readonly Guid? _generation;
    private readonly int _parentProcessId;

    private UatRuntimePhaseJournal(
        string directory,
        string profile,
        string runRoot,
        string suffix,
        Guid? generation,
        int parentProcessId)
    {
        _directory = directory;
        _profile = profile;
        _runRoot = runRoot;
        _credentialSuffix = suffix;
        _generation = generation;
        _parentProcessId = parentProcessId;
    }

    public static UatRuntimePhaseJournal? FromEnvironment(int? workerParentProcessId = null)
    {
        var destination = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        if (workerParentProcessId is { } parent)
        {
            return TryCreateForChild(
                destination,
                Environment.GetEnvironmentVariable(DataVariable),
                Environment.GetEnvironmentVariable(CredentialVariable),
                Environment.GetEnvironmentVariable(GenerationVariable),
                Environment.GetEnvironmentVariable(ParentVariable),
                parent);
        }
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GenerationVariable)))
        {
            // A child must use the child admission route, including its actual --parent-pid.
            return null;
        }

        return TryCreate(
            destination,
            Environment.GetEnvironmentVariable(DataVariable),
            Environment.GetEnvironmentVariable(CredentialVariable));
    }

    public static UatRuntimePhaseJournal? TryCreateForChild(
        string? directory,
        string? profile,
        string? suffix,
        string? generation,
        string? declaredParent,
        int actualParent)
    {
        if (actualParent <= 0 ||
            !Guid.TryParseExact(generation, "N", out var id) ||
            id == Guid.Empty ||
            !int.TryParse(declaredParent, NumberStyles.None, CultureInfo.InvariantCulture, out var parent) ||
            parent != actualParent)
        {
            return null;
        }

        return TryCreate(directory, profile, suffix, id, parent);
    }

    public static UatRuntimePhaseJournal? TryCreate(
        string? directory,
        string? profile,
        string? suffix,
        Guid? generation = null,
        int? parentProcessId = null)
    {
        // Strong JOURNEY-only admission, not a replacement for the general credential-store policy.
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(profile) ||
            suffix is not { Length: 40 } ||
            !suffix.StartsWith("journey-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(suffix[8..], "N", out var run) ||
            run == Guid.Empty ||
            generation == Guid.Empty ||
            parentProcessId is <= 0)
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EnviousWispr-AppJourney-Uat-" + run.ToString("N")));
            var data = Path.GetFullPath(profile);
            var destination = Path.GetFullPath(directory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(data), Path.Combine(root, "profile"), comparison) ||
                !string.Equals(Path.TrimEndingDirectorySeparator(destination), Path.Combine(root, "runtime-phases"), comparison) ||
                !SafeDirectory(root) ||
                !SafeDirectory(data) ||
                (Directory.Exists(destination) && !SafeDirectory(destination)))
            {
                return null;
            }

            return new(destination, data, root, suffix, generation, parentProcessId ?? Environment.ProcessId);
        }
        catch (Exception exception) when (DiagnosticFailure(exception))
        {
            return null;
        }
    }

    public IReadOnlyDictionary<string, string> ChildEnvironment(Guid generation)
    {
        if (generation == Guid.Empty)
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>
        {
            [DirectoryVariable] = _directory,
            [DataVariable] = _profile,
            [CredentialVariable] = _credentialSuffix,
            [GenerationVariable] = generation.ToString("N"),
            [ParentVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
        };
    }

    public IDisposable BeginRequest(Guid sessionId, Guid requestId)
    {
        if (_generation is not { } generation || sessionId == Guid.Empty || requestId == Guid.Empty)
        {
            return NoScope.Instance;
        }

        var previous = Ambient.Value;
        Ambient.Value = new(this, sessionId, requestId, generation, Environment.ProcessId);
        return new Restore(previous);
    }

    public static NativeCall BeginNativeCall(UatNativeOperation operation)
    {
        var context = Ambient.Value;
        if (context is null || !Enum.IsDefined(operation))
        {
            return NativeCall.Disabled;
        }

        var phases = operation switch
        {
            UatNativeOperation.Preprocessor => (UatRuntimePhase.PreprocessorEntered, UatRuntimePhase.PreprocessorReturned, UatRuntimePhase.PreprocessorFailed),
            UatNativeOperation.Encoder => (UatRuntimePhase.EncoderEntered, UatRuntimePhase.EncoderReturned, UatRuntimePhase.EncoderFailed),
            UatNativeOperation.Decoder => (UatRuntimePhase.DecoderEntered, UatRuntimePhase.DecoderReturned, UatRuntimePhase.DecoderFailed),
            UatNativeOperation.WhisperProcessor => (UatRuntimePhase.WhisperProcessorEntered, UatRuntimePhase.WhisperProcessorReturned, UatRuntimePhase.WhisperProcessorFailed),
            _ => default,
        };
        context.Record(phases.Item1);
        return new NativeCall(context, phases.Item2, phases.Item3);
    }

    public static void RecordSession(UatRuntimePhase phase, Guid? sessionId = null) =>
        FromEnvironment()?.Record(phase, sessionId ?? DictationScope.Current);

    public static RequestCorrelationScope BeginRequestCorrelation(out Guid requestId)
    {
        requestId = Guid.NewGuid();
        var previous = AmbientParentRequest.Value;
        var box = new CorrelatedRequestBox(requestId);
        AmbientParentRequest.Value = box;
        return new RequestCorrelationScope(box, previous);
    }

    public static bool TryTakeCorrelatedRequestId(out Guid requestId)
    {
        if (AmbientParentRequest.Value is { } box)
        {
            requestId = box.NextOrAllocate();
            return true;
        }

        requestId = Guid.Empty;
        return false;
    }

    public void Record(
        UatRuntimePhase phase,
        Guid? sessionId = null,
        Guid? requestId = null,
        Guid? generation = null,
        int? workerProcessId = null,
        int? exitCode = null,
        int? startSample = null,
        int? endSample = null,
        int? coveredSamples = null,
        int? remainingSamples = null,
        int? speechSamples = null,
        int? silenceSamples = null,
        int? sampleRate = null)
    {
        if (!Enum.IsDefined(phase) ||
            sessionId == Guid.Empty ||
            requestId == Guid.Empty ||
            generation == Guid.Empty ||
            workerProcessId is <= 0 ||
            (exitCode is not null && phase != UatRuntimePhase.WorkerExitObserved) ||
            startSample is < 0 ||
            endSample is < 0 ||
            (startSample is not null && endSample is not null && endSample < startSample) ||
            coveredSamples is < 0 ||
            remainingSamples is < 0 ||
            speechSamples is < 0 ||
            silenceSamples is < 0 ||
            sampleRate is <= 0)
        {
            return;
        }

        try
        {
            var record = new PhaseRecord(
                1,
                Environment.ProcessId,
                _parentProcessId,
                workerProcessId ?? (_generation is not null ? Environment.ProcessId : null),
                generation ?? _generation,
                sessionId,
                requestId,
                phase,
                Stopwatch.GetTimestamp(),
                Stopwatch.Frequency,
                exitCode,
                startSample,
                endSample,
                coveredSamples,
                remainingSamples,
                speechSamples,
                silenceSamples,
                sampleRate);
            var line = JsonSerializer.Serialize(record, Json) + "\n";
            lock (WriteGate)
            {
                if (!SafeDirectory(_runRoot) || !SafeDirectory(_profile))
                {
                    return;
                }

                Directory.CreateDirectory(_directory);
                if (!SafeDirectory(_directory))
                {
                    return;
                }

                var file = Path.Combine(_directory, "process-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".jsonl");
                if (File.Exists(file) &&
                    ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(file).Length + System.Text.Encoding.UTF8.GetByteCount(line) > MaximumFileBytes))
                {
                    return;
                }

                File.AppendAllText(file, line);
            }
        }
        catch (Exception exception) when (DiagnosticFailure(exception))
        {
            // No substitute event: missing evidence stays missing and cannot certify a native control.
        }
    }

    private static bool SafeDirectory(string path) =>
        Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static bool DiagnosticFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private sealed record PhaseRecord(
        int SchemaVersion,
        int ProcessId,
        int ParentProcessId,
        int? WorkerProcessId,
        Guid? GenerationId,
        Guid? SessionId,
        Guid? RequestId,
        UatRuntimePhase Phase,
        long Timestamp,
        long Frequency,
        int? ExitCode,
        int? StartSample = null,
        int? EndSample = null,
        int? CoveredSamples = null,
        int? RemainingSamples = null,
        int? SpeechSamples = null,
        int? SilenceSamples = null,
        int? SampleRate = null);

    internal sealed class CorrelatedRequestBox(Guid initialRequestId)
    {
        private readonly object _gate = new();
        private Guid _current = initialRequestId;
        private bool _firstTaken;

        public Guid Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        public Guid NextOrAllocate()
        {
            lock (_gate)
            {
                if (!_firstTaken)
                {
                    _firstTaken = true;
                    return _current;
                }

                _current = Guid.NewGuid();
                return _current;
            }
        }
    }

    public sealed class RequestCorrelationScope : IDisposable
    {
        private readonly CorrelatedRequestBox _box;
        private readonly CorrelatedRequestBox? _previous;
        private bool _disposed;

        internal RequestCorrelationScope(CorrelatedRequestBox box, CorrelatedRequestBox? previous)
        {
            _box = box;
            _previous = previous;
        }

        public Guid RequestId => _box.Current;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AmbientParentRequest.Value = _previous;
        }
    }

    internal sealed record RequestContext(
        UatRuntimePhaseJournal Journal,
        Guid SessionId,
        Guid RequestId,
        Guid Generation,
        int WorkerProcessId)
    {
        public void Record(UatRuntimePhase phase) =>
            Journal.Record(phase, SessionId, RequestId, Generation, WorkerProcessId);
    }

    private sealed class Restore(RequestContext? previous) : IDisposable
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

    public sealed class NativeCall : IDisposable
    {
        internal static NativeCall Disabled { get; } = new(null, default, default);
        private readonly RequestContext? _context;
        private readonly UatRuntimePhase _returned;
        private readonly UatRuntimePhase _failed;
        private int _ended;

        internal NativeCall(RequestContext? context, UatRuntimePhase returned, UatRuntimePhase failed)
        {
            _context = context;
            _returned = returned;
            _failed = failed;
        }

        public void Returned()
        {
            if (_context is null || Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return;
            }

            _context.Record(_returned);
        }

        public void Dispose()
        {
            if (_context is null || Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return;
            }

            _context.Record(_failed);
        }
    }
}
