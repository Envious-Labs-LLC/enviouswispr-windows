using System.Text.Json;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

public sealed class OnboardingFlowTests
{
    private static OnboardingFlow CreateFlowAtStep(
        OnboardingStep targetStep,
        SpeechEngineReadiness engine = SpeechEngineReadiness.Ready,
        SpeechModelDelivery model = SpeechModelDelivery.Installed,
        SpeechModelDelivery polishModel = SpeechModelDelivery.Installed,
        MicrophoneConsent mic = MicrophoneConsent.Allowed,
        DictationRecordingMode mode = DictationRecordingMode.PushToTalk)
    {
        var flow = new OnboardingFlow();
        flow.ReportEngine(engine);
        flow.ReportModel(model);
        flow.ReportPolishModel(polishModel);
        flow.ReportMicrophone(mic);
        flow.ReportRecordingKey("Ctrl+Win", mode);
        flow.Begin();
        flow.ReportEngine(engine);
        flow.ReportModel(model);
        flow.ReportPolishModel(polishModel);
        flow.ReportMicrophone(mic);
        flow.ReportRecordingKey("Ctrl+Win", mode);

        if (targetStep == OnboardingStep.Welcome)
        {
            return flow;
        }

        flow.AdvanceFromWelcome();
        if (targetStep == OnboardingStep.Language)
        {
            return flow;
        }

        flow.ContinueFromLanguage();
        if (targetStep == OnboardingStep.SpeechPick)
        {
            return flow;
        }

        if (targetStep == OnboardingStep.SpeechCompare)
        {
            flow.OpenSpeechCompare();
            return flow;
        }

        flow.ContinueFromSpeechPick();
        if (targetStep == OnboardingStep.PolishIntro)
        {
            return flow;
        }

        flow.ContinueFromPolishIntro();
        if (targetStep == OnboardingStep.PolishPick)
        {
            return flow;
        }

        if (targetStep == OnboardingStep.PolishSeeAll)
        {
            flow.OpenPolishSeeAll();
            return flow;
        }

        if (targetStep == OnboardingStep.Downloads)
        {
            flow.ContinueFromPolishPick(downloadsNeededOverride: true);
            return flow;
        }

        flow.ContinueFromPolishPick(downloadsNeededOverride: true);
        flow.ContinueFromDownloads();
        if (targetStep == OnboardingStep.Permissions)
        {
            return flow;
        }

        flow.ContinueFromPermissions(MicrophoneConsent.Allowed);
        if (targetStep == OnboardingStep.Key)
        {
            return flow;
        }

        if (targetStep == OnboardingStep.GettingReady)
        {
            flow.BeginLegacyWarming();
            return flow;
        }

        flow.ContinueFromKey();
        if (targetStep == OnboardingStep.Practice)
        {
            return flow;
        }

        flow.SkipPractice();
        if (targetStep == OnboardingStep.Closing)
        {
            return flow;
        }

        flow.CompleteClosing();
        return flow;
    }

    [Fact]
    public void ForwardNavigationTraversesAllStepsAndBackStackRewindsCleanly()
    {
        var flow = new OnboardingFlow();
        flow.ReportMicrophone(MicrophoneConsent.Allowed);
        flow.ReportRecordingKey("F8", DictationRecordingMode.Toggle);
        flow.ReportEngine(SpeechEngineReadiness.Preparing);
        flow.ReportModel(SpeechModelDelivery.Missing);
        flow.ReportPolishModel(SpeechModelDelivery.Missing);
        flow.Begin();

        Assert.Equal(OnboardingStep.Welcome, flow.Step);
        Assert.False(flow.CanGoBack);

        flow.AdvanceFromWelcome();
        Assert.Equal(OnboardingStep.Language, flow.Step);
        flow.SelectLanguage("fr");
        Assert.Equal("fr", flow.Choices.LanguageCode);

        flow.ContinueFromLanguage();
        Assert.Equal(OnboardingStep.SpeechPick, flow.Step);
        flow.OpenSpeechCompare();
        Assert.Equal(OnboardingStep.SpeechCompare, flow.Step);
        Assert.True(flow.GoBack());
        Assert.Equal(OnboardingStep.SpeechPick, flow.Step);

        flow.OpenSpeechCompare();
        flow.SelectSpeech(FinalAsrEngine.Parakeet, ParakeetModelIds.Final);
        flow.ContinueFromSpeechPick();
        Assert.Equal(OnboardingStep.PolishIntro, flow.Step);

        flow.ContinueFromPolishIntro();
        Assert.Equal(OnboardingStep.PolishPick, flow.Step);
        flow.OpenPolishSeeAll();
        Assert.Equal(OnboardingStep.PolishSeeAll, flow.Step);
        flow.SelectPolish(OnboardingPolishChoiceKind.EgOne, EgOneModelCatalog.MiniId);
        flow.ClosePolishSeeAll();
        Assert.Equal(OnboardingStep.PolishPick, flow.Step);

        flow.ContinueFromPolishPick();
        Assert.Equal(OnboardingStep.Downloads, flow.Step);

        flow.ContinueFromDownloads();
        Assert.Equal(OnboardingStep.Permissions, flow.Step);
        Assert.False(flow.ContinueFromPermissions(MicrophoneConsent.Blocked));
        Assert.Equal(OnboardingStep.Permissions, flow.Step);
        Assert.True(flow.ContinueFromPermissions(MicrophoneConsent.Allowed));
        Assert.Equal(OnboardingStep.Key, flow.Step);

        flow.MarkKeyRehearsalCompleted();
        Assert.True(flow.KeyRehearsalCompleted);
        flow.ContinueFromKey();
        Assert.Equal(OnboardingStep.GettingReady, flow.Step);

        // Once speech + polish + mic become ready, Gate advances cleanly to Practice.
        flow.ReportEngine(SpeechEngineReadiness.Ready);
        flow.ReportModel(SpeechModelDelivery.Installed);
        flow.ReportPolishModel(SpeechModelDelivery.Installed);
        Assert.True(flow.TryAdvanceFromGate());
        Assert.Equal(OnboardingStep.Practice, flow.Step);

        // Going back from Practice skips stale GettingReady when gate is ReadyAutoSkip.
        Assert.True(flow.GoBack());
        Assert.Equal(OnboardingStep.Key, flow.Step);

        // And stepping forward from Key auto-skips GettingReady straight to Practice.
        flow.ContinueFromKey();
        Assert.Equal(OnboardingStep.Practice, flow.Step);

        // Walk back all the way to Welcome.
        var rewound = new List<OnboardingStep>();
        while (flow.GoBack())
        {
            rewound.Add(flow.Step);
        }

        Assert.Equal(
            [
                OnboardingStep.Key,
                OnboardingStep.Permissions,
                OnboardingStep.Downloads,
                OnboardingStep.PolishPick,
                OnboardingStep.PolishIntro,
                OnboardingStep.SpeechPick,
                OnboardingStep.Language,
                OnboardingStep.Welcome,
            ],
            rewound);
        Assert.False(flow.CanGoBack);
    }

    [Fact]
    public void GettingReadyGateSupportsAutoSkipSkipAheadAndContinueWithoutAiPolishRecovery()
    {
        // Case 1: Speech + EG-1 + Mic all ready -> Key auto-skips GettingReady to Practice.
        var readyFlow = CreateFlowAtStep(
            OnboardingStep.Key,
            engine: SpeechEngineReadiness.Ready,
            model: SpeechModelDelivery.Installed,
            polishModel: SpeechModelDelivery.Installed,
            mic: MicrophoneConsent.Allowed);
        readyFlow.ContinueFromKey();
        Assert.Equal(OnboardingStep.Practice, readyFlow.Step);

        // Case 2: Speech ready, EG-1 still downloading -> WaitingWithPolishDownloading -> "Skip ahead" advances to Practice preserving EgOne.
        var waitingFlow = CreateFlowAtStep(
            OnboardingStep.Key,
            engine: SpeechEngineReadiness.Ready,
            model: SpeechModelDelivery.Installed,
            polishModel: SpeechModelDelivery.Downloading,
            mic: MicrophoneConsent.Allowed);
        waitingFlow.ContinueFromKey();
        Assert.Equal(OnboardingStep.GettingReady, waitingFlow.Step);
        Assert.Equal(OnboardingGateDecision.WaitingWithPolishDownloading, waitingFlow.EvaluateGate());
        Assert.True(waitingFlow.CanSkipAheadDuringGate);
        Assert.True(waitingFlow.SkipAheadWithoutPolish());
        Assert.Equal(OnboardingStep.Practice, waitingFlow.Step);
        Assert.Equal(OnboardingPolishChoiceKind.EgOne, waitingFlow.Choices.PolishChoice);

        // Case 3: Speech ready, EG-1 failed or cancelled -> "Continue without AI polish" clears polish choice to None and advances.
        foreach (var stoppedStage in new[] { SpeechModelDelivery.Failed, SpeechModelDelivery.Cancelled })
        {
            waitingFlow = CreateFlowAtStep(
                OnboardingStep.Key,
                engine: SpeechEngineReadiness.Ready,
                model: SpeechModelDelivery.Installed,
                polishModel: stoppedStage,
                mic: MicrophoneConsent.Allowed);
            waitingFlow.ContinueFromKey();
            Assert.Equal(OnboardingStep.GettingReady, waitingFlow.Step);
            Assert.Equal(OnboardingGateDecision.PolishFailedContinueWithout, waitingFlow.EvaluateGate());
            Assert.False(waitingFlow.CanSkipAheadDuringGate);
            Assert.True(waitingFlow.CanContinueWithoutAiPolishOnGate);
            Assert.True(waitingFlow.ContinueWithoutAiPolish());
            Assert.Equal(OnboardingStep.Practice, waitingFlow.Step);
            Assert.Equal(OnboardingPolishChoiceKind.None, waitingFlow.Choices.PolishChoice);
            Assert.Null(waitingFlow.Choices.EgOneModelId);
        }
    }

    [Fact]
    public async Task CheckpointStoreSavesAtomicallyRestoresCleanlyClearsAndNeverStoresApiKeys()
    {
        var expectedDefaultSuffix = Path.Combine("Envious Labs", "EnviousWispr", "onboarding-checkpoint.json");
        Assert.EndsWith(expectedDefaultSuffix, OnboardingCheckpointStore.DefaultFilePath(), StringComparison.Ordinal);

        var directory = Directory.CreateTempSubdirectory("ew-checkpoint-");
        try
        {
            var path = Path.Combine(directory.FullName, "onboarding-checkpoint.json");
            using var store = new OnboardingCheckpointStore(path);

            Assert.Equal(OnboardingCheckpointLoadStatus.Missing, (await store.LoadAsync()).Status);

            var flow = CreateFlowAtStep(OnboardingStep.PolishPick);
            flow.SelectLanguage("ja");
            flow.SelectSpeech(FinalAsrEngine.Whisper, WhisperModelIds.Final);
            flow.SelectPolish(OnboardingPolishChoiceKind.OpenAI, cloudModelId: "gpt-4o-mini");

            var checkpoint = flow.ToCheckpoint(new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero));
            Assert.True(await store.SaveAsync(checkpoint));

            var rawJson = await File.ReadAllTextAsync(path);
            string[] forbiddenSecretKeys = ["apiKey", "api_key", "secret", "password", "token", "bearer", "sk-"];
            Assert.All(forbiddenSecretKeys, key =>
                Assert.DoesNotContain(key, rawJson, StringComparison.OrdinalIgnoreCase));

            var reloaded = await store.LoadAsync();
            Assert.Equal(OnboardingCheckpointLoadStatus.Loaded, reloaded.Status);
            Assert.NotNull(reloaded.Checkpoint);

            var resumedFlow = new OnboardingFlow();
            var restoreDecision = resumedFlow.Restore(reloaded.Checkpoint, hasCompletedOnboarding: false);
            Assert.True(restoreDecision.ShouldResume);
            Assert.Equal(OnboardingStep.PolishPick, resumedFlow.Step);
            Assert.Equal("ja", resumedFlow.Choices.LanguageCode);
            Assert.Equal(FinalAsrEngine.Whisper, resumedFlow.Choices.SpeechEngine);
            Assert.Equal(OnboardingPolishChoiceKind.OpenAI, resumedFlow.Choices.PolishChoice);
            Assert.Equal("gpt-4o-mini", resumedFlow.Choices.CloudModelId);

            // Overlay sub-steps collapse to their owning picker step on restore; worked practice jumps to Closing; Done never resumes.
            var legacyParakeetCheckpoint = checkpoint with
            {
                Choices = checkpoint.Choices with
                {
                    SpeechEngine = FinalAsrEngine.Parakeet,
                    SpeechModelId = ParakeetModelIds.LegacyV3,
                },
            };
            var normalizedParakeetDecision = OnboardingRestore.Resolve(
                legacyParakeetCheckpoint,
                hasCompletedOnboarding: false);
            Assert.Equal(ParakeetModelIds.Final, normalizedParakeetDecision.Choices.SpeechModelId);

            var compareDecision = OnboardingRestore.Resolve(
                checkpoint with { Step = OnboardingStep.SpeechCompare },
                hasCompletedOnboarding: false);
            Assert.Equal(OnboardingStep.SpeechPick, compareDecision.Step);

            var workedDecision = OnboardingRestore.Resolve(
                checkpoint with { Step = OnboardingStep.Practice, PracticeSucceeded = true },
                hasCompletedOnboarding: false);
            Assert.Equal(OnboardingStep.Closing, workedDecision.Step);

            Assert.False(OnboardingRestore.Resolve(
                checkpoint with { Step = OnboardingStep.Done },
                hasCompletedOnboarding: false).ShouldResume);
            Assert.False(OnboardingRestore.Resolve(
                checkpoint,
                hasCompletedOnboarding: true).ShouldResume);

            // Corrupt checkpoint returns Corrupt safely without throwing.
            await File.WriteAllTextAsync(path, "{invalid json");
            Assert.Equal(OnboardingCheckpointLoadStatus.Corrupt, (await store.LoadAsync()).Status);

            Assert.True(await store.ClearAsync());
            Assert.Equal(OnboardingCheckpointLoadStatus.Missing, (await store.LoadAsync()).Status);
        }
        finally
        {
            if (directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public void ClosingWalkthroughMatchesSevenBeatsAndAdvancesToDone()
    {
        var flow = CreateFlowAtStep(OnboardingStep.Closing);

        Assert.Equal(7, OnboardingFlow.ClosingBeatCount);
        Assert.Equal(7, OnboardingCopy.ClosingBeats.Count);

        double[] expectedDurations = [3.5, 4.5, 7.5, 6.5, 6.0, 4.5, 3.0];
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(i, flow.ClosingBeatIndex);
            Assert.Equal(expectedDurations[i], flow.CurrentClosingBeat.DurationSeconds);
            Assert.False(flow.AdvanceClosingBeat());
        }

        Assert.Equal(6, flow.ClosingBeatIndex);
        Assert.Equal(3.0, flow.CurrentClosingBeat.DurationSeconds);
        Assert.True(flow.AdvanceClosingBeat());
        Assert.Equal(OnboardingStep.Done, flow.Step);
        Assert.Empty(flow.BackStack);

        var jumpFlow = CreateFlowAtStep(OnboardingStep.Closing);
        jumpFlow.SetClosingBeat(4);
        Assert.Equal(4, jumpFlow.ClosingBeatIndex);
        jumpFlow.SetClosingBeat(99);
        Assert.Equal(OnboardingFlow.ClosingBeatCount - 1, jumpFlow.ClosingBeatIndex);
        jumpFlow.RestartClosing();
        Assert.Equal(0, jumpFlow.ClosingBeatIndex);
        Assert.Equal(OnboardingStep.Closing, jumpFlow.Step);

        var summary = OnboardingCopy.FormatClosingSummary(
            OnboardingCopy.ClosingBeats[0],
            "Parakeet",
            "EG-1 Mini",
            "Ctrl+Win");
        Assert.Contains("Parakeet", summary, StringComparison.Ordinal);
        Assert.Contains("EG-1 Mini", summary, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Win", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PracticeFlowHandlesWorkedSilenceMissedBoxAndFailureAndRecovery()
    {
        var flow = CreateFlowAtStep(OnboardingStep.Practice);

        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));
        Assert.True(flow.MayFinish(OnboardingExit.SkipPractice));

        // Take in flight locks Skip and Finish.
        flow.TakeStarted("");
        Assert.Equal(OnboardingPracticeState.Listening, flow.PracticeState);
        Assert.False(flow.MayFinish(OnboardingExit.SkipPractice));
        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));

        // Missed box -> Recover by typing/pasting into the box -> Worked.
        flow.TakeDelivered(OnboardingTakeDelivery.Clipboard);
        flow.TakeEnded("");
        Assert.Equal(OnboardingPracticeState.MissedTheBox, flow.PracticeState);
        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));

        flow.PracticeTextChanged("hello world");
        Assert.Equal(OnboardingPracticeState.Worked, flow.PracticeState);
        Assert.Equal(OnboardingPracticeOutcome.Worked, flow.PracticeOutcome);
        Assert.True(flow.MayFinish(OnboardingExit.FinishSetup));

        // A silence after a worked run keeps PracticeSucceeded and finishes cleanly.
        flow.TakeStarted("hello world");
        flow.TakeEnded("hello world");
        Assert.Equal(OnboardingPracticeState.SaidNothing, flow.PracticeState);
        Assert.True(flow.PracticeSucceeded);
        Assert.True(flow.MayFinish(OnboardingExit.FinishSetup));

        Assert.True(flow.ContinueFromPractice());
        Assert.Equal(OnboardingStep.Closing, flow.Step);
    }

    [Fact]
    public void FailureAndCannotHearAndSkipPracticeSemanticsHold()
    {
        var flow = CreateFlowAtStep(OnboardingStep.Practice);

        flow.TakeStarted("");
        flow.TakeDelivered(OnboardingTakeDelivery.Clipboard);
        flow.TakeFailed();
        flow.TakeEnded("");
        Assert.Equal(OnboardingPracticeState.SomethingBroke, flow.PracticeState);

        flow.ReportMicrophone(MicrophoneConsent.Blocked);
        Assert.Equal(OnboardingPracticeState.CannotHear, flow.PracticeState);
        Assert.True(flow.MayFinish(OnboardingExit.SkipPractice));

        flow.ReportMicrophone(MicrophoneConsent.Allowed);
        Assert.Equal(OnboardingPracticeState.Waiting, flow.PracticeState);

        Assert.True(flow.SkipPractice());
        Assert.Equal(OnboardingPracticeOutcome.Skipped, flow.PracticeOutcome);
        Assert.Equal(OnboardingStep.Closing, flow.Step);
    }

    [Fact]
    public void KeyLineReportsEitherShortcutOrUnavailableProblem()
    {
        var flow = new OnboardingFlow();
        Assert.Equal("Getting your keybind ready\u2026", OnboardingCopy.KeyLine(flow));

        flow.ReportRecordingKeyUnavailable("The recording shortcut is already in use.");
        Assert.Equal("The recording shortcut is already in use.", OnboardingCopy.KeyLine(flow));

        flow.ReportRecordingKey("Ctrl+Win", DictationRecordingMode.PushToTalk);
        Assert.Equal("Your key: Ctrl+Win", OnboardingCopy.KeyLine(flow));
        Assert.True(flow.RecordingKeyHasGestures);

        flow.ReportRecordingKey("F8", DictationRecordingMode.Toggle);
        Assert.False(flow.RecordingKeyHasGestures);
    }

    [Fact]
    public void UnknownMicrophoneConsentPassesSetupGateAndListeningSurvivesBlockedMicUpdate()
    {
        var flow = CreateFlowAtStep(
            OnboardingStep.Permissions,
            mic: MicrophoneConsent.Unknown);
        Assert.True(flow.MicrophoneAllowed);
        Assert.True(flow.ContinueFromPermissions(MicrophoneConsent.Unknown));
        Assert.Equal(OnboardingStep.Key, flow.Step);

        flow.ContinueFromKey();
        Assert.Equal(OnboardingStep.Practice, flow.Step);

        flow.TakeStarted("");
        Assert.Equal(OnboardingPracticeState.Listening, flow.PracticeState);
        flow.ReportMicrophone(MicrophoneConsent.Blocked);
        Assert.Equal(OnboardingPracticeState.Listening, flow.PracticeState);

        flow.TakeEnded("");
        Assert.Equal(OnboardingPracticeState.SaidNothing, flow.PracticeState);
    }

    [Fact]
    public void WizardStepNumbersMatchSixMilestonesCleanly()
    {
        Assert.Equal(6, OnboardingFlow.PrimaryStepCount);

        foreach (var step in Enum.GetValues<OnboardingStep>())
        {
            var number = OnboardingFlow.PrimaryStepNumberFor(step);
            Assert.InRange(number, 1, OnboardingFlow.PrimaryStepCount);
        }

        // Hidden-strip screens
        Assert.False(OnboardingFlow.ShowsProgressStrip(OnboardingStep.Welcome));
        Assert.False(OnboardingFlow.ShowsProgressStrip(OnboardingStep.Closing));
        Assert.False(OnboardingFlow.ShowsProgressStrip(OnboardingStep.Done));
        Assert.Null(OnboardingFlow.MilestoneIndexFor(OnboardingStep.Welcome));
        Assert.Null(OnboardingFlow.MilestoneIndexFor(OnboardingStep.Closing));
        Assert.Null(OnboardingFlow.MilestoneIndexFor(OnboardingStep.Done));

        // 6 milestone segments matching macOS Setup
        Assert.Equal(0, OnboardingFlow.MilestoneIndexFor(OnboardingStep.Language));
        Assert.Equal(1, OnboardingFlow.MilestoneIndexFor(OnboardingStep.SpeechPick));
        Assert.Equal(1, OnboardingFlow.MilestoneIndexFor(OnboardingStep.SpeechCompare));
        Assert.Equal(2, OnboardingFlow.MilestoneIndexFor(OnboardingStep.PolishIntro));
        Assert.Equal(2, OnboardingFlow.MilestoneIndexFor(OnboardingStep.PolishPick));
        Assert.Equal(2, OnboardingFlow.MilestoneIndexFor(OnboardingStep.PolishSeeAll));
        Assert.Equal(2, OnboardingFlow.MilestoneIndexFor(OnboardingStep.Downloads));
        Assert.Equal(3, OnboardingFlow.MilestoneIndexFor(OnboardingStep.Permissions));
        Assert.Equal(4, OnboardingFlow.MilestoneIndexFor(OnboardingStep.Key));
        Assert.Equal(5, OnboardingFlow.MilestoneIndexFor(OnboardingStep.GettingReady));
        Assert.Equal(5, OnboardingFlow.MilestoneIndexFor(OnboardingStep.Practice));

        Assert.Equal("Step 1 of 6: Language", OnboardingCopy.MilestoneLabel(OnboardingStep.Language));
        Assert.Equal("Step 2 of 6: Speech model", OnboardingCopy.MilestoneLabel(OnboardingStep.SpeechCompare));
        Assert.Equal("Step 3 of 6: AI polish", OnboardingCopy.MilestoneLabel(OnboardingStep.Downloads));
        Assert.Equal("Step 4 of 6: Permissions", OnboardingCopy.MilestoneLabel(OnboardingStep.Permissions));
        Assert.Equal("Step 5 of 6: Your key", OnboardingCopy.MilestoneLabel(OnboardingStep.Key));
        Assert.Equal("Step 6 of 6: First dictation", OnboardingCopy.MilestoneLabel(OnboardingStep.Practice));
    }

    [Fact]
    public void RevampedWindowsNativeCopyContractsHold()
    {
        Assert.Equal(4, OnboardingCopy.WelcomeValueRows.Count);
        Assert.Contains(
            OnboardingCopy.WelcomeValueRows,
            row => row.Body == "Runs locally on GPU or CPU. Works offline once models download.");

        Assert.Equal("One quick permission", OnboardingCopy.PermissionsHeadline(microphoneAllowed: false));
        Assert.Equal("Microphone is ready", OnboardingCopy.PermissionsHeadline(microphoneAllowed: true));
        Assert.DoesNotContain("Accessibility", OnboardingCopy.PermissionsCardBody, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("Um, I think we should, uh, meet tomorrow morning.", OnboardingCopy.PracticePrompt);
        Assert.Equal(
            "Dictation worked",
            OnboardingCopy.PracticeHeadline(OnboardingPracticeState.Worked, succeeded: true, polishSkippedOrFailed: true));
        Assert.Equal("Basic cleanup for now", OnboardingCopy.PracticeBasicCleanupTitle);
        Assert.Equal(
            "AI polish keeps downloading while EnviousWispr is open and turns on by itself when it is done.",
            OnboardingCopy.GatePolishDownloadingNotice);
        Assert.Contains(
            "AI polish keeps downloading while EnviousWispr is open and turns on by itself when it is done.",
            OnboardingCopy.GateSlowPolishBody,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OnboardingPracticeState.Waiting, false, "Time for your first dictation!")]
    [InlineData(OnboardingPracticeState.Waiting, true, "That is it. You are set.")]
    [InlineData(OnboardingPracticeState.Listening, false, "Listening\u2026")]
    [InlineData(OnboardingPracticeState.Worked, true, "That is it. You are set.")]
    [InlineData(OnboardingPracticeState.SaidNothing, false, "All quiet")]
    [InlineData(OnboardingPracticeState.SomethingBroke, false, "That did not work")]
    [InlineData(OnboardingPracticeState.MissedTheBox, false, "Click the box first")]
    [InlineData(OnboardingPracticeState.CannotHear, false, "We cannot hear you")]
    public void EveryPracticeStateHasExpectedHeadline(OnboardingPracticeState state, bool succeeded, string expected) =>
        Assert.Equal(expected, OnboardingCopy.PracticeHeadline(state, succeeded));

    [Fact]
    public void NoSetupSentenceCarriesADash()
    {
        List<string> sentences =
        [
            OnboardingCopy.WelcomeTitle,
            OnboardingCopy.WelcomeSubtitle,
            OnboardingCopy.WelcomePrimaryCta,
            OnboardingCopy.WelcomeFooterNote,
            OnboardingCopy.PermissionsTitle,
            OnboardingCopy.PermissionsReadyTitle,
            OnboardingCopy.PermissionsSubtitle,
            OnboardingCopy.PermissionsCardTitle,
            OnboardingCopy.PermissionsCardBody,
            OnboardingCopy.SetupPaused,
            OnboardingCopy.TrySetupAgain,
            OnboardingCopy.Retry,
            OnboardingCopy.Cancelling,
            OnboardingCopy.OneTimeSetup,
            OnboardingCopy.EngineUnavailable,
            OnboardingCopy.StartingEngine,
            OnboardingCopy.WarmingTitle,
            OnboardingCopy.WarmingFailedTitle,
            OnboardingCopy.WarmingSubtitle,
            OnboardingCopy.WarmingFailedSubtitle,
            OnboardingCopy.WarmingFailedMessage,
            OnboardingCopy.WarmingCaption,
            OnboardingCopy.WarmingSkip,
            OnboardingCopy.WarmingSkipAfterFailure,
            OnboardingCopy.GateTitle,
            OnboardingCopy.GateSkipAhead,
            OnboardingCopy.GateKeepWaiting,
            OnboardingCopy.GateContinueWithoutPolish,
            OnboardingCopy.GateSlowPolishTitle,
            OnboardingCopy.GateSlowPolishBody,
            OnboardingCopy.GatePolishDownloadingNotice,
            OnboardingCopy.GatePolishFailedNotice,
            OnboardingCopy.PracticePrompt,
            OnboardingCopy.PracticeWorkedWithoutPolishHeadline,
            OnboardingCopy.PracticeWorkedWithoutPolishSubhead("Ctrl+Win", DictationRecordingMode.PushToTalk),
            OnboardingCopy.PracticeWorkedWithoutPolishSubhead("F8", DictationRecordingMode.Toggle),
            OnboardingCopy.PracticeBasicCleanupTitle,
            OnboardingCopy.PracticeBasicCleanupBody,
            OnboardingCopy.PracticeFootnote,
            OnboardingCopy.PracticeSkip,
            OnboardingCopy.PracticeBusy,
            OnboardingCopy.TurnOnMicrophone,
            OnboardingCopy.FinishWithoutModel,
            OnboardingCopy.KeyUsage(DictationRecordingMode.PushToTalk),
            OnboardingCopy.KeyUsage(DictationRecordingMode.Toggle),
        ];

        foreach (var row in OnboardingCopy.WelcomeValueRows)
        {
            sentences.Add(row.Title);
            sentences.Add(row.Body);
        }

        foreach (var step in Enum.GetValues<OnboardingStep>())
        {
            sentences.Add(OnboardingCopy.StepOf(step));
            sentences.Add(OnboardingCopy.MilestoneLabel(step));
        }

        foreach (var beat in OnboardingCopy.ClosingBeats)
        {
            sentences.Add(beat.Title);
            sentences.Add(beat.Subtitle);
            sentences.Add(OnboardingCopy.FormatClosingSummary(beat, "Parakeet", "EG-1 Mini", "Ctrl+Win"));
        }

        foreach (var state in Enum.GetValues<OnboardingPracticeState>())
        {
            foreach (var mode in Enum.GetValues<DictationRecordingMode>())
            {
                foreach (var delivery in Enum.GetValues<OnboardingTakeDelivery>())
                {
                    sentences.Add(OnboardingCopy.PracticeHeadline(state, succeeded: false));
                    sentences.Add(OnboardingCopy.PracticeSubhead(state, false, "F8", mode, delivery));
                }
            }
        }

        Assert.All(sentences, sentence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(sentence));
            Assert.DoesNotContain((char)0x2014, sentence);
            Assert.DoesNotContain((char)0x2013, sentence);
        });
    }

    [Fact]
    public async Task FinishingStoresCompletionAndRunningSetupAgainStoresItsAbsenceAndNothingElse()
    {
        var directory = Directory.CreateTempSubdirectory("ew-onboarding-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var store = new JsonSettingsStore(path);
            var before = AppSettings.Default with
            {
                LaunchCount = 3,
                Preferences = AppSettings.Default.Preferences with { Theme = AppTheme.Dark },
            };
            Assert.False(before.HasCompletedOnboarding);
            using (var presenter = new SettingsPresenter(store, before))
            {
                Assert.True((await presenter.SaveAsync(OnboardingFlow.Completed)).Saved);
                await presenter.DrainAsync();
            }

            var reloaded = (await new JsonSettingsStore(path).LoadAsync()).Settings;
            Assert.True(reloaded.HasCompletedOnboarding);
            Assert.Equal(3, reloaded.LaunchCount);
            Assert.Equal(AppTheme.Dark, reloaded.Preferences.Theme);

            using (var presenter = new SettingsPresenter(new JsonSettingsStore(path), reloaded))
            {
                Assert.True((await presenter.SaveAsync(OnboardingFlow.Restarted)).Saved);
                await presenter.DrainAsync();
            }

            var restarted = (await new JsonSettingsStore(path).LoadAsync()).Settings;
            Assert.False(restarted.HasCompletedOnboarding);
            Assert.Equal(3, restarted.LaunchCount);
            Assert.Equal(AppTheme.Dark, restarted.Preferences.Theme);
            Assert.Equal(reloaded.Preferences.Dictation, restarted.Preferences.Dictation);
        }
        finally
        {
            foreach (var file in directory.EnumerateFiles())
            {
                file.Delete();
            }

            directory.Delete();
        }
    }

    [Fact]
    public void SwitchingSpeechOrPolishSelectionInvalidatesStaleReadiness()
    {
        var flow = new OnboardingFlow();
        flow.ReportMicrophone(MicrophoneConsent.Allowed);
        flow.ReportRecordingKey("F8", DictationRecordingMode.Toggle);
        flow.Begin();
        flow.AdvanceFromWelcome();
        flow.ContinueFromLanguage();

        flow.ReportEngine(SpeechEngineReadiness.Ready);
        flow.ReportModel(SpeechModelDelivery.Installed);
        flow.ReportPolishModel(SpeechModelDelivery.Installed);

        // Re-selecting the same speech model keeps readiness intact.
        flow.SelectSpeech(FinalAsrEngine.Parakeet, ParakeetModelIds.Final);
        Assert.Equal(SpeechEngineReadiness.Ready, flow.Engine);
        Assert.Equal(SpeechModelDelivery.Installed, flow.Model);

        // Switching to a different speech model invalidates prior readiness and download guard.
        flow.DownloadStarted();
        flow.RequestCancel();
        flow.SelectSpeech(FinalAsrEngine.Whisper, WhisperModelIds.Final);
        Assert.Equal(SpeechEngineReadiness.Preparing, flow.Engine);
        Assert.Equal(SpeechModelDelivery.Unknown, flow.Model);
        Assert.False(flow.IsSpeechReady);
        Assert.False(flow.CancelRequested);
        Assert.Equal(SpeechModelDelivery.Installed, flow.PolishModel);

        flow.ReportEngine(SpeechEngineReadiness.NotInstalled);
        flow.ReportModel(SpeechModelDelivery.Missing);
        flow.ContinueFromSpeechPick();
        flow.ContinueFromPolishIntro();

        // Re-selecting the same EG-1 model keeps polish readiness intact; changing size resets it.
        flow.SelectPolish(OnboardingPolishChoiceKind.EgOne, EgOneModelCatalog.MiniId);
        Assert.Equal(SpeechModelDelivery.Installed, flow.PolishModel);
        flow.SelectPolish(OnboardingPolishChoiceKind.EgOne, EgOneModelCatalog.FullId);
        Assert.Equal(SpeechModelDelivery.Unknown, flow.PolishModel);
        Assert.False(flow.IsPolishReadyForGate);

        // Navigating forward routes back into Downloads because both selected models need setup/downloads.
        flow.ContinueFromPolishPick();
        Assert.Equal(OnboardingStep.Downloads, flow.Step);
        Assert.True(flow.ShouldStartDownload);
        Assert.True(flow.NeedsDownloadStart);
    }
}
