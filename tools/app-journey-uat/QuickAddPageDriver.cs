using System.Diagnostics;
using System.Windows.Automation;

namespace EnviousWispr.AppJourney.Uat;

/// <summary>Reads what Quick Add put on the Dictionary page, and presses "Add word", the way a person does.</summary>
/// <remarks>
/// THROUGH UI AUTOMATION, FOUND BY NAME WITHIN THE APP'S PROCESS. Quick Add fills the page's "When I say" and "Write"
/// fields with the selection and stops; the word is stored only when "Add word" is pressed. A control that moved or
/// was renamed fails loudly as an instrument failure rather than passing for the wrong reason.
/// </remarks>
internal static class QuickAddPageDriver
{
    /// <summary>True once the "When I say" field holds exactly the word, within the timeout.</summary>
    public static bool WaitForSpokenForm(int processId, string word, TimeSpan timeout)
    {
        var field = Find(processId, "When I say", ControlType.Edit, timeout);
        if (!field.TryGetCurrentPattern(ValuePattern.Pattern, out var found) || found is not ValuePattern value)
        {
            throw JourneyExpectationException.Instrument("The \"When I say\" field offers no value pattern.");
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (string.Equals(value.Current.Value, word, StringComparison.Ordinal))
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>Presses the Dictionary page's "Add word" button through its invoke pattern.</summary>
    public static void PressAddWord(int processId)
    {
        var button = Find(processId, "Add word", ControlType.Button, TimeSpan.FromSeconds(10));
        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var found) || found is not InvokePattern invoke)
        {
            throw JourneyExpectationException.Instrument("The \"Add word\" button cannot be invoked through UI Automation.");
        }

        invoke.Invoke();
    }

    private static AutomationElement Find(int processId, string name, ControlType type, TimeSpan timeout)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                if (AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition) is { } element)
                {
                    return element;
                }
            }
            catch (ElementNotAvailableException)
            {
                // The tree changed under the read; the next look reads it again.
            }

            Thread.Sleep(250);
        }

        throw JourneyExpectationException.Instrument(
            $"No {type.ProgrammaticName} named \"{name}\" appeared in the app within {timeout.TotalSeconds:0} seconds.");
    }
}
