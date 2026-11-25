using System.Runtime.InteropServices;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Settings;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App;

/// <summary>
/// Leave-confirmation guards for leaving Dictation > Engine or AI Polish while setup is
/// incomplete or a download is still running (#357, macOS SpeechSetupLeave + PolishSetupLeaveDialog parity).
/// </summary>
public sealed partial class MainWindow
{
    private SettingsRoute _currentSettingsRoute = new("history", null);
    private FinalAsrEngine? _speechEngineWhenVisitBegan;
    private PolishProvider? _polishProviderWhenVisitBegan;
    private bool _isHandlingGuardedNavigation;
    private bool _isLeaveDialogOpen;
    private SettingsRoute? _pendingSupersededRoute;

    private bool CanGuardLeave =>
        !_session.Closing &&
        !_shuttingDownProductWindows &&
        OnboardingView?.Visibility != Visibility.Visible;

    private void CommitNavigationRouteState(SettingsRoute route)
    {
        var previous = _currentSettingsRoute;
        _currentSettingsRoute = route;

        if (!previous.IsDictationEngine && route.IsDictationEngine)
        {
            _speechEngineWhenVisitBegan = SelectedFinalEngine();
        }
        else if (!route.IsDictationEngine)
        {
            _speechEngineWhenVisitBegan = null;
        }

        if (!previous.IsAiPolish && route.IsAiPolish)
        {
            _polishProviderWhenVisitBegan = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        }
        else if (!route.IsAiPolish)
        {
            _polishProviderWhenVisitBegan = null;
        }
    }

    private void NavigateToDictationTab(string tabKey)
    {
        var targetRoute = SettingsRoute.Normalize("settings-dictation", tabKey);
        if (TryBeginGuardedLeave(targetRoute, revertSidebarSelection: false))
        {
            return;
        }

        CommitNavigationRouteState(targetRoute);
        SelectDictationTab(tabKey);
    }

    private bool TryBeginGuardedLeave(SettingsRoute targetRoute, bool revertSidebarSelection)
    {
        if (!CanGuardLeave)
        {
            return false;
        }

        if (_isLeaveDialogOpen)
        {
            _pendingSupersededRoute = targetRoute;
            if (revertSidebarSelection)
            {
                SelectNavigationItemWithoutNavigation(_currentSettingsRoute.PageTag);
            }

            return true;
        }

        if (!TryEvaluateLeaveRequest(targetRoute, out _, out _, out _))
        {
            return false;
        }

        if (revertSidebarSelection)
        {
            SelectNavigationItemWithoutNavigation(_currentSettingsRoute.PageTag);
        }

        _ = PromptAndHandleSetupLeaveAsync(targetRoute);
        return true;
    }

    private bool TryEvaluateLeaveRequest(
        SettingsRoute targetRoute,
        out SetupLeaveDialogContent? dialogContent,
        out SpeechSetupLeaveRequest? speechRequest,
        out PolishSetupLeaveRequest? polishRequest)
    {
        dialogContent = null;
        speechRequest = null;
        polishRequest = null;

        if (!CanGuardLeave)
        {
            return false;
        }

        if (_currentSettingsRoute.IsDictationEngine && !targetRoute.IsDictationEngine)
        {
            speechRequest = SpeechSetupLeaveGuard.CreateRequest(
                _currentSettingsRoute,
                targetRoute,
                CaptureSpeechSetupFacts(),
                _speechEngineWhenVisitBegan);
            if (speechRequest is not null)
            {
                dialogContent = SpeechSetupLeaveGuard.BuildContent(speechRequest);
                return true;
            }
        }

        if (_currentSettingsRoute.IsAiPolish && !targetRoute.IsAiPolish)
        {
            polishRequest = PolishSetupLeaveGuard.CreateRequest(
                _currentSettingsRoute,
                targetRoute,
                CapturePolishSetupFacts(),
                _polishProviderWhenVisitBegan,
                IsPolishProviderReadyForGoBack);
            if (polishRequest is not null)
            {
                dialogContent = PolishSetupLeaveGuard.BuildContent(polishRequest);
                return true;
            }
        }

        return false;
    }

    private SpeechSetupFacts CaptureSpeechSetupFacts()
    {
        bool? parakeetInstalled = _parakeetModelState.DeliveryStage == SpeechModelDelivery.Unknown
            ? null
            : _parakeetModelState.IsInstalled;
        bool? whisperInstalled = _whisperModelState.DeliveryStage == SpeechModelDelivery.Unknown
            ? null
            : _whisperModelState.IsInstalled;
        return new SpeechSetupFacts(
            PickedEngine: SelectedFinalEngine(),
            ParakeetInstalled: parakeetInstalled,
            WhisperInstalled: whisperInstalled,
            ParakeetDownloading: _activeSpeechDownloadEngine == FinalAsrEngine.Parakeet
                || _parakeetModelState.DeliveryStage is SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating,
            WhisperDownloading: _activeSpeechDownloadEngine == FinalAsrEngine.Whisper
                || _whisperModelState.DeliveryStage is SpeechModelDelivery.Downloading or SpeechModelDelivery.Activating);
    }

    private PolishSetupFacts CapturePolishSetupFacts()
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        var isCloud = IsCloudProvider(provider);
        var hasStoredKey = !isCloud || _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found;
        var keyRejected = isCloud && _cloudKeyRejectedProvider == provider;
        var hasUnsavedKeyDraft = isCloud && !string.IsNullOrWhiteSpace(ApiKeyPasswordBox?.Password);

        var ollamaView = provider == PolishProvider.Ollama ? OllamaModels?.View : null;
        var ollamaDownloading = ollamaView?.Rows.Any(row => row.Downloading) == true;
        var ollamaSnapshotKnown = ollamaView is not null
            && ollamaView.Setup.StatusBadge is not ("Checking" or "Starting");
        OllamaServerState? ollamaServer = null;
        var ollamaFound = false;
        var ollamaSelectedInstalled = false;
        if (ollamaView is not null)
        {
            ollamaFound = ollamaView.Setup.Action != OllamaSetupAction.DownloadOllama;
            ollamaServer = ollamaView.Setup.StatusBadge switch
            {
                "Invalid endpoint" => OllamaServerState.EndpointInvalid,
                "Not responding" => OllamaServerState.NotResponding,
                "Not running" or "Not installed" => OllamaServerState.NotListening,
                "Running" when ollamaView.Setup.Action == OllamaSetupAction.DownloadRecommended => OllamaServerState.NoModels,
                "Running" => OllamaServerState.Ready,
                "Starting" => OllamaServerState.Starting,
                _ => null,
            };
            ollamaSelectedInstalled = !string.IsNullOrWhiteSpace(_selectedPolishModelId)
                && ollamaView.Rows.Any(row =>
                    row.Installed && OllamaModelCatalog.SameModel(row.Id, _selectedPolishModelId));
        }

        var egOneState = provider == PolishProvider.EgOne
            ? ResolveEgOneDisplayState(ResolveSelectedEgOneModelId())
            : null;

        return new PolishSetupFacts(
            Provider: provider,
            CloudHasStoredKey: hasStoredKey,
            CloudKeyRejected: keyRejected,
            CloudHasUnsavedKeyDraft: hasUnsavedKeyDraft,
            SelectedModelId: _selectedPolishModelId,
            OllamaSnapshotKnown: ollamaSnapshotKnown,
            OllamaServer: ollamaServer,
            OllamaFound: ollamaFound,
            OllamaDownloading: ollamaDownloading,
            OllamaSelectedModelInstalled: ollamaSelectedInstalled,
            SelectedEgOneState: egOneState,
            EgOneServerExecutableExists: _egOneServerExecutableExists);
    }

    private bool IsPolishProviderReadyForGoBack(PolishProvider provider)
    {
        if (provider == PolishProvider.None)
        {
            return false;
        }

        if (IsCloudProvider(provider))
        {
            return _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found
                && _cloudKeyRejectedProvider != provider;
        }

        if (provider == PolishProvider.EgOne)
        {
            var state = ResolveEgOneDisplayState(ResolveSelectedEgOneModelId());
            return state.IsInstalled && _egOneServerExecutableExists;
        }

        if (provider == PolishProvider.Ollama && OllamaModels?.View is { } view)
        {
            return string.Equals(view.Setup.StatusBadge, "Running", StringComparison.Ordinal)
                && view.Rows.Any(row => row.Installed);
        }

        return false;
    }

    private async Task PromptAndHandleSetupLeaveAsync(SettingsRoute initialTargetRoute)
    {
        if (_isLeaveDialogOpen)
        {
            _pendingSupersededRoute = initialTargetRoute;
            return;
        }

        if (!TryEvaluateLeaveRequest(initialTargetRoute, out var content, out var speechRequest, out var polishRequest) ||
            content is null)
        {
            NavigateCommittedRoute(initialTargetRoute);
            return;
        }

        if (Content?.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        _isLeaveDialogOpen = true;
        _pendingSupersededRoute = null;
        SetupLeaveDialogAction? chosenAction = null;

        try
        {
            var dialog = BuildSetupLeaveContentDialog(xamlRoot, content, action => chosenAction = action);
            await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        }
        catch (COMException)
        {
            return;
        }
        finally
        {
            _isLeaveDialogOpen = false;
        }

        if (!CanGuardLeave)
        {
            _pendingSupersededRoute = null;
            return;
        }

        var finalTarget = _pendingSupersededRoute ?? initialTargetRoute;
        _pendingSupersededRoute = null;

        if (speechRequest is not null)
        {
            await ApplySpeechLeaveResolutionAsync(
                chosenAction,
                speechRequest with { Target = finalTarget }).ConfigureAwait(true);
            return;
        }

        if (polishRequest is not null)
        {
            await ApplyPolishLeaveResolutionAsync(
                chosenAction,
                polishRequest with { Target = finalTarget }).ConfigureAwait(true);
        }
    }

    private ContentDialog BuildSetupLeaveContentDialog(
        XamlRoot xamlRoot,
        SetupLeaveDialogContent content,
        Action<SetupLeaveDialogAction> onButtonPressed)
    {
        var bodyStack = new StackPanel
        {
            Spacing = 16,
        };
        bodyStack.Children.Add(new TextBlock
        {
            Text = content.Message,
            TextWrapping = TextWrapping.Wrap,
        });

        var actionsPanel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        Button? primaryButton = null;
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = content.Title,
            Content = bodyStack,
        };

        foreach (var buttonSpec in content.Buttons)
        {
            var styleKey = buttonSpec.IsPrimary ? "BrandPrimaryButtonStyle" : "BrandQuietButtonStyle";
            var button = new Button
            {
                Content = buttonSpec.Title,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = Application.Current.Resources[styleKey] as Style,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, buttonSpec.Title);
            button.Click += (_, _) =>
            {
                onButtonPressed(buttonSpec.Action);
                dialog.Hide();
            };

            if (buttonSpec.IsPrimary && primaryButton is null)
            {
                primaryButton = button;
            }

            actionsPanel.Children.Add(button);
        }

        bodyStack.Children.Add(actionsPanel);
        if (primaryButton is not null)
        {
            dialog.Opened += (_, _) => primaryButton.Focus(FocusState.Programmatic);
        }

        return dialog;
    }

    private Task ApplySpeechLeaveResolutionAsync(
        SetupLeaveDialogAction? chosenAction,
        SpeechSetupLeaveRequest request)
    {
        var outcome = SpeechSetupLeaveGuard.Resolve(
            chosenAction,
            request,
            CaptureSpeechSetupFacts(),
            _speechEngineWhenVisitBegan);

        switch (outcome.Kind)
        {
            case SpeechSetupLeaveOutcomeKind.SetUp when outcome.Engine is { } engine:
                ShowSpeechEngineNotice(string.Empty);
                if (IsSpeechEngineBusy?.Invoke() == true)
                {
                    ShowSpeechEngineNotice(EnginePolishPresentation.SpeechDownloadBlockedBusyNotice);
                }
                else
                {
                    SpeechEngineDownloadRequested?.Invoke(engine);
                }

                break;

            case SpeechSetupLeaveOutcomeKind.RestoreAndNavigate when outcome.Engine is { } previousEngine && outcome.Target is { } restoreTarget:
                ApplySelectedEngineCard(previousEngine);
                NavigateCommittedRoute(restoreTarget);
                break;

            case SpeechSetupLeaveOutcomeKind.Navigate when outcome.Target is { } navigateTarget:
                NavigateCommittedRoute(navigateTarget);
                break;
        }

        return Task.CompletedTask;
    }

    private async Task ApplyPolishLeaveResolutionAsync(
        SetupLeaveDialogAction? chosenAction,
        PolishSetupLeaveRequest request)
    {
        var outcome = PolishSetupLeaveGuard.Resolve(
            chosenAction,
            request,
            CapturePolishSetupFacts(),
            _polishProviderWhenVisitBegan,
            IsPolishProviderReadyForGoBack);

        switch (outcome.Kind)
        {
            case PolishSetupLeaveOutcomeKind.Stay:
                FocusIncompletePolishControl(request.Problem);
                break;

            case PolishSetupLeaveOutcomeKind.RestoreAndNavigate when outcome.Provider is { } previousProvider && outcome.Target is { } restoreTarget:
                await ApplySelectedPolishProviderCardAsync(
                    PolishProviderIndex(previousProvider),
                    previousProvider,
                    allowDefaultModel: true).ConfigureAwait(true);
                NavigateCommittedRoute(restoreTarget);
                break;

            case PolishSetupLeaveOutcomeKind.Navigate when outcome.Target is { } navigateTarget:
                NavigateCommittedRoute(navigateTarget);
                break;
        }
    }

    private void FocusIncompletePolishControl(PolishSetupProblemKind problem)
    {
        switch (problem)
        {
            case PolishSetupProblemKind.CloudKeyMissing:
            case PolishSetupProblemKind.CloudKeyUnsavedDraft:
            case PolishSetupProblemKind.CloudKeyRejected:
                ApiKeyPasswordBox?.Focus(FocusState.Programmatic);
                break;
            case PolishSetupProblemKind.CloudNoModel:
            case PolishSetupProblemKind.OllamaNoModel:
                PolishModelPicker?.Focus(FocusState.Programmatic);
                break;
            case PolishSetupProblemKind.EgOneUnavailable:
                if (PolishProviderChooserPanel is not null)
                {
                    PolishProviderChooserPanel.Visibility = Visibility.Visible;
                }

                PolishProviderComboBox?.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void SelectNavigationItemWithoutNavigation(string pageTag)
    {
        var row = NavigationRows()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), pageTag, StringComparison.Ordinal));
        if (row is null && string.Equals(pageTag, "history", StringComparison.Ordinal))
        {
            row = HistoryNavItem;
        }

        _isHandlingGuardedNavigation = true;
        try
        {
            ProductNavigation.SelectedItem = row;
        }
        finally
        {
            _isHandlingGuardedNavigation = false;
        }
    }

    private void NavigateCommittedRoute(SettingsRoute route)
    {
        if (string.Equals(route.PageTag, "whats-new", StringComparison.Ordinal))
        {
            _isHandlingGuardedNavigation = true;
            try
            {
                ProductNavigation.SelectedItem = null;
            }
            finally
            {
                _isHandlingGuardedNavigation = false;
            }

            CommitNavigationRouteState(route);
            ShowPage("whats-new");
            return;
        }

        var row = NavigationRows()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), route.PageTag, StringComparison.Ordinal));
        if (row is null && string.Equals(route.PageTag, "history", StringComparison.Ordinal))
        {
            row = HistoryNavItem;
        }

        if (row is null)
        {
            return;
        }

        _isHandlingGuardedNavigation = true;
        try
        {
            if (!ReferenceEquals(ProductNavigation.SelectedItem, row))
            {
                ProductNavigation.SelectedItem = row;
            }
        }
        finally
        {
            _isHandlingGuardedNavigation = false;
        }

        CommitNavigationRouteState(route);
        ShowPage(route.PageTag, route.TabKey);
    }
}
