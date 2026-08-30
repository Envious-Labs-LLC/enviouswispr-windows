namespace EnviousWispr.Core.Audio;

/// <summary>
/// Turns what a short microphone test actually received into one sentence.
/// </summary>
/// <remarks>
/// THREE KINDS OF NOTHING, AND THEY SEND SOMEBODY TO THREE DIFFERENT PLACES. A microphone that
/// delivered no packets at all is a device or a permission problem. One that delivered packets
/// Windows MARKED as deliberately silent is Windows saying there is nothing to hear, which is a
/// muted or unrouted device. One that delivered real packets of zeroes is a device that is open and
/// producing nothing, which is the fault that cost a day on the development machine because every
/// surface reported it as working. A bare "no sound" points at none of them.
///
/// A DECISION IN CORE SO EVERY BRANCH IS CHECKABLE WITHOUT A MICROPHONE. The capture is the part
/// that needs real hardware; which sentence follows from the counts is not.
/// </remarks>
public static class MicrophoneTestVerdict
{
    /// <summary>
    /// Below this peak, a room is not quiet, it is absent.
    /// </summary>
    /// <remarks>
    /// EVEN A SILENT ROOM HAS A FLOOR. A real microphone in a still room still reports a peak in the
    /// thousandths, so a peak under this is not a person being quiet.
    /// </remarks>
    public const float SilenceThreshold = 0.0005f;

    /// <summary>Above this peak, somebody clearly spoke rather than breathed.</summary>
    public const float SpeechThreshold = 0.05f;

    /// <param name="packets">Audio packets the device delivered.</param>
    /// <param name="silentPackets">How many Windows marked as deliberately silent.</param>
    /// <param name="peak">The loudest sample seen.</param>
    public static string For(int packets, int silentPackets, float peak)
    {
        if (packets == 0)
        {
            return "The microphone opened but sent nothing at all. Check it is still plugged in, "
                + "and that nothing else has taken it.";
        }

        if (silentPackets == packets)
        {
            return "Windows delivered only silence from this microphone. It is usually muted, or "
                + "switched to a device that is not the one you are speaking into.";
        }

        if (peak < SilenceThreshold)
        {
            return "The microphone is open and sending nothing but zeroes. That is not a quiet room, "
                + "it is no signal: try another microphone, or unplug and reconnect this one.";
        }

        return peak >= SpeechThreshold
            ? "Heard you clearly. This microphone is working."
            : "Something is arriving, but very quietly. Move closer, or raise this microphone's "
                + "level in Windows sound settings.";
    }
}
