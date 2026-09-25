using System.Diagnostics;
using System.Windows.Automation;

namespace EnviousWispr.AppJourney.Uat;

/// <summary>Changes the Whisper language the way a person does: the Transcription page, the picker, Save.</summary>
/// <remarks>
/// THROUGH UI AUTOMATION, NOT A FILE WRITE. The question is whether a change made in the running app
/// reaches the next take (#241), and the app never re-reads its settings file while it runs, so a file
/// written behind its back would test nothing the person can do. Every step goes through a control's own
/// pattern, found by name within the app's process: a control that moved or was renamed fails loudly as
/// an instrument failure rather than passing for the wrong reason.
/// </remarks>
internal static class TranscriptionPageDriver
{
    public static void ChooseWhisperLanguage(int processId, string languageName)
    {
        _ = Find(processId, "EnviousWispr navigation", TimeSpan.FromSeconds(30));
        Select(Find(processId, "Transcription", TimeSpan.FromSeconds(10)));
        var picker = Find(processId, "Whisper language", TimeSpan.FromSeconds(10));
        if (!picker.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expanderPattern) ||
            expanderPattern is not ExpandCollapsePattern expander)
        {
            throw JourneyExpectationException.Instrument("The Whisper language picker offers no expand pattern.");
        }

        expander.Expand();
        Select(Find(processId, languageName, TimeSpan.FromSeconds(10)));
        if (expander.Current.ExpandCollapseState != ExpandCollapseState.Collapsed)
        {
            expander.Collapse();
        }

        Invoke(Find(processId, "Save settings", TimeSpan.FromSeconds(10)));
        _ = Find(processId, "Settings saved", TimeSpan.FromSeconds(10));
    }

    private static AutomationElement Find(int processId, string name, TimeSpan timeout)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.NameProperty, name));
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition) is { } element)
            {
                return element;
            }

            Thread.Sleep(250);
        }

        throw JourneyExpectationException.Instrument(
            $"No control named \"{name}\" appeared in the app within {timeout.TotalSeconds:0} seconds.");
    }

    private static void Select(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var found) ||
            found is not SelectionItemPattern pattern)
        {
            throw JourneyExpectationException.Instrument(
                $"\"{element.Current.Name}\" cannot be selected through UI Automation.");
        }

        pattern.Select();
    }

    private static void Invoke(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var found) ||
            found is not InvokePattern pattern)
        {
            throw JourneyExpectationException.Instrument(
                $"\"{element.Current.Name}\" cannot be invoked through UI Automation.");
        }

        pattern.Invoke();
    }
}
