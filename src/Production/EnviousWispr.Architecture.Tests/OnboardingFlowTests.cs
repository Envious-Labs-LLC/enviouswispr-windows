using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Input;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Settings;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The first-run flow's decisions without the window: the screen order, every gate, the practice box's reading of a
/// take, and the one fact that is stored.
/// </summary>
/// <remarks>
/// EACH GATE IS TESTED FROM BOTH SIDES. A gate that only ever says yes and a gate that only ever says no both pass a
/// one-sided test, and the first is exactly what the Windows first run shipped as: one screen whose checks were
/// reported and never enforced.
/// </remarks>
public sealed class OnboardingFlowTests
{
    private static OnboardingFlow AtChecklist(SpeechEngineReadiness engine = SpeechEngineReadiness.Preparing,
        SpeechModelDelivery model = SpeechModelDelivery.Unknown)
    {
        var flow = new OnboardingFlow();
        flow.ReportEngine(engine);
        flow.ReportModel(model);
        flow.Begin();
        flow.GetStarted();
        return flow;
    }

    private static OnboardingFlow AtPractice(DictationRecordingMode mode = DictationRecordingMode.PushToTalk)
    {
        var flow = AtChecklist(SpeechEngineReadiness.Ready, SpeechModelDelivery.Installed);
        flow.ReportRecordingKey("F8", mode);
        flow.ReportMicrophone(MicrophoneConsent.Allowed);
        Assert.True(flow.AdvanceToPermission());
        Assert.True(flow.ContinueFromPermission(MicrophoneConsent.Allowed));
        flow.BeginWarming();
        Assert.True(flow.BeginPractice());
        Assert.Equal(OnboardingScreen.Practice, flow.Screen);
        return flow;
    }

    [Fact]
    public void TheFiveScreensComeInTheMacOrderAndCountFromOne()
    {
        var flow = new OnboardingFlow();
        flow.ReportEngine(SpeechEngineReadiness.Ready);
        flow.ReportModel(SpeechModelDelivery.Installed);
        flow.ReportMicrophone(MicrophoneConsent.Allowed);
        var seen = new List<(OnboardingScreen, int)> { (flow.Screen, flow.StepNumber) };

        flow.GetStarted();
        seen.Add((flow.Screen, flow.StepNumber));
        Assert.Equal(OnboardingSetupPhase.Checklist, flow.SetupPhase);
        Assert.True(flow.AdvanceToPermission());
        Assert.Equal(OnboardingSetupPhase.Permission, flow.SetupPhase);
        Assert.Equal(2, flow.StepNumber);
        Assert.True(flow.ContinueFromPermission(MicrophoneConsent.Allowed));
        seen.Add((flow.Screen, flow.StepNumber));
        flow.BeginWarming();
        seen.Add((flow.Screen, flow.StepNumber));
        Assert.True(flow.BeginPractice());
        seen.Add((flow.Screen, flow.StepNumber));

        Assert.Equal(
            [
                (OnboardingScreen.Welcome, 1),
                (OnboardingScreen.SettingUp, 2),
                (OnboardingScreen.Ready, 3),
                (OnboardingScreen.WarmingUp, 4),
                (OnboardingScreen.Practice, 5),
            ],
            seen);
        Assert.Equal(OnboardingFlow.ScreenCount, flow.StepNumber);
        Assert.Equal("Step 5 of 5", OnboardingCopy.StepOf(flow.StepNumber));
    }

    [Fact]
    public void AMoveMadeFromTheWrongScreenDoesNothing()
    {
        var flow = new OnboardingFlow();
        flow.ReportEngine(SpeechEngineReadiness.Ready);

        // From the welcome, nothing but Get Started moves.
        Assert.False(flow.AdvanceToPermission());
        Assert.False(flow.ContinueFromPermission(MicrophoneConsent.Allowed));
        flow.BeginWarming();
        Assert.False(flow.BeginPractice());
        Assert.Equal(OnboardingScreen.Welcome, flow.Screen);

        // A second Get Started from the checklist does not restart it.
        flow.GetStarted();
        Assert.True(flow.AdvanceToPermission());
        flow.GetStarted();
        Assert.Equal(OnboardingSetupPhase.Permission, flow.SetupPhase);
    }

    [Theory]
    [InlineData(SpeechEngineReadiness.Preparing, false)]
    [InlineData(SpeechEngineReadiness.NotInstalled, false)]
    [InlineData(SpeechEngineReadiness.Unavailable, false)]
    [InlineData(SpeechEngineReadiness.Ready, true)]
    public void TheChecklistMovesOnOnlyOnAReadyEngine(SpeechEngineReadiness engine, bool opens)
    {
        var flow = AtChecklist(engine, SpeechModelDelivery.Installed);

        Assert.Equal(opens, flow.ChecklistComplete);
        Assert.Equal(opens, flow.ShouldAdvanceFromChecklist);
        Assert.Equal(opens, flow.AdvanceToPermission());
        Assert.Equal(opens ? OnboardingSetupPhase.Permission : OnboardingSetupPhase.Checklist, flow.SetupPhase);
    }

    [Theory]
    // THE ENGINE ANSWERING READY OUTRANKS EVERY DOWNLOAD STATE: a stale "downloading" row must not hold the gate shut.
    [InlineData(SpeechModelDelivery.Unknown, SpeechEngineReadiness.Ready, OnboardingChecklistStatus.Completed)]
    [InlineData(SpeechModelDelivery.Downloading, SpeechEngineReadiness.Ready, OnboardingChecklistStatus.Completed)]
    [InlineData(SpeechModelDelivery.Failed, SpeechEngineReadiness.Ready, OnboardingChecklistStatus.Completed)]
    [InlineData(SpeechModelDelivery.Unknown, SpeechEngineReadiness.Preparing, OnboardingChecklistStatus.InProgress)]
    [InlineData(SpeechModelDelivery.Installed, SpeechEngineReadiness.Preparing, OnboardingChecklistStatus.InProgress)]
    [InlineData(SpeechModelDelivery.Installed, SpeechEngineReadiness.Unavailable, OnboardingChecklistStatus.Error)]
    [InlineData(SpeechModelDelivery.Missing, SpeechEngineReadiness.NotInstalled, OnboardingChecklistStatus.Pending)]
    [InlineData(SpeechModelDelivery.Downloading, SpeechEngineReadiness.NotInstalled, OnboardingChecklistStatus.InProgress)]
    [InlineData(SpeechModelDelivery.Activating, SpeechEngineReadiness.Preparing, OnboardingChecklistStatus.InProgress)]
    [InlineData(SpeechModelDelivery.Failed, SpeechEngineReadiness.NotInstalled, OnboardingChecklistStatus.Error)]
    [InlineData(SpeechModelDelivery.CannotDownload, SpeechEngineReadiness.NotInstalled, OnboardingChecklistStatus.Error)]
    [InlineData(SpeechModelDelivery.Cancelled, SpeechEngineReadiness.NotInstalled, OnboardingChecklistStatus.Pending)]
    public void TheSpeechModelRowReadsTheEngineFirstAndTheDownloadSecond(
        SpeechModelDelivery model,
        SpeechEngineReadiness engine,
        OnboardingChecklistStatus expected) =>
        Assert.Equal(expected, AtChecklist(engine, model).ModelItem);

    [Fact]
    public void TheDownloadStartsByItselfOnceWhenTheChecklistAppearsAndNeverAgainWithoutThePerson()
    {
        var flow = new OnboardingFlow();
        flow.ReportEngine(SpeechEngineReadiness.NotInstalled);
        flow.ReportModel(SpeechModelDelivery.Missing);

        // Not from the welcome: a download is asked for by Get Started, not by opening the app.
        Assert.False(flow.ShouldStartDownload);
        flow.GetStarted();
        Assert.True(flow.ShouldStartDownload);
        flow.DownloadStarted();
        Assert.False(flow.ShouldStartDownload);

        // A cancel then a missing report must not start it again by itself: the person's Cancel was a choice.
        flow.ReportModel(SpeechModelDelivery.Downloading);
        flow.ReportModel(SpeechModelDelivery.Cancelled);
        flow.ReportModel(SpeechModelDelivery.Missing);
        Assert.False(flow.ShouldStartDownload);

        // A new visit asks again.
        flow.Begin();
        flow.GetStarted();
        Assert.True(flow.ShouldStartDownload);
    }

    [Theory]
    [InlineData(SpeechModelDelivery.Installed)]
    [InlineData(SpeechModelDelivery.Unknown)]
    [InlineData(SpeechModelDelivery.Downloading)]
    [InlineData(SpeechModelDelivery.Failed)]
    [InlineData(SpeechModelDelivery.CannotDownload)]
    public void OnlyAMissingModelStartsADownload(SpeechModelDelivery model) =>
        Assert.False(AtChecklist(SpeechEngineReadiness.NotInstalled, model).ShouldStartDownload);

    [Fact]
    public void CancelIsAcknowledgedAtOnceAndPausesUntilThePersonStartsAgain()
    {
        var flow = AtChecklist(SpeechEngineReadiness.NotInstalled, SpeechModelDelivery.Installed);
        flow.RequestCancel();
        Assert.False(flow.CancelRequested);

        flow.ReportModel(SpeechModelDelivery.Downloading);
        Assert.True(flow.CanCancelSetup);
        flow.RequestCancel();
        Assert.True(flow.CancelRequested);
        Assert.False(flow.CanCancelSetup);

        // A late progress report while the cancel drains keeps the acknowledgement up.
        flow.ReportModel(SpeechModelDelivery.Downloading);
        Assert.True(flow.CancelRequested);

        flow.ReportModel(SpeechModelDelivery.Cancelled);
        Assert.False(flow.CancelRequested);
        Assert.True(flow.SetupPaused);
        Assert.Equal(OnboardingChecklistStatus.Pending, flow.ModelItem);
        Assert.False(flow.CanRetrySetup);
        Assert.False(flow.AdvanceToPermission());
    }

    [Theory]
    [InlineData(SpeechModelDelivery.Failed, SpeechEngineReadiness.NotInstalled, true)]
    [InlineData(SpeechModelDelivery.Installed, SpeechEngineReadiness.Unavailable, true)]
    // RETRYING A BUILD THAT CANNOT DOWNLOAD WHAT IT NEEDS WOULD FAIL THE SAME WAY FOR EVER.
    [InlineData(SpeechModelDelivery.CannotDownload, SpeechEngineReadiness.NotInstalled, false)]
    [InlineData(SpeechModelDelivery.Downloading, SpeechEngineReadiness.NotInstalled, false)]
    [InlineData(SpeechModelDelivery.Installed, SpeechEngineReadiness.Ready, false)]
    public void RetryIsOfferedWhereItCanHelp(SpeechModelDelivery model, SpeechEngineReadiness engine, bool offered) =>
        Assert.Equal(offered, AtChecklist(engine, model).CanRetrySetup);

    [Theory]
    [InlineData(SpeechModelDelivery.CannotDownload, SpeechEngineReadiness.NotInstalled, true)]
    // EVERY STATE A RETRY CAN FIX KEEPS THE GATE SHUT: the way past exists only where nothing can ever open it.
    [InlineData(SpeechModelDelivery.Failed, SpeechEngineReadiness.NotInstalled, false)]
    [InlineData(SpeechModelDelivery.Cancelled, SpeechEngineReadiness.NotInstalled, false)]
    [InlineData(SpeechModelDelivery.Missing, SpeechEngineReadiness.NotInstalled, false)]
    [InlineData(SpeechModelDelivery.Downloading, SpeechEngineReadiness.NotInstalled, false)]
    [InlineData(SpeechModelDelivery.Installed, SpeechEngineReadiness.Unavailable, false)]
    [InlineData(SpeechModelDelivery.CannotDownload, SpeechEngineReadiness.Ready, false)]
    public void OnlyAModelThisBuildCanNeverGetOffersAWayPastTheGate(
        SpeechModelDelivery model,
        SpeechEngineReadiness engine,
        bool offered)
    {
        var flow = AtChecklist(engine, model);
        Assert.Equal(offered, flow.CanFinishWithoutModel);
        Assert.Equal(offered, flow.MayFinish(OnboardingExit.SkipUnavailableModel));

        // Never from the welcome, the permission or later: it is the checklist's own exit.
        flow.Begin();
        Assert.False(flow.MayFinish(OnboardingExit.SkipUnavailableModel));
    }

    [Theory]
    [InlineData(MicrophoneConsent.Allowed, true)]
    // A SWITCH THAT COULD NOT BE READ SAYS NOTHING, and refusing on it would trap somebody behind a switch that is on.
    [InlineData(MicrophoneConsent.Unknown, true)]
    [InlineData(MicrophoneConsent.Blocked, false)]
    public void ThePermissionGateIsTheWindowsMicrophoneSwitch(MicrophoneConsent consent, bool opens)
    {
        var flow = AtChecklist(SpeechEngineReadiness.Ready, SpeechModelDelivery.Installed);
        Assert.True(flow.AdvanceToPermission());

        Assert.Equal(opens, flow.ContinueFromPermission(consent));
        Assert.Equal(opens ? OnboardingScreen.Ready : OnboardingScreen.SettingUp, flow.Screen);
    }

    [Fact]
    public void ContinueReadsTheSwitchAtThePressNotTheLastPoll()
    {
        var flow = AtChecklist(SpeechEngineReadiness.Ready, SpeechModelDelivery.Installed);
        Assert.True(flow.AdvanceToPermission());
        flow.ReportMicrophone(MicrophoneConsent.Allowed);
        Assert.True(flow.MicrophoneAllowed);

        // Turned off between the poll and the press.
        Assert.False(flow.ContinueFromPermission(MicrophoneConsent.Blocked));
        Assert.Equal(OnboardingSetupPhase.Permission, flow.SetupPhase);
        Assert.False(flow.MicrophoneAllowed);

        Assert.True(flow.ContinueFromPermission(MicrophoneConsent.Allowed));
    }

    [Theory]
    [InlineData(SpeechEngineReadiness.Ready, OnboardingWarmingOutcome.Ready, true)]
    [InlineData(SpeechEngineReadiness.Preparing, OnboardingWarmingOutcome.Waiting, false)]
    [InlineData(SpeechEngineReadiness.Unavailable, OnboardingWarmingOutcome.Failed, false)]
    [InlineData(SpeechEngineReadiness.NotInstalled, OnboardingWarmingOutcome.Failed, false)]
    public void TheWarmUpOpensOnlyOnAReadyEngineAndCanAlwaysBeSkipped(
        SpeechEngineReadiness engine,
        OnboardingWarmingOutcome outcome,
        bool opens)
    {
        var flow = AtChecklist(SpeechEngineReadiness.Ready, SpeechModelDelivery.Installed);
        Assert.True(flow.AdvanceToPermission());
        Assert.True(flow.ContinueFromPermission(MicrophoneConsent.Allowed));
        Assert.False(flow.MayFinish(OnboardingExit.SkipWarming));
        flow.BeginWarming();
        flow.ReportEngine(engine);

        Assert.Equal(outcome, flow.WarmingOutcome);
        Assert.True(flow.MayFinish(OnboardingExit.SkipWarming));
        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));
        Assert.Equal(opens, flow.BeginPractice());

        // A retry that brings the engine up opens it.
        flow.ReportEngine(SpeechEngineReadiness.Ready);
        Assert.True(flow.Screen == OnboardingScreen.Practice || flow.BeginPractice());
    }

    [Fact]
    public void ATakeThatFillsTheBoxWorksAndOpensFinishSetup()
    {
        var flow = AtPractice();
        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));
        Assert.True(flow.MayFinish(OnboardingExit.SkipPractice));

        flow.TakeStarted(string.Empty);
        Assert.Equal(OnboardingPracticeState.Listening, flow.PracticeState);
        Assert.True(flow.TakeInFlight);
        // HELD WHILE A TAKE IS IN FLIGHT: leaving would take away the box the take is about to type into.
        Assert.False(flow.MayFinish(OnboardingExit.SkipPractice));
        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));

        flow.PracticeTextChanged("Hello there.");
        flow.TakeDelivered(OnboardingTakeDelivery.Delivered);
        flow.TakeEnded("Hello there.");

        Assert.Equal(OnboardingPracticeState.Worked, flow.PracticeState);
        Assert.True(flow.PracticeSucceeded);
        Assert.True(flow.MayFinish(OnboardingExit.FinishSetup));
        Assert.True(flow.MayFinish(OnboardingExit.SkipPractice));
    }

    [Fact]
    public void ASilentTakeIsAllQuietAndLeavesFinishSetupShut()
    {
        var flow = AtPractice();
        flow.TakeStarted(string.Empty);
        flow.TakeEnded(string.Empty);

        Assert.Equal(OnboardingPracticeState.SaidNothing, flow.PracticeState);
        Assert.False(flow.PracticeSucceeded);
        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));
        Assert.True(flow.MayFinish(OnboardingExit.SkipPractice));
    }

    [Theory]
    [InlineData(OnboardingTakeDelivery.Clipboard)]
    [InlineData(OnboardingTakeDelivery.Delivered)]
    public void WordsThatWentElsewhereAreAMissedBoxNotSilence(OnboardingTakeDelivery delivery)
    {
        var flow = AtPractice();
        flow.TakeStarted(string.Empty);
        flow.TakeDelivered(delivery);
        flow.TakeEnded(string.Empty);

        Assert.Equal(OnboardingPracticeState.MissedTheBox, flow.PracticeState);
        Assert.Equal(delivery, flow.MissedTo);
        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));
    }

    [Theory]
    [InlineData(OnboardingTakeDelivery.None)]
    [InlineData(OnboardingTakeDelivery.Clipboard)]
    [InlineData(OnboardingTakeDelivery.Failed)]
    public void AFailureOutranksEveryOtherReading(OnboardingTakeDelivery delivery)
    {
        var flow = AtPractice();
        flow.TakeStarted(string.Empty);
        flow.TakeDelivered(delivery);
        flow.TakeFailed();
        flow.TakeEnded(string.Empty);

        Assert.Equal(OnboardingPracticeState.SomethingBroke, flow.PracticeState);
    }

    [Fact]
    public void AFailedDeliveryIsSomethingBrokeEvenWithoutAFailedStatus()
    {
        var flow = AtPractice();
        flow.TakeStarted(string.Empty);
        flow.TakeDelivered(OnboardingTakeDelivery.Failed);
        flow.TakeEnded(string.Empty);

        Assert.Equal(OnboardingPracticeState.SomethingBroke, flow.PracticeState);
    }

    [Fact]
    public void WordsArrivingJustAfterTheTakeWasReadAsSilentAreTheTakeWorking()
    {
        var flow = AtPractice();
        flow.TakeStarted(string.Empty);
        flow.TakeEnded(string.Empty);
        Assert.Equal(OnboardingPracticeState.SaidNothing, flow.PracticeState);

        // The paste went through the input queue and landed after the take's end.
        flow.PracticeTextChanged("Late words.");
        Assert.Equal(OnboardingPracticeState.Worked, flow.PracticeState);
        Assert.True(flow.MayFinish(OnboardingExit.FinishSetup));
    }

    [Fact]
    public void ASecondTakeIsJudgedAgainstItsOwnStartNotTheFirstTakesWords()
    {
        var flow = AtPractice();
        flow.TakeStarted(string.Empty);
        flow.PracticeTextChanged("First.");
        flow.TakeEnded("First.");
        Assert.Equal(OnboardingPracticeState.Worked, flow.PracticeState);

        flow.TakeStarted("First.");
        flow.TakeEnded("First.");
        Assert.Equal(OnboardingPracticeState.SaidNothing, flow.PracticeState);
        // SUCCESS IS STICKY: the person has seen the product work, and a quiet second try does not take it away.
        Assert.True(flow.PracticeSucceeded);
        Assert.True(flow.MayFinish(OnboardingExit.FinishSetup));
    }

    [Fact]
    public void ClearingTheBoxAfterASuccessKeepsFinishSetup()
    {
        var flow = AtPractice();
        flow.PracticeTextChanged("typed by hand");
        Assert.True(flow.PracticeSucceeded);
        flow.PracticeTextChanged(string.Empty);
        Assert.True(flow.PracticeSucceeded);
        Assert.True(flow.MayFinish(OnboardingExit.FinishSetup));
    }

    [Fact]
    public void ABlockedMicrophoneIsCannotHearAndClearsWhenTheSwitchComesOn()
    {
        var flow = AtPractice();
        flow.ReportMicrophone(MicrophoneConsent.Blocked);
        Assert.Equal(OnboardingPracticeState.CannotHear, flow.PracticeState);

        // A take cannot open against a blocked switch.
        flow.TakeStarted(string.Empty);
        Assert.Equal(OnboardingPracticeState.CannotHear, flow.PracticeState);
        Assert.True(flow.MayFinish(OnboardingExit.SkipPractice));

        flow.ReportMicrophone(MicrophoneConsent.Allowed);
        Assert.Equal(OnboardingPracticeState.Waiting, flow.PracticeState);
    }

    [Fact]
    public void AReopenedSetupDoesNotInheritAPreviousVisitsSuccess()
    {
        var flow = AtPractice();
        flow.PracticeTextChanged("Hello.");
        Assert.True(flow.MayFinish(OnboardingExit.FinishSetup));

        flow.Begin();
        Assert.Equal(OnboardingScreen.Welcome, flow.Screen);
        Assert.False(flow.PracticeSucceeded);
        Assert.Equal(OnboardingPracticeState.Waiting, flow.PracticeState);
        Assert.False(flow.MayFinish(OnboardingExit.FinishSetup));
    }

    [Fact]
    public void StatusesOutsideThePracticeScreenMoveNothing()
    {
        var flow = AtChecklist(SpeechEngineReadiness.Ready, SpeechModelDelivery.Installed);
        flow.TakeStarted(string.Empty);
        flow.PracticeTextChanged("words");
        flow.TakeEnded("words");

        Assert.Equal(OnboardingPracticeState.Waiting, flow.PracticeState);
        Assert.False(flow.PracticeSucceeded);
    }

    [Theory]
    [InlineData("Ctrl+Win", true)]
    [InlineData("RightCtrl", true)]
    [InlineData("LeftShift", true)]
    [InlineData("F8", false)]
    [InlineData("Ctrl+Alt+W", false)]
    [InlineData("Ctrl+RightShift", false)]
    public void HandsFreeIsPromisedOnlyForTheKeysTheHookGivesItTo(string key, bool unlocks)
    {
        var parsed = HotkeyGestureParser.Parse(key);
        Assert.True(parsed.Succeeded, key);
        Assert.Equal(unlocks, HotkeyGestureParser.UnlocksGestures(parsed.Gesture!.Value));

        var flow = new OnboardingFlow();
        flow.ReportRecordingKey(key, DictationRecordingMode.PushToTalk);
        Assert.Equal(unlocks, flow.RecordingKeyHasGestures);
    }

    [Fact]
    public void TheKeyRowIsReportedButNeverAGate()
    {
        var flow = AtChecklist(SpeechEngineReadiness.Ready, SpeechModelDelivery.Installed);
        flow.ReportRecordingKeyUnavailable("The recording shortcut is already in use.");

        Assert.Equal(OnboardingChecklistStatus.Error, flow.KeyItem);
        Assert.Equal("The recording shortcut is already in use.", OnboardingCopy.KeyLine(flow));
        Assert.True(flow.AdvanceToPermission());

        flow.ReportRecordingKey("Ctrl+Win", DictationRecordingMode.PushToTalk);
        Assert.Equal(OnboardingChecklistStatus.Completed, flow.KeyItem);
        Assert.Equal("Your key: Ctrl+Win", OnboardingCopy.KeyLine(flow));
    }

    [Theory]
    [InlineData(OnboardingPracticeState.Waiting, false, "Time for your first dictation!")]
    [InlineData(OnboardingPracticeState.Waiting, true, "That is it. You are set.")]
    [InlineData(OnboardingPracticeState.Listening, false, "Listening…")]
    [InlineData(OnboardingPracticeState.Worked, true, "That is it. You are set.")]
    [InlineData(OnboardingPracticeState.SaidNothing, false, "All quiet")]
    [InlineData(OnboardingPracticeState.SomethingBroke, false, "That did not work")]
    [InlineData(OnboardingPracticeState.MissedTheBox, false, "Click the box first")]
    [InlineData(OnboardingPracticeState.CannotHear, false, "We cannot hear you")]
    public void EveryPracticeStateHasTheMacHeadline(OnboardingPracticeState state, bool succeeded, string expected) =>
        Assert.Equal(expected, OnboardingCopy.PracticeHeadline(state, succeeded));

    [Fact]
    public void ThePracticeCopyNamesTheKeyTheReadyScreenShowed()
    {
        Assert.Equal(
            "Hold Ctrl+Win and say something.\nLet go when you are done.",
            OnboardingCopy.PracticeSubhead(
                OnboardingPracticeState.Waiting, false, "Ctrl+Win", DictationRecordingMode.PushToTalk, OnboardingTakeDelivery.None));
        Assert.Equal(
            "Go ahead. Let go of F8 when you are done.",
            OnboardingCopy.PracticeSubhead(
                OnboardingPracticeState.Listening, false, "F8", DictationRecordingMode.PushToTalk, OnboardingTakeDelivery.None));
        Assert.Equal(
            "Press F8 and say something.\nPress it again when you are done.",
            OnboardingCopy.PracticeSubhead(
                OnboardingPracticeState.Waiting, false, "F8", DictationRecordingMode.Toggle, OnboardingTakeDelivery.None));
        Assert.Contains(
            "went to the clipboard",
            OnboardingCopy.PracticeSubhead(
                OnboardingPracticeState.MissedTheBox, false, "F8", DictationRecordingMode.PushToTalk, OnboardingTakeDelivery.Clipboard));
        Assert.Contains(
            "went to another window",
            OnboardingCopy.PracticeSubhead(
                OnboardingPracticeState.MissedTheBox, false, "F8", DictationRecordingMode.PushToTalk, OnboardingTakeDelivery.Delivered));
    }

    [Fact]
    public void NoSetupSentenceCarriesADash()
    {
        var sentences = new List<string>
        {
            OnboardingCopy.SetupPaused, OnboardingCopy.TrySetupAgain, OnboardingCopy.Retry, OnboardingCopy.Cancelling,
            OnboardingCopy.OneTimeSetup, OnboardingCopy.EngineUnavailable, OnboardingCopy.StartingEngine,
            OnboardingCopy.WarmingTitle, OnboardingCopy.WarmingFailedTitle, OnboardingCopy.WarmingSubtitle,
            OnboardingCopy.WarmingFailedSubtitle, OnboardingCopy.WarmingFailedMessage, OnboardingCopy.WarmingCaption,
            OnboardingCopy.WarmingSkip, OnboardingCopy.WarmingSkipAfterFailure, OnboardingCopy.PracticeFootnote,
            OnboardingCopy.PracticeSkip, OnboardingCopy.PracticeBusy, OnboardingCopy.TurnOnMicrophone,
            OnboardingCopy.FinishWithoutModel,
            OnboardingCopy.KeyUsage(DictationRecordingMode.PushToTalk), OnboardingCopy.KeyUsage(DictationRecordingMode.Toggle),
        };
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

            // READ BACK FROM DISK BY A NEW STORE, so the assertion is about what a relaunch reads, not what the
            // presenter remembers.
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
}
