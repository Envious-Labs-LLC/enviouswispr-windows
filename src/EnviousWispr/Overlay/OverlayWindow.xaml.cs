using System.Windows;
using System.Windows.Media;

namespace EnviousWispr;

/// Borderless top-right status pill. Non-activating so it never steals focus
/// from the app the user is dictating into.
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, ex | 0x08000000 | 0x00000080 | 0x00000008);
        PositionTopRight();
    }

    private const int GwlExStyle = -20;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Top + 24;
    }

    public void SetState(string state, string detail, Color dot)
    {
        if (Dispatcher.CheckAccess()) Apply(state, detail, dot);
        else Dispatcher.BeginInvoke(() => Apply(state, detail, dot));
    }

    private void Apply(string state, string detail, Color dot)
    {
        StateText.Text = state;
        DetailText.Text = detail;
        StateDot.Fill = new SolidColorBrush(dot);
    }
}
