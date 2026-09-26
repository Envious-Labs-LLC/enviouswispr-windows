using System.Text.Json;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.AppImport;

namespace EnviousWispr.Services.Words;

/// <summary>Another dictation app EnviousWispr can read words out of, read only and only when the person picks it.</summary>
/// <remarks>
/// NOTHING HERE RUNS UNTIL THE PERSON ASKS. No adapter touches the disk at launch or in the background:
/// <see cref="IsInstalled"/> is asked only once the person is looking at the app list, and <see cref="Load"/> only after
/// they choose one (macOS <c>SmartImportAdapter</c>). Nothing is ever written to the other app's files.
/// </remarks>
public interface IWordImportApp
{
    /// <summary>A closed identifier (<c>wispr_flow</c>, <c>handy</c>), never a display name.</summary>
    string Id { get; }

    /// <summary>What the person sees.</summary>
    string DisplayName { get; }

    /// <summary>Whether the app's store is on this PC.</summary>
    bool IsInstalled { get; }

    /// <summary>Reads every word, refusing and COUNTING the rows that are not the person's words, and maps them onto pairs.</summary>
    /// <exception cref="WordImportException">Not found, unreadable, or too big.</exception>
    CustomWordAppBatch Load();
}

/// <summary>The apps "From another app" offers for words (macOS <c>SmartImportRegistry.v1</c>, which lists eight).</summary>
/// <remarks>
/// TWO OF THE MAC'S EIGHT, AND EVERY ABSENCE IS A DECISION WITH ITS REASON. An adapter is offered only when the app
/// exists on Windows AND where it keeps the person's words there was verified, on a real install or in the vendor's
/// own source. A reader aimed at a guessed location finds nothing, or the wrong file.
///
/// - Wispr Flow: measured on a real Windows install. Same <c>flow.sqlite</c>, same <c>Dictionary</c> table and
///   columns as the Mac, under the roaming application-data folder.
/// - Handy: read from its source (cjpais/Handy). The settings store is <c>settings_store.json</c>, resolved by the
///   Tauri store plugin against the app-data folder, which on Windows is the roaming folder plus the bundle
///   identifier <c>com.pais.handy</c>. A portable install keeps it beside its program instead, a folder this app
///   cannot know, and is not found.
/// - TypeWhisper: its Windows app is a separate program keeping a JSON dictionary under one of several data roots
///   that have changed between versions; which one a shipping install uses was not verified.
/// - Superwhisper and Spokenly: Windows apps exist, but where and how they keep words on Windows is not documented
///   and was not measured (the Mac readers use a home-folder settings file and a macOS preferences domain).
/// - FluidVoice, Vox and Juno: macOS apps; no Windows build whose store could be read was found.
/// </remarks>
public static class WordImportApps
{
    public static IReadOnlyList<IWordImportApp> All { get; } =
    [
        new WisprFlowWordApp(WisprFlowDatabase.DefaultPath),
        new HandyWordApp(HandyWordApp.DefaultStorePath),
    ];

    /// <summary>The names for the "none found" sentence.</summary>
    public static string SupportedNames => ImportAppNames.Join([.. All.Select(app => app.DisplayName)]);
}

/// <summary>Wispr Flow's words: live, non-snippet rows of its <c>Dictionary</c> table, read from a private copy (macOS <c>WisprFlowAdapter</c>).</summary>
/// <remarks>
/// <c>isDeleted</c> is a soft delete: importing it would resurrect a word the person deliberately removed, the single
/// worst thing this adapter could do. <c>isSnippet</c> rows are text expansions, a different feature. Both are
/// COUNTED rather than hidden by a WHERE clause, so a store that refused everything does not read as empty.
/// <c>replacement</c>, when present, is the corrected spelling and <c>phrase</c> the misspelling that prompted it;
/// with no replacement the phrase is the word itself. <c>ORDER BY id</c> gives "earlier" a stable meaning, and
/// <c>LIMIT</c> is one past the ceiling so "too many" is knowable.
/// </remarks>
public sealed class WisprFlowWordApp : IWordImportApp
{
    private readonly string _databasePath;

    public WisprFlowWordApp(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public string Id => "wispr_flow";

    public string DisplayName => "Wispr Flow";

    public bool IsInstalled => File.Exists(_databasePath);

    public CustomWordAppBatch Load()
    {
        if (!IsInstalled)
        {
            throw new WordImportException(WordImportFailure.AppNotFound, WordImportMessages.AppNotFound(DisplayName));
        }

        var sql =
            "SELECT phrase, replacement, isDeleted, isSnippet FROM Dictionary " +
            $"ORDER BY id COLLATE BINARY ASC LIMIT {WordImportLimits.MaximumSourceEntries + 1}";
        List<ImportedAppWord> words;
        int excluded;
        try
        {
            (words, excluded) = WisprFlowDatabase.Read(_databasePath, sql, row =>
            {
                var phrase = row.RequiredText(0);
                var replacement = row.OptionalText(1);
                var isDeleted = row.RequiredBoolean(2);
                var isSnippet = row.RequiredBoolean(3);
                return isDeleted || isSnippet ? null : Word(replacement, phrase);
            });
        }
        catch (RivalStoreUnreadableException)
        {
            throw new WordImportException(WordImportFailure.AppStoreUnreadable, WordImportMessages.AppUnreadable(DisplayName));
        }

        return CustomWordAppImport.Build(Id, DisplayName, new ImportedAppWords(words, excluded));
    }

    /// <summary>A corrected spelling is the word and the phrase that prompted it its spoken form; no correction, the phrase is the word.</summary>
    internal static ImportedAppWord Word(string? replacement, string phrase)
    {
        var trimmed = replacement?.Trim();
        return string.IsNullOrEmpty(trimmed) ? new ImportedAppWord(phrase) : new ImportedAppWord(trimmed, [phrase]);
    }
}

/// <summary>Handy's words: the <c>custom_words</c> list in its settings store, read twice and accepted only when both reads agree (macOS <c>HandyAdapter</c>).</summary>
/// <remarks>
/// HANDY REWRITES THE FILE IN PLACE on every settings change, so a read can land in the window where it is truncated,
/// or span the rewrite and splice two generations into JSON that parses and describes a list nobody typed (measured on
/// the Mac: 2 zero-byte reads in 604,959). Two byte-identical reads in a row prove the accepted bytes existed; the
/// decode runs inside the attempt, so two agreeing empty reads retry instead of failing the import.
///
/// BOTH LEVELS ARE OPTIONAL BECAUSE HANDY'S OWN DECODER MAKES THEM SO: a store missing <c>settings</c>, missing
/// <c>custom_words</c>, or holding null is what Handy reads as a person with no custom words, and refusing it would
/// call a file unreadable that Handy opens happily. Only those two keys are read, so an unrelated field Handy would
/// salvage cannot block the import. Handy ships no vocabulary of its own, so nothing is excluded, and
/// <c>custom_filler_words</c> is deliberately NOT read: it is a list of words to REMOVE.
/// </remarks>
public sealed class HandyWordApp : IWordImportApp
{
    /// <summary>A word list is small; anything larger is not one, and is refused before it is decoded.</summary>
    internal const int MaximumBytes = 8 * 1024 * 1024;

    private const int Attempts = 3;

    private readonly string _storePath;
    private readonly Func<string, byte[]> _read;

    public HandyWordApp(string storePath)
        : this(storePath, ReadBounded)
    {
    }

    /// <summary>For tests: drives an exact torn/good sequence of reads without racing a real app.</summary>
    internal HandyWordApp(string storePath, Func<string, byte[]> read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        ArgumentNullException.ThrowIfNull(read);
        _storePath = storePath;
        _read = read;
    }

    /// <summary><c>%APPDATA%\com.pais.handy\settings_store.json</c>: Tauri's app-data folder is the roaming folder plus the bundle identifier.</summary>
    public static string DefaultStorePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.pais.handy", "settings_store.json");

    public string Id => "handy";

    public string DisplayName => "Handy";

    public bool IsInstalled => File.Exists(_storePath);

    public CustomWordAppBatch Load()
    {
        if (!IsInstalled)
        {
            throw new WordImportException(WordImportFailure.AppNotFound, WordImportMessages.AppNotFound(DisplayName));
        }

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            byte[] first;
            byte[] second;
            try
            {
                first = _read(_storePath);
                second = _read(_storePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Unreadable();
            }

            if (!first.AsSpan().SequenceEqual(second) || Decode(first) is not { } words)
            {
                continue;
            }

            return CustomWordAppImport.Build(Id, DisplayName, new ImportedAppWords(words, 0));
        }

        // Handy is writing as we read. Refusing is the honest half, and the sentence already offers quitting it.
        throw Unreadable();
    }

    /// <summary>The words in one agreed read, or null when the bytes are not a Handy store.</summary>
    internal static IReadOnlyList<ImportedAppWord>? Decode(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("settings", out var settings) || settings.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            if (settings.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!settings.TryGetProperty("custom_words", out var custom) || custom.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            if (custom.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var words = new List<ImportedAppWord>();
            foreach (var item in custom.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                words.Add(new ImportedAppWord(item.GetString()!));
            }

            return words;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Bounded by the bytes actually read, so a file that grows during the read is still stopped.</summary>
    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaximumBytes)
            {
                throw new IOException("The store is larger than a word list can be.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private WordImportException Unreadable() =>
        new(WordImportFailure.AppStoreUnreadable, WordImportMessages.AppUnreadable(DisplayName));
}
