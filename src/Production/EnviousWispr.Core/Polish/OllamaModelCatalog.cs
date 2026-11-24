namespace EnviousWispr.Core.Polish;

/// <summary>How a local model did in the macOS cleanup benchmark, the only evidence either app has about one.</summary>
/// <remarks>
/// MEASURED ON macOS, NOT HERE. The bands come from the Mac app's behaviour corpus (OllamaModelVerdicts.swift),
/// graded 2026-08-10/11: Recommended is 30% or better, Mixed 14% to 29%, Unreliable 1% to 13%, and Not recommended
/// produced no acceptable result in any of twenty cases. The model's weights are the same on Windows; the speed is
/// not, and nothing here claims it. Ref: #213.
/// </remarks>
public enum OllamaModelVerdict
{
    Recommended,
    Mixed,

    /// <summary>We have not measured this model. Says who did not do it, rather than implying a reading.</summary>
    NotTested,
    Unreliable,
    NotRecommended,
}

/// <summary>One model the AI Polish page offers to download.</summary>
/// <param name="Id">The Ollama tag it is pulled by.</param>
/// <param name="Parameters">As Ollama's library states it, for the row's detail line.</param>
/// <param name="DownloadSize">Approximate and dated - a label, never used to decide anything.</param>
public sealed record OllamaCatalogEntry(
    string Id,
    string DisplayName,
    string Parameters,
    string DownloadSize,
    OllamaModelVerdict Verdict,
    string Note);

/// <summary>The curated local models and what macOS measured about them. Ref: #213, macOS OllamaSetupService.modelCatalog.</summary>
/// <remarks>
/// A LIST RATHER THAN THE LIBRARY. Ollama's library holds thousands of models and most of them cannot clean a
/// dictation; these eleven are the ones macOS measured, so each row can say what it did. Anything else a person
/// has installed still appears on the page and in the picker, as "Not tested by us".
/// </remarks>
public static class OllamaModelCatalog
{
    /// <summary>When the download sizes were last checked against the Ollama registry, on macOS.</summary>
    public static readonly DateOnly SizesCheckedOn = new(2026, 8, 11);

    /// <summary>The model a person is pointed at first: best in the measured corpus, and a 2 GB download.</summary>
    public const string RecommendedModelId = "qwen2.5:3b";

    /// <summary>Shown once above the list, never on one row: no local model did well on other languages.</summary>
    public const string NonEnglishCaveat = "No local model handled other languages well in our tests.";

    public static IReadOnlyList<OllamaCatalogEntry> Entries { get; } =
    [
        new("gemma3n:e4b", "Gemma 3 Nano (4B)", "4B", "~7.5 GB", OllamaModelVerdict.Mixed, Mixed),
        new("llama3.2", "Llama 3.2", "3B", "~2 GB", OllamaModelVerdict.Unreliable, Unreliable),
        new("llama3.2:1b", "Llama 3.2 (1B)", "1B", "~1.3 GB", OllamaModelVerdict.NotRecommended, FailedEverything),
        new("mistral", "Mistral", "7B", "~4.4 GB", OllamaModelVerdict.Unreliable, Unreliable),
        new("phi3", "Phi-3 Mini", "3.8B", "~2.2 GB", OllamaModelVerdict.NotRecommended, FailedEverything),
        new("gemma2:2b", "Gemma 2 (2B)", "2B", "~1.6 GB", OllamaModelVerdict.Mixed, Mixed),
        new("gemma2", "Gemma 2", "9B", "~5.4 GB", OllamaModelVerdict.Mixed, Mixed),
        new("qwen2.5:3b", "Qwen 2.5 (3B)", "3B", "~1.9 GB", OllamaModelVerdict.Recommended, "best in our tests, may follow dictated instructions"),
        new("qwen2.5:7b", "Qwen 2.5 (7B)", "7B", "~4.7 GB", OllamaModelVerdict.Recommended, "resists dictated instructions, sometimes drops or invents words"),
        new("qwen3:0.6b", "Qwen 3 (0.6B)", "0.6B", "~523 MB", OllamaModelVerdict.Recommended, "scored well in our tests, very small download"),
        new("tinyllama", "TinyLlama", "1.1B", "~638 MB", OllamaModelVerdict.NotRecommended, FailedEverything),
    ];

    private const string Mixed = "mixed results, often mishandles other languages";
    private const string Unreliable = "rarely cleans dictation correctly";
    private const string FailedEverything = "failed every test we ran";

    /// <summary>The measured models that are not offered for download, so an installed one still reads truthfully.</summary>
    private static readonly OllamaCatalogEntry[] MeasuredOnly =
    [
        new("deepseek-r1:1.5b", "DeepSeek R1 (1.5B)", "1.5B", "", OllamaModelVerdict.Unreliable, Unreliable),
    ];

    /// <summary>The same model under Ollama's two spellings: <c>foo</c> and <c>foo:latest</c>. Case never matters.</summary>
    public static string Canonical(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var trimmed = modelId.Trim();
        return trimmed.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^":latest".Length]
            : trimmed;
    }

    public static bool SameModel(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shared identity for an Ollama cloud model, which Ollama advertises at its cloud catalog as <c>glm-5.2</c> or
    /// <c>gpt-oss:120b</c> and registers locally under <c>glm-5.2:cloud</c> or <c>gpt-oss:120b-cloud</c>. Strips one
    /// trailing <c>:cloud</c> or <c>-cloud</c> suffix, then canonicalizes. Ref: macOS OllamaSetupService.hostedCatalogKey.
    /// </summary>
    public static string HostedCatalogKey(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        var trimmed = modelId.Trim();
        if (trimmed.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^":cloud".Length];
        }
        else if (trimmed.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"-cloud".Length];
        }

        return Canonical(trimmed);
    }

    public static bool SameHostedModel(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(HostedCatalogKey(left), HostedCatalogKey(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an Ollama model ID carries the <c>:cloud</c> or <c>-cloud</c> suffix used by Ollama's hosted cloud models.
    /// Hosted models must never be chosen as an automatic default without an explicit user selection.
    /// </summary>
    public static bool LooksLikeHostedCloudModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var trimmed = modelId.Trim();
        return trimmed.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an <c>OLLAMA_HOST</c> setting is empty or binds exclusively to a loopback address on this PC.
    /// Any wildcard (<c>0.0.0.0</c>, <c>*</c>, <c>::</c>) or LAN address returns <c>false</c>.
    /// </summary>
    public static bool IsLoopbackOllamaHostVariable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith(':'))
        {
            // ":11434" in Go net.SplitHostPort binds to all interfaces (0.0.0.0 / ::).
            return false;
        }

        var candidate = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"http://{trimmed}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(uri.Host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "::", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "[::]", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "*", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.IsLoopback;
    }

    /// <summary>Whether a model row matches the user's search query by id, canonical id, or display name.</summary>
    public static bool MatchesSearch(string? query, string id, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var needle = query.Trim();
        return id.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            Canonical(id).Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            HostedCatalogKey(id).Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            (displayName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>The curated entries matching <paramref name="query"/>, preserving verdict-band ordering.</summary>
    public static IReadOnlyList<OllamaCatalogEntry> FilterEntries(string? query) =>
        OrderedByVerdict(Entries, entry => entry.Id)
            .Where(entry => MatchesSearch(query, entry.Id, entry.DisplayName))
            .ToArray();

    /// <summary>The offered entry for an id, or null. Only these can be downloaded or removed from the page.</summary>
    public static OllamaCatalogEntry? Offered(string modelId) =>
        Entries.FirstOrDefault(entry => SameModel(entry.Id, modelId));

    /// <summary>What was measured about any id; an id nobody measured is <see cref="OllamaModelVerdict.NotTested"/>, never a guess.</summary>
    public static (OllamaModelVerdict Verdict, string Note) VerdictFor(string modelId) =>
        (Offered(modelId) ?? MeasuredOnly.FirstOrDefault(entry => SameModel(entry.Id, modelId))) is { } entry
            ? (entry.Verdict, entry.Note)
            : (OllamaModelVerdict.NotTested, string.Empty);

    public static string Label(OllamaModelVerdict verdict) => verdict switch
    {
        OllamaModelVerdict.Recommended => "Recommended",
        OllamaModelVerdict.Mixed => "Mixed results",
        OllamaModelVerdict.NotTested => "Not tested by us",
        OllamaModelVerdict.Unreliable => "Unreliable",
        OllamaModelVerdict.NotRecommended => "Not recommended",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    /// <summary>
    /// Orders by band and never within one: two models in the same band keep the order they came in. The bands are
    /// what was measured; the gaps inside a band are smaller than the instrument's own noise, so ranking them would
    /// invent a distinction (macOS, founder 2026-08-11). NOT TESTED SITS ABOVE THE MEASURED FAILURES: no evidence
    /// should not outrank bad evidence.
    /// </summary>
    public static IEnumerable<T> OrderedByVerdict<T>(IEnumerable<T> models, Func<T, string> id) =>
        models.OrderBy(model => (int)VerdictFor(id(model)).Verdict);

    /// <summary>
    /// Resolves how to launch Ollama on Windows: prefers the desktop tray app (<c>ollama app.exe</c> via shell execution)
    /// and falls back to <c>ollama.exe serve</c> (<c>CreateNoWindow = true</c>) only when a CLI-only install exists.
    /// </summary>
    public static OllamaLaunchTarget? ResolveOllamaLaunchTarget(
        string? localAppDataPath,
        string? pathEnvironment,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        if (!string.IsNullOrWhiteSpace(localAppDataPath))
        {
            var desktopApp = Path.Combine(localAppDataPath, "Programs", "Ollama", "ollama app.exe");
            if (SafeFileExists(desktopApp, exists))
            {
                return new OllamaLaunchTarget(desktopApp, Arguments: null, UseShellExecute: true, CreateNoWindow: false);
            }
        }

        if (FindExecutableOnPath("ollama app.exe", pathEnvironment, exists) is { } pathDesktopApp)
        {
            return new OllamaLaunchTarget(pathDesktopApp, Arguments: null, UseShellExecute: true, CreateNoWindow: false);
        }

        if (!string.IsNullOrWhiteSpace(localAppDataPath))
        {
            var cliBinary = Path.Combine(localAppDataPath, "Programs", "Ollama", "ollama.exe");
            if (SafeFileExists(cliBinary, exists))
            {
                return new OllamaLaunchTarget(cliBinary, Arguments: "serve", UseShellExecute: false, CreateNoWindow: true);
            }
        }

        if (FindExecutableOnPath("ollama.exe", pathEnvironment, exists) is { } pathCliBinary)
        {
            return new OllamaLaunchTarget(pathCliBinary, Arguments: "serve", UseShellExecute: false, CreateNoWindow: true);
        }

        return null;
    }

    private static string? FindExecutableOnPath(string executableName, string? pathEnvironment, Func<string, bool> exists)
    {
        foreach (var directory in (pathEnvironment ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executableName);
                if (SafeFileExists(candidate, exists))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid path characters cannot hold Ollama.
            }
        }

        return null;
    }

    private static bool SafeFileExists(string path, Func<string, bool> exists)
    {
        try
        {
            return exists(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed record OllamaLaunchTarget(
    string FileName,
    string? Arguments,
    bool UseShellExecute,
    bool CreateNoWindow);

/// <summary>One line of a download's progress, as Ollama streams it.</summary>
/// <param name="Status">Ollama's own phase word ("pulling manifest", "verifying sha256 digest"...), never shown raw.</param>
/// <param name="Digest">The layer this line is about; completed and total describe that layer, not the model.</param>
/// <param name="Quiet">
/// No line for a minute. Not a failure: Ollama hashes a large layer without saying anything, and the download is
/// still alive. The page says it is still working and keeps Stop available.
/// </param>
public sealed record OllamaPullUpdate(
    OllamaPullPhase Phase,
    string? Digest = null,
    long? Completed = null,
    long? Total = null,
    bool Quiet = false);

/// <summary>What stage a download is in, from Ollama's status words.</summary>
public enum OllamaPullPhase
{
    Starting,
    Downloading,
    Verifying,
    Finishing,
}

/// <summary>How a download ended.</summary>
public enum OllamaPullOutcome
{
    /// <summary>Ollama said "success". Only this is success: a layer at 100% is not the model.</summary>
    Succeeded,

    /// <summary>The person stopped it. Ollama may keep what it had, to resume next time.</summary>
    Cancelled,

    /// <summary>The stream ended before Ollama said "success": a dropped connection, a stopped server.</summary>
    Interrupted,
    DiskFull,

    /// <summary>Ollama could not reach its registry.</summary>
    NetworkFailed,

    /// <summary>The registry has no such model.</summary>
    NotFound,

    /// <summary>Ollama answered with an error the app has no better word for.</summary>
    Refused,

    /// <summary>Nothing answered at the endpoint.</summary>
    ServerUnavailable,
    EndpointInvalid,
}

/// <summary>How a removal ended.</summary>
public enum OllamaDeleteOutcome
{
    Deleted,

    /// <summary>Already gone - removed elsewhere, which is the state the person asked for.</summary>
    NotFound,
    Refused,
    ServerUnavailable,
    EndpointInvalid,
}

/// <summary>How resolving and registering a hosted Ollama model (<c>-cloud</c> / <c>:cloud</c>) ended.</summary>
public enum OllamaHostedAddOutcome
{
    Succeeded,
    Cancelled,
    Ambiguous,
    NoUsableName,
    Unreachable,
    PullFailed,
    ServerUnavailable,
    EndpointInvalid,
}

public sealed record OllamaHostedAddResult(
    OllamaHostedAddOutcome Outcome,
    string? ResolvedModelId = null,
    OllamaPullOutcome? PullOutcome = null);

/// <summary>How a local Ollama model warm-up request ended.</summary>
public enum OllamaWarmupOutcome
{
    Warmed,
    SkippedRemote,
    SkippedUnknownModel,
    Cancelled,
    Failed,
    ServerUnavailable,
    EndpointInvalid,
}

/// <summary>Whether Ollama's persisted settings and environment variables are safe to start without exposing the server to the LAN.</summary>
public enum OllamaLaunchSafety
{
    SafeLocalOnly,
    BlockedByNetworkExposure,
}
