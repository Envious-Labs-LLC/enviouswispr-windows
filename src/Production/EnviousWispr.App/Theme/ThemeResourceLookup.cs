using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace EnviousWispr.App;

/// <summary>
/// Resolves themed XAML resources against High Contrast first, then the active Light or Dark theme.
/// </summary>
internal static class ThemeResourceLookup
{
    internal static string? ResolveThemeDictionaryKey(
        ElementTheme actualTheme,
        bool isHighContrast,
        ApplicationTheme? appTheme = null)
    {
        if (isHighContrast)
        {
            return "HighContrast";
        }

        return actualTheme switch
        {
            ElementTheme.Dark => "Dark",
            ElementTheme.Light => "Light",
            _ => appTheme switch
            {
                ApplicationTheme.Dark => "Dark",
                ApplicationTheme.Light => "Light",
                _ => null,
            },
        };
    }

    internal static bool IsHighContrastActive()
    {
        try
        {
            return new AccessibilitySettings().HighContrast;
        }
        catch
        {
            return false;
        }
    }

    internal static T? Resolve<T>(FrameworkElement? scope, string key) where T : class
    {
        var app = Application.Current;
        var themeKey = ResolveThemeDictionaryKey(
            scope?.ActualTheme ?? ElementTheme.Default,
            IsHighContrastActive(),
            app?.RequestedTheme);

        if (scope is not null)
        {
            if (themeKey is not null
                && TryFindInThemeDictionaries<T>(scope.Resources, themeKey, key) is { } scopedThemed)
            {
                return scopedThemed;
            }

            if (scope.Resources.TryGetValue(key, out var local) && local is T localTyped)
            {
                return localTyped;
            }
        }

        var appResources = app?.Resources;
        if (appResources is null)
        {
            return null;
        }

        if (themeKey is not null
            && TryFindInThemeDictionaries<T>(appResources, themeKey, key) is { } themed)
        {
            return themed;
        }

        return appResources.TryGetValue(key, out var appRes) && appRes is T appTyped
            ? appTyped
            : null;
    }

    private static T? TryFindInThemeDictionaries<T>(
        ResourceDictionary dictionary,
        string themeKey,
        string key) where T : class
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themeObj)
            && themeObj is ResourceDictionary themeDict
            && themeDict.TryGetValue(key, out var themeVal)
            && themeVal is T typed)
        {
            return typed;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (TryFindInThemeDictionaries<T>(merged, themeKey, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
