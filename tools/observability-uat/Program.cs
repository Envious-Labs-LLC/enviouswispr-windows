using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EnviousWispr.Core.Diagnostics;
using EnviousWispr.Core.Settings;
using EnviousWispr.Services.Diagnostics;

var tempDirectory = Path.Combine(
    Path.GetTempPath(),
    "EnviousWispr.Observability.Uat",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDirectory);
try
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var endpointText = $"http://127.0.0.1:{port}/v1/events";
    if (!TelemetryEndpointPolicy.TryNormalize(endpointText, allowLoopbackHttp: true, out var endpoint))
    {
        throw new InvalidOperationException("The bounded loopback UAT endpoint was rejected.");
    }

    var source = Path.Combine(tempDirectory, "app.jsonl");
    var export = Path.Combine(tempDirectory, "export.jsonl");
    await using (var logger = new PrivacySafeObservabilityLogger(
        new JsonLineFileLogger(source, enabled: false),
        new HttpPrivacySafeTelemetryTransport(endpoint!)))
    {
        var now = DateTimeOffset.UtcNow;
        logger.Configure(new ObservabilityPreferences(true, 14, false), now);
        logger.Write(new AppLogEntry(now, AppEventCode.ApplicationStarting));
        await Task.Delay(150);
        var preConsentRequests = listener.Pending() ? 1 : 0;

        var receive = ReceiveOneAsync(listener, CancellationToken.None);
        logger.Configure(new ObservabilityPreferences(true, 14, true), now);
        logger.Write(new AppLogEntry(
            now,
            AppEventCode.RuntimeSelectionObserved,
            AppFailureCategory.None,
            ElapsedMilliseconds: 42,
            Engine: DiagnosticEngineChoice.Whisper,
            HardwareClass: DiagnosticHardwareClass.GpuPresent));
        var body = await receive.WaitAsync(TimeSpan.FromSeconds(5));

        using var document = JsonDocument.Parse(body);
        var telemetryFields = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "timestamp", "event", "failure", "elapsedMilliseconds", "provider",
            "errorCode", "engine", "hardwareClass",
        };
        var typedTelemetryOnly = telemetryFields.IsSubsetOf(allowed) &&
            !ContainsForbiddenName(telemetryFields);

        const string injected =
            "{\"timestamp\":\"2026-08-26T12:00:00Z\",\"event\":\"ShellShown\",\"failure\":\"None\",\"transcript\":\"PRIVATE_SENTINEL\"}";
        await File.AppendAllTextAsync(source, injected + Environment.NewLine);
        var exportResult = await new JsonDiagnosticExportService(source).ExportAsync(
            export,
            14,
            now);
        var exported = await File.ReadAllTextAsync(export);
        var forbiddenContentAbsent = !exported.Contains("PRIVATE_SENTINEL", StringComparison.Ordinal) &&
            !exported.Contains("transcript", StringComparison.OrdinalIgnoreCase) &&
            !exported.Contains("clipboard", StringComparison.OrdinalIgnoreCase) &&
            !exported.Contains("audio", StringComparison.OrdinalIgnoreCase);

        var passed = preConsentRequests == 0 &&
            typedTelemetryOnly &&
            exportResult.Succeeded &&
            exportResult.ExportedRecordCount == 2 &&
            forbiddenContentAbsent;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            preConsentRequests,
            postConsentRequests = 1,
            telemetryFieldCount = telemetryFields.Count,
            typedTelemetryOnly,
            exportResult.ExportedRecordCount,
            forbiddenContentAbsent,
            passed,
        }));
        return passed ? 0 : 2;
    }
}
finally
{
    if (Directory.Exists(tempDirectory))
    {
        Directory.Delete(tempDirectory, recursive: true);
    }
}

static bool ContainsForbiddenName(IEnumerable<string> names) => names.Any(name =>
    name.Contains("text", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("clipboard", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("context", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
    name.EndsWith("Id", StringComparison.OrdinalIgnoreCase));

static async Task<string> ReceiveOneAsync(TcpListener listener, CancellationToken cancellationToken)
{
    using var client = await listener.AcceptTcpClientAsync(cancellationToken);
    await using var stream = client.GetStream();
    var received = new List<byte>();
    var buffer = new byte[4096];
    var headerEnd = -1;
    while (headerEnd < 0)
    {
        var count = await stream.ReadAsync(buffer, cancellationToken);
        if (count == 0)
        {
            throw new IOException("Telemetry UAT request ended before its headers completed.");
        }

        received.AddRange(buffer.AsSpan(0, count).ToArray());
        headerEnd = FindHeaderEnd(received);
    }

    var header = Encoding.ASCII.GetString(received.Take(headerEnd).ToArray());
    var contentLengthLine = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
    var contentLength = int.Parse(
        contentLengthLine[(contentLengthLine.IndexOf(':') + 1)..],
        System.Globalization.CultureInfo.InvariantCulture);
    var bodyStart = headerEnd + 4;
    while (received.Count - bodyStart < contentLength)
    {
        var count = await stream.ReadAsync(buffer, cancellationToken);
        if (count == 0)
        {
            throw new IOException("Telemetry UAT request ended before its body completed.");
        }

        received.AddRange(buffer.AsSpan(0, count).ToArray());
    }

    var response = Encoding.ASCII.GetBytes(
        "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(response, cancellationToken);
    return Encoding.UTF8.GetString(received.Skip(bodyStart).Take(contentLength).ToArray());
}

static int FindHeaderEnd(IReadOnlyList<byte> bytes)
{
    for (var index = 0; index <= bytes.Count - 4; index++)
    {
        if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
            bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
        {
            return index;
        }
    }

    return -1;
}
