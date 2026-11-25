namespace EnviousWispr.Core.Settings;

/// <summary>Category assigned to a dictionary entry.</summary>
public enum WordCategory
{
    General,
    Person,
    Brand,
    Acronym,
    Domain,
}

/// <summary>Display labels, filter options, and offline heuristic classification for dictionary categories.</summary>
public static class WordCategoryFormatting
{
    private static readonly string[] DomainKeywords =
    [
        "api", "sdk", "cli", "sql", "http", "https", "json", "yaml", "toml", "xml",
        "grpc", "graphql", "rest", "oauth", "jwt", "tls", "ssl", "dns", "ssh",
        "async", "await", "mutex", "regex", "cache", "redis", "kafka", "docker",
        "kube", "kubectl", "kubernetes", "postgres", "postgresql", "sqlite",
        "terraform", "ansible", "webpack", "vite", "typescript", "javascript",
        "python", "rust", "golang", "dotnet", "xaml", "winui", "whisper", "parakeet",
        "onnx", "cuda", "vram", "llm", "rag", "embedding", "tokenizer", "inference",
        "neuro", "cardio", "gastro", "hema", "onco", "pharma", "statute", "tort",
        "subpoena", "deposition", "plaintiff", "defendant", "appellant", "certiorari",
    ];

    public static IReadOnlyList<WordCategory> All { get; } =
    [
        WordCategory.General,
        WordCategory.Person,
        WordCategory.Brand,
        WordCategory.Acronym,
        WordCategory.Domain,
    ];

    public static string Label(WordCategory category) => category switch
    {
        WordCategory.General => "General",
        WordCategory.Person => "Person",
        WordCategory.Brand => "Brand",
        WordCategory.Acronym => "Acronym",
        WordCategory.Domain => "Domain",
        _ => "General",
    };

    /// <summary>Guesses the most plausible category for a canonical word without requiring AI polish.</summary>
    public static WordCategory Classify(string? replacement)
    {
        var trimmed = replacement?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return WordCategory.General;
        }

        if (LooksLikeAcronym(trimmed))
        {
            return WordCategory.Acronym;
        }

        if (LooksLikeDomainTerm(trimmed))
        {
            return WordCategory.Domain;
        }

        if (LooksLikePersonName(trimmed))
        {
            return WordCategory.Person;
        }

        if (LooksLikeBrand(trimmed))
        {
            return WordCategory.Brand;
        }

        return WordCategory.General;
    }

    private static bool LooksLikeAcronym(string text)
    {
        if (text.Length is < 2 or > 10 || text.Contains(' '))
        {
            return false;
        }

        var upperCount = 0;
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
            {
                if (!char.IsUpper(ch))
                {
                    return false;
                }

                upperCount++;
            }
            else if (!char.IsDigit(ch) && ch is not '&' and not '-' and not '/')
            {
                return false;
            }
        }

        return upperCount >= 2;
    }

    private static bool LooksLikeDomainTerm(string text)
    {
        if (text.Contains('_') || text.Contains("::", StringComparison.Ordinal) || text.Contains('/'))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        if (lower.EndsWith(".io", StringComparison.Ordinal) ||
            lower.EndsWith(".js", StringComparison.Ordinal) ||
            lower.EndsWith(".ts", StringComparison.Ordinal) ||
            lower.EndsWith(".py", StringComparison.Ordinal) ||
            lower.EndsWith(".cs", StringComparison.Ordinal) ||
            lower.EndsWith(".sql", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Length >= 2 && char.IsLower(text[0]) && text.Skip(1).Any(char.IsUpper))
        {
            return true;
        }

        foreach (var keyword in DomainKeywords)
        {
            if (lower.Equals(keyword, StringComparison.Ordinal) ||
                (keyword.Length >= 4 && lower.Contains(keyword, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikePersonName(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 2 or > 3)
        {
            return false;
        }

        if (words[0].TrimEnd('.').ToLowerInvariant() is "dr" or "mr" or "mrs" or "ms" or "prof")
        {
            return true;
        }

        return words.All(word =>
            word.Length >= 2 &&
            char.IsUpper(word[0]) &&
            word.Skip(1).All(ch => char.IsLower(ch) || ch is '-' or '\''));
    }

    private static bool LooksLikeBrand(string text)
    {
        if (text.Length >= 2 && char.IsUpper(text[0]) && text.Any(char.IsLower))
        {
            return true;
        }

        return text.Any(char.IsDigit) && text.Any(char.IsLetter);
    }
}
