using System.ComponentModel;
using System.Globalization;
using EnviousWispr.App.Controls;
using EnviousWispr.App.Services;
using EnviousWispr.Core.Credentials;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.History;
using EnviousWispr.Core.Polish;
using EnviousWispr.Core.Presentation;
using EnviousWispr.Core.Reliability;
using EnviousWispr.Core.Runtime;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;
using EnviousWispr.Pipeline;
using EnviousWispr.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EnviousWispr.App;

/// <summary>Dictation history diary, search, filtering, re-polish, playback, and recovery card UI.</summary>
public sealed partial class MainWindow
{
    public sealed class HistoryDayHeaderViewModel
    {
        public HistoryDayHeaderViewModel(HistoryDayHeader header, int dayEntryCount)
        {
            DayHeaderTitle = !string.IsNullOrWhiteSpace(header.SecondaryDateLabel)
                ? header.SecondaryDateLabel
                : header.PrimaryTitle;
            DayHeaderSubtitle = !string.IsNullOrWhiteSpace(header.RelativeLabel)
                ? header.RelativeLabel.ToUpper(CultureInfo.CurrentCulture)
                : string.Empty;
            DayHeaderSubtitleVisibility = !string.IsNullOrEmpty(DayHeaderSubtitle)
                ? Visibility.Visible
                : Visibility.Collapsed;
            DayCountText = HistoryDiaryPresentation.FormatItemCount(dayEntryCount);
            AccessibilityLabel = $"{header.AccessibilityLabel}, {DayCountText}";
        }

        public string DayHeaderTitle { get; }

        public string DayHeaderSubtitle { get; }

        public Visibility DayHeaderSubtitleVisibility { get; }

        public string DayCountText { get; }

        public string AccessibilityLabel { get; }

        public override string ToString() => AccessibilityLabel;
    }

    public sealed class HistoryItemViewModel : INotifyPropertyChanged
    {
        private readonly DictationHistoryEntry _entry;
        private HistoryDiffResult _diffResult;
        private bool _isSelectionMode;
        private bool _isSelected;
        private bool _isTranscriptExpanded;
        private bool _isShowingEdits;
        private HistoryEditsViewMode _editsViewMode;
        private bool _hasOverflow;
        private bool _isGlobalBusy;
        private bool _isRepolishingThisCard;
        private bool _isPrimaryPasteTarget;
        private HistoryPolishReadiness _polishReadiness;
        private bool _hasAudioRecording;
        private long _audioDurationMs;
        private IReadOnlyList<float> _waveformPeaks;
        private bool _isPlaybackActive;
        private bool _isPlaying;
        private long _playbackPositionMs;
        private bool _isCopiedPrimary;
        private string? _inlineNotice;

        public HistoryItemViewModel(
            DictationHistoryEntry entry,
            bool isSelectionMode,
            bool isSelected,
            bool isTranscriptExpanded,
            bool isShowingEdits,
            HistoryEditsViewMode editsViewMode,
            HistoryDiffResult diffResult,
            bool isGlobalBusy,
            bool isRepolishingThisCard,
            bool isPrimaryPasteTarget,
            HistoryPolishReadiness polishReadiness,
            bool hasAudioRecording,
            long audioDurationMs,
            IReadOnlyList<float>? waveformPeaks,
            HistoryPlaybackSnapshot playbackSnapshot,
            bool isCopiedPrimary,
            string? inlineNotice)
        {
            _entry = entry;
            _diffResult = diffResult ?? HistoryDiffResult.Empty;
            _isSelectionMode = isSelectionMode;
            _isSelected = isSelected;
            _isTranscriptExpanded = isTranscriptExpanded;
            _isShowingEdits = isShowingEdits;
            _editsViewMode = editsViewMode;
            _isGlobalBusy = isGlobalBusy;
            _isRepolishingThisCard = isRepolishingThisCard;
            _isPrimaryPasteTarget = isPrimaryPasteTarget;
            _polishReadiness = polishReadiness;
            _hasAudioRecording = hasAudioRecording;
            _audioDurationMs = Math.Max(0L, audioDurationMs);
            _waveformPeaks = HistoryPlaybackPresentation.NormalizeOrFallbackPeaks(waveformPeaks);
            _isCopiedPrimary = isCopiedPrimary;
            _inlineNotice = inlineNotice;

            Id = entry.Id;
            Text = entry.Text;
            OriginalText = entry.OriginalText ?? string.Empty;
            var localCreated = entry.CreatedAt.ToLocalTime();
            CreatedDisplay = localCreated.ToString("f", CultureInfo.CurrentCulture);
            TimeDisplay = localCreated.ToString("t", CultureInfo.CurrentCulture);
            IsTemporary = entry.ExpiresAt is not null;
            HasPolishEdits = HistoryDiaryPresentation.HasPolishEdits(entry);

            FileChipVisibility = HistoryPresenter.IsFileTranscription(entry) ? Visibility.Visible : Visibility.Collapsed;
            RecoveryChipVisibility = IsTemporary ? Visibility.Visible : Visibility.Collapsed;
            RecoveryChipText = entry.ExpiresAt is { } expires
                ? FormatRecoveryExpiryChip(expires)
                : string.Empty;
            HeldSafelyChipVisibility = !entry.WasDelivered ? Visibility.Visible : Visibility.Collapsed;

            _hasOverflow = ShouldOfferShowMore(DisplayedCopyText);
            ApplySnapshotFields(playbackSnapshot);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public DictationHistoryEntry Entry => _entry;

        public Guid Id { get; }

        public string Text { get; }

        public string OriginalText { get; }

        public string CreatedDisplay { get; }

        public string TimeDisplay { get; }

        public bool IsTemporary { get; }

        public bool HasPolishEdits { get; }

        public bool HasAudioRecording => _hasAudioRecording;

        public long AudioDurationMs => _audioDurationMs;

        public Visibility FileChipVisibility { get; }

        public Visibility RecoveryChipVisibility { get; }

        public string RecoveryChipText { get; }

        public Visibility HeldSafelyChipVisibility { get; }

        public string ProvenanceText => BuildProvenanceText(_entry, _hasAudioRecording, _audioDurationMs);

        public Visibility KeepButtonVisibility => IsTemporary ? Visibility.Visible : Visibility.Collapsed;

        public Visibility HasPolishEditsVisibility => HasPolishEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility SelectionCheckBoxVisibility => _isSelectionMode ? Visibility.Visible : Visibility.Collapsed;

        public bool IsSelected => _isSelected;

        public string SelectionCheckBoxLabel => $"Select dictation from {TimeDisplay}";

        public Visibility SelectedAccentVisibility =>
            _isSelectionMode && _isSelected ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ActiveCardOutlineVisibility =>
            (_isSelectionMode && _isSelected) || _isPlaying ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ShowEditsInactiveVisibility =>
            HasPolishEdits && !_isShowingEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ShowEditsActiveVisibility =>
            HasPolishEdits && _isShowingEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ShowEditsPanelVisibility =>
            HasPolishEdits && _isShowingEdits ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PolishedActiveVisibility =>
            _editsViewMode == HistoryEditsViewMode.Polished ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PolishedInactiveVisibility =>
            _editsViewMode != HistoryEditsViewMode.Polished ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MarkedUpActiveVisibility =>
            _editsViewMode == HistoryEditsViewMode.MarkedUp ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MarkedUpInactiveVisibility =>
            _editsViewMode != HistoryEditsViewMode.MarkedUp ? Visibility.Visible : Visibility.Collapsed;

        public Visibility OriginalActiveVisibility =>
            _editsViewMode == HistoryEditsViewMode.Original ? Visibility.Visible : Visibility.Collapsed;

        public Visibility OriginalInactiveVisibility =>
            _editsViewMode != HistoryEditsViewMode.Original ? Visibility.Visible : Visibility.Collapsed;

        public HistoryEditsViewMode ActiveTranscriptViewMode =>
            HasPolishEdits && _isShowingEdits ? _editsViewMode : HistoryEditsViewMode.Polished;

        public IReadOnlyList<MarkedUpRun> MarkedUpRuns => _diffResult.Runs;

        public Visibility MarkedUpLegendVisibility =>
            HasPolishEdits
            && _isShowingEdits
            && _editsViewMode == HistoryEditsViewMode.MarkedUp
            && _diffResult.ChangeCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        public bool IsShowingOriginalView =>
            HasPolishEdits && _isShowingEdits && _editsViewMode == HistoryEditsViewMode.Original;

        public string ComparisonStatusText => _editsViewMode switch
        {
            HistoryEditsViewMode.Original => HistoryEditsPresentation.BeforePolishStatusText,
            HistoryEditsViewMode.MarkedUp => _diffResult.SummaryText,
            _ => HistoryEditsPresentation.PolishedStatusText,
        };

        public string CopyButtonGlyph => _isCopiedPrimary ? "\uE73E" : "\uE8C8";

        public string CopyButtonText => _isCopiedPrimary ? "Copied" : "Copy";

        public string DisplayedCopyText =>
            IsShowingOriginalView && !string.IsNullOrWhiteSpace(_entry.OriginalText)
                ? _entry.OriginalText
                : _entry.Text;

        public string DisplayText => DisplayedCopyText;

        public int TranscriptMaxLines => _isTranscriptExpanded || (_isShowingEdits && HasPolishEdits) ? 0 : 4;

        public Visibility ShowMoreToggleVisibility =>
            _hasOverflow && !(_isShowingEdits && HasPolishEdits) ? Visibility.Visible : Visibility.Collapsed;

        public string ShowMoreButtonText => _isTranscriptExpanded ? "Show less" : "Show more";

        public Visibility PlaybackRowVisibility => _hasAudioRecording ? Visibility.Visible : Visibility.Collapsed;

        public bool CanTogglePlayback =>
            HistoryPlaybackPresentation.CanTogglePlayback(_hasAudioRecording, _isGlobalBusy, _isPlaying);

        public string PlaybackButtonGlyph => _isPlaying ? "\uE769" : "\uE768";

        public string PlaybackButtonAutomationName =>
            _isPlaying ? $"Pause recording from {TimeDisplay}" : $"Play recording from {TimeDisplay}";

        public string PlaybackScrubberLabel => $"Recording playback position, {PlaybackClockText}";

        public IReadOnlyList<float> WaveformPeaks => _waveformPeaks;

        public bool IsPlaybackActive => _isPlaybackActive;

        public double PlaybackProgress =>
            _isPlaybackActive && _audioDurationMs > 0
                ? Math.Clamp((double)_playbackPositionMs / _audioDurationMs, 0.0, 1.0)
                : 0.0;

        public string PlaybackCurrentClockText =>
            HistoryPlaybackPresentation.FormatClock(
                _isPlaybackActive ? Math.Clamp(_playbackPositionMs, 0L, Math.Max(0L, _audioDurationMs)) : 0L);

        public string PlaybackTotalClockText => HistoryPlaybackPresentation.FormatClock(_audioDurationMs);

        public string PlaybackClockText =>
            HistoryPlaybackPresentation.FormatClockDisplay(_playbackPositionMs, _audioDurationMs, _isPlaybackActive);

        public string InlineNoticeText => _inlineNotice ?? string.Empty;

        public Visibility InlineNoticeVisibility =>
            !string.IsNullOrWhiteSpace(_inlineNotice) ? Visibility.Visible : Visibility.Collapsed;

        public bool IsRepolishing => _isRepolishingThisCard;

        public Visibility RepolishingVisibility => _isRepolishingThisCard ? Visibility.Visible : Visibility.Collapsed;

        public bool CanRunCardActions => !_isGlobalBusy;

        public Visibility RepolishButtonVisibility =>
            _polishReadiness.IsEnabled ? Visibility.Visible : Visibility.Collapsed;

        public bool CanRepolish => _polishReadiness.CanRepolishEntry(_isGlobalBusy, IsTemporary);

        public string PasteButtonAutomationId => _isPrimaryPasteTarget ? "PasteHistoryButton" : string.Empty;

        public void SetSelectionState(bool isSelectionMode, bool isSelected)
        {
            if (_isSelectionMode == isSelectionMode && _isSelected == isSelected)
            {
                return;
            }

            _isSelectionMode = isSelectionMode;
            _isSelected = isSelected;
            Notify(
                nameof(SelectionCheckBoxVisibility),
                nameof(IsSelected),
                nameof(SelectedAccentVisibility),
                nameof(ActiveCardOutlineVisibility));
        }

        public void SetPolishReadiness(HistoryPolishReadiness readiness)
        {
            if (_polishReadiness == readiness)
            {
                return;
            }

            _polishReadiness = readiness;
            Notify(nameof(RepolishButtonVisibility), nameof(CanRepolish));
        }

        public void SetCopiedPrimary(bool isCopied)
        {
            if (_isCopiedPrimary == isCopied)
            {
                return;
            }

            _isCopiedPrimary = isCopied;
            Notify(nameof(CopyButtonGlyph), nameof(CopyButtonText));
        }

        public void SetInlineNotice(string? notice)
        {
            var normalized = string.IsNullOrWhiteSpace(notice) ? null : notice.Trim();
            if (string.Equals(_inlineNotice, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _inlineNotice = normalized;
            Notify(nameof(InlineNoticeText), nameof(InlineNoticeVisibility));
        }

        public void SetWaveformData(IReadOnlyList<float> peaks, long durationMs)
        {
            _waveformPeaks = peaks;
            if (durationMs > 0)
            {
                _audioDurationMs = durationMs;
            }

            Notify(
                nameof(WaveformPeaks),
                nameof(PlaybackProgress),
                nameof(PlaybackCurrentClockText),
                nameof(PlaybackTotalClockText),
                nameof(PlaybackClockText),
                nameof(PlaybackScrubberLabel),
                nameof(ProvenanceText));
        }

        public void MarkRecordingMissing()
        {
            if (!_hasAudioRecording)
            {
                return;
            }

            _hasAudioRecording = false;
            _isPlaybackActive = false;
            _isPlaying = false;
            _playbackPositionMs = 0;
            Notify(
                nameof(PlaybackRowVisibility),
                nameof(CanTogglePlayback),
                nameof(IsPlaybackActive),
                nameof(ActiveCardOutlineVisibility),
                nameof(ProvenanceText));
        }

        public void SetPlaybackSnapshot(HistoryPlaybackSnapshot snapshot)
        {
            var wasActive = _isPlaybackActive;
            var wasPlaying = _isPlaying;
            var prevPos = _playbackPositionMs;
            var prevDuration = _audioDurationMs;
            var prevProgress = PlaybackProgress;
            var prevCurrentClock = PlaybackCurrentClockText;
            var prevTotalClock = PlaybackTotalClockText;

            ApplySnapshotFields(snapshot);
            if (wasActive == _isPlaybackActive
                && wasPlaying == _isPlaying
                && prevPos == _playbackPositionMs
                && prevDuration == _audioDurationMs)
            {
                return;
            }

            if (wasPlaying != _isPlaying)
            {
                Notify(nameof(PlaybackButtonGlyph), nameof(PlaybackButtonAutomationName), nameof(CanTogglePlayback));
            }

            if (wasActive != _isPlaybackActive)
            {
                Notify(nameof(IsPlaybackActive));
            }

            if (wasPlaying != _isPlaying)
            {
                Notify(nameof(ActiveCardOutlineVisibility));
            }

            if (Math.Abs(prevProgress - PlaybackProgress) > 1e-6 || wasActive != _isPlaybackActive)
            {
                Notify(nameof(PlaybackProgress));
            }

            var currentClockChanged = !string.Equals(prevCurrentClock, PlaybackCurrentClockText, StringComparison.Ordinal);
            var totalClockChanged = !string.Equals(prevTotalClock, PlaybackTotalClockText, StringComparison.Ordinal);
            if (currentClockChanged)
            {
                Notify(nameof(PlaybackCurrentClockText));
            }

            if (totalClockChanged)
            {
                Notify(nameof(PlaybackTotalClockText));
            }

            if (currentClockChanged || totalClockChanged || wasActive != _isPlaybackActive)
            {
                Notify(nameof(PlaybackClockText), nameof(PlaybackScrubberLabel));
            }
        }

        private void ApplySnapshotFields(HistoryPlaybackSnapshot snapshot)
        {
            var isTarget = snapshot.ActiveEntryId == Id;
            _isPlaybackActive = isTarget;
            _isPlaying = isTarget && snapshot.IsPlaying;
            _playbackPositionMs = isTarget ? snapshot.PositionMs : 0;
            if (isTarget && snapshot.DurationMs > 0)
            {
                _audioDurationMs = snapshot.DurationMs;
            }
        }

        public void SetShowingEdits(
            bool isShowingEdits,
            HistoryEditsViewMode? initialMode = null,
            HistoryDiffResult? diffResult = null)
        {
            var modeChanged = initialMode is { } mode && _editsViewMode != mode;
            var diffChanged = diffResult is not null && !ReferenceEquals(_diffResult, diffResult);
            if (_isShowingEdits == isShowingEdits && !modeChanged && !diffChanged)
            {
                return;
            }

            _isShowingEdits = isShowingEdits;
            if (initialMode is { } nextMode)
            {
                _editsViewMode = nextMode;
            }

            if (diffResult is not null)
            {
                _diffResult = diffResult;
            }

            _hasOverflow = _hasOverflow || ShouldOfferShowMore(DisplayedCopyText);
            Notify(
                nameof(ShowEditsInactiveVisibility),
                nameof(ShowEditsActiveVisibility),
                nameof(ShowEditsPanelVisibility),
                nameof(ActiveTranscriptViewMode),
                nameof(PolishedActiveVisibility),
                nameof(PolishedInactiveVisibility),
                nameof(MarkedUpActiveVisibility),
                nameof(MarkedUpInactiveVisibility),
                nameof(OriginalActiveVisibility),
                nameof(OriginalInactiveVisibility),
                nameof(ComparisonStatusText),
                nameof(MarkedUpRuns),
                nameof(MarkedUpLegendVisibility),
                nameof(IsShowingOriginalView),
                nameof(DisplayedCopyText),
                nameof(DisplayText),
                nameof(TranscriptMaxLines),
                nameof(ShowMoreToggleVisibility));
        }

        public void SetEditsViewMode(HistoryEditsViewMode viewMode)
        {
            if (_editsViewMode == viewMode)
            {
                return;
            }

            _editsViewMode = viewMode;
            _hasOverflow = _hasOverflow || ShouldOfferShowMore(DisplayedCopyText);
            Notify(
                nameof(ActiveTranscriptViewMode),
                nameof(PolishedActiveVisibility),
                nameof(PolishedInactiveVisibility),
                nameof(MarkedUpActiveVisibility),
                nameof(MarkedUpInactiveVisibility),
                nameof(OriginalActiveVisibility),
                nameof(OriginalInactiveVisibility),
                nameof(ComparisonStatusText),
                nameof(MarkedUpLegendVisibility),
                nameof(IsShowingOriginalView),
                nameof(DisplayedCopyText),
                nameof(DisplayText),
                nameof(ShowMoreToggleVisibility));
        }

        public void SetTranscriptExpanded(bool isExpanded)
        {
            if (_isTranscriptExpanded == isExpanded)
            {
                return;
            }

            _isTranscriptExpanded = isExpanded;
            Notify(nameof(TranscriptMaxLines), nameof(ShowMoreButtonText));
        }

        public void MarkHasOverflow()
        {
            if (_hasOverflow)
            {
                return;
            }

            _hasOverflow = true;
            Notify(nameof(ShowMoreToggleVisibility));
        }

        public void SetOperationState(bool isGlobalBusy, bool isRepolishingThisCard, bool isPrimaryPasteTarget)
        {
            if (_isGlobalBusy == isGlobalBusy
                && _isRepolishingThisCard == isRepolishingThisCard
                && _isPrimaryPasteTarget == isPrimaryPasteTarget)
            {
                return;
            }

            _isGlobalBusy = isGlobalBusy;
            _isRepolishingThisCard = isRepolishingThisCard;
            _isPrimaryPasteTarget = isPrimaryPasteTarget;
            Notify(
                nameof(IsRepolishing),
                nameof(RepolishingVisibility),
                nameof(CanRunCardActions),
                nameof(CanTogglePlayback),
                nameof(RepolishButtonVisibility),
                nameof(CanRepolish),
                nameof(PasteButtonAutomationId));
        }

        private void Notify(params string[] propertyNames)
        {
            if (PropertyChanged is null)
            {
                return;
            }

            foreach (var name in propertyNames)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(name));
            }
        }

        private static string FormatRecoveryExpiryChip(DateTimeOffset expiresAt)
        {
            var localExpiry = expiresAt.ToLocalTime();
            var pattern = DateOnly.FromDateTime(localExpiry.DateTime) == DateOnly.FromDateTime(DateTime.Now)
                ? "t"
                : "g";
            return $"Recovery · expires {localExpiry.ToString(pattern, CultureInfo.CurrentCulture)}";
        }

        private static string BuildProvenanceText(
            DictationHistoryEntry entry,
            bool hasAudioRecording,
            long audioDurationMs)
        {
            var parts = new List<string>(5);

            if (hasAudioRecording && audioDurationMs > 0)
            {
                parts.Add(HistoryPlaybackPresentation.FormatClock(audioDurationMs));
            }

            parts.Add(HistoryDiaryPresentation.FormatEngineDisplayName(entry.EngineId));

            if (entry.WasPolished)
            {
                var polishName = HistoryDiaryPresentation.FormatPolishModelLabel(entry.PolishModelId)
                    ?? HistoryDiaryPresentation.FormatPolishProviderLabel(entry.PolishProviderId);
                parts.Add(string.IsNullOrWhiteSpace(polishName) ? "AI Polish" : polishName);
            }
            else
            {
                parts.Add("Text cleanup");
            }

            if (!string.IsNullOrWhiteSpace(entry.LanguageCode))
            {
                parts.Add(LanguageCatalog.DisplayNameFor(entry.LanguageCode));
            }

            if (!hasAudioRecording)
            {
                parts.Add(string.IsNullOrWhiteSpace(entry.AudioFileName) ? "No recording" : "Recording unavailable");
            }

            return string.Join(" · ", parts);
        }

        private static bool ShouldOfferShowMore(string text)
        {
            if (text.Length > 240)
            {
                return true;
            }

            var lineBreaks = 0;
            foreach (var ch in text)
            {
                if (ch == '\n' && ++lineBreaks >= 3)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// What a screen reader announces for this row.
        /// </summary>
        public override string ToString() => $"{Text}, dictated {CreatedDisplay}";
    }
}

public sealed class HistoryDiaryRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DayHeaderTemplate { get; set; }

    public DataTemplate? CardTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        MainWindow.HistoryDayHeaderViewModel => DayHeaderTemplate,
        MainWindow.HistoryItemViewModel => CardTemplate,
        _ => base.SelectTemplateCore(item),
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
