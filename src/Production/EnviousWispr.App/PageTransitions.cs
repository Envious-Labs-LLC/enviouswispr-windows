using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace EnviousWispr.App;

/// <summary>
/// Fades a page in when it is shown, instead of it simply appearing.
/// </summary>
/// <remarks>
/// THE APP HAD NO MOTION AT ALL, AND THAT IS MOST OF WHAT MAKES A WINDOWS APP LOOK OLD. Counted
/// rather than guessed: zero theme transitions and zero animations across every XAML file in the
/// project. Pages are shown by switching a Border's visibility, so moving between Home, History and
/// Settings was a hard cut - the whole surface replaced between one frame and the next, which reads
/// as a redraw rather than as navigation.
///
/// AN IMPLICIT SHOW ANIMATION RATHER THAN A THEME TRANSITION, because the pages are not added and
/// removed - they are already there and their VISIBILITY changes, which an EntranceThemeTransition
/// never sees. This is the mechanism that fires on exactly that change.
///
/// A FADE AND TWELVE PIXELS, WHICH IS DELIBERATELY LESS THAN IT WANTS TO BE. Windows moves a page a
/// short distance and gets out of the way; anything longer or further turns navigation into a thing
/// somebody waits for, and this app is used in the middle of somebody else's work.
///
/// IT ASKS WINDOWS FIRST. Theme transitions honour the system's animation setting on their own, and
/// composition animations do not - so somebody who has turned motion off, often because it makes
/// them ill, would keep getting it from here unless it is checked. Read once at construction rather
/// than per navigation: it is a setting people change in Settings, not mid-dictation, and reading it
/// on every page switch spends a system call on an answer that has not moved.
/// </remarks>
internal static class PageTransitions
{
    /// <summary>How long a page takes to arrive.</summary>
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(220);

    /// <summary>How far it travels on the way in.</summary>
    private const float Rise = 12f;

    /// <summary>Whether this person wants motion at all.</summary>
    private static readonly bool Wanted = ReadAnimationsEnabled();

    /// <summary>Gives every page the same arrival, or none if motion is switched off.</summary>
    public static void Attach(params UIElement[] pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (!Wanted || pages.Length == 0)
        {
            return;
        }

        // ONE COMPOSITOR AND ONE ANIMATION FOR ALL OF THEM. Every page arrives the same way, and
        // building it once is also what stops seven copies drifting apart.
        var compositor = ElementCompositionPreview.GetElementVisual(pages[0]).Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = Duration;

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.Target = "Translation";
        rise.InsertKeyFrame(0f, new Vector3(0f, Rise, 0f));
        rise.InsertKeyFrame(1f, Vector3.Zero);
        rise.Duration = Duration;

        var arrival = compositor.CreateAnimationGroup();
        arrival.Add(fade);
        arrival.Add(rise);

        foreach (var page in pages)
        {
            // WITHOUT THIS THE MOVEMENT IS SILENTLY DROPPED. Translation is not animatable on an
            // element until it is switched on, and nothing reports that it was not - the fade would
            // play, the movement would not, and the result looks like a fade somebody chose.
            ElementCompositionPreview.SetIsTranslationEnabled(page, true);
            ElementCompositionPreview.SetImplicitShowAnimation(page, arrival);
        }
    }

    private static bool ReadAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or UnauthorizedAccessException or TypeLoadException)
        {
            // A SETTING THAT CANNOT BE READ IS A SETTING THAT SAYS NO. Motion somebody did not ask
            // for is the failure that matters here, and it is the one that makes people unwell.
            return false;
        }
    }
}
