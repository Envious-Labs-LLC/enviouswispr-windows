using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

const int ProtocolVersion = 1;

var parentProcessId = ReadIntegerArgument(args, "--parent-pid");
var healthDelayMilliseconds = ReadIntegerArgument(args, "--health-delay-ms", defaultValue: 0);
if (parentProcessId <= 0 || healthDelayMilliseconds < 0)
{
    return 2;
}

Process parent;
try
{
    parent = Process.GetProcessById(parentProcessId);
}
catch (ArgumentException)
{
    return 3;
}

using (parent)
{
    parent.EnableRaisingEvents = true;
    parent.Exited += (_, _) => Environment.Exit(24);

    while (await Console.In.ReadLineAsync() is { } line)
    {
        RuntimeWorkerRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RuntimeWorkerRequest>(line);
        }
        catch (JsonException)
        {
            await WriteResponseAsync(new RuntimeWorkerResponse(
                ProtocolVersion,
                RequestId: Guid.Empty,
                Status: "invalid"));
            continue;
        }

        if (request is null ||
            request.ProtocolVersion != ProtocolVersion ||
            request.RequestId == Guid.Empty)
        {
            await WriteResponseAsync(new RuntimeWorkerResponse(
                ProtocolVersion,
                request?.RequestId ?? Guid.Empty,
                Status: "invalid"));
            continue;
        }

        if (string.Equals(request.Command, "health", StringComparison.Ordinal))
        {
            if (healthDelayMilliseconds > 0)
            {
                await Task.Delay(healthDelayMilliseconds);
            }

            await WriteResponseAsync(new RuntimeWorkerResponse(
                ProtocolVersion,
                request.RequestId,
                Status: "ready"));
            continue;
        }

        if (string.Equals(request.Command, "shutdown", StringComparison.Ordinal))
        {
            await WriteResponseAsync(new RuntimeWorkerResponse(
                ProtocolVersion,
                request.RequestId,
                Status: "stopping"));
            return 0;
        }

        await WriteResponseAsync(new RuntimeWorkerResponse(
            ProtocolVersion,
            request.RequestId,
            Status: "unsupported"));
    }
}

return 0;

static int ReadIntegerArgument(string[] arguments, string name, int defaultValue = -1)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length &&
        int.TryParse(
            arguments[index + 1],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
        ? value
        : defaultValue;
}

static async Task WriteResponseAsync(RuntimeWorkerResponse response)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
    await Console.Out.FlushAsync();
}

internal sealed record RuntimeWorkerRequest(int ProtocolVersion, Guid RequestId, string Command);

internal sealed record RuntimeWorkerResponse(int ProtocolVersion, Guid RequestId, string Status);
