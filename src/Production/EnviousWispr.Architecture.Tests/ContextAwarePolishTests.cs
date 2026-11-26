using System.Diagnostics;
using System.Reflection;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using Microsoft.CodeAnalysis.CSharp;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// Proves macOS-parity context-aware polish: target app resolution from process identity only,
/// strict privacy boundaries (no window title or UIA inspection), sanitization, prompt threading,
/// and prompt invariants across Cloud, Ollama, and EG-1.
/// </summary>
public sealed class ContextAwarePolishTests
{
    [Theory]
    [InlineData("winword", "Microsoft Word")]
    [InlineData("winword.exe", "Microsoft Word")]
    [InlineData("WINWORD", "Microsoft Word")]
    [InlineData("msedge", "Microsoft Edge")]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("code", "Visual Studio Code")]
    [InlineData("code.exe", "Visual Studio Code")]
    [InlineData("windowsterminal", "Windows Terminal")]
    [InlineData("slack", "Slack")]
    [InlineData("ms-teams", "Microsoft Teams")]
    [InlineData("teams", "Microsoft Teams")]
    [InlineData("olk", "Microsoft Outlook")]
    [InlineData("outlook", "Microsoft Outlook")]
    [InlineData("notepad", "Notepad")]
    [InlineData("notepad.exe", "Notepad")]
    [InlineData("notepad++", "Notepad++")]
    [InlineData("excel", "Microsoft Excel")]
    [InlineData("powerpnt", "Microsoft PowerPoint")]
    [InlineData("cmd", "Command Prompt")]
    [InlineData("powershell", "PowerShell")]
    [InlineData("pwsh", "PowerShell")]
    [InlineData("explorer", "File Explorer")]
    [InlineData("obsidian", "Obsidian")]
    public void CuratedFriendlyNamesMapExpectedProcesses(string processName, string expectedFriendlyName)
    {
        Assert.Equal(expectedFriendlyName, TargetAppNameResolver.MapFriendlyName(processName));
    }

    [Theory]
    [InlineData("sourcetree", "Sourcetree")]
    [InlineData("SourceTree", "SourceTree")]
    [InlineData("my_custom_tool", "My Custom Tool")]
    [InlineData("custom-editor", "Custom-Editor")]
    [InlineData("FOOBAR", "Foobar")]
    public void UnknownProcessesFallBackToCleanTitleCasing(string processName, string expectedFormatted)
    {
        Assert.Equal(expectedFormatted, TargetAppNameResolver.FormatFallbackTitleCase(processName));
        Assert.Equal(expectedFormatted, TargetAppNameResolver.MapFriendlyName(processName));
    }

    [Theory]
    [InlineData("normal app", "normal app")]
    [InlineData("app\r\nname", "appname")]
    [InlineData("app\tname\0", "appname")]
    [InlineData("<script>alert('x')</script>", "scriptalert('x')/script")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("<>", null)]
    [InlineData("\r\n\t", null)]
    public void SanitizationStripsControlCharsAndTags(string? input, string? expected)
    {
        Assert.Equal(expected, TargetAppNameResolver.Sanitize(input));
    }

    [Fact]
    public void SanitizationCapsLengthAt64Chars()
    {
        var longName = new string('a', 100);
        var sanitized = TargetAppNameResolver.Sanitize(longName);

        Assert.NotNull(sanitized);
        Assert.Equal(TargetAppNameResolver.MaximumNameLength, sanitized.Length);
        Assert.Equal(64, TargetAppNameResolver.MaximumNameLength);
    }

    [Fact]
    public void ProcessResolutionNeverAccessesMainWindowTitleOrUiaNames()
    {
        var repoRoot = RepositoryRoot();
        var filesToCheck = new[]
        {
            Path.Combine(repoRoot, "src", "Production", "EnviousWispr.Core", "Input", "TargetAppNameResolver.cs"),
            Path.Combine(repoRoot, "src", "Production", "EnviousWispr.Services", "Input", "WindowsForegroundTargetProvider.cs"),
        };

        foreach (var file in filesToCheck)
        {
            var code = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            var tokens = tree.GetRoot().DescendantTokens()
                .Select(token => token.ValueText ?? string.Empty)
                .ToArray();

            Assert.DoesNotContain(tokens, word =>
                word.Contains("MainWindowTitle", StringComparison.OrdinalIgnoreCase) ||
                word.Contains("NameProperty", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TargetAppNameIsNotPresentInAppLogEntryOrPrivacySafeDiagnosticRecord()
    {
        var logProperties = typeof(AppLogEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name);
        var recordProperties = typeof(PrivacySafeDiagnosticRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name);

        Assert.DoesNotContain(logProperties, name => name.Contains("TargetAppName", StringComparison.OrdinalIgnoreCase) || name.Contains("AppName", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recordProperties, name => name.Contains("TargetAppName", StringComparison.OrdinalIgnoreCase) || name.Contains("AppName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TargetAppNameResolverResolvesTargetWindowIdSafely()
    {
        var targetWithProcess = new TargetWindowId(101, ProcessId: 42, FocusedElementId: null, ProcessName: "winword");
        Assert.Equal("Microsoft Word", TargetAppNameResolver.Resolve(targetWithProcess));

        var invalidTarget = new TargetWindowId(0, ProcessId: 42, ProcessName: "winword");
        Assert.Null(TargetAppNameResolver.Resolve(invalidTarget));

        Assert.Null(TargetAppNameResolver.Resolve(null));
    }

    [Fact]
    public void CloudPromptAppendsContextWhenAppNameIsPresent()
    {
        var promptWithApp = CloudPolishPrompt.BuildSystemPrompt("en", 20, "Microsoft Word");
        Assert.Contains("\n\nThe user is dictating in Microsoft Word.", promptWithApp, StringComparison.Ordinal);

        var promptWithoutApp = CloudPolishPrompt.BuildSystemPrompt("en", 20, null);
        Assert.DoesNotContain("The user is dictating in", promptWithoutApp, StringComparison.Ordinal);

        var promptWithEmptyApp = CloudPolishPrompt.BuildSystemPrompt("en", 20, "   ");
        Assert.DoesNotContain("The user is dictating in", promptWithEmptyApp, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudPromptEmitsLanguageOnlyWhenPinned()
    {
        var pinned = CloudPolishPrompt.BuildSystemPrompt("en", 20, "Notepad");
        Assert.Contains("LANGUAGE: This transcript is in en. Clean it in en.\n\n", pinned, StringComparison.Ordinal);

        var auto = CloudPolishPrompt.BuildSystemPrompt("auto", 20, "Notepad");
        Assert.DoesNotContain("LANGUAGE:", auto, StringComparison.Ordinal);

        var nullLang = CloudPolishPrompt.BuildSystemPrompt(null, 20, "Notepad");
        Assert.DoesNotContain("LANGUAGE:", nullLang, StringComparison.Ordinal);

        // In all cases, unconditional language-preservation is present
        const string unconditional = "Keep the cleaned text in the same language(s) and script(s) as the transcript.";
        Assert.Contains(unconditional, pinned, StringComparison.Ordinal);
        Assert.Contains(unconditional, auto, StringComparison.Ordinal);
        Assert.Contains(unconditional, nullLang, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudPromptNeverCarriesVocabularyKeywords()
    {
        var repoRoot = RepositoryRoot();
        var files = new[]
        {
            Path.Combine(repoRoot, "src", "Production", "EnviousWispr.LLM", "CloudPolishPrompt.cs"),
            Path.Combine(repoRoot, "src", "Production", "EnviousWispr.LLM", "CloudPolishProviderBase.cs"),
        };

        foreach (var file in files)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var tokens = tree.GetRoot().DescendantTokens()
                .Select(t => t.ValueText ?? string.Empty)
                .ToArray();

            Assert.DoesNotContain(tokens, word =>
                word.Contains("Vocabulary", StringComparison.Ordinal) ||
                word.Contains("SPELLINGS", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void OllamaSystemPromptCarriesLanguageAndAppContext()
    {
        var prompt = OllamaLocalPrompt.BuildSystemPrompt(["term"], "es", "Visual Studio Code");
        Assert.Contains("\n\nLANGUAGE: This transcript is in es. Clean it in es.", prompt, StringComparison.Ordinal);
        Assert.Contains("\n\nThe user is dictating in Visual Studio Code.", prompt, StringComparison.Ordinal);
        Assert.Contains(PolishVocabulary.SystemGuidance, prompt, StringComparison.Ordinal);

        // Custom words are NEVER interpolated into system prompt
        Assert.DoesNotContain("term", prompt, StringComparison.Ordinal);

        // When auto, LANGUAGE is not emitted
        var autoPrompt = OllamaLocalPrompt.BuildSystemPrompt(["term"], "auto", "Visual Studio Code");
        Assert.DoesNotContain("LANGUAGE:", autoPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OllamaUserMessageCarriesSpellingsBlock()
    {
        var message = OllamaLocalPrompt.BuildUserMessage("here is some spoken text", ["MySpecialProduct"]);
        Assert.Contains("<SPELLINGS>\n- MySpecialProduct\n</SPELLINGS>", message, StringComparison.Ordinal);
        Assert.Contains("Transcript to clean:\n\nhere is some spoken text", message, StringComparison.Ordinal);
    }

    [Fact]
    public void EgOnePromptRemainsUntouched()
    {
        Assert.Equal("eg1-v2-named-language", EgOnePrompt.TemplateId);
        Assert.Contains("Copy-edit the dictated transcript into clean text", EgOnePrompt.SystemPrompt, StringComparison.Ordinal);

        var repoRoot = RepositoryRoot();
        var file = Path.Combine(repoRoot, "src", "Production", "EnviousWispr.LLM", "EgOnePrompt.cs");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var tokens = tree.GetRoot().DescendantTokens()
            .Select(t => t.ValueText ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(tokens, word =>
            word.Contains("TargetAppName", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("appName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PolishExecutorThreadsContextIntoPolishRequest()
    {
        PolishRequest? captured = null;
        var fakeProvider = new RecordingPolishProvider(request => captured = request);
        var setup = new PolishSetup(fakeProvider, UsesLocalRuntime: false, RuntimeResourceKind.Cpu);
        var executor = new PolishExecutor(
            new NoopAdmission(),
            new NoopEffects(),
            () => []);

        var input = new ProcessedText(DictationSessionId.Create(), "hello world");
        _ = await executor.TryPolishAsync(setup, input, "en", targetAppName: "Slack", recognitionLanguage: "en", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Slack", captured.TargetAppName);
        Assert.Equal("en", captured.RecognitionLanguage);
        Assert.Equal("en", captured.DetectedLanguage);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class RecordingPolishProvider(Action<PolishRequest> record) : IPolishProvider
    {
        public string ProviderId => "recording";

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<PolishResult> TryPolishAsync(PolishRequest request, CancellationToken cancellationToken = default)
        {
            record(request);
            return Task.FromResult(new PolishResult(request.Input, PolishAttemptStatus.Unchanged));
        }
    }

    private sealed class NoopAdmission : IRuntimeResourceAdmission
    {
        public Task<RuntimeResourceAcquireResult> AcquireAsync(
            RuntimeResourceKind resource,
            RuntimeWorkloadKind workload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeResourceAcquireResult(true, new FakeLease()));

        private sealed class FakeLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NoopEffects : IPolishAttemptEffects
    {
        public void RecordPolishStarted(string providerId) { }
        public void RecordPolishFinished(string providerId, PolishResult result, bool usedLocalRuntime, long elapsedMilliseconds) { }
    }
}
