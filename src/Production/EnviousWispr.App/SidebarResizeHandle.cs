using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace EnviousWispr.App;

/// <summary>The strip on the sidebar's edge that drags the sidebar narrower or wider.</summary>
/// <remarks>
/// A CLASS OF ITS OWN ONLY FOR THE CURSOR. WinUI keeps an element's pointer shape behind the protected
/// ProtectedCursor property, so the east-west resize arrow can only be shown from a derived element. It is a
/// ContentControl rather than a panel so it can take keyboard focus as well as the pointer. The drag itself
/// belongs to the window, which owns the sidebar width (MainWindow.Shell.cs).
/// </remarks>
public sealed partial class SidebarResizeHandle : ContentControl
{
    public SidebarResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
