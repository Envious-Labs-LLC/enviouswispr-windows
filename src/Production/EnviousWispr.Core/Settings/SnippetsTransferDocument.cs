using System.Text;
using System.Text.Json;

namespace EnviousWispr.Core.Settings;

/// <summary>The file Export writes and Import reads: macOS's <c>EnviousWispr Snippets.json</c>, format 1.</summary>
/// <remarks>
/// THE MAC'S FILE, NOT A WINDOWS COUSIN OF IT, so a person moving between the two apps carries their snippets both
/// ways. The shape is macOS <c>SnippetsTransferDocument</c> as shipped: a top-level object with <c>keyword</c>,
/// <c>snippets</c> and <c>version</c>, each snippet carrying <c>createdAt</c> (seconds since 2001-01-01 UTC, as
/// Foundation's default date encoding writes it), <c>expansion</c>, <c>id</c> (an upper-case UUID) and
/// <c>trigger</c>; keys sorted, as the Mac's encoder sorts them.
///
/// WINDOWS STORES NO ID OR CREATION TIME, so export mints a fresh id per snippet and stamps the export moment.
/// Neither is ever read back: import mints its own (macOS <c>candidatesForImport</c>), so exporting twice cannot
/// collide on identity.
///
/// THE KEYWORD IS WRITTEN AND NEVER APPLIED (founder 2026-09-16: "it should definitely only bring the snippets").
/// It is decoded so the file is recognised as whole, and then ignored.
///
/// A file is recognised as ours by its SHAPE, a top-level object carrying <c>snippets</c>, because v1 shipped
/// without a format marker and adding one would make every existing export "not ours".
/// </remarks>
public sealed record SnippetsTransferDocument(int Version, string Keyword, IReadOnlyList<SnippetImportCandidate> Snippets)
{
    public const int CurrentVersion = 1;

    /// <summary>The name the Mac suggests for an export, kept so the sentences that name it are the same on both.</summary>
    public const string DefaultFileName = "EnviousWispr Snippets.json";

    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The export file for these snippets and this keyword, as UTF-8 JSON text.</summary>
    /// <param name="newId">Where each snippet's id comes from; a fresh GUID in the app.</param>
    public static string Write(
        IReadOnlyList<SnippetEntry> snippets,
        string keyword,
        DateTimeOffset exportedAt,
        Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(snippets);
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentNullException.ThrowIfNull(newId);
        var createdAt = Math.Floor((exportedAt - ReferenceDate).TotalSeconds);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            // Readable text, not \u escapes for every accent: the file is meant to be opened and edited. The
            // relaxed encoder still escapes what JSON itself requires (quotes, backslashes, control characters).
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("keyword", keyword);
            writer.WriteStartArray("snippets");
            foreach (var snippet in snippets)
            {
                writer.WriteStartObject();
                writer.WriteNumber("createdAt", createdAt);
                writer.WriteString("expansion", snippet.Body);
                writer.WriteString("id", newId().ToString("D").ToUpperInvariant());
                writer.WriteString("trigger", snippet.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Whether the text parses as JSON at all.</summary>
    public static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads an export, or refuses with the sentence that says why: not ours, damaged, or from a newer version.</summary>
    /// <remarks>
    /// THE VERSION IS JUDGED BEFORE THE PAYLOAD, as on macOS: a future format may change a snippet's fields, and
    /// decoding everything first would call a newer file "damaged" when the truth is "update the app". Below 1 is a
    /// schema that never existed, so damaged; above <see cref="CurrentVersion"/> is a real future format.
    /// </remarks>
    public static SnippetsTransferDocument Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new SnippetImportException(SnippetImportFailure.Malformed, SnippetImportMessages.Damaged);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("snippets", out var snippets))
            {
                throw new SnippetImportException(SnippetImportFailure.NotOurs, SnippetImportMessages.NotOurFile);
            }

            if (!root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var version) ||
                snippets.ValueKind != JsonValueKind.Array)
            {
                throw new SnippetImportException(SnippetImportFailure.Malformed, SnippetImportMessages.Damaged);
            }

            if (version < 1)
            {
                throw new SnippetImportException(SnippetImportFailure.Malformed, SnippetImportMessages.Damaged);
            }

            if (version > CurrentVersion)
            {
                throw new SnippetImportException(SnippetImportFailure.NewerVersion, SnippetImportMessages.UnsupportedVersion(version));
            }

            if (!root.TryGetProperty("keyword", out var keywordElement) || keywordElement.ValueKind != JsonValueKind.String)
            {
                throw new SnippetImportException(SnippetImportFailure.Malformed, SnippetImportMessages.Damaged);
            }

            var read = new List<SnippetImportCandidate>();
            foreach (var entry in snippets.EnumerateArray())
            {
                // EVERY FIELD THE MAC REQUIRES IS REQUIRED HERE, so a file one app calls damaged the other does too.
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryString(entry, "trigger", out var trigger) ||
                    !TryString(entry, "expansion", out var expansion) ||
                    !TryString(entry, "id", out var id) || !Guid.TryParseExact(id, "D", out _) ||
                    !entry.TryGetProperty("createdAt", out var created) || created.ValueKind != JsonValueKind.Number)
                {
                    throw new SnippetImportException(SnippetImportFailure.Malformed, SnippetImportMessages.Damaged);
                }

                read.Add(new SnippetImportCandidate(trigger, expansion));
            }

            return new SnippetsTransferDocument(version, keywordElement.GetString()!, read);
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }
}
