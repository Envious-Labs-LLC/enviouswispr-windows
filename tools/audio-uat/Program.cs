using System.Text.Json;
using EnviousWispr.Audio;
using EnviousWispr.Core.Audio;
using EnviousWispr.Core.Dictation;
using EnviousWispr.Core.Errors;

using var catalog = new WasapiDeviceCatalog();
var devices = await catalog.GetCaptureDevicesAsync();
if (devices.Count == 0)
{
    Console.WriteLine(JsonSerializer.Serialize(new { captureDevices = 0, outcome = "NoDevice" }));
    return 2;
}

var defaultCapture = await CaptureAsync(DeviceId: null, TimeSpan.FromSeconds(2));
var selectedCapture = await CaptureAsync(devices[0].Id, TimeSpan.FromSeconds(1));

await using var overlapCapture = new WasapiAudioCapture();
var overlapRequest = new AudioCaptureRequest(DictationSessionId.Create());
var firstStart = await overlapCapture.StartAsync(overlapRequest);
var secondStart = await overlapCapture.StartAsync(overlapRequest);
await Task.Delay(TimeSpan.FromMilliseconds(150));
var cancel = await overlapCapture.CancelAsync();

var summary = new
{
    captureDevices = devices.Count,
    defaultDevices = devices.Count(device => device.IsDefault),
    defaultCapture,
    selectedCapture,
    overlapRejected = firstStart.Succeeded &&
        !secondStart.Succeeded &&
        secondStart.Error?.Code == AppErrorCode.CaptureAlreadyActive,
    cancelSucceeded = cancel.Succeeded && !overlapCapture.IsCapturing,
};
Console.WriteLine(JsonSerializer.Serialize(summary));

return CapturePassed(defaultCapture, minimumDurationMilliseconds: 1_500) &&
    CapturePassed(selectedCapture, minimumDurationMilliseconds: 750) &&
    summary.overlapRejected &&
    summary.cancelSucceeded
    ? 0
    : 4;

static async Task<CaptureMetrics> CaptureAsync(AudioDeviceId? DeviceId, TimeSpan duration)
{
    await using var capture = new WasapiAudioCapture();
    var levelGate = new object();
    var observedPeak = 0f;
    double rmsSum = 0;
    var levelEvents = 0;
    capture.LevelChanged += (_, level) =>
    {
        lock (levelGate)
        {
            observedPeak = Math.Max(observedPeak, level.Peak);
            rmsSum += level.RootMeanSquare;
            levelEvents++;
        }
    };

    var started = await capture.StartAsync(new AudioCaptureRequest(DictationSessionId.Create(), DeviceId));
    if (!started.Succeeded)
    {
        return new CaptureMetrics(
            SampleRate: 0,
            Channels: 0,
            SampleCount: 0,
            DurationMilliseconds: 0,
            Outcome: "StartFailed",
            Error: started.Error?.Code.ToString(),
            LevelEvents: 0,
            Peak: 0,
            AverageRootMeanSquare: 0);
    }

    await Task.Delay(duration);
    var result = await capture.StopAsync();
    lock (levelGate)
    {
        return new CaptureMetrics(
            result.SampleRate,
            result.Channels,
            result.Samples.Length,
            result.Samples.Length * 1000L / result.SampleRate,
            result.Outcome.ToString(),
            result.Error?.Code.ToString(),
            levelEvents,
            observedPeak,
            levelEvents == 0 ? 0 : rmsSum / levelEvents);
    }
}

static bool CapturePassed(CaptureMetrics result, long minimumDurationMilliseconds) =>
    result.Outcome == AudioCaptureOutcome.Completed.ToString() &&
    result.SampleRate == AudioSampleConverter.TargetSampleRate &&
    result.Channels == 1 &&
    result.DurationMilliseconds >= minimumDurationMilliseconds &&
    result.LevelEvents > 0;

internal sealed record CaptureMetrics(
    int SampleRate,
    int Channels,
    int SampleCount,
    long DurationMilliseconds,
    string Outcome,
    string? Error,
    int LevelEvents,
    float Peak,
    double AverageRootMeanSquare);
