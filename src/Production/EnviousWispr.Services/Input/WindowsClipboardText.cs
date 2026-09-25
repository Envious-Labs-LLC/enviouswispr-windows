namespace EnviousWispr.Services.Input;

/// <summary>The one clipboard read the rest of the app may make without the delivery route: plain text, unchanged.</summary>
/// <remarks>
/// PUBLIC ON ITS OWN rather than by opening the delivery class, whose other members borrow and restore
/// the clipboard and belong to the delivery route alone.
/// </remarks>
public static class WindowsClipboardText
{
    /// <inheritdoc cref="WindowsClipboardPaste.TryReadTextAsync"/>
    public static Task<string?> TryReadAsync(CancellationToken cancellationToken) =>
        WindowsClipboardPaste.TryReadTextAsync(cancellationToken);
}
