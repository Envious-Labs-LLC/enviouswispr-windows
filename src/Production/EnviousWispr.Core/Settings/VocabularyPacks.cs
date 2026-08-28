namespace EnviousWispr.Core.Settings;

/// <summary>A ready-made word list a user can add in one action.</summary>
public sealed record VocabularyPack(string Id, string Name, string Description, string Words);

/// <summary>
/// The word lists that ship with the app.
/// </summary>
/// <remarks>
/// WHY THESE WORDS AND NOT A BIGGER LIST. Every entry here is a term speech recognition reliably
/// gets wrong AND whose correct spelling is not a matter of opinion. A pack of plausible-looking
/// domain words that a recogniser already handles is worse than no pack: it fills the user's list
/// with entries that never fire, and the next time they look at Your Words they cannot tell which
/// of their own corrections still matter.
///
/// SO THE BAR FOR ADDING A ROW IS THAT SOMEONE COULD BE WRONG ABOUT IT. "kubernetes" is heard as
/// "cuban eddies" and there is exactly one right spelling. A medical or legal pack would need
/// someone who knows the domain to say which spelling is right, and inventing one here would be
/// the kind of confident-and-unfounded content this repo keeps catching in other forms.
///
/// PACKS ARE PLAIN TEXT IN THE SAME FORMAT A USER'S OWN FILE USES, deliberately. Installing one
/// runs through exactly the import path an imported file does - same collision rules, same
/// conflict reporting, same refusal to overwrite a correction someone tuned by hand. A pack that
/// installed by a special route would be a second implementation of merging words, and the two
/// would drift.
/// </remarks>
public static class VocabularyPacks
{
    /// <summary>Terms this product's own users say, which recognisers reliably mangle.</summary>
    public static readonly VocabularyPack EnviousWisprTerms = new(
        "enviouswispr",
        "EnviousWispr terms",
        "The names this app uses, so they come out spelled the way they are written.",
        """
        envious wispr,EnviousWispr
        envious whisper,EnviousWispr
        parakeet,Parakeet
        whisper kit,WhisperKit
        ollama,Ollama
        e g one,EG-1
        eg one,EG-1
        """);

    /// <summary>Software terms that are spoken constantly and transcribed badly.</summary>
    public static readonly VocabularyPack SoftwareTerms = new(
        "software",
        "Software terms",
        "Tools and technologies that speech recognition often mishears.",
        """
        kubernetes,Kubernetes
        cuban eddies,Kubernetes
        post gres,PostgreSQL
        postgres,PostgreSQL
        my sequel,MySQL
        no sequel,NoSQL
        get hub,GitHub
        git hub,GitHub
        vs code,VS Code
        java script,JavaScript
        type script,TypeScript
        node js,Node.js
        dot net,.NET
        c sharp,C#
        rest api,REST API
        o auth,OAuth
        json,JSON
        yaml,YAML
        """);

    /// <summary>Every pack the app ships, in the order they are offered.</summary>
    public static IReadOnlyList<VocabularyPack> All { get; } = [EnviousWisprTerms, SoftwareTerms];
}
