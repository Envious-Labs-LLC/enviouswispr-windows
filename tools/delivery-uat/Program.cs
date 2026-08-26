using EnviousWispr.Core.Dictation;
using EnviousWispr.Pipeline;
using EnviousWispr.Services.Input;
using System.Text.Json;

var fixture = ArgumentValue(args, "--fixture") ?? "plain";
var outputPath = ArgumentValue(args, "--output") ?? Path.Combine(
    Path.GetTempPath(),
    $"EnviousWispr-delivery-uat-{Environment.ProcessId}.jsonl");
Action<string> emit = line =>
{
    Console.WriteLine(line);
    File.AppendAllText(outputPath, line + Environment.NewLine);
};
var captureDelay = Milliseconds(args, "--capture-delay-ms", 20_000);
var deliveryDelay = Milliseconds(args, "--delivery-delay-ms", 1_500);
var (text, language) = fixture.ToLowerInvariant() switch
{
    "plain" => ("phase thirteen delivery proof", "en"),
    "seam" => ("Synthetic continuation.", "en"),
    "period" => ("Synthetic sentence.", "en"),
    "multiline" => ("synthetic command\r\nsecond line", "en"),
    "unicode" => ("世界", "zh"),
    _ => throw new ArgumentException("Unknown --fixture. Use plain, seam, period, multiline, or unicode."),
};

emit(
    $"ARMED fixture={fixture} capture_in_ms={captureDelay.TotalMilliseconds:0} " +
    $"deliver_after_ms={deliveryDelay.TotalMilliseconds:0}");
await Task.Delay(captureDelay);

var target = new WindowsForegroundTargetProvider().CaptureForegroundTarget();
if (target is null || !target.Value.IsValid)
{
    emit(JsonSerializer.Serialize(new
    {
        captured = false,
        result = TextDeliveryRefusalReason.TargetUnavailable.ToString(),
    }));
    return 2;
}

using var adapter = new WindowsTextTargetAdapter();
var context = await adapter.CaptureContextAsync(
    target.Value,
    TextDeliveryOptions.Default);
emit(JsonSerializer.Serialize(new
{
    captured = true,
    processFrozen = target.Value.ProcessId != 0,
    elementFrozen = target.Value.FocusedElementId is not null,
    contextStatus = context.Status.ToString(),
    targetKind = context.Context?.TargetKind.ToString() ?? TextTargetKind.Unknown.ToString(),
    textContext = context.Context?.HasTextContext == true,
    protectedField = context.Status == TargetContextStatus.Protected,
}));

await Task.Delay(deliveryDelay);
var delivery = new ContextAwareTextDelivery(adapter);
var result = await delivery.DeliverAsync(new TextDeliveryRequest(
    new ProcessedText(DictationSessionId.Create(), text),
    target.Value,
    language,
    TextDeliveryOptions.Default));
emit(JsonSerializer.Serialize(new
{
    route = result.Route.ToString(),
    result.Delivered,
    result.ClipboardFallback,
    result.ClipboardRestored,
    refusal = result.RefusalReason.ToString(),
    repair = result.RepairDisposition.ToString(),
    recoveryHeldInMemory = delivery.RecoveryText is not null,
}));

return result.Delivered || result.ClipboardFallback ? 0 : 3;

static string? ArgumentValue(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static TimeSpan Milliseconds(string[] arguments, string name, int fallback)
{
    var raw = ArgumentValue(arguments, name);
    return int.TryParse(raw, out var value) && value is >= 0 and <= 30_000
        ? TimeSpan.FromMilliseconds(value)
        : TimeSpan.FromMilliseconds(fallback);
}
