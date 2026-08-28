using System.Text;

namespace EnviousWispr.Core.Audio;

/// <summary>
/// Joins the pieces of a transcript that were recognised separately while the user was speaking.
/// </summary>
/// <remarks>
/// THE SEAM IS THE WHOLE PROBLEM. Each piece is recognised without knowing the others exist, so a
/// recogniser given the second half of a sentence capitalises it like the start of one and may end
/// it with a full stop it invented. Joining them naively produces "I think we should ship. This
/// week." where the user said one sentence.
///
/// SO THE JOIN IS A DECISION RATHER THAN A CONCATENATION, and it is deliberately conservative:
/// every rule here either fixes something a recogniser reliably does at a boundary, or does
/// nothing. Nothing tries to improve the text.
///
/// WHAT IT DOES NOT DO, stated so the next reader does not expect it. It does not re-punctuate, it
/// does not merge sentences that the user really did separate, and it cannot recover a word split
/// across a boundary - the planner exists to make sure that never happens, because no amount of
/// text repair fixes a syllable the recogniser never heard as part of a word.
/// </remarks>
public sealed class StreamingTranscriptAccumulator
{
    private readonly StringBuilder _text = new();

    /// <summary>Whether anything has been added yet.</summary>
    public bool IsEmpty => _text.Length == 0;

    /// <summary>
    /// Adds one recognised piece.
    /// </summary>
    /// <remarks>
    /// EMPTY PIECES ARE DROPPED RATHER THAN JOINED. A commit covering a stretch the recogniser made
    /// nothing of returns an empty string, and joining it would add a separator with no words on
    /// one side - a double space, or a leading space on the whole transcript.
    /// </remarks>
    public void Append(string? piece)
    {
        var trimmed = piece?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        if (_text.Length == 0)
        {
            _text.Append(trimmed);
            return;
        }

        var previousEnd = _text[^1];

        // A recogniser handed the middle of a sentence still capitalises its first word. Lower it
        // ONLY when the previous piece did not end a sentence, and only for a word that is not
        // capitalised for its own reasons - a name, an acronym, or "I" - which is why the test is
        // "the rest of the word is lower case" rather than "the first letter is upper case".
        var joinsMidSentence = previousEnd is not ('.' or '!' or '?');
        var next = joinsMidSentence ? LowerFirstWordIfPlainlyCapitalised(trimmed) : trimmed;

        _text.Append(' ').Append(next);
    }

    /// <summary>The transcript so far.</summary>
    public override string ToString() => _text.ToString();

    private static string LowerFirstWordIfPlainlyCapitalised(string piece)
    {
        if (!char.IsUpper(piece[0]))
        {
            return piece;
        }

        var wordEnd = 1;
        while (wordEnd < piece.Length && char.IsLetter(piece[wordEnd]))
        {
            wordEnd++;
        }

        var word = piece[..wordEnd];

        // "I" and "I'm" are capitalised for their own reason and a single upper-case letter tells
        // us nothing either way, so both are left alone.
        if (word.Length == 1)
        {
            return piece;
        }

        // An acronym - NASA, API - has more upper case after the first letter and must not be
        // touched. Only a plain Capitalised word is lowered.
        for (var index = 1; index < word.Length; index++)
        {
            if (char.IsUpper(word[index]))
            {
                return piece;
            }
        }

        return char.ToLowerInvariant(piece[0]) + piece[1..];
    }
}
