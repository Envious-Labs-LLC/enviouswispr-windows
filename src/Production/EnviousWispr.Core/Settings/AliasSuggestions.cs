namespace EnviousWispr.Core.Settings;

/// <summary>
/// Turns a model's reply into alias candidates a user can accept or ignore.
/// </summary>
/// <remarks>
/// WHAT THE FEATURE IS. A user adding "Kubernetes" to their words has to guess what the recogniser
/// will actually hear - "cuban eddies", "cooper netties" - and they only find out by being misheard
/// and coming back. Asking a model for the likely mishearings turns that from a week of noticing
/// into one screen.
///
/// THE PARSING IS THE PART THAT MUST BE RIGHT, which is why it is here and testable rather than
/// inside the provider call. A model asked for a list returns a list MOST of the time: sometimes it
/// numbers them, sometimes it apologises first, sometimes it repeats the word it was given, and
/// occasionally it returns a paragraph. Every one of those has to become either good candidates or
/// none - never a plausible-looking alias the user did not ask for and cannot explain.
///
/// SUGGESTIONS ARE CANDIDATES, NEVER ADDITIONS. Nothing here writes to the user's list. A wrong
/// alias silently added is a correction that fires on words the user never said, and they would
/// have no way to connect it to a suggestion they were shown once.
/// </remarks>
public static class AliasSuggestions
{
    /// <summary>The most candidates worth showing at once.</summary>
    /// <remarks>
    /// A model asked for mishearings will happily produce twenty, and the tail of that list is
    /// noise. Five is enough to cover the ones a person would recognise, and short enough that
    /// reading all of them is quicker than ignoring them.
    /// </remarks>
    public const int MaximumSuggestions = 5;

    /// <summary>The longest a candidate may be, in characters.</summary>
    /// <remarks>
    /// A mishearing of a word is about as long as the word. This exists to reject a model that
    /// returned a sentence, which would otherwise become an alias nobody could ever trigger.
    /// </remarks>
    public const int MaximumLength = 60;

    /// <summary>
    /// The usable candidates in a model's reply.
    /// </summary>
    /// <param name="reply">Whatever the model returned.</param>
    /// <param name="spokenForm">The term being taught, so the model cannot suggest it back.</param>
    /// <param name="existing">Aliases the user already has, so nothing is offered twice.</param>
    public static IReadOnlyList<string> Parse(
        string? reply,
        string spokenForm,
        IReadOnlyList<string> existing)
    {
        ArgumentNullException.ThrowIfNull(spokenForm);
        ArgumentNullException.ThrowIfNull(existing);

        if (string.IsNullOrWhiteSpace(reply))
        {
            return [];
        }

        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase) { spokenForm };
        var suggestions = new List<string>();

        foreach (var raw in reply.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Clean(raw);
            if (candidate.Length == 0 || candidate.Length > MaximumLength)
            {
                continue;
            }

            // A line with no letters is a separator, a bullet on its own, or punctuation the model
            // emitted for shape. Adding it would give the user an alias made of dashes.
            if (!candidate.Any(char.IsLetter))
            {
                continue;
            }

            if (!seen.Add(candidate))
            {
                continue;
            }

            suggestions.Add(candidate);
            if (suggestions.Count == MaximumSuggestions)
            {
                break;
            }
        }

        return suggestions;
    }

    /// <summary>
    /// Strips the decoration a model puts around a list item.
    /// </summary>
    /// <remarks>
    /// Numbering, bullets, quotes and trailing punctuation, in that order, because they nest:
    /// a line can be `1. "cuban eddies",` and every layer has to come off for the alias underneath
    /// to match anything the recogniser produces.
    /// </remarks>
    private static string Clean(string line)
    {
        var text = line.Trim();

        // "1." or "1)" or "-" or "*" at the start.
        var index = 0;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            index++;
        }

        if (index > 0 && index < text.Length && (text[index] == '.' || text[index] == ')'))
        {
            text = text[(index + 1)..].TrimStart();
        }
        else if (text.StartsWith('-') || text.StartsWith('*') || text.StartsWith('•'))
        {
            text = text[1..].TrimStart();
        }

        return text.Trim('"', '\'', ',', '.', ';', ':', ' ');
    }
}
