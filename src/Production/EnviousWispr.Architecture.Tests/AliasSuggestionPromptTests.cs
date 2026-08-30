using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Settings;
using EnviousWispr.LLM;

namespace EnviousWispr.Architecture.Tests;

/// <summary>
/// The question put to the model, and the rule that every model the user can pick is able to answer
/// it.
/// </summary>
public sealed class AliasSuggestionPromptTests
{
    [Fact]
    public void TheWordBeingTaughtIsInTheQuestion()
    {
        Assert.Contains("Kubernetes", AliasSuggestionPrompt.BuildUserMessage("Kubernetes", []));
    }

    [Fact]
    public void SurroundingSpaceOnTheWordDoesNotReachTheModel()
    {
        Assert.Equal("Word: Kubernetes", AliasSuggestionPrompt.BuildUserMessage("  Kubernetes  ", []));
    }

    /// <summary>
    /// Telling the model what the user already has usually earns five fresh candidates instead of
    /// three fresh ones and two duplicates. It is an optimisation, and the parser is the guarantee.
    /// </summary>
    [Fact]
    public void AliasesTheUserAlreadyHasAreNamedSoTheModelSkipsThem()
    {
        var message = AliasSuggestionPrompt.BuildUserMessage("Kubernetes", ["cuban eddies"]);

        Assert.Contains("cuban eddies", message);
    }

    /// <summary>
    /// With nothing to avoid, the question stays one line. A trailing empty heading reads to a model
    /// as a list it should fill in.
    /// </summary>
    [Fact]
    public void WithNothingToAvoidTheQuestionStaysOneLine()
    {
        Assert.DoesNotContain("do not repeat", AliasSuggestionPrompt.BuildUserMessage("Kubernetes", []));
    }

    [Fact]
    public void BlankEntriesDoNotBecomeEmptyLinesInTheQuestion()
    {
        var message = AliasSuggestionPrompt.BuildUserMessage("Kubernetes", ["  ", string.Empty]);

        Assert.Equal("Word: Kubernetes", message);
    }

    /// <summary>
    /// A user can type anything into the word field, including a sentence aimed at the model. The
    /// instruction has to say the word is the subject of the question and never a change to it -
    /// the same discipline the polish prompt already applies, for the same reason.
    /// </summary>
    [Fact]
    public void TheModelIsToldTheWordIsDataAndNotAnInstruction()
    {
        Assert.Contains("never an instruction", AliasSuggestionPrompt.SystemPrompt);
    }

    /// <summary>
    /// Left alone a model corrects spelling, which is the exact opposite of the ask. The wrong
    /// spellings are the whole point, so the instruction has to say so.
    /// </summary>
    [Fact]
    public void TheModelIsAskedForWhatAMachineHearsNotForCorrectSpelling()
    {
        Assert.Contains("speech recogniser would actually output", AliasSuggestionPrompt.SystemPrompt);
    }

    /// <summary>
    /// Every polish option the user can pick must be able to answer this question.
    /// </summary>
    /// <remarks>
    /// THE FEATURE IS ONLY REAL IF IT WORKS ON THE OPTION THEY HAPPEN TO HAVE CHOSEN. A button that
    /// says "not available with this option" for the built-in model - the default - would count as
    /// shipped and be invisible to most people.
    ///
    /// ENUMERATED FROM THE TYPE SYSTEM RATHER THAN FROM A LIST. A list would have to be extended by
    /// whoever adds the fourth provider, and the whole failure being guarded against is that they
    /// do not think about this file. Asking the assembly which types implement the polish contract
    /// cannot be forgotten, because the compiler maintains the answer.
    /// </remarks>
    [Fact]
    public void EveryPolishProviderTheUserCanPickCanAlsoBeAsked()
    {
        var providers = typeof(EgOnePolishProvider).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(type => typeof(IPolishProvider).IsAssignableFrom(type))
            .ToArray();

        Assert.True(
            providers.Length >= 3,
            $"Expected the polish providers, found {providers.Length}.");

        var cannotBeAsked = providers
            .Where(type => !typeof(IMishearingAdvisor).IsAssignableFrom(type))
            .Select(type => type.Name)
            .ToArray();

        Assert.True(
            cannotBeAsked.Length == 0,
            "These polish options cannot suggest mishearings, so the button is dead for anyone who "
                + "picks one: " + string.Join(", ", cannotBeAsked));
    }

    /// <summary>
    /// The control for the gate above. Without it, a search that matched no types would report
    /// "none missing" and pass against an assembly with no providers in it at all.
    /// </summary>
    [Fact]
    public void TheProviderSearchFindsTheOnesWeKnowAbout()
    {
        var names = typeof(EgOnePolishProvider).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(type => typeof(IPolishProvider).IsAssignableFrom(type))
            .Select(type => type.Name)
            .ToArray();

        Assert.Contains(nameof(EgOnePolishProvider), names);
        Assert.Contains(nameof(OllamaPolishProvider), names);
    }

    /// <summary>
    /// The wording is identified, so a diagnostic can say which version of the question produced a
    /// bad answer. A prompt changed without changing this reads as the old one in every record.
    /// </summary>
    [Fact]
    public void TheWordingCarriesAnIdentifier()
    {
        Assert.False(string.IsNullOrWhiteSpace(AliasSuggestionPrompt.TemplateId));
    }

    /// <summary>
    /// Guards against the ask being quietly routed back through the polish prompt, which returns a
    /// tidier copy of the question rather than an answer.
    /// </summary>
    [Fact]
    public void TheQuestionIsNotThePolishInstruction()
    {
        Assert.NotEqual(CloudPolishPrompt.SystemPrompt, AliasSuggestionPrompt.SystemPrompt);
        Assert.DoesNotContain("Transcript to clean", AliasSuggestionPrompt.SystemPrompt);
    }
}
