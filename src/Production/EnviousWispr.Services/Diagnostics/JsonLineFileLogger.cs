using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnviousWispr.Core.Diagnostics;

namespace EnviousWispr.Services.Diagnostics;

public sealed class JsonLineFileLogger : IAppLogger
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _writeLock = new();
    private readonly string _path;

    public JsonLineFileLogger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public void Write(AppLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine;
            lock (_writeLock)
            {
                File.AppendAllText(_path, line);
            }
        }
        catch (IOException)
        {
            // Diagnostics are best-effort and can never break dictation or startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics are best-effort and can never break dictation or startup.
        }
        catch (SecurityException)
        {
            // Diagnostics are best-effort and can never break dictation or startup.
        }
    }
}
