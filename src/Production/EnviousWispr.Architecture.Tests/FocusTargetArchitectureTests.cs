using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Input;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class FocusTargetArchitectureTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public async Task LoadMigratesV25AndPortableProfileV18AndRoundTripsPasteTargetMode()
    {
        await JsonSettingsStoreTests.WithTestDirectoryAsync(async directory =>
        {
            var settingsPath = Path.Combine(directory, "settings-v25.json");
            var v25Settings = JsonSettingsStoreTests.CreatePopulatedSettings() with { SchemaVersion = 25 };
            var settingsNode = JsonNode.Parse(JsonSerializer.Serialize(v25Settings, JsonSettingsStore.SerializerOptions))!.AsObject();
            settingsNode["preferences"]!.AsObject().Remove("pasteTargetMode");
            await File.WriteAllTextAsync(settingsPath, settingsNode.ToJsonString(JsonSettingsStore.SerializerOptions));

            var loaded = await new JsonSettingsStore(settingsPath).LoadAsync();

            Assert.Equal(SettingsLoadStatus.Migrated, loaded.Status);
            Assert.Equal(25, loaded.SourceSchemaVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
            Assert.Equal(PasteTargetMode.WhereDictationStarted, loaded.Settings.Preferences.PasteTargetMode);

            var profilePath = Path.Combine(directory, "profile-v18.enviouswispr.json");
            var v18Profile = v25Settings.ToPortableProfile() with { SchemaVersion = 18 };
            var profileNode = JsonNode.Parse(JsonSerializer.Serialize(v18Profile, JsonSettingsStore.SerializerOptions))!.AsObject();
            profileNode["preferences"]!.AsObject().Remove("pasteTargetMode");
            await File.WriteAllTextAsync(profilePath, profileNode.ToJsonString(JsonSettingsStore.SerializerOptions));

            var imported = await new JsonPortableProfileService().ImportAsync(profilePath);
            Assert.Equal(PortableProfileImportStatus.Imported, imported.Status);
            Assert.Equal(PortableProfile.CurrentSchemaVersion, imported.Profile?.SchemaVersion);
            Assert.Equal(PasteTargetMode.WhereDictationStarted, imported.Profile?.Preferences.PasteTargetMode);

            var store = new JsonSettingsStore(Path.Combine(directory, "presenter-roundtrip.json"));
            using var presenter = new SettingsPresenter(store, AppSettings.Default);

            var saveCursor = await presenter.SaveGeneralAsync(CreateGeneralInput(returnToStartWindow: false));
            Assert.True(saveCursor.Saved);
            Assert.Equal(PasteTargetMode.WhereCursorIsAtRelease, presenter.Current.Preferences.PasteTargetMode);

            var saveStart = await presenter.SaveGeneralAsync(CreateGeneralInput(returnToStartWindow: true));
            Assert.True(saveStart.Saved);
            Assert.Equal(PasteTargetMode.WhereDictationStarted, presenter.Current.Preferences.PasteTargetMode);

            var reloaded = await store.LoadAsync();
            Assert.Equal(SettingsLoadStatus.Loaded, reloaded.Status);
            Assert.Equal(PasteTargetMode.WhereDictationStarted, reloaded.Settings.Preferences.PasteTargetMode);
        });

        var invalid = AppSettings.Default with
        {
            Preferences = AppSettings.Default.Preferences with
            {
                PasteTargetMode = (PasteTargetMode)99,
            },
        };
        Assert.NotNull(AppSettingsValidator.Validate(invalid, AppErrorStage.SettingsLoad));

        var root = FindRepositoryRoot();
        var document = AppXamlSources.LoadMainWindowAndPagesDocument(root);
        Assert.NotNull(document.Descendants().FirstOrDefault(e => (string?)e.Attribute(XName.Get("Name", XamlNamespace)) == "ReturnToStartWindowToggle"));
    }

    [Fact]
    public void RestoreAndFocusCoreSkipsAttachWhenDirectActivationSucceedsOrAlreadyForeground()
    {
        nint foreground = 0x1001;
        var calls = new List<string>();

        var alreadyFront = WindowsWindowIdentity.RestoreAndFocusCore(
            window: 0x1001,
            isWindow: _ => true,
            isIconic: _ => false,
            showWindowRestore: _ => calls.Add("restore"),
            getForegroundWindow: () => foreground,
            getWindowThreadProcessId: (nint h, out uint pid) => { pid = 10; return 100; },
            getCurrentThreadId: () => 50,
            tryGetProcessName: _ => "notepad",
            bringWindowToTop: _ => { calls.Add("top"); return true; },
            setForegroundWindow: h => { calls.Add("setfg"); foreground = h; return true; },
            setActiveWindow: h => { calls.Add("active"); return h; },
            attachThreadInput: (_, _, _) => { calls.Add("attach"); return true; });

        Assert.True(alreadyFront);
        Assert.Empty(calls);

        foreground = 0x2002;
        var restoredDirectly = WindowsWindowIdentity.RestoreAndFocusCore(
            window: 0x1001,
            isWindow: _ => true,
            isIconic: _ => true,
            showWindowRestore: _ => calls.Add("restore"),
            getForegroundWindow: () => foreground,
            getWindowThreadProcessId: (nint h, out uint pid) => { pid = 10; return h == 0x1001 ? 100u : 200u; },
            getCurrentThreadId: () => 50,
            tryGetProcessName: _ => "notepad",
            bringWindowToTop: _ => { calls.Add("top"); return true; },
            setForegroundWindow: h => { calls.Add("setfg"); foreground = h; return true; },
            setActiveWindow: h => { calls.Add("active"); return h; },
            attachThreadInput: (_, _, _) => { calls.Add("attach"); return true; });

        Assert.True(restoredDirectly);
        Assert.Equal(["restore", "top", "setfg"], calls);
    }

    [Fact]
    public void RestoreAndFocusCoreUsesBoundedAttachAndDetachesOnThrow()
    {
        nint foreground = 0x2002;
        var directAttempts = 0;
        var attachEvents = new List<string>();

        var restored = WindowsWindowIdentity.RestoreAndFocusCore(
            window: 0x1001,
            isWindow: _ => true,
            isIconic: _ => false,
            showWindowRestore: _ => { },
            getForegroundWindow: () => foreground,
            getWindowThreadProcessId: (nint h, out uint pid) =>
            {
                pid = h == 0x1001 ? 101u : 202u;
                return h == 0x1001 ? 1001u : 2002u;
            },
            getCurrentThreadId: () => 50,
            tryGetProcessName: _ => "notepad",
            bringWindowToTop: _ => true,
            setForegroundWindow: h =>
            {
                directAttempts++;
                if (directAttempts >= 2)
                {
                    foreground = h;
                    return true;
                }

                return false;
            },
            setActiveWindow: h => h,
            attachThreadInput: (a, b, attach) =>
            {
                attachEvents.Add($"{a}->{b}:{attach}");
                return true;
            });

        Assert.True(restored);
        Assert.Equal(["50->2002:True", "50->1001:True", "50->1001:False", "50->2002:False"], attachEvents);

        attachEvents.Clear();
        foreground = 0x2002;
        var attemptsBeforeThrow = 0;
        Assert.Throws<InvalidOperationException>(() =>
            WindowsWindowIdentity.RestoreAndFocusCore(
                window: 0x1001,
                isWindow: _ => true,
                isIconic: _ => false,
                showWindowRestore: _ => { },
                getForegroundWindow: () => foreground,
                getWindowThreadProcessId: (nint h, out uint pid) =>
                {
                    pid = h == 0x1001 ? 101u : 202u;
                    return h == 0x1001 ? 1001u : 2002u;
                },
                getCurrentThreadId: () => 50,
                tryGetProcessName: _ => "notepad",
                bringWindowToTop: _ => true,
                setForegroundWindow: _ =>
                {
                    attemptsBeforeThrow++;
                    if (attemptsBeforeThrow >= 2)
                    {
                        throw new InvalidOperationException("simulated failure");
                    }

                    return false;
                },
                setActiveWindow: h => h,
                attachThreadInput: (a, b, attach) =>
                {
                    attachEvents.Add($"{a}->{b}:{attach}");
                    return true;
                }));

        Assert.Equal(["50->2002:True", "50->1001:True", "50->1001:False", "50->2002:False"], attachEvents);
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("LockApp")]
    [InlineData("LogonUI")]
    [InlineData("CredentialUIBroker")]
    [InlineData("ScreenClippingHost")]
    public void RestoreAndFocusCoreNeverStealsProtectedSecurityScreen(string protectedProcess)
    {
        nint foreground = 0x9999;
        var attached = false;

        var result = WindowsWindowIdentity.RestoreAndFocusCore(
            window: 0x1001,
            isWindow: _ => true,
            isIconic: _ => false,
            showWindowRestore: _ => { },
            getForegroundWindow: () => foreground,
            getWindowThreadProcessId: (nint h, out uint pid) =>
            {
                pid = h == 0x1001 ? 101u : 999u;
                return h == 0x1001 ? 1001u : 9001u;
            },
            getCurrentThreadId: () => 50,
            tryGetProcessName: pid => pid == 999u ? protectedProcess : "notepad",
            bringWindowToTop: _ => true,
            setForegroundWindow: _ => false,
            setActiveWindow: h => h,
            attachThreadInput: (_, _, _) => { attached = true; return true; });

        Assert.False(result);
        Assert.False(attached);
    }

    [Fact]
    public void EvaluateFocusedElementAllowsSameWindowEditableReplacementFieldWhileBlockingPasswordAndNonTextControls()
    {
        var rule = WindowsPastePolicy.Resolve("msedge", "Chrome_WidgetWin_1", isControlEdit: true);
        var uiaTarget = new TargetWindowId(0x1111, 4242, FocusedElementId: "42.1.10", ProcessName: "msedge");

        var replacedEdit = WindowsTextTargetAdapter.EvaluateFocusedElement(
            uiaTarget,
            new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 4242,
                RuntimeId: "42.1.25",
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: true,
                ClassName: "Edit"),
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => rule);
        Assert.Null(replacedEdit.Failure);
        Assert.Equal("42.1.25", replacedEdit.EffectiveFocusId);

        var replacedDocument = WindowsTextTargetAdapter.EvaluateFocusedElement(
            uiaTarget,
            new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 4242,
                RuntimeId: "42.1.30",
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: false,
                ClassName: "RichEditD2DPT",
                IsControlDocument: true),
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => rule);
        Assert.Null(replacedDocument.Failure);
        Assert.Equal("42.1.30", replacedDocument.EffectiveFocusId);

        var switchedToPassword = WindowsTextTargetAdapter.EvaluateFocusedElement(
            uiaTarget,
            new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 4242,
                RuntimeId: "42.1.99",
                HasKeyboardFocus: true,
                IsPassword: true,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: true,
                ClassName: "Edit"),
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => rule);
        Assert.Equal(TextDeliveryRefusalReason.ProtectedField, switchedToPassword.Failure?.RefusalReason);

        var nonTextFocus = WindowsTextTargetAdapter.EvaluateFocusedElement(
            uiaTarget,
            new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 4242,
                RuntimeId: "42.1.77",
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: false,
                ClassName: "Button",
                IsControlDocument: false),
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => rule);
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, nonTextFocus.Failure?.RefusalReason);

        var win32Target = new TargetWindowId(0x1111, 4242, FocusedElementId: "win32:100", ProcessName: "notepad");
        var replacedWin32Edit = WindowsTextTargetAdapter.EvaluateFocusedElement(
            win32Target,
            new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 4242,
                RuntimeId: string.Empty,
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: true,
                ClassName: "Edit"),
            win32ChildFocusId: "win32:200",
            isExplicitHistoryPaste: false,
            (_, _) => WindowsPastePolicy.Resolve("notepad", "Notepad", "Edit", isControlEdit: true));
        Assert.Null(replacedWin32Edit.Failure);
        Assert.Equal("win32:200", replacedWin32Edit.EffectiveFocusId);

        var electronTarget = new TargetWindowId(0x2222, 7777, FocusedElementId: null, ProcessName: "chrome");
        var electronFocus = new WindowsTextTargetAdapter.FocusedElementFacts(
            ProcessId: 7777,
            RuntimeId: string.Empty,
            HasKeyboardFocus: true,
            IsPassword: false,
            IsEnabled: true,
            IsKeyboardFocusable: true,
            IsControlEdit: false,
            ClassName: "Chrome_RenderWidgetHostHWND");
        var electronRule = WindowsPastePolicy.Resolve("chrome", "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", isControlEdit: false);

        var startModeElectronRefused = WindowsTextTargetAdapter.EvaluateFocusedElement(
            electronTarget,
            electronFocus,
            win32ChildFocusId: null,
            isExplicitHistoryPaste: false,
            (_, _) => electronRule);
        Assert.Equal(TextDeliveryRefusalReason.AccessibilityUnavailable, startModeElectronRefused.Failure?.RefusalReason);

        var releaseModeElectronAllowed = WindowsTextTargetAdapter.EvaluateFocusedElement(
            electronTarget,
            electronFocus,
            win32ChildFocusId: null,
            isExplicitHistoryPaste: true,
            (_, _) => electronRule);
        Assert.Null(releaseModeElectronAllowed.Failure);
        Assert.True(releaseModeElectronAllowed.WindowFallbackOnly);

        var releaseModeElectronPasswordBlocked = WindowsTextTargetAdapter.EvaluateFocusedElement(
            electronTarget,
            electronFocus with { IsPassword = true },
            win32ChildFocusId: null,
            isExplicitHistoryPaste: true,
            (_, _) => electronRule);
        Assert.Equal(TextDeliveryRefusalReason.ProtectedField, releaseModeElectronPasswordBlocked.Failure?.RefusalReason);
    }

    [Fact]
    public async Task CaptureReleaseTargetHonorsFrozenSessionOptionsEvenWhenPreferencesChangeMidRecording()
    {
        var liveOptions = TextDeliveryOptions.Default with
        {
            TargetMode = PasteTargetMode.WhereCursorIsAtRelease,
            CopyInsteadOfPaste = false,
        };
        var currentTarget = new TargetWindowId(0x1111, 100, FocusedElementId: "win32:10", ProcessName: "notepad");
        var capture = new StubAudioCapture();
        var controller = new PushToTalkSessionController(
            capture,
            new DelegateTargetProvider(() => currentTarget),
            deliveryOptions: () => liveOptions,
            minimumHoldDuration: TimeSpan.Zero);

        var started = await controller.PressAsync();
        Assert.Equal(SessionTransitionKind.Started, started.Kind);

        // Simulate user flipping BOTH settings back while recording is still active.
        var releaseWindow = new TargetWindowId(0x2222, 200, FocusedElementId: "win32:20", ProcessName: "notepad");
        currentTarget = releaseWindow;
        liveOptions = TextDeliveryOptions.Default with
        {
            TargetMode = PasteTargetMode.WhereDictationStarted,
            CopyInsteadOfPaste = true,
        };

        var capturedAtRelease = controller.CaptureReleaseTarget();

        Assert.NotNull(capturedAtRelease);
        Assert.Equal(releaseWindow, capturedAtRelease!.Target);
        await controller.CancelAsync();
    }

    [Fact]
    public void EvaluateFocusedElementRejectsSameWindowReplacementInReleaseModeAcrossUiaAndWin32()
    {
        var rule = WindowsPastePolicy.Resolve("msedge", "Chrome_WidgetWin_1", isControlEdit: true);
        var uiaReleaseTarget = new TargetWindowId(0x1111, 4242, FocusedElementId: "42.1.10", ProcessName: "msedge");

        var movedUiaField = WindowsTextTargetAdapter.EvaluateFocusedElement(
            uiaReleaseTarget,
            new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 4242,
                RuntimeId: "42.1.25",
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: true,
                ClassName: "Edit"),
            win32ChildFocusId: null,
            isExplicitHistoryPaste: true,
            (_, _) => rule,
            allowSameWindowEditableReplacement: false);
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, movedUiaField.Failure?.RefusalReason);

        var win32ReleaseTarget = new TargetWindowId(0x1111, 4242, FocusedElementId: "win32:100", ProcessName: "notepad");
        var movedWin32Field = WindowsTextTargetAdapter.EvaluateFocusedElement(
            win32ReleaseTarget,
            new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 4242,
                RuntimeId: string.Empty,
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: true,
                ClassName: "Edit"),
            win32ChildFocusId: "win32:200",
            isExplicitHistoryPaste: true,
            (_, _) => WindowsPastePolicy.Resolve("notepad", "Notepad", "Edit", isControlEdit: true),
            allowSameWindowEditableReplacement: false);
        Assert.Equal(TextDeliveryRefusalReason.TargetChanged, movedWin32Field.Failure?.RefusalReason);

        var exactWin32Field = WindowsTextTargetAdapter.EvaluateFocusedElement(
            win32ReleaseTarget,
            new WindowsTextTargetAdapter.FocusedElementFacts(
                ProcessId: 4242,
                RuntimeId: string.Empty,
                HasKeyboardFocus: true,
                IsPassword: false,
                IsEnabled: true,
                IsKeyboardFocusable: true,
                IsControlEdit: true,
                ClassName: "Edit"),
            win32ChildFocusId: "win32:100",
            isExplicitHistoryPaste: true,
            (_, _) => WindowsPastePolicy.Resolve("notepad", "Notepad", "Edit", isControlEdit: true),
            allowSameWindowEditableReplacement: false);
        Assert.Null(exactWin32Field.Failure);
        Assert.Equal("win32:100", exactWin32Field.EffectiveFocusId);
    }

    [Fact]
    public async Task CaptureReleaseTargetHonorsFrozenPressOptionsDuringPendingAudioStartup()
    {
        var liveOptions = TextDeliveryOptions.Default with
        {
            TargetMode = PasteTargetMode.WhereCursorIsAtRelease,
            CopyInsteadOfPaste = false,
        };
        var releaseWindow = new TargetWindowId(0x2222, 200, FocusedElementId: "win32:20", ProcessName: "notepad");
        var currentTarget = new TargetWindowId(0x1111, 100, FocusedElementId: "win32:10", ProcessName: "notepad");
        var capture = new BlockingStartAudioCapture();
        var controller = new PushToTalkSessionController(
            capture,
            new DelegateTargetProvider(() => currentTarget),
            deliveryOptions: () => liveOptions,
            minimumHoldDuration: TimeSpan.Zero);

        var pressTask = controller.PressAsync();
        await capture.StartEntered.Task;
        Assert.Null(controller.CurrentSession);

        currentTarget = releaseWindow;
        liveOptions = TextDeliveryOptions.Default with
        {
            TargetMode = PasteTargetMode.WhereDictationStarted,
            CopyInsteadOfPaste = true,
        };

        var capturedDuringStartup = controller.CaptureReleaseTarget();
        Assert.NotNull(capturedDuringStartup);
        Assert.Equal(releaseWindow, capturedDuringStartup!.Target);

        capture.ReleaseStart.TrySetResult(new AudioOperationResult(Succeeded: true));
        var started = await pressTask;
        Assert.Equal(SessionTransitionKind.Started, started.Kind);
        await controller.CancelAsync();
    }

    [Fact]
    public async Task CoordinatorCapturesReleaseTargetAtCancelledAdmissionForEscapeRecovery()
    {
        var liveOptions = TextDeliveryOptions.Default with
        {
            TargetMode = PasteTargetMode.WhereCursorIsAtRelease,
            CopyInsteadOfPaste = false,
        };
        var currentTarget = new TargetWindowId(0x1111, 100, FocusedElementId: "win32:10", ProcessName: "notepad");
        var escapeWindow = new TargetWindowId(0x3333, 300, FocusedElementId: "win32:30", ProcessName: "notepad");
        var capture = new BlockingStartAudioCapture();
        var controller = new PushToTalkSessionController(
            capture,
            new DelegateTargetProvider(() => currentTarget),
            deliveryOptions: () => liveOptions,
            minimumHoldDuration: TimeSpan.Zero);
        var executor = new CapturingSessionExecutor(controller);
        await using var coordinator = new DictationSessionCoordinator(
            executor,
            controller.CaptureStartContext,
            captureReleaseTargetWithOptions: controller.CaptureReleaseTarget);

        var pressTask = coordinator.SubmitAsync(PushToTalkSignal.Pressed);
        await capture.StartEntered.Task;

        currentTarget = escapeWindow;
        liveOptions = TextDeliveryOptions.Default with
        {
            TargetMode = PasteTargetMode.WhereDictationStarted,
            CopyInsteadOfPaste = true,
        };

        var cancelTask = coordinator.SubmitAsync(PushToTalkSignal.Cancelled);
        capture.ReleaseStart.TrySetResult(new AudioOperationResult(Succeeded: true));
        await pressTask;
        await cancelTask;

        Assert.NotNull(executor.LastCancelledReleaseTarget);
        Assert.Equal(escapeWindow, executor.LastCancelledReleaseTarget!.Target);
    }

    private sealed class BlockingStartAudioCapture : IAudioCapture
    {
        public TaskCompletionSource<bool> StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<AudioOperationResult> ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }

        public async Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            StartEntered.TrySetResult(true);
            var result = await ReleaseStart.Task.ConfigureAwait(false);
            IsCapturing = result.Succeeded;
            return result;
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(new DictationSessionId(Guid.NewGuid()), ReadOnlyMemory<float>.Empty, 16_000, 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingSessionExecutor(PushToTalkSessionController controller) : ISessionCommandExecutor
    {
        public ReleaseTargetContext? LastCancelledReleaseTarget { get; private set; }

        public bool HasRetainedCleanup => false;

        public Task<bool> JoinRetainedCleanupAsync() => Task.FromResult(true);

        public async Task<SessionCommandResult> ExecuteAsync(SessionCommand command, CancellationToken stoppingToken)
        {
            if (command.Signal == PushToTalkSignal.Pressed && command.StartContext is { } startContext)
            {
                var started = await controller.PressAsync(startContext, stoppingToken).ConfigureAwait(false);
                return new SessionCommandResult(SessionCommandDisposition.Applied, started.Session);
            }

            if (command.Signal == PushToTalkSignal.Cancelled)
            {
                LastCancelledReleaseTarget = command.ReleaseTarget;
                await controller.CancelAsync(stoppingToken).ConfigureAwait(false);
                return new SessionCommandResult(SessionCommandDisposition.Applied);
            }

            return new SessionCommandResult(SessionCommandDisposition.Ignored);
        }

        public Task ExpireAsync(SessionCommand command) => Task.CompletedTask;

        public bool TryCancelProcessingForEscape() => false;
    }

    private sealed class DelegateTargetProvider(Func<TargetWindowId?> read) : IForegroundTargetProvider
    {
        public TargetWindowId? CaptureForegroundTarget() => read();
    }

    private sealed class StubAudioCapture : IAudioCapture
    {
        public event EventHandler<AudioLevel>? LevelChanged
        {
            add { }
            remove { }
        }

        public bool IsCapturing { get; private set; }

        public Task<AudioOperationResult> StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            IsCapturing = true;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new CapturedAudio(new DictationSessionId(Guid.NewGuid()), ReadOnlyMemory<float>.Empty, 16_000, 1));
        }

        public Task<AudioOperationResult> CancelAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.FromResult(new AudioOperationResult(Succeeded: true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static GeneralSettingsInput CreateGeneralInput(bool returnToStartWindow) => new(
        RecordingShortcut: "F8",
        CancelShortcut: "Escape",
        QuickAddShortcut: "Ctrl+Alt+W",
        FinalEngineIndex: 0,
        WordCorrection: true,
        FillerRemoval: false,
        EmojiFormatter: true,
        SpokenPunctuation: false,
        WhisperLanguage: "auto",
        EnglishSpellingIndex: 0,
        RecordingModeIndex: 0,
        EscapeRecovery: false,
        AutoStop: false,
        AutoStopSeconds: 3.0,
        PolishProviderIndex: 0,
        PolishModel: string.Empty,
        OllamaEndpoint: string.Empty,
        HistoryEnabled: true,
        HistoryRetentionDays: 30,
        ThemeIndex: 0,
        LivePreview: false,
        OverlayPositionIndex: 0,
        LevelRailPill: false,
        PlayRecordingSounds: false,
        RecordingSoundPairing: RecordingSoundPairing.WhisperTick,
        CopyInsteadOfPaste: false,
        LocalDiagnostics: true,
        DiagnosticRetentionDays: 30,
        ShareTelemetry: false,
        TelemetryAvailable: true,
        MicrophoneId: null,
        PasteLastShortcut: string.Empty,
        CopyLastShortcut: string.Empty,
        ReturnToStartWindow: returnToStartWindow);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "EnviousWispr.Windows.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void CommitPathPassesCapturedFocusClassNameToTargetPolicyAndCandidateDeleteFlag()
    {
        var root = FindRepositoryRoot();
        var adapterSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Production",
            "EnviousWispr.Services",
            "Input",
            "WindowsTextTargetAdapter.cs"));

        Assert.Contains("elementClassName: focus?.ClassName", adapterSource, StringComparison.Ordinal);
        Assert.Contains("WindowsTextDeliveryPolicy.ShouldCheckCaretTextUnchanged", adapterSource, StringComparison.Ordinal);
        Assert.Contains("checkCaretTextUnchanged: verifyCaretText", adapterSource, StringComparison.Ordinal);
        Assert.Contains("PreflightInput(", adapterSource, StringComparison.Ordinal);
        Assert.Contains("verifyCaretText)),", adapterSource, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlBarDetectionUsesTwoFactorDetectorAndRemovesSubstringHacks()
    {
        var root = FindRepositoryRoot();
        var adapterSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Production",
            "EnviousWispr.Services",
            "Input",
            "WindowsTextTargetAdapter.cs"));

        Assert.Contains("WindowsBrowserAddressBarDetector.IsChromiumAddressBar", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("automationId.Contains(\"address\"", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("automationId.Contains(\"urlbar\"", adapterSource, StringComparison.Ordinal);
    }
}
