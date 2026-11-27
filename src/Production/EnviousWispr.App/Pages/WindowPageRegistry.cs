using Microsoft.UI.Xaml;

namespace EnviousWispr.App.Pages;

internal enum WindowPageKey
{
    History,
    WhatsNew,
    Dictionary,
    Snippets,
    Settings,
    Onboarding,
}

internal sealed class WindowPageRegistration
{
    public WindowPageRegistration(
        WindowPageKey key,
        bool isContentPage,
        Func<bool> isLoaded,
        Action ensureLoaded,
        Func<FrameworkElement?> getElement,
        Action? onThemeChanged = null,
        Action? onShutdown = null)
    {
        Key = key;
        IsContentPage = isContentPage;
        IsLoaded = isLoaded;
        EnsureLoaded = ensureLoaded;
        GetElement = getElement;
        OnThemeChanged = onThemeChanged;
        OnShutdown = onShutdown;
    }

    public WindowPageKey Key { get; }

    public bool IsContentPage { get; }

    public Func<bool> IsLoaded { get; }

    public Action EnsureLoaded { get; }

    public Func<FrameworkElement?> GetElement { get; }

    public Action? OnThemeChanged { get; }

    public Action? OnShutdown { get; }
}

/// <summary>Coordinates load-on-demand page lifecycle, visibility switching, theme refresh, and shutdown for MainWindow.</summary>
internal sealed class WindowPageRegistry
{
    private readonly Dictionary<WindowPageKey, WindowPageRegistration> _pages = new();
    private readonly List<WindowPageRegistration> _ordered = [];

    public void Register(WindowPageRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_pages.ContainsKey(registration.Key))
        {
            throw new InvalidOperationException($"Duplicate registration for page key '{registration.Key}'.");
        }

        _pages[registration.Key] = registration;
        _ordered.Add(registration);
    }

    public bool IsLoaded(WindowPageKey key) =>
        _pages.TryGetValue(key, out var page) && page.IsLoaded();

    public void EnsureLoaded(WindowPageKey key)
    {
        if (_pages.TryGetValue(key, out var page) && !page.IsLoaded())
        {
            page.EnsureLoaded();
        }
    }

    public FrameworkElement? ActivateContentPage(WindowPageKey activeKey)
    {
        EnsureLoaded(activeKey);

        FrameworkElement? activeElement = null;
        foreach (var page in _ordered)
        {
            if (!page.IsContentPage || !page.IsLoaded())
            {
                continue;
            }

            if (page.GetElement() is not { } element)
            {
                continue;
            }

            var isTarget = page.Key == activeKey;
            element.Visibility = isTarget ? Visibility.Visible : Visibility.Collapsed;
            if (isTarget)
            {
                activeElement = element;
            }
        }

        return activeElement;
    }

    public void NotifyThemeChanged()
    {
        foreach (var page in _ordered)
        {
            if (page.IsLoaded())
            {
                page.OnThemeChanged?.Invoke();
            }
        }
    }

    public void ShutdownLoadedPages()
    {
        foreach (var page in _ordered)
        {
            if (page.IsLoaded())
            {
                page.OnShutdown?.Invoke();
            }
        }
    }
}
