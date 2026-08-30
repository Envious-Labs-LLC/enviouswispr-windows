namespace EnviousWispr.Core.Input;

/// <summary>
/// Which two keybind fields are asking for the same key.
/// </summary>
/// <remarks>
/// ONE DEFINITION OF "THESE CLASH", USED BY BOTH THE FIELD AND THE SAVE BUTTON. Saving already
/// refused three matching shortcuts, so nothing broken could ever be written. What was missing was
/// the part a person experiences: two fields reading the same thing, looking settled, with the
/// objection arriving only after they press Save. Both now ask this.
/// </remarks>
public static class HotkeyConflictDetector
{
    /// <summary>Two roles that resolved to the same key.</summary>
    public readonly record struct Clash(string FirstRole, string SecondRole, string Gesture);

    /// <summary>Every pair of roles that resolved to the same key.</summary>
    /// <remarks>
    /// TEXT THAT DOES NOT PARSE IS NOT A CLASH, and two fields of unparseable text are not a clash
    /// with each other. Comparing the parse results directly would make two empty fields collide,
    /// which is the state every fresh field starts in.
    /// </remarks>
    public static IReadOnlyList<Clash> Find(IReadOnlyList<(string Role, string Text)> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var parsed = bindings
            .Select(binding => (binding.Role, HotkeyGestureParser.Parse(binding.Text).Gesture))
            .ToArray();

        var clashes = new List<Clash>();
        for (var first = 0; first < parsed.Length; first++)
        {
            for (var second = first + 1; second < parsed.Length; second++)
            {
                if (parsed[first].Gesture is { } gesture && gesture == parsed[second].Gesture)
                {
                    clashes.Add(new Clash(parsed[first].Role, parsed[second].Role, gesture.ToString()));
                }
            }
        }

        return clashes;
    }

    /// <summary>The sentence shown under the fields, naming what collided with what.</summary>
    public static string Describe(IReadOnlyList<Clash> clashes)
    {
        ArgumentNullException.ThrowIfNull(clashes);

        if (clashes.Count == 0)
        {
            return string.Empty;
        }

        var sentences = clashes
            .Select(clash => $"{clash.FirstRole} and {clash.SecondRole} are both set to {clash.Gesture}.");

        return string.Join(" ", sentences) + " Give each one its own shortcut.";
    }
}
