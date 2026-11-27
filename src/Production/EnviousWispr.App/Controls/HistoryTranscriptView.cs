using EnviousWispr.Presentation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Text;

namespace EnviousWispr.App.Controls;

/// <summary>
/// Single-surface History transcript presenter rendering Polished, Marked up (inline Myers diff), or Original text.
/// </summary>
public sealed class HistoryTranscriptView : Grid
{
    public static readonly DependencyProperty ViewModeProperty = DependencyProperty.Register(
        nameof(ViewMode),
        typeof(HistoryEditsViewMode),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(HistoryEditsViewMode.Polished, OnContentPropertyChanged));

    public static readonly DependencyProperty PolishedTextProperty = DependencyProperty.Register(
        nameof(PolishedText),
        typeof(string),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(string.Empty, OnContentPropertyChanged));

    public static readonly DependencyProperty OriginalTextProperty = DependencyProperty.Register(
        nameof(OriginalText),
        typeof(string),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(string.Empty, OnContentPropertyChanged));

    public static readonly DependencyProperty MarkedUpRunsProperty = DependencyProperty.Register(
        nameof(MarkedUpRuns),
        typeof(IReadOnlyList<MarkedUpRun>),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(null, OnContentPropertyChanged));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines),
        typeof(int),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty BodyBrushProperty = DependencyProperty.Register(
        nameof(BodyBrush),
        typeof(Brush),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(null, OnContentPropertyChanged));

    public static readonly DependencyProperty AddedTextBrushProperty = DependencyProperty.Register(
        nameof(AddedTextBrush),
        typeof(Brush),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(null, OnContentPropertyChanged));

    public static readonly DependencyProperty AddedHighlightBrushProperty = DependencyProperty.Register(
        nameof(AddedHighlightBrush),
        typeof(Brush),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(null, OnContentPropertyChanged));

    public static readonly DependencyProperty RemovedBrushProperty = DependencyProperty.Register(
        nameof(RemovedBrush),
        typeof(Brush),
        typeof(HistoryTranscriptView),
        new PropertyMetadata(null, OnContentPropertyChanged));

    private readonly RichTextBlock _richText;
    private bool _rebuildQueued;
    private bool _hasRendered;
    private HistoryEditsViewMode _lastViewMode;
    private string? _lastPolishedText;
    private string? _lastOriginalText;
    private IReadOnlyList<MarkedUpRun>? _lastMarkedUpRuns;
    private Brush? _lastBodyBrush;
    private Brush? _lastAddedTextBrush;
    private Brush? _lastAddedHighlightBrush;
    private Brush? _lastRemovedBrush;

    public HistoryTranscriptView()
    {
        _richText = new RichTextBlock
        {
            FontSize = 15,
            LineHeight = 22,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.DetectFromContent,
        };
        _richText.IsTextTrimmedChanged += (sender, args) => IsTextTrimmedChanged?.Invoke(sender, args);
        Children.Add(_richText);
    }

    public event TypedEventHandler<RichTextBlock, IsTextTrimmedChangedEventArgs>? IsTextTrimmedChanged;

    public HistoryEditsViewMode ViewMode
    {
        get => (HistoryEditsViewMode)GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    public string PolishedText
    {
        get => (string)GetValue(PolishedTextProperty);
        set => SetValue(PolishedTextProperty, value);
    }

    public string OriginalText
    {
        get => (string)GetValue(OriginalTextProperty);
        set => SetValue(OriginalTextProperty, value);
    }

    public IReadOnlyList<MarkedUpRun>? MarkedUpRuns
    {
        get => (IReadOnlyList<MarkedUpRun>?)GetValue(MarkedUpRunsProperty);
        set => SetValue(MarkedUpRunsProperty, value);
    }

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public Brush? BodyBrush
    {
        get => (Brush?)GetValue(BodyBrushProperty);
        set => SetValue(BodyBrushProperty, value);
    }

    public Brush? AddedTextBrush
    {
        get => (Brush?)GetValue(AddedTextBrushProperty);
        set => SetValue(AddedTextBrushProperty, value);
    }

    public Brush? AddedHighlightBrush
    {
        get => (Brush?)GetValue(AddedHighlightBrushProperty);
        set => SetValue(AddedHighlightBrushProperty, value);
    }

    public Brush? RemovedBrush
    {
        get => (Brush?)GetValue(RemovedBrushProperty);
        set => SetValue(RemovedBrushProperty, value);
    }

    public bool IsTextTrimmed => _richText.IsTextTrimmed;

    private static void OnContentPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HistoryTranscriptView view)
        {
            view.ScheduleRebuild();
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HistoryTranscriptView view)
        {
            view._richText.MaxLines = Math.Max(0, view.MaxLines);
        }
    }

    private void ScheduleRebuild()
    {
        if (_rebuildQueued)
        {
            return;
        }

        if (DispatcherQueue is { } queue)
        {
            _rebuildQueued = true;
            if (queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, FlushRebuild))
            {
                return;
            }

            _rebuildQueued = false;
        }

        RebuildDocumentIfChanged();
    }

    private void FlushRebuild()
    {
        _rebuildQueued = false;
        RebuildDocumentIfChanged();
    }

    private void RebuildDocumentIfChanged()
    {
        var viewMode = ViewMode;
        var polished = PolishedText;
        var original = OriginalText;
        var markedUpRuns = MarkedUpRuns;
        var bodyBrush = BodyBrush;
        var addedTextBrushProp = AddedTextBrush;
        var addedHighlightBrush = AddedHighlightBrush;
        var removedBrush = RemovedBrush;

        if (_hasRendered
            && _lastViewMode == viewMode
            && string.Equals(_lastPolishedText, polished, StringComparison.Ordinal)
            && string.Equals(_lastOriginalText, original, StringComparison.Ordinal)
            && ReferenceEquals(_lastMarkedUpRuns, markedUpRuns)
            && ReferenceEquals(_lastBodyBrush, bodyBrush)
            && ReferenceEquals(_lastAddedTextBrush, addedTextBrushProp)
            && ReferenceEquals(_lastAddedHighlightBrush, addedHighlightBrush)
            && ReferenceEquals(_lastRemovedBrush, removedBrush))
        {
            _richText.MaxLines = Math.Max(0, MaxLines);
            return;
        }

        _hasRendered = true;
        _lastViewMode = viewMode;
        _lastPolishedText = polished;
        _lastOriginalText = original;
        _lastMarkedUpRuns = markedUpRuns;
        _lastBodyBrush = bodyBrush;
        _lastAddedTextBrush = addedTextBrushProp;
        _lastAddedHighlightBrush = addedHighlightBrush;
        _lastRemovedBrush = removedBrush;

        RebuildDocument();
    }

    private void RebuildDocument()
    {
        _richText.MaxLines = Math.Max(0, MaxLines);
        _richText.Blocks.Clear();
        _richText.TextHighlighters.Clear();

        var bodyBrush = BodyBrush ?? AddedTextBrush;
        var addedTextBrush = AddedTextBrush ?? bodyBrush;
        var paragraph = new Paragraph();

        if (ViewMode == HistoryEditsViewMode.Original)
        {
            var text = NormalizeNewlines(OriginalText);
            paragraph.Inlines.Add(new Run
            {
                Text = text,
                Foreground = bodyBrush,
            });
            _richText.Blocks.Add(paragraph);
            return;
        }

        var runs = MarkedUpRuns;
        if (ViewMode != HistoryEditsViewMode.MarkedUp || runs is null || runs.Count == 0)
        {
            var text = NormalizeNewlines(PolishedText);
            paragraph.Inlines.Add(new Run
            {
                Text = text,
                Foreground = bodyBrush,
            });
            _richText.Blocks.Add(paragraph);
            return;
        }

        var highlighter = AddedHighlightBrush is not null
            ? new TextHighlighter
            {
                Background = AddedHighlightBrush,
                Foreground = addedTextBrush,
            }
            : null;

        var offset = 0;
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var normalized = NormalizeNewlines(run.Text);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            if (run.Kind == MarkedUpRunKind.Same)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = normalized,
                    Foreground = bodyBrush,
                });
                offset += normalized.Length;
                continue;
            }

            var (core, trailing) = SplitTrailingWhitespace(normalized);
            if (run.Kind == MarkedUpRunKind.Removed)
            {
                if (core.Length > 0)
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = core,
                        Foreground = RemovedBrush ?? bodyBrush,
                        TextDecorations = TextDecorations.Strikethrough,
                    });
                    offset += core.Length;
                }

                if (trailing.Length > 0)
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = trailing,
                        Foreground = bodyBrush,
                    });
                    offset += trailing.Length;
                }
                else if (i + 1 < runs.Count && runs[i + 1].Kind == MarkedUpRunKind.Added)
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = " ",
                        Foreground = bodyBrush,
                    });
                    offset += 1;
                }

                continue;
            }

            if (core.Length > 0)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = core,
                    Foreground = addedTextBrush,
                    FontWeight = FontWeights.SemiBold,
                });
                highlighter?.Ranges.Add(new TextRange
                {
                    StartIndex = offset,
                    Length = core.Length,
                });
                offset += core.Length;
            }

            if (trailing.Length > 0)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = trailing,
                    Foreground = bodyBrush,
                });
                offset += trailing.Length;
            }
        }

        _richText.Blocks.Add(paragraph);
        if (highlighter is not null && highlighter.Ranges.Count > 0)
        {
            _richText.TextHighlighters.Add(highlighter);
        }
    }

    private static string NormalizeNewlines(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static (string Core, string Trailing) SplitTrailingWhitespace(string text)
    {
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (text[..end], text[end..]);
    }
}
