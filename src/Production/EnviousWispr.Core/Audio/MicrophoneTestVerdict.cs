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
    /// <summary>Above this root-mean-square, somebody clearly spoke rather than breathed.</summary>
    /// <remarks>
    /// MEASURED, NOT CHOSEN. Ordinary speech on the development hardware reads about 0.004, and a
    /// still room reads in the ten-thousandths, so this sits between them with room on both sides.
    /// It is the only threshold left: "no signal" is now reserved for an EXACT zero.
    /// </remarks>
    public const float SpeechThreshold = 0.002f;

    /// <param name="packets">Audio packets the device delivered.</param>
    /// <param name="silentPackets">How many Windows marked as deliberately silent.</param>
    /// <param name="rootMeanSquare">
    /// The loudest root-mean-square seen. The same number the meter is drawn from, so the words and
    /// the picture cannot disagree.
    /// </param>
    public static string For(int packets, int silentPackets, float rootMeanSquare)
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

        // AN EXACT ZERO, AND NOTHING ELSE, IS "NO SIGNAL". A threshold here condemned working
        // hardware: a quiet or low-gain microphone reading four ten-thousandths was told it was
        // sending nothing but zeroes, while the meter beside it lit a bar. Anything that is not
        // exactly nothing IS something, and the honest answer for a small something is that it is
        // quiet.
        if (rootMeanSquare <= 0f)
        {
            return "The microphone is open and sending nothing but zeroes. That is not a quiet room, "
                + "it is no signal: try another microphone, or unplug and reconnect this one.";
        }

        return rootMeanSquare >= SpeechThreshold
            ? "Heard you clearly. This microphone is working."
            : "Something is arriving, but very quietly. Move closer, or raise this microphone's "
                + "level in Windows sound settings.";
    }
}
