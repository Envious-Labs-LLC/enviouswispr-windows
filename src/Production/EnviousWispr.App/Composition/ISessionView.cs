using EnviousWispr.Core.Presentation;

namespace EnviousWispr.App.Composition;

/// <summary>What a dictation shows the person: the shell's window, reached on the window's thread.</summary>
/// <remarks>
/// FOUR SINKS AND NO DECISIONS. The shell implements each with one dispatch to the window and
/// nothing else, so the session's wiring can be built and driven without a window at all; the words
/// and the choice of which sink are made on this side, by the effects the composition owns.
/// </remarks>
public interface ISessionView
{
    void ShowStatus(DictationStatus status);

    void ShowNotice(string title, string message, bool isError = false);

    void ShowMainWindow();

    void ReportDelivery(DictationStatus delivered, string? detectedLanguage);
}
