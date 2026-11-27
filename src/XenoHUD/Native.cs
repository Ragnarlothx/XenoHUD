using System.Runtime.InteropServices;

namespace XenoHUD;

static class Native
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    public const uint LWA_ALPHA = 0x2;

    /// <summary>Opacity and click-through for our own top-level window (no WinForms handle recreation).</summary>
    public static void SetOverlayStyle(IntPtr hwnd, double opacity, bool clickThrough)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        bool layered = opacity < 0.999 || clickThrough;
        ex = layered ? ex | WS_EX_LAYERED : ex & ~WS_EX_LAYERED;
        ex = clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        if (layered) SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(Math.Clamp(opacity, 0.2, 1) * 255), LWA_ALPHA);
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] public static extern bool IsVisibleWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    /// <summary>Windows that exist but aren't shown (other virtual desktops, suspended Store apps).</summary>
    public static bool IsCloaked(IntPtr hWnd) => DwmGetWindowAttribute(hWnd, 14 /*DWMWA_CLOAKED*/, out var c, sizeof(int)) == 0 && c != 0;
    public static bool IsToolWindow(IntPtr hWnd) => (GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() & 0x80 /*WS_EX_TOOLWINDOW*/) != 0;

    public static string ClassName(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(128);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public const int GWL_STYLE = -16;
    public const long WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_VISIBLE = 0x10000000;
    public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
    public const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;
    public const uint WM_HOTKEY = 0x0312, WM_NCLBUTTONDOWN = 0xA1;
    public const int HTCAPTION = 2;

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

    /// <summary>Title of each process's main window (visible, unowned, titled top-level window; first in z-order),
    /// in one EnumWindows pass. Process.MainWindowTitle walks every window again for each process.</summary>
    public static Dictionary<int, string> WindowTitlesByPid()
    {
        var map = new Dictionary<int, string>();
        var sb = new System.Text.StringBuilder(256);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (map.ContainsKey(pid)) return true;
            sb.Clear(); sb.EnsureCapacity(len + 1);
            if (GetWindowText(h, sb, len + 1) > 0) map[pid] = sb.ToString();
            return true;
        }, IntPtr.Zero);
        return map;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>A monitor's own scale (1.0 = 100%, 1.5 = 150%, 2.0 = 200%) from its effective DPI.</summary>
    public static double MonitorScale(Screen screen)
    {
        try
        {
            var m = MonitorFromPoint(new POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 }, 2);
            if (GetDpiForMonitor(m, 0, out var x, out _) == 0 && x > 0) return x / 96.0;
        }
        catch { }
        return 1;
    }

    /// <summary>Dark title bar for our own window only.</summary>
    public static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }
}
