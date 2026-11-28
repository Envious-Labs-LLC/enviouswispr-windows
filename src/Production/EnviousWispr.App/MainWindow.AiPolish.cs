using System.ComponentModel;
using System.Globalization;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Onboarding;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Presentation;
using EnviousWispr.Services.Diagnostics;
using EnviousWispr.Services.Distribution;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace EnviousWispr.App;

/// <summary>
/// One card in a choice list. Carries its own selection, because the list that shows it does not.
/// </summary>
/// <remarks>
/// The choice lists were <c>RadioButtons</c> controls and are now plain <c>ItemsControl</c>s. That
/// change was forced by layout: RadioButtons arranges each item at the item's own desired size and
/// never consults the item's alignment, so six provider cards rendered at six different widths
/// tracking six description lengths. Three attempts to make the container hand its width down -
/// stretching the control, stretching the items, a minimum width bound to the list - all failed,
/// the last one inertly, producing byte-identical measurements across two builds.
///
/// An ItemsControl over a StackPanel gives each item the panel's full width, which is all that was
/// ever wanted. The items are still RadioButtons, so the card style and its template are untouched.
/// What an ItemsControl does not have is a selection, so selection moved here, onto the data, where
/// two-way binding keeps it and the card in step without anyone reaching into a container.
/// </remarks>
public sealed class SelectableChoiceOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public SelectableChoiceOption(string name, string description, string? badgeText = null)
    {
        Name = name;
        Description = description;
        BadgeText = badgeText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;


    public string Name { get; }

    public string Description { get; }

    public string? BadgeText { get; }

    public Visibility BadgeVisibility =>
        string.IsNullOrWhiteSpace(BadgeText) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public override string ToString() => Name;
}

/// <summary>AI Polish provider selection, EG-1 cards and asset checks, model discovery, and cloud API key management.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The index of the chosen card, or 0 when nothing is chosen yet.
    /// </summary>
    /// <remarks>
    /// Falls back to 0 rather than -1 deliberately: every caller feeds this straight into an enum
    /// or a clamp, and a -1 would either throw or quietly become a different setting. The first
    /// option in each of these lists is the safe default - Parakeet, None, System, Top.
    /// </remarks>
    private static int SelectedIndexOf(SelectableChoiceOption[] choices)
    {
        var index = RawSelectedIndexOf(choices);
        return index >= 0 ? index : 0;
    }

    /// <summary>The selected index as it is, -1 for nothing selected: what the General save hands the presenter, which decides what a missing choice means.</summary>
    private static int RawSelectedIndexOf(SelectableChoiceOption[] choices) =>
        Array.FindIndex(choices, choice => choice.IsSelected);

    private static void SelectChoice(SelectableChoiceOption[] choices, int index)
    {
        for (var i = 0; i < choices.Length; i++)
        {
            choices[i].IsSelected = i == index;
        }
    }

    // INSTANCE-OWNED, NOT STATIC. A selection is state, and state shared between two windows is a
    // choice made in one appearing in the other. There is one window today; the rule costs nothing
    // and the second instance would otherwise be a defect nobody wrote.
    private readonly SelectableChoiceOption[] PolishProviderChoices =
    [
        new("None", "Leaves the deterministic transcript unchanged."),
        new("EG-1", "Polishes locally with the bundled Envious Grammar model.", "Recommended"),
        new("Ollama", "Polishes locally with an Ollama model on this PC."),
        new("OpenAI", "Sends transcript text directly to your OpenAI account."),
        new("Anthropic", "Sends transcript text directly to your Anthropic account."),
        new("Gemini", "Sends transcript text directly to your Google Gemini account."),
    ];

    private PolishProvider _lastNonNonePolishProvider = PolishProvider.EgOne;
    private PolishProvider? _lastActivePolishProvider;

    private string _selectedPolishModelId = string.Empty;
    private string? _selectedEgOneModelId;
    private PolishProvider? _cloudKeyRejectedProvider;
    private bool _isUpdatingPolishModelPicker;
    private bool _isUpdatingEgOneCards;
    private HardwareProfile? _hardwareProfile;
    private SystemMemorySampler? _egOneMemorySampler;
    private DispatcherQueueTimer? _egOneMemoryTimer;
    private bool _wasEgOneMemorySamplingVisible;
    private readonly Dictionary<string, EgOneMemoryPresentation> _latestEgOneMemoryPresentations = new(StringComparer.Ordinal);

    /// <summary>Queries the live EG-1 helper server lifecycle state keyed by normalized model ID.</summary>
    public Func<IReadOnlyDictionary<string, EgOneServerActivity>>? GetEgOneServerActivities { get; set; }

    /// <summary>Refreshes EG-1 card memory attribution immediately when the local helper starts, reaches ready, or stops.</summary>
    public void NotifyEgOneActivityChanged()
    {
        if (!IsEgOneMemorySamplingVisible())
        {
            return;
        }

        UpdateEgOneCards();
    }

    /// <summary>The person asked to download an EG-1 model size.</summary>
    public event Action<string>? EgOneModelDownloadRequested;

    /// <summary>The person asked for the running EG-1 model download to stop.</summary>
    public event Action? EgOneModelDownloadCancelRequested;

    /// <summary>The person confirmed removing an installed EG-1 model size.</summary>
    public event Action<string>? EgOneModelRemoveRequested;

    private readonly Dictionary<string, EgOneModelState> _egOneModelStates = new(StringComparer.Ordinal)
    {
        [EnginePolishPresentation.EgOneFullModelId] = EgOneModelState.Unknown(EnginePolishPresentation.EgOneFullModelId),
        [EnginePolishPresentation.EgOneMiniModelId] = EgOneModelState.Unknown(EnginePolishPresentation.EgOneMiniModelId),
        [EnginePolishPresentation.EgOneMicroModelId] = EgOneModelState.Unknown(EnginePolishPresentation.EgOneMicroModelId),
    };
    private bool _egOneServerExecutableExists;
    private string? _activeEgOneDownloadModelId;
    private bool _hasEgOneInventorySnapshot;
    private string? _cloudPolishNotice;
    private string? _ollamaPolishNotice;
    private string? _lastOllamaModelsNotice;

    public void SetCloudPolishNotice(string? notice)
    {
        _cloudPolishNotice = string.IsNullOrWhiteSpace(notice) ? null : notice.Trim();
        UpdateActivePolishProviderDisplay();
    }

    public void SetOllamaPolishNotice(string? notice)
    {
        _ollamaPolishNotice = string.IsNullOrWhiteSpace(notice) ? null : notice.Trim();
        if (OllamaNoticeText is not null)
        {
            var effectiveNotice = HistoryPlaybackPresentation.ResolveOllamaNoticeDisplay(
                _ollamaPolishNotice,
                _lastOllamaModelsNotice);
            SetLiveRegion(
                OllamaNoticeText,
                effectiveNotice ?? string.Empty,
                effectiveNotice is null ? Visibility.Collapsed : Visibility.Visible);
        }

        UpdateActivePolishProviderDisplay();
    }

    internal async void EnableAiPolishToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var isEnabled = EnableAiPolishToggle.IsOn;
        if (AiPolishEnabledContent is not null)
        {
            AiPolishEnabledContent.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        PolishProvider targetProvider;
        if (isEnabled)
        {
            targetProvider = _lastNonNonePolishProvider == PolishProvider.None
                ? PolishProvider.EgOne
                : _lastNonNonePolishProvider;
        }
        else
        {
            var current = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
            if (current != PolishProvider.None)
            {
                _lastNonNonePolishProvider = current;
            }

            targetProvider = PolishProvider.None;
        }

        _lastActivePolishProvider = targetProvider;
        await ApplySelectedPolishProviderCardAsync(
            PolishProviderIndex(targetProvider),
            targetProvider,
            allowDefaultModel: false).ConfigureAwait(true);
    }

    internal void ChangePolishProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (PolishProviderChooserPanel is not null)
        {
            PolishProviderChooserPanel.Visibility =
                PolishProviderChooserPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }
    }

    internal void EgOneChooseProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (PolishProviderChooserPanel is not null)
        {
            PolishProviderChooserPanel.Visibility = Visibility.Visible;
        }

        PolishProviderComboBox?.Focus(FocusState.Programmatic);
    }

    private void UpdateActivePolishProviderDisplay()
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        if (provider != PolishProvider.None)
        {
            _lastNonNonePolishProvider = provider;
        }

        var isPolishEnabled = provider != PolishProvider.None;
        if (EnableAiPolishToggle is not null && EnableAiPolishToggle.IsOn != isPolishEnabled)
        {
            EnableAiPolishToggle.IsOn = isPolishEnabled;
        }
        if (AiPolishEnabledContent is not null)
        {
            AiPolishEnabledContent.Visibility = isPolishEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (ActivePolishProviderNameText is not null)
        {
            ActivePolishProviderNameText.Text = ProviderDisplayName(provider);
        }
        if (ActivePolishProviderRecommendedBadge is not null)
        {
            ActivePolishProviderRecommendedBadge.Visibility = provider == PolishProvider.EgOne
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var selectedEgOneState = provider == PolishProvider.EgOne
            ? ResolveEgOneDisplayState(ResolveSelectedEgOneModelId())
            : null;
        var isOllamaHosted = provider == PolishProvider.Ollama && IsSelectedOllamaModelHosted();
        var summaryBadge = HistoryPlaybackPresentation.ApplyPolishNoticeToBadge(
            EnginePolishPresentation.DescribeActivePolishProvider(
                provider,
                selectedEgOneState,
                _egOneServerExecutableExists,
                isOllamaHosted),
            provider,
            isOllamaHosted,
            _cloudPolishNotice);
        ApplyActivePolishProviderBadge(summaryBadge);
        if (ActivePolishProviderHeaderGrid is { ActualWidth: > 0 } headerGrid)
        {
            UpdateActivePolishProviderHeaderLayout(headerGrid.ActualWidth);
        }

        if (ActivePolishProviderIcon is not null)
        {
            ActivePolishProviderIcon.Glyph = provider switch
            {
                PolishProvider.EgOne => "\uE70F",
                PolishProvider.Ollama => "\uE968",
                _ => "\uE753",
            };
        }

        var outputLanguage = EnginePolishPresentation.DescribeDictationOutputLanguage(provider);
        if (DictationOutputLanguageCard is not null)
        {
            DictationOutputLanguageCard.Visibility = outputLanguage.IsVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (DictationOutputLanguageStatusText is not null && outputLanguage.IsVisible)
        {
            DictationOutputLanguageStatusText.Text = outputLanguage.StatusText;
        }

        if (EgOneSettingsPanel is not null)
        {
            EgOneSettingsPanel.Visibility = provider == PolishProvider.EgOne ? Visibility.Visible : Visibility.Collapsed;
        }
        if (provider == PolishProvider.EgOne)
        {
            UpdateEgOneMemoryTimer(tickImmediately: true, renderOnChange: false);
            UpdateEgOneCards();
        }
        else
        {
            UpdateEgOneMemoryTimer(tickImmediately: false);
        }
        if (OllamaSettingsPanel is not null)
        {
            OllamaSettingsPanel.Visibility = provider == PolishProvider.Ollama ? Visibility.Visible : Visibility.Collapsed;
        }
        if (CloudPolishSettingsPanel is not null)
        {
            CloudPolishSettingsPanel.Visibility = ProviderSettingsPresenter.UsesAModel(provider)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        UpdateOllamaLivePollTimer();
        UpdateHistoryPolishState();
    }

    public void SetEgOneModels(
        IReadOnlyDictionary<string, EgOneModelState> states,
        bool serverExecutableExists,
        string? downloadingModelId)
    {
        ArgumentNullException.ThrowIfNull(states);
        foreach (var modelId in EgOneModelCatalog.AllIds)
        {
            if (states.TryGetValue(modelId, out var state))
            {
                _egOneModelStates[modelId] = state;
            }
        }

        _egOneServerExecutableExists = serverExecutableExists;
        _activeEgOneDownloadModelId = downloadingModelId;
        _hasEgOneInventorySnapshot = true;
        UpdateEgOneCards();
        UpdateActivePolishProviderBadgeIfEgOneSelected();
        SyncLiveInventoryToOnboarding();
    }

    public void SetEgOneDelivery(string modelId, ModelDeliveryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        var normalizedId = EnginePolishPresentation.NormalizeEgOneModelId(modelId);
        var existing = _egOneModelStates.GetValueOrDefault(
            normalizedId,
            EgOneModelState.Unknown(normalizedId));
        _egOneModelStates[normalizedId] = existing with
        {
            DeliveryStage = presentation.Stage,
            DownloadPercent = presentation.Percent,
            StatusDetail = presentation.Text,
        };
        _activeEgOneDownloadModelId = presentation.Stage == SpeechModelDelivery.Downloading
            ? normalizedId
            : null;
        _hasEgOneInventorySnapshot = true;
        UpdateEgOneCards();
        UpdateActivePolishProviderBadgeIfEgOneSelected();
        SyncLiveInventoryToOnboarding();
    }

    private string ResolveSelectedEgOneModelId()
    {
        var rawModelId = _selectedEgOneModelId;
        if (string.IsNullOrWhiteSpace(rawModelId) &&
            _settings?.Preferences.Polish.Provider == PolishProvider.EgOne)
        {
            rawModelId = _settings.Preferences.Polish.ModelId;
        }

        return EnginePolishPresentation.NormalizeEgOneModelId(rawModelId);
    }

    private void UpdateActivePolishProviderBadgeIfEgOneSelected()
    {
        if (PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) != PolishProvider.EgOne)
        {
            return;
        }

        var selectedState = ResolveEgOneDisplayState(ResolveSelectedEgOneModelId());
        var summaryBadge = EnginePolishPresentation.DescribeActivePolishProvider(
            PolishProvider.EgOne,
            selectedState,
            _egOneServerExecutableExists,
            isOllamaHosted: false);
        ApplyActivePolishProviderBadge(summaryBadge);
        if (ActivePolishProviderHeaderGrid is { ActualWidth: > 0 } headerGrid)
        {
            UpdateActivePolishProviderHeaderLayout(headerGrid.ActualWidth);
        }
        UpdateHistoryPolishState();
    }

    private void ApplyActivePolishProviderBadge(ActivePolishProviderBadgePresentation summaryBadge)
    {
        if (ActivePolishProviderBadge is not null &&
            Application.Current.Resources[summaryBadge.ChipStyleKey] is Style chipStyle)
        {
            ActivePolishProviderBadge.Style = chipStyle;
        }

        if (ActivePolishProviderBadgeText is not null)
        {
            ActivePolishProviderBadgeText.Text = summaryBadge.BadgeText;
            if (Application.Current.Resources[summaryBadge.ChipTextStyleKey] is Style textStyle)
            {
                ActivePolishProviderBadgeText.Style = textStyle;
            }
        }

        if (ActivePolishProviderStatusText is not null)
        {
            ActivePolishProviderStatusText.Text = summaryBadge.StatusText;
        }
    }

    public void ShowEgOneNotice(string message)
    {
        var normalized = message ?? string.Empty;
        _latestEgOneNotice = normalized;
        if (EgOneNoticeText is null || EgOneNoticeBanner is null)
        {
            return;
        }

        SetLiveText(EgOneNoticeText, normalized);
        EgOneNoticeBanner.Visibility = string.IsNullOrWhiteSpace(normalized)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private EgOneModelState ResolveEgOneDisplayState(string modelId)
    {
        var normalizedId = EnginePolishPresentation.NormalizeEgOneModelId(modelId);
        if (_hasEgOneInventorySnapshot && _egOneModelStates.TryGetValue(normalizedId, out var state))
        {
            return state;
        }

        var configuredModelId = EnginePolishPresentation.NormalizeEgOneModelId(
            _selectedEgOneModelId ?? (_settings?.Preferences.Polish.Provider == PolishProvider.EgOne
                ? _settings.Preferences.Polish.ModelId
                : null));
        var (serverExists, modelExists) = CheckEgOneLocalAssets(normalizedId, configuredModelId);
        _egOneServerExecutableExists = serverExists;
        var hasBundledManifest = ModelDelivery.BundledModelManifests.TryRead(normalizedId) is not null;
        return new EgOneModelState(
            ModelId: normalizedId,
            IsInstalled: modelExists,
            CanRemove: modelExists &&
                EgOneRuntimeLocator.IsModelFileReady(EgOneRuntimeLocator.ResolveModelFile(_dataDirectory, normalizedId)) &&
                Directory.Exists(Path.Combine(_dataDirectory, "models", normalizedId)),
            HasBundledManifest: hasBundledManifest,
            DeliveryStage: modelExists ? SpeechModelDelivery.Installed : SpeechModelDelivery.Missing);
    }

    internal SetupHardwareSnapshot BuildSetupHardwareSnapshot(string? selectedLanguageCode = null)
    {
        var profile = _hardwareProfile ??= HardwareProfileSummary.GetCurrent();
        var languageCode = selectedLanguageCode
            ?? _selectedWhisperLanguage
            ?? _settings?.Preferences.Dictation.WhisperLanguage;
        return new SetupHardwareSnapshot(
            TotalRamGb: profile.TotalRamGb,
            GpuKind: profile.GpuKind,
            DedicatedVramGb: profile.DedicatedVramGb,
            PhysicalCoreCount: profile.PhysicalCoreCount,
            PerformanceCoreCount: profile.PerformanceCoreCount,
            Architecture: profile.Architecture,
            AvailableDiskGb: profile.AvailableDiskGb,
            SelectedLanguageCode: languageCode,
            SystemLanguageCode: CultureInfo.CurrentUICulture.Name,
            CudaRuntimeInstalled: _cudaRuntimeAvailable);
    }

    private SystemMemorySampler EnsureEgOneMemorySampler() =>
        _egOneMemorySampler ??= new SystemMemorySampler(
            SystemMemoryReader.ReadCurrent,
            IsEgOneMemorySamplingVisible);

    private bool IsEgOneMemorySamplingVisible()
    {
        if (_shuttingDownProductWindows ||
            _session.Closing ||
            !IsSettingsPageLoaded ||
            SettingsPage is not { Visibility: Visibility.Visible } ||
            AiPolishSection is not { Visibility: Visibility.Visible } ||
            EgOneSettingsPanel is not { Visibility: Visibility.Visible } ||
            PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) != PolishProvider.EgOne)
        {
            return false;
        }

        if (_onboardingLoaded && OnboardingView is { Visibility: Visibility.Visible })
        {
            return false;
        }

        try
        {
            if (AppWindow is not { IsVisible: true })
            {
                return false;
            }

            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    internal void UpdateEgOneMemoryTimer(bool tickImmediately, bool renderOnChange = true)
    {
        var isVisible = IsEgOneMemorySamplingVisible();
        if (!isVisible)
        {
            _wasEgOneMemorySamplingVisible = false;
            _egOneMemoryTimer?.Stop();
            return;
        }

        var becameVisible = !_wasEgOneMemorySamplingVisible;
        _wasEgOneMemorySamplingVisible = true;
        var sampler = EnsureEgOneMemorySampler();
        var sampled = tickImmediately && sampler.Tick();
        if (renderOnChange && (sampled || becameVisible))
        {
            UpdateEgOneCards();
        }

        if (_egOneMemoryTimer is null && DispatcherQueue is { } queue)
        {
            _egOneMemoryTimer = queue.CreateTimer();
            _egOneMemoryTimer.Interval = SystemMemorySampler.SampleInterval;
            _egOneMemoryTimer.IsRepeating = true;
            _egOneMemoryTimer.Tick += OnEgOneMemoryTimerTick;
        }

        if (_egOneMemoryTimer is { IsRunning: false })
        {
            _egOneMemoryTimer.Start();
        }
    }

    private void StopEgOneMemoryTimer()
    {
        _wasEgOneMemorySamplingVisible = false;
        if (_egOneMemoryTimer is not null)
        {
            _egOneMemoryTimer.Stop();
            _egOneMemoryTimer.Tick -= OnEgOneMemoryTimerTick;
            _egOneMemoryTimer = null;
        }
    }

    private void OnEgOneMemoryTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsEgOneMemorySamplingVisible())
        {
            UpdateEgOneMemoryTimer(tickImmediately: false);
            return;
        }

        var sampler = EnsureEgOneMemorySampler();
        if (sampler.Tick())
        {
            UpdateEgOneCards();
        }
    }

    private void UpdateEgOneCards()
    {
        if (_isUpdatingEgOneCards
            || EgOneSizeFullCard is null
            || EgOneSizeMiniCard is null
            || EgOneSizeMicroCard is null)
        {
            return;
        }

        var profile = _hardwareProfile ??= HardwareProfileSummary.GetCurrent();
        var sampler = EnsureEgOneMemorySampler();
        if (sampler.LatestReading is null && IsEgOneMemorySamplingVisible())
        {
            sampler.Tick();
        }

        var liveReading = sampler.LatestReading;
        if (EgOneMemoryText is not null)
        {
            EgOneMemoryText.Text = EgOneMemoryBarPresentation.FormatPcMemoryHeading(
                profile.TotalRamGb,
                profile.GpuKind,
                profile.DedicatedVramGb,
                profile.PhysicalCoreCount,
                liveReading);
            if (EgOneSectionHeaderGrid is { ActualWidth: > 0 } sectionHeaderGrid)
            {
                UpdateEgOneSectionHeaderLayout(sectionHeaderGrid.ActualWidth);
            }
        }

        var totalPhysicalBytes = liveReading?.TotalPhysicalBytes ??
            (profile.TotalRamGb > 0d && double.IsFinite(profile.TotalRamGb)
                ? Math.Max(1UL, (ulong)Math.Round(profile.TotalRamGb * EgOneMemoryFacts.BytesPerGiB, MidpointRounding.AwayFromZero))
                : (ulong?)null);
        var recommendation = WindowsSetupRecommender.Recommend(BuildSetupHardwareSnapshot());
        var recommendedModelId = EnginePolishPresentation.NormalizeEgOneModelId(recommendation.Polish.PrimaryModelId);
        var serverActivities = GetEgOneServerActivities?.Invoke();

        var rawModelId = _selectedEgOneModelId;
        if (string.IsNullOrWhiteSpace(rawModelId) &&
            _settings?.Preferences.Polish.Provider == PolishProvider.EgOne)
        {
            rawModelId = _settings.Preferences.Polish.ModelId;
        }

        var normalizedModelId = EnginePolishPresentation.NormalizeEgOneModelId(rawModelId);

        _isUpdatingEgOneCards = true;
        try
        {
            EgOneSizeFullCard.IsChecked = normalizedModelId == EnginePolishPresentation.EgOneFullModelId;
            EgOneSizeMiniCard.IsChecked = normalizedModelId == EnginePolishPresentation.EgOneMiniModelId;
            EgOneSizeMicroCard.IsChecked = normalizedModelId == EnginePolishPresentation.EgOneMicroModelId;
        }
        finally
        {
            _isUpdatingEgOneCards = false;
        }

        var selectedState = ResolveEgOneDisplayState(normalizedModelId);
        var slots = new[]
        {
            (
                EnginePolishPresentation.EgOneFullModelId,
                EgOneSizeFullCard,
                EgOneFullRecommendedBadge,
                EgOneFullStatusBadge,
                EgOneFullStatusBadgeText,
                EgOneFullTaglineText,
                EgOneFullDescriptionText,
                EgOneFullSpecsText,
                EgOneFullDetailText,
                EgOneFullProgress,
                EgOneFullDownloadButton,
                EgOneFullCancelButton,
                EgOneFullRemoveButton,
                EgOneFullChooseProviderButton
            ),
            (
                EnginePolishPresentation.EgOneMiniModelId,
                EgOneSizeMiniCard,
                EgOneMiniRecommendedBadge,
                EgOneMiniStatusBadge,
                EgOneMiniStatusBadgeText,
                EgOneMiniTaglineText,
                EgOneMiniDescriptionText,
                EgOneMiniSpecsText,
                EgOneMiniDetailText,
                EgOneMiniProgress,
                EgOneMiniDownloadButton,
                EgOneMiniCancelButton,
                EgOneMiniRemoveButton,
                EgOneMiniChooseProviderButton
            ),
            (
                EnginePolishPresentation.EgOneMicroModelId,
                EgOneSizeMicroCard,
                EgOneMicroRecommendedBadge,
                EgOneMicroStatusBadge,
                EgOneMicroStatusBadgeText,
                EgOneMicroTaglineText,
                EgOneMicroDescriptionText,
                EgOneMicroSpecsText,
                EgOneMicroDetailText,
                EgOneMicroProgress,
                EgOneMicroDownloadButton,
                EgOneMicroCancelButton,
                EgOneMicroRemoveButton,
                EgOneMicroChooseProviderButton
            ),
        };

        foreach (var slot in slots)
        {
            var state = ResolveEgOneDisplayState(slot.Item1);
            var otherInFlight = _activeEgOneDownloadModelId is not null &&
                !string.Equals(_activeEgOneDownloadModelId, slot.Item1, StringComparison.Ordinal);
            var activity = serverActivities is not null &&
                serverActivities.TryGetValue(slot.Item1, out var resolvedActivity)
                    ? resolvedActivity
                    : EgOneServerActivity.Inactive;
            var card = EnginePolishPresentation.DescribeEgOneCard(
                slot.Item1,
                normalizedModelId,
                recommendedModelId,
                state,
                otherInFlight,
                _egOneServerExecutableExists,
                totalPhysicalBytes,
                liveReading?.UsedBytes,
                activity);
            if (card.Memory is { } memory)
            {
                _latestEgOneMemoryPresentations[slot.Item1] = memory;
            }

            ApplyEgOneCardPresentation(
                card,
                slot.Item2,
                slot.Item3,
                slot.Item4,
                slot.Item5,
                slot.Item6,
                slot.Item7,
                slot.Item8,
                slot.Item9,
                slot.Item10,
                slot.Item11,
                slot.Item12,
                slot.Item13,
                slot.Item14);
            ApplyEgOneMemoryPresentation(slot.Item1, card);
        }

        if (EgOneInstallStatusText is not null)
        {
            EgOneInstallStatusText.Text = EnginePolishPresentation.FormatEgOneInstallStatus(
                _egOneServerExecutableExists,
                selectedState.IsInstalled);
        }
    }

    internal void ActivePolishProviderHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateActivePolishProviderHeaderLayout(e.NewSize.Width);

    private void UpdateActivePolishProviderHeaderLayout(double available)
    {
        if (ActivePolishProviderNameText is null || ActivePolishProviderBadgesPanel is null || available <= 0)
        {
            return;
        }

        var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        ActivePolishProviderNameText.Measure(infinite);
        var recWidth = 0d;
        var recVisible = ActivePolishProviderRecommendedBadge is { Visibility: Visibility.Visible };
        if (recVisible)
        {
            ActivePolishProviderRecommendedBadge!.Measure(infinite);
            recWidth = ActivePolishProviderRecommendedBadge.DesiredSize.Width;
        }

        var statusWidth = 0d;
        if (ActivePolishProviderBadge is { Visibility: Visibility.Visible })
        {
            ActivePolishProviderBadge.Measure(infinite);
            statusWidth = ActivePolishProviderBadge.DesiredSize.Width;
        }

        var badgesHorizontalWidth = recVisible && statusWidth > 0d
            ? recWidth + 8d + statusWidth
            : recWidth + statusWidth;

        var inline = ActivePolishProviderNameText.DesiredSize.Width + 8d + badgesHorizontalWidth <= available;
        if (inline)
        {
            ActivePolishProviderBadgesPanel.Orientation = Orientation.Horizontal;
            Grid.SetRow(ActivePolishProviderBadgesPanel, 0);
            Grid.SetColumn(ActivePolishProviderBadgesPanel, 1);
            Grid.SetColumnSpan(ActivePolishProviderBadgesPanel, 1);
            return;
        }

        ActivePolishProviderBadgesPanel.Orientation = badgesHorizontalWidth <= available
            ? Orientation.Horizontal
            : Orientation.Vertical;
        Grid.SetRow(ActivePolishProviderBadgesPanel, 1);
        Grid.SetColumn(ActivePolishProviderBadgesPanel, 0);
        Grid.SetColumnSpan(ActivePolishProviderBadgesPanel, 2);
    }

    internal void EgOneSectionHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateEgOneSectionHeaderLayout(e.NewSize.Width);

    private void UpdateEgOneSectionHeaderLayout(double available)
    {
        if (EgOneSectionTitleText is null || EgOneMemoryText is null || available <= 0)
        {
            return;
        }

        var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        EgOneSectionTitleText.Measure(infinite);
        EgOneMemoryText.Measure(infinite);
        var stack = EgOneSectionTitleText.DesiredSize.Width + EgOneMemoryText.DesiredSize.Width + 16d > available;
        Grid.SetRow(EgOneMemoryText, stack ? 1 : 0);
        Grid.SetColumn(EgOneMemoryText, stack ? 0 : 1);
        Grid.SetColumnSpan(EgOneMemoryText, stack ? 2 : 1);
        EgOneMemoryText.HorizontalAlignment = stack ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    internal void EgOneCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveThreeCardGridLayout(
            EgOneCardsGrid,
            e.NewSize.Width,
            stackThreshold: 700,
            [EgOneFullCardContainer, EgOneMiniCardContainer, EgOneMicroCardContainer]);

    internal void EgOneCardFooter_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || e.NewSize.Height <= 0)
        {
            return;
        }

        var targetCard = tag switch
        {
            "eg-1" => EgOneSizeFullCard,
            "eg-1-mini" => EgOneSizeMiniCard,
            "eg-1-micro" => EgOneSizeMicroCard,
            _ => null,
        };
        if (targetCard is not null)
        {
            targetCard.Padding = new Thickness(16, 16, 16, Math.Ceiling(e.NewSize.Height) + 22);
        }
    }

    private static void ApplyEgOneCardPresentation(
        EgOneCardPresentation card,
        RadioButton? cardButton,
        Border? recommendedBadge,
        Border? statusBadge,
        TextBlock? statusBadgeText,
        TextBlock? taglineText,
        TextBlock? descriptionText,
        TextBlock? specsText,
        TextBlock? detailText,
        ProgressBar? progressBar,
        Button? downloadButton,
        Button? cancelButton,
        Button? removeButton,
        Button? chooseProviderButton = null)
    {
        if (cardButton is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cardButton, card.AutomationName);
            cardButton.IsEnabled = card.CanSelect;
        }

        if (recommendedBadge is not null)
        {
            recommendedBadge.Visibility = card.IsRecommended ? Visibility.Visible : Visibility.Collapsed;
        }

        if (statusBadge is not null)
        {
            var chipStyleKey = card.IsInstalled
                ? "BrandSuccessChipStyle"
                : card.ShowProgress
                    ? "BrandAccentChipStyle"
                    : "BrandNeutralChipStyle";
            if (Application.Current.Resources[chipStyleKey] is Style chipStyle)
            {
                statusBadge.Style = chipStyle;
            }
        }

        if (statusBadgeText is not null)
        {
            statusBadgeText.Text = card.StatusText;
            var textStyleKey = card.IsInstalled
                ? "BrandSuccessChipTextStyle"
                : card.ShowProgress
                    ? "BrandAccentChipTextStyle"
                    : "BrandNeutralChipTextStyle";
            if (Application.Current.Resources[textStyleKey] is Style textStyle)
            {
                statusBadgeText.Style = textStyle;
            }
        }

        if (taglineText is not null)
        {
            taglineText.Text = card.Tagline;
        }

        if (descriptionText is not null)
        {
            descriptionText.Text = card.Description;
        }

        if (specsText is not null)
        {
            specsText.Text = $"{card.QualityText} \u00B7 {card.SpeedText} \u00B7 {card.DiskSizeText}";
        }

        if (detailText is not null)
        {
            detailText.Text = card.StatusDetail;
        }

        if (progressBar is not null)
        {
            progressBar.Visibility = card.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            progressBar.Value = card.DownloadPercent;
        }

        if (downloadButton is not null)
        {
            downloadButton.Content = card.DownloadButtonText;
            downloadButton.IsEnabled = card.DownloadButtonEnabled;
            downloadButton.Visibility = card.ShowDownloadButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (cancelButton is not null)
        {
            cancelButton.Visibility = card.ShowCancelButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (removeButton is not null)
        {
            removeButton.IsEnabled = card.RemoveButtonEnabled;
            removeButton.Visibility = card.ShowRemoveButton ? Visibility.Visible : Visibility.Collapsed;
        }

        if (chooseProviderButton is not null)
        {
            chooseProviderButton.Visibility = card.ShowChooseProviderButton ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal void EgOneMemoryPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string modelId })
        {
            UpdateEgOneMemoryTrackAndLayout(modelId);
        }
    }

    private void ApplyEgOneMemoryPresentation(string modelId, EgOneCardPresentation card)
    {
        if (card.Memory is not { } memory)
        {
            return;
        }

        var controls = ResolveEgOneMemoryControls(modelId);
        if (controls is null)
        {
            return;
        }

        var (panel, headerGrid, valueRow, valueText, lightestText, track, usedBar, peakBar, keyGrid, inUseEntry, inUseText, sizeEntry, sizeDot, sizeText, percentText, warningText) = controls.Value;
        var warned = memory.Warning != EgOneMemoryWarning.None;

        if (valueText is not null)
        {
            valueText.Text = memory.ValueText;
            var valueStyleKey = warned ? "BrandMemoryWarningTextStyle" : "BrandMemoryDefaultTextStyle";
            if (Application.Current.Resources[valueStyleKey] is Style valueStyle)
            {
                valueText.Style = valueStyle;
            }
        }

        if (lightestText is not null)
        {
            lightestText.Visibility = !string.IsNullOrWhiteSpace(memory.LightestMark)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var fillStyleKey = warned ? "BrandMemoryWarningFillStyle" : "BrandMemoryAccentFillStyle";
        if (Application.Current.Resources[fillStyleKey] is Style fillStyle)
        {
            if (peakBar is not null)
            {
                peakBar.Style = fillStyle;
            }

            if (sizeDot is not null)
            {
                sizeDot.Style = fillStyle;
            }
        }

        if (keyGrid is not null)
        {
            if (memory.Key is { } key)
            {
                keyGrid.Visibility = Visibility.Visible;
                if (inUseText is not null)
                {
                    inUseText.Text = key.InUseLabel;
                }

                if (sizeText is not null)
                {
                    sizeText.Text = key.SizeLabel;
                    var sizeStyleKey = warned ? "BrandMemoryWarningTextStyle" : "BrandMemoryAccentTextStyle";
                    if (Application.Current.Resources[sizeStyleKey] is Style sizeStyle)
                    {
                        sizeText.Style = sizeStyle;
                    }
                }

                if (percentText is not null)
                {
                    percentText.Text = key.PercentText;
                    var percentStyleKey = warned ? "BrandMemoryWarningTextStyle" : "BrandMemorySecondaryTextStyle";
                    if (Application.Current.Resources[percentStyleKey] is Style percentStyle)
                    {
                        percentText.Style = percentStyle;
                    }
                }
            }
            else
            {
                keyGrid.Visibility = Visibility.Collapsed;
            }
        }

        if (warningText is not null)
        {
            warningText.Text = memory.WarningText ?? string.Empty;
            warningText.Visibility = string.IsNullOrWhiteSpace(memory.WarningText)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        UpdateEgOneMemoryTrackAndLayout(modelId);
    }

    private void UpdateEgOneMemoryTrackAndLayout(string modelId)
    {
        if (!_latestEgOneMemoryPresentations.TryGetValue(modelId, out var memory))
        {
            return;
        }

        var controls = ResolveEgOneMemoryControls(modelId);
        if (controls is null)
        {
            return;
        }

        var (panel, headerGrid, valueRow, _, _, track, usedBar, peakBar, keyGrid, inUseEntry, _, sizeEntry, _, _, percentText, _) = controls.Value;
        var trackWidth = track?.ActualWidth ?? 0d;
        if (trackWidth > 0d)
        {
            var usedWidth = memory.UsedFraction > 0d
                ? Math.Clamp(Math.Round(trackWidth * memory.UsedFraction), 0d, trackWidth)
                : 0d;
            if (usedBar is not null)
            {
                usedBar.Width = usedWidth;
                usedBar.Visibility = usedWidth > 0d ? Visibility.Visible : Visibility.Collapsed;
            }

            var peakLeft = Math.Clamp(Math.Round(trackWidth * memory.PeakStart), 0d, trackWidth);
            var peakWidth = memory.PeakFraction > 0d
                ? Math.Clamp(Math.Round(trackWidth * memory.PeakFraction), 0d, Math.Max(0d, trackWidth - peakLeft))
                : 0d;
            if (peakBar is not null)
            {
                peakBar.Margin = new Thickness(peakLeft, 0d, 0d, 0d);
                peakBar.Width = peakWidth;
                peakBar.Visibility = peakWidth > 0d ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        var panelWidth = panel?.ActualWidth ?? 0d;
        if (panelWidth <= 0d)
        {
            return;
        }

        if (headerGrid is not null && valueRow is not null)
        {
            valueRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var stackHeader = valueRow.DesiredSize.Width + 72d > panelWidth;
            Grid.SetRow(valueRow, stackHeader ? 1 : 0);
            Grid.SetColumn(valueRow, stackHeader ? 0 : 1);
            Grid.SetColumnSpan(valueRow, stackHeader ? 2 : 1);
            valueRow.HorizontalAlignment = stackHeader ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        if (keyGrid is { Visibility: Visibility.Visible } &&
            inUseEntry is not null &&
            sizeEntry is not null &&
            percentText is not null)
        {
            var keyWidth = keyGrid.ActualWidth > 0d ? keyGrid.ActualWidth : panelWidth;
            var infinity = new Size(double.PositiveInfinity, double.PositiveInfinity);
            inUseEntry.Measure(infinity);
            sizeEntry.Measure(infinity);
            percentText.Measure(infinity);

            var gap = 12d;
            var firstTwoWidth = inUseEntry.DesiredSize.Width + gap + sizeEntry.DesiredSize.Width;
            var oneLineFits = firstTwoWidth + gap + percentText.DesiredSize.Width <= keyWidth;
            var twoLinesFit = firstTwoWidth <= keyWidth;

            if (oneLineFits)
            {
                Grid.SetRow(inUseEntry, 0);
                Grid.SetColumn(inUseEntry, 0);
                Grid.SetColumnSpan(inUseEntry, 1);

                Grid.SetRow(sizeEntry, 0);
                Grid.SetColumn(sizeEntry, 1);
                Grid.SetColumnSpan(sizeEntry, 1);

                Grid.SetRow(percentText, 0);
                Grid.SetColumn(percentText, 2);
                Grid.SetColumnSpan(percentText, 1);
                percentText.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else if (twoLinesFit)
            {
                Grid.SetRow(inUseEntry, 0);
                Grid.SetColumn(inUseEntry, 0);
                Grid.SetColumnSpan(inUseEntry, 1);

                Grid.SetRow(sizeEntry, 0);
                Grid.SetColumn(sizeEntry, 1);
                Grid.SetColumnSpan(sizeEntry, 2);

                Grid.SetRow(percentText, 1);
                Grid.SetColumn(percentText, 0);
                Grid.SetColumnSpan(percentText, 3);
                percentText.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                Grid.SetRow(inUseEntry, 0);
                Grid.SetColumn(inUseEntry, 0);
                Grid.SetColumnSpan(inUseEntry, 3);

                Grid.SetRow(sizeEntry, 1);
                Grid.SetColumn(sizeEntry, 0);
                Grid.SetColumnSpan(sizeEntry, 3);

                Grid.SetRow(percentText, 2);
                Grid.SetColumn(percentText, 0);
                Grid.SetColumnSpan(percentText, 3);
                percentText.HorizontalAlignment = HorizontalAlignment.Left;
            }
        }
    }

    private (
        StackPanel? Panel,
        Grid? HeaderGrid,
        StackPanel? ValueRow,
        TextBlock? ValueText,
        TextBlock? LightestText,
        Border? Track,
        Border? UsedBar,
        Border? PeakBar,
        Grid? KeyGrid,
        StackPanel? InUseEntry,
        TextBlock? InUseText,
        StackPanel? SizeEntry,
        Border? SizeDot,
        TextBlock? SizeText,
        TextBlock? PercentText,
        TextBlock? WarningText)? ResolveEgOneMemoryControls(string modelId) =>
        EnginePolishPresentation.NormalizeEgOneModelId(modelId) switch
        {
            EnginePolishPresentation.EgOneMiniModelId => (
                EgOneMiniMemoryPanel,
                EgOneMiniMemoryHeaderGrid,
                EgOneMiniMemoryValueRow,
                EgOneMiniMemoryValueText,
                EgOneMiniMemoryLightestText,
                EgOneMiniMemoryTrack,
                EgOneMiniMemoryUsedBar,
                EgOneMiniMemoryPeakBar,
                EgOneMiniMemoryKeyGrid,
                EgOneMiniMemoryKeyInUseEntry,
                EgOneMiniMemoryKeyInUseText,
                EgOneMiniMemoryKeySizeEntry,
                EgOneMiniMemoryKeySizeDot,
                EgOneMiniMemoryKeySizeText,
                EgOneMiniMemoryKeyPercentText,
                EgOneMiniMemoryWarningText),
            EnginePolishPresentation.EgOneMicroModelId => (
                EgOneMicroMemoryPanel,
                EgOneMicroMemoryHeaderGrid,
                EgOneMicroMemoryValueRow,
                EgOneMicroMemoryValueText,
                EgOneMicroMemoryLightestText,
                EgOneMicroMemoryTrack,
                EgOneMicroMemoryUsedBar,
                EgOneMicroMemoryPeakBar,
                EgOneMicroMemoryKeyGrid,
                EgOneMicroMemoryKeyInUseEntry,
                EgOneMicroMemoryKeyInUseText,
                EgOneMicroMemoryKeySizeEntry,
                EgOneMicroMemoryKeySizeDot,
                EgOneMicroMemoryKeySizeText,
                EgOneMicroMemoryKeyPercentText,
                EgOneMicroMemoryWarningText),
            _ => (
                EgOneFullMemoryPanel,
                EgOneFullMemoryHeaderGrid,
                EgOneFullMemoryValueRow,
                EgOneFullMemoryValueText,
                EgOneFullMemoryLightestText,
                EgOneFullMemoryTrack,
                EgOneFullMemoryUsedBar,
                EgOneFullMemoryPeakBar,
                EgOneFullMemoryKeyGrid,
                EgOneFullMemoryKeyInUseEntry,
                EgOneFullMemoryKeyInUseText,
                EgOneFullMemoryKeySizeEntry,
                EgOneFullMemoryKeySizeDot,
                EgOneFullMemoryKeySizeText,
                EgOneFullMemoryKeyPercentText,
                EgOneFullMemoryWarningText),
        };

    internal void EgOneDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string modelId ||
            !EnginePolishPresentation.IsKnownEgOneModelId(modelId))
        {
            return;
        }

        ShowEgOneNotice(string.Empty);
        EgOneModelDownloadRequested?.Invoke(EnginePolishPresentation.NormalizeEgOneModelId(modelId));
    }

    internal void CancelEgOneDownloadButton_Click(object sender, RoutedEventArgs e) =>
        EgOneModelDownloadCancelRequested?.Invoke();

    internal async void EgOneRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Closing ||
            (sender as FrameworkElement)?.Tag is not string rawModelId ||
            !EnginePolishPresentation.IsKnownEgOneModelId(rawModelId))
        {
            return;
        }

        var normalizedId = EnginePolishPresentation.NormalizeEgOneModelId(rawModelId);
        ShowEgOneNotice(string.Empty);
        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowEgOneNotice(EnginePolishPresentation.EgOneRemovalBlockedBusyNotice);
            return;
        }

        var selectedEgOne = EnginePolishPresentation.NormalizeEgOneModelId(
            _selectedEgOneModelId ?? (_settings?.Preferences.Polish.Provider == PolishProvider.EgOne
                ? _settings.Preferences.Polish.ModelId
                : null));
        var activeProvider = _settings?.Preferences.Polish.Provider
            ?? PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        var isSelected = string.Equals(normalizedId, selectedEgOne, StringComparison.Ordinal);
        if (isSelected)
        {
            if (Content?.XamlRoot is not { } xamlRoot)
            {
                return;
            }

            var confirmation = EnginePolishPresentation.DescribeEgOneRemovalDialog(
                normalizedId,
                isCurrentlySelected: true,
                egOneActiveProvider: activeProvider == PolishProvider.EgOne);
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                RequestedTheme = WindowRoot.ActualTheme,
                Title = confirmation.Title,
                Content = new TextBlock
                {
                    Text = confirmation.Message,
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = confirmation.ConfirmButtonText,
                PrimaryButtonStyle = Application.Current.Resources["BrandDestructiveButtonStyle"] as Style,
                CloseButtonText = confirmation.CancelButtonText,
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _session.Closing)
            {
                return;
            }
        }

        if (IsSpeechEngineBusy?.Invoke() == true)
        {
            ShowEgOneNotice(EnginePolishPresentation.EgOneRemovalBlockedBusyNotice);
            return;
        }

        EgOneModelRemoveRequested?.Invoke(normalizedId);
    }

    private (bool ServerExists, bool ModelExists) CheckEgOneLocalAssets(string normalizedModelId, string? configuredModelId = null)
    {
        try
        {
            var serverExecutable = EgOneRuntimeLocator.ResolveServerExecutable(
                AppContext.BaseDirectory,
                _dataDirectory,
                _storeInstalled,
                Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_SERVER_EXE"));
            var serverExists = File.Exists(serverExecutable);

            var envOverride = configuredModelId is not null &&
                string.Equals(normalizedModelId, configuredModelId, StringComparison.Ordinal)
                    ? Environment.GetEnvironmentVariable("ENVIOUSWISPR_EG1_MODEL_PATH")
                    : null;
            var modelFile = EgOneRuntimeLocator.ResolveModelFile(
                _dataDirectory,
                normalizedModelId,
                envOverride);
            var modelExists = EgOneRuntimeLocator.IsModelFileReady(modelFile);

            return (serverExists, modelExists);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, false);
        }
    }

    internal void EgOneSizeCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _isUpdatingEgOneCards || _settings is null)
        {
            return;
        }

        var modelId = (sender as FrameworkElement)?.Tag as string;
        if (!EnginePolishPresentation.IsKnownEgOneModelId(modelId))
        {
            return;
        }

        var normalized = EnginePolishPresentation.NormalizeEgOneModelId(modelId);
        _selectedEgOneModelId = normalized;
        _selectedPolishModelId = _polishModelSetByApp = normalized;

        UpdateEgOneCards();
        UpdateActivePolishProviderBadgeIfEgOneSelected();
        _ = TriggerAutoSaveAsync();
    }

    internal void EgOneUnloadPolicyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        UpdateEgOneUnloadImmediatelyNotice();
        _ = TriggerAutoSaveAsync();
    }

    private void UpdateEgOneUnloadImmediatelyNotice()
    {
        if (EgOneUnloadPolicyComboBox is null || EgOneUnloadImmediatelyNotice is null)
        {
            return;
        }

        var selectedPolicy = SettingsPresenter.PolishUnloadPolicyFromIndex(
            EgOneUnloadPolicyComboBox.SelectedIndex);
        EgOneUnloadImmediatelyNotice.Visibility = selectedPolicy == PolishUnloadPolicy.Immediately
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private int ResolvePolishProviderIndexFromCard(object sender) =>
        sender is RadioButton { DataContext: SelectableChoiceOption option }
        && Array.IndexOf(PolishProviderChoices, option) is var idx and >= 0
            ? idx
            : SelectedIndexOf(PolishProviderChoices);

    private async Task ApplySelectedPolishProviderCardAsync(
        int selectedIndex,
        PolishProvider provider,
        bool allowDefaultModel = true)
    {
        SelectChoice(PolishProviderChoices, selectedIndex);
        _lastActivePolishProvider = provider;
        if (provider == PolishProvider.EgOne)
        {
            _selectedEgOneModelId ??= EnginePolishPresentation.EgOneFullModelId;
            _selectedPolishModelId = _polishModelSetByApp = _selectedEgOneModelId;
        }

        UpdateActivePolishProviderDisplay();
        if (PolishProviderChooserPanel is not null)
        {
            PolishProviderChooserPanel.Visibility = Visibility.Collapsed;
        }

        ApiKeyPasswordBox.Password = string.Empty;
        _cloudKeyRejectedProvider = null;
        RefreshApiKeyStatus();
        var chooseDefault = allowDefaultModel && provider != PolishProvider.EgOne;
        await RefreshPolishModelChoicesAsync(
            provider,
            chooseDefault: chooseDefault).ConfigureAwait(true);
        if (provider == PolishProvider.EgOne)
        {
            UpdateEgOneCards();
        }

        _ = TriggerAutoSaveAsync();
    }

    internal async void PolishProviderCardChecked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var selectedIndex = ResolvePolishProviderIndexFromCard(sender);
        var provider = PolishProviderFromIndex(selectedIndex);
        if (_lastActivePolishProvider == provider)
        {
            return;
        }

        await ApplySelectedPolishProviderCardAsync(selectedIndex, provider).ConfigureAwait(true);
    }

    internal async void PolishProviderCardClicked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings || _settings is null)
        {
            return;
        }

        var selectedIndex = ResolvePolishProviderIndexFromCard(sender);
        var provider = PolishProviderFromIndex(selectedIndex);
        if (_lastActivePolishProvider == provider)
        {
            if (PolishProviderChooserPanel is not null)
            {
                PolishProviderChooserPanel.Visibility = Visibility.Collapsed;
            }

            return;
        }

        await ApplySelectedPolishProviderCardAsync(selectedIndex, provider).ConfigureAwait(true);
    }

    internal void PolishModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings || _isUpdatingPolishModelPicker || _settings is null)
        {
            return;
        }

        if (PolishModelPicker.SelectedItem is string modelId &&
            !string.Equals(_selectedPolishModelId, modelId, StringComparison.Ordinal))
        {
            _selectedPolishModelId = _polishModelSetByApp = modelId;
            UpdateActivePolishProviderDisplay();
            _ = WarmUpSelectedOllamaModelAsync(modelId);
            _ = TriggerAutoSaveAsync();
        }
    }

    private bool IsSelectedOllamaModelHosted()
    {
        var current = _selectedPolishModelId.Trim();
        if (string.IsNullOrEmpty(current))
        {
            current = _settings?.Preferences.Polish.ModelId?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(current))
        {
            return false;
        }

        if (OllamaModelCatalog.LooksLikeHostedCloudModel(current))
        {
            return true;
        }

        return OllamaModels?.View.HostedRows.Any(row =>
            row.Installed &&
            OllamaModelCatalog.SameModel(row.Id, current)) == true;
    }

    internal async void SaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        switch (_session.Provider.SaveKey(provider, ApiKeyPasswordBox.Password))
        {
            case ApiKeySaveOutcome.NotACloudProvider:
                ShowMessage(
                    "No cloud key needed",
                    "Choose OpenAI, Anthropic, or Gemini before storing a provider key.",
                    InfoBarSeverity.Informational);
                return;
            case ApiKeySaveOutcome.EmptyKey:
                ShowMessage("Enter an API key", "The key field is empty.", InfoBarSeverity.Warning);
                ApiKeyPasswordBox.Focus(FocusState.Programmatic);
                return;
            case ApiKeySaveOutcome.Saved:
                ApiKeyPasswordBox.Password = string.Empty;
                _cloudKeyRejectedProvider = null;
                RefreshApiKeyStatus();
                UpdateHistoryPolishState();
                ShowMessage(
                    $"{ProviderDisplayName(provider)} key saved",
                    "The key is stored in Windows Credential Manager. It is not part of settings, profiles, history, or diagnostics.",
                    InfoBarSeverity.Success);
                await RefreshPolishModelChoicesAsync(provider, chooseDefault: true)
                    .ConfigureAwait(true);
                UpdateActivePolishProviderDisplay();
                _ = TriggerAutoSaveAsync();
                return;
            default:
                ApiKeyPasswordBox.Password = string.Empty;
                RefreshApiKeyStatus();
                ShowMessage(
                    "API key was not saved",
                    "Windows Credential Manager is unavailable. No key was written to settings or exports.",
                    InfoBarSeverity.Error);
                return;
        }
    }

    internal async void RemoveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        switch (_session.Provider.CheckKeyRemoval(provider))
        {
            case ApiKeyRemovalCheck.NotACloudProvider:
                return;
            case ApiKeyRemovalCheck.NothingStored:
                RefreshApiKeyStatus();
                ShowMessage(
                    "No stored key to remove",
                    $"No {ProviderDisplayName(provider)} key is stored for EnviousWispr in Windows Credential Manager.",
                    InfoBarSeverity.Informational);
                return;
            default:
                break;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = $"Remove the stored {ProviderDisplayName(provider)} key?",
            Content = "This deletes the EnviousWispr credential from Windows Credential Manager. Settings, history, dictionary entries, and snippets are not affected.",
            PrimaryButtonText = "Remove key",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (_session.Provider.RemoveKey(provider) == ApiKeyRemoveOutcome.Removed)
        {
            ApiKeyPasswordBox.Password = string.Empty;
            _cloudKeyRejectedProvider = null;
            RefreshApiKeyStatus();
            UpdateHistoryPolishState();
            await RefreshPolishModelChoicesAsync(provider, chooseDefault: false)
                .ConfigureAwait(true);
            UpdateActivePolishProviderDisplay();
            ShowMessage(
                $"{ProviderDisplayName(provider)} key removed",
                "The stored credential was removed from Windows Credential Manager.",
                InfoBarSeverity.Success);
        }
        else
        {
            RefreshApiKeyStatus();
            ShowMessage(
                "API key could not be removed",
                "Windows Credential Manager is unavailable. Existing settings were not changed.",
                InfoBarSeverity.Error);
        }
    }

    internal async void RefreshPolishModelsButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        if (provider is PolishProvider.None or PolishProvider.EgOne)
        {
            return;
        }

        // A DISTINCT IN-PROGRESS LINE, SO A RETRY IS AUDIBLE. The setter only announces when the
        // words change, which is right - but a refresh that fails the same way twice then produces
        // the same sentence twice and says nothing the second time, leaving somebody who pressed the
        // button with no confirmation it did anything. Saying "refreshing" first guarantees the
        // result is always a change from what preceded it.
        SetLiveText(ApiKeyStatusText, "Refreshing the list of available models...");
        await RefreshPolishModelChoicesAsync(provider, chooseDefault: false)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// What the app itself last wrote into the model field: loaded, chosen as a default, or repaired. The field still
    /// reading it means the person has not edited it, which is what lets a repair replace it. Ref: #213 review.
    /// </summary>
    private string _polishModelSetByApp = string.Empty;

    /// <param name="ollamaChange">A download or removal just made on the Ollama models block, to repair the field against; null otherwise.</param>
    /// <param name="silentPoll">True when triggered by background live polling or startup detection so the Ollama models block is not re-fetched.</param>
    private async Task RefreshPolishModelChoicesAsync(
        PolishProvider provider,
        bool chooseDefault,
        OllamaModelChange? ollamaChange = null,
        bool silentPoll = false)
    {
        if (!IsSettingsPageLoaded || PolishModelPicker is null || OllamaEndpointTextBox is null)
        {
            return;
        }

        if (ollamaChange is null && !silentPoll)
        {
            ShowOllamaModels(provider);
        }

        var isCloudProvider = IsCloudProvider(provider);
        var hasSavedCloudKey = !isCloudProvider || _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found;
        var usesAModel = ProviderSettingsPresenter.UsesAModel(provider);
        var showModelPicker = usesAModel && hasSavedCloudKey;
        PolishModelPickerRow.Visibility = showModelPicker ? Visibility.Visible : Visibility.Collapsed;
        CloudModelsKeyRequiredRow.Visibility = isCloudProvider && !hasSavedCloudKey
            ? Visibility.Visible
            : Visibility.Collapsed;
        OllamaEndpointTextBoxRow.Visibility = provider == PolishProvider.Ollama
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyPasswordBoxRow.Visibility = isCloudProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApiKeyButtonPanel.Visibility = isCloudProvider
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshPolishModelsButtonRow.Visibility = provider == PolishProvider.Ollama ||
            (isCloudProvider && hasSavedCloudKey)
                ? Visibility.Visible
                : Visibility.Collapsed;
        RefreshPolishModelsButton.IsEnabled = false;

        if (isCloudProvider && !hasSavedCloudKey)
        {
            _isUpdatingPolishModelPicker = true;
            try
            {
                PolishModelPicker.ItemsSource = Array.Empty<string>();
                PolishModelPicker.SelectedIndex = -1;
                PolishModelPicker.PlaceholderText = "No models listed";
                PolishModelPicker.IsEnabled = false;
            }
            finally
            {
                _isUpdatingPolishModelPicker = false;
            }
        }

        // THE ENDPOINT AND SELECTION ARE READ HERE, ON THE UI THREAD, BEFORE THE DISCOVERY;
        // if another refresh overtakes this one, or if the person changes provider/selection meanwhile,
        // no stale listing overwrites what they chose last.
        var endpoint = NullIfBlank(OllamaEndpointTextBox.Text);
        var selectionBefore = _selectedPolishModelId;
        var listing = await _session.Provider.ListModelsAsync(provider, endpoint)
            .ConfigureAwait(true);
        if (listing is null || _session.Closing || !_session.Provider.IsCurrent(listing.Ticket))
        {
            return;
        }

        if (PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices)) != provider ||
            (provider == PolishProvider.Ollama &&
             !string.Equals(NullIfBlank(OllamaEndpointTextBox.Text), endpoint, StringComparison.Ordinal)))
        {
            return;
        }

        if (isCloudProvider && _session.Provider.KeyStatus(provider) != ApiKeyReadStatus.Found)
        {
            RefreshApiKeyStatus();
            return;
        }

        var selectedMeanwhile = !string.Equals(_selectedPolishModelId, selectionBefore, StringComparison.Ordinal);
        var choices = _session.Provider.Choose(listing, _selectedPolishModelId, chooseDefault && !selectedMeanwhile);

        var providerUsesAModel = ProviderSettingsPresenter.UsesAModel(provider);
        var pickerReady = providerUsesAModel && (!isCloudProvider || _session.Provider.KeyStatus(provider) == ApiKeyReadStatus.Found);
        var displayedChoices = pickerReady
            ? ProviderSettingsPresenter.IncludeActiveSelectionIfMissing(choices, _selectedPolishModelId)
            : choices;
        RefreshPolishModelsButton.IsEnabled = pickerReady;
        _isUpdatingPolishModelPicker = true;
        try
        {
            PolishModelPicker.ItemsSource = displayedChoices.Models;
            PolishModelPicker.PlaceholderText = displayedChoices.Models.Count > 0
                ? "Choose an available model"
                : "No models listed";
            PolishModelPicker.IsEnabled = pickerReady && displayedChoices.Models.Count > 0;
            if (displayedChoices.ModelToApply is { } model)
            {
                _selectedPolishModelId = _polishModelSetByApp = model;
            }

            PolishModelPicker.SelectedIndex = displayedChoices.SelectedIndex;

            if (ollamaChange is not null &&
                !selectedMeanwhile &&
                displayedChoices.Discovery is { Status: PolishModelDiscoveryStatus.Ready } &&
                OllamaModelsPresenter.RepairSelection(
                    displayedChoices.Models,
                    _selectedPolishModelId,
                    _polishModelSetByApp,
                    ollamaChange) is { } repaired)
            {
                _selectedPolishModelId = _polishModelSetByApp = repaired;
                var repairedIndex = -1;
                for (var i = 0; i < displayedChoices.Models.Count && repairedIndex < 0; i++)
                {
                    repairedIndex = OllamaModelCatalog.SameModel(displayedChoices.Models[i], repaired) ? i : -1;
                }

                PolishModelPicker.SelectedIndex = repairedIndex;
            }
        }
        finally
        {
            _isUpdatingPolishModelPicker = false;
        }

        if (choices.Discovery is { } discovery)
        {
            _cloudKeyRejectedProvider = discovery.Status == PolishModelDiscoveryStatus.KeyRejected
                ? provider
                : null;
            SetLiveText(ApiKeyStatusText, ModelDiscoveryNotice(provider, discovery));
            UpdateHistoryPolishState();
        }
    }

    /// <summary>The words for what a model discovery came back with.</summary>
    private static string ModelDiscoveryNotice(PolishProvider provider, PolishModelDiscovery discovery)
    {
        var count = discovery.ModelIds.Count;
        var plural = count == 1 ? string.Empty : "s";
        return discovery.Status switch
        {
            PolishModelDiscoveryStatus.Ready when provider == PolishProvider.Ollama =>
                $"{count.ToString(CultureInfo.CurrentCulture)} Ollama model{plural} available. Local models stay on this PC; hosted models run on Ollama's servers.",
            PolishModelDiscoveryStatus.OllamaNotReady =>
                "Ollama is not ready at this endpoint. Start Ollama or enter another loopback endpoint.",
            PolishModelDiscoveryStatus.Ready when count > 0 =>
                $"{count.ToString(CultureInfo.CurrentCulture)} compatible {ProviderDisplayName(provider)} model{plural} available to the stored key. No transcript or generation request was sent.",
            PolishModelDiscoveryStatus.Ready =>
                $"{ProviderDisplayName(provider)} returned no compatible transcript-polish models.",
            PolishModelDiscoveryStatus.MissingCredential =>
                "Save your API key to load available models.",
            PolishModelDiscoveryStatus.CredentialUnavailable =>
                "Windows Credential Manager could not provide the stored key. No provider request was sent.",
            PolishModelDiscoveryStatus.KeyRejected =>
                $"{ProviderDisplayName(provider)} rejected the stored key while listing models. Replace the key and try again.",
            PolishModelDiscoveryStatus.ProviderUnavailable =>
                $"{ProviderDisplayName(provider)} model discovery is temporarily unavailable. Check your connection and try again.",
            PolishModelDiscoveryStatus.InvalidResponse =>
                $"{ProviderDisplayName(provider)} returned an unrecognized model catalog. Try refreshing again.",
            _ => throw new ArgumentOutOfRangeException(nameof(discovery), discovery.Status, null),
        };
    }

    private void RefreshApiKeyStatus()
    {
        if (!IsSettingsPageLoaded
            || ApiKeyPasswordBox is null
            || SaveApiKeyButton is null
            || RemoveApiKeyButton is null
            || ApiKeyStatusText is null)
        {
            return;
        }

        var provider = PolishProviderFromIndex(SelectedIndexOf(PolishProviderChoices));
        var isCloudProvider = IsCloudProvider(provider);
        ApiKeyPasswordBox.IsEnabled = isCloudProvider;
        SaveApiKeyButton.IsEnabled = isCloudProvider;
        RemoveApiKeyButton.IsEnabled = isCloudProvider;
        ApiKeyPasswordBox.PlaceholderText = isCloudProvider
            ? $"Enter {ProviderDisplayName(provider)} API key"
            : "Choose a direct cloud provider to manage its key";

        if (!isCloudProvider)
        {
            SetLiveText(
                ApiKeyStatusText,
    provider switch
                {
                    PolishProvider.Ollama => "Ollama runs on this PC and does not use a direct cloud API key here. Local models stay on this PC; hosted models run on Ollama's servers.",
                    PolishProvider.EgOne => "EG-1 runs on this PC and does not use a cloud API key.",
                    _ => "AI polish is off; no provider key is used.",
            
                });
            return;
        }

        SetLiveText(
            ApiKeyStatusText,
    _session.Provider.KeyStatus(provider) switch
            {
                ApiKeyReadStatus.Found =>
                    $"{CredentialArticle(provider)} {ProviderDisplayName(provider)} key is stored in Windows Credential Manager.",
                // SAY WHAT IT MEANS FOR THE DICTATION, NOT JUST WHAT IS TRUE OF THE MACHINE. "No key is
                // stored" is a fact about storage; the thing a person needs to know is that every
                // dictation will come out unpolished until they add one, and nothing will tell them
                // again at the moment it happens. macOS carries the same warning for the same reason.
                ApiKeyReadStatus.Missing =>
                    $"No {ProviderDisplayName(provider)} key is stored on this PC. Dictation still "
                        + "works, and cleanup falls back to your raw, unedited text every time until "
                        + "you add one.",
                _ => "Windows Credential Manager status is unavailable. No key value was revealed.",
        
            });
    }

    private static PolishProvider PolishProviderFromIndex(int index) => SettingsPresenter.PolishProviderFromIndex(index);

    private static bool IsCloudProvider(PolishProvider provider) => ProviderSettingsPresenter.IsCloudProvider(provider);

    private static string ProviderDisplayName(PolishProvider provider) => provider switch
    {
        PolishProvider.EgOne => "EG-1",
        PolishProvider.OpenAI => "OpenAI",
        PolishProvider.Anthropic => "Anthropic",
        PolishProvider.Gemini => "Gemini",
        _ => provider.ToString(),
    };

    private static string CredentialArticle(PolishProvider provider) => provider is
        PolishProvider.OpenAI or PolishProvider.Anthropic
            ? "An"
            : "A";

    private static int PolishProviderIndex(PolishProvider provider) => SettingsPresenter.PolishProviderIndex(provider);
}
