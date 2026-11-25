using System.Diagnostics;
using System.Windows.Automation;

namespace EnviousWispr.AppJourney.Uat;

/// <summary>Changes the Whisper language the way a person does: the Dictation Settings page and the picker (auto-saved).</summary>
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
        Select(Find(processId, "Dictation Settings", TimeSpan.FromSeconds(10)));
        var picker = Find(processId, "Dictation language", TimeSpan.FromSeconds(10));
        Activate(picker);

        var searchBox = Find(processId, "Search languages", TimeSpan.FromSeconds(10));
        if (!searchBox.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) ||
            valuePatternObj is not ValuePattern valuePattern)
        {
            throw JourneyExpectationException.Instrument("The language picker search box offers no value pattern.");
        }

        valuePattern.SetValue(languageName);
        var match = FindContaining(processId, languageName, TimeSpan.FromSeconds(10));
        Activate(match);

        WaitForAppliedLanguage(picker, languageName, TimeSpan.FromSeconds(10));
    }

    private static void WaitForAppliedLanguage(AutomationElement picker, string languageName, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            var status = picker.Current.ItemStatus ?? string.Empty;
            if (status.Contains(languageName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw JourneyExpectationException.Instrument(
            $"The running app did not apply Whisper language \"{languageName}\" within {timeout.TotalSeconds:0} seconds "
            + $"(itemStatus=\"{picker.Current.ItemStatus}\").");
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

    private static AutomationElement FindContaining(int processId, string substring, TimeSpan timeout)
    {
        var listCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            var items = AutomationElement.RootElement.FindAll(TreeScope.Descendants, listCondition);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var name = item.Current.Name ?? string.Empty;
                if (name.Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            Thread.Sleep(250);
        }

        throw JourneyExpectationException.Instrument(
            $"No language option matching \"{substring}\" appeared in the picker within {timeout.TotalSeconds:0} seconds.");
    }

    private static void Activate(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj) &&
            invokeObj is InvokePattern invokePattern)
        {
            invokePattern.Invoke();
            return;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectObj) &&
            selectObj is SelectionItemPattern selectPattern)
        {
            selectPattern.Select();
            return;
        }

        throw JourneyExpectationException.Instrument(
            $"\"{element.Current.Name}\" cannot be activated through UI Automation.");
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
}
