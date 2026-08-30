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

    // WHICH KIND OF SILENCE, IF IT IS SILENT. Windows marks a packet as deliberately silent when it
    // is handing over zeroes on purpose; zeroes WITHOUT that flag are a microphone that is on and
    // delivering nothing, which is a different fault entirely. Read off the capture itself, because
    // the analyser is right that an interface variable here buys nothing.
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
            AverageRootMeanSquare: 0,
            Packets: 0,
            SilentPackets: 0,
            LoudestRootMeanSquare: 0);
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
            levelEvents == 0 ? 0 : rmsSum / levelEvents,
            capture.LastPacketCount,
            capture.LastSilentPacketCount,
            capture.LastRootMeanSquare);
    }
}

// A CAPTURE THAT HEARD NOTHING USED TO PASS THIS. It started, it ran for the right duration, it
// produced the right sample rate and the right count of samples, and every one of them was zero -
// and every assertion here was about the shape of the recording rather than its contents. A capture
// path handing the app digital silence sailed through, twice a day, for months.
//
// A ROOM IS NEVER EXACTLY ZERO. Even a quiet one has a floor, so a peak of exactly nothing is not
// quiet, it is nothing arriving. That is the assertion that was missing.
static bool CapturePassed(CaptureMetrics result, long minimumDurationMilliseconds) =>
    result.Outcome == AudioCaptureOutcome.Completed.ToString() &&
    result.SampleRate == AudioSampleConverter.TargetSampleRate &&
    result.Channels == 1 &&
    result.DurationMilliseconds >= minimumDurationMilliseconds &&
    result.LevelEvents > 0 &&
    result.Peak > 0 &&
    result.LoudestRootMeanSquare > 0 &&
    result.SilentPackets < result.Packets;

internal sealed record CaptureMetrics(
    int SampleRate,
    int Channels,
    int SampleCount,
    long DurationMilliseconds,
    string Outcome,
    string? Error,
    int LevelEvents,
    float Peak,
    double AverageRootMeanSquare,
    int Packets,
    int SilentPackets,
    float LoudestRootMeanSquare);
