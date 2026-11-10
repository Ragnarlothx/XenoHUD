using static XenoHUD.Native;

namespace XenoHUD;

/// <summary>
/// Puts our window behind the desktop icons, the way live-wallpaper apps do.
/// This is runtime-only: no wallpaper file, registry value or setting is changed,
/// and closing the app (or Detach) puts the normal desktop straight back.
/// The desktop icons sit on top, so this mode is look-only; clicks need HUD or window mode.
/// </summary>
sealed class DesktopLayer
{
    IntPtr workerW;
    IntPtr savedStyle;
    public bool Attached { get; private set; }

    static IntPtr FindWorkerW()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // Ask Explorer to create the WorkerW that sits between the wallpaper and the icons.
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        // Windows 11 24H2+: the WorkerW is a child of Progman.
        var child = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (child != IntPtr.Zero) return child;

        // Older builds: the WorkerW is the top-level window right after the one holding the icons.
        var found = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                found = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public bool Attach(Form form, bool allMonitors)
    {
        if (Attached) return true;
        workerW = FindWorkerW();
        if (workerW == IntPtr.Zero) return false;

        var h = form.Handle;
        savedStyle = GetWindowLongPtr(h, GWL_STYLE);
        var style = (savedStyle.ToInt64() & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD | WS_VISIBLE;

        var area = allMonitors ? SystemInformation.VirtualScreen : Screen.PrimaryScreen!.Bounds;
        SetParent(h, workerW);
        SetWindowLongPtr(h, GWL_STYLE, new IntPtr(style));
        var pt = new POINT { X = area.Left, Y = area.Top };
        ScreenToClient(workerW, ref pt);
        SetWindowPos(h, IntPtr.Zero, pt.X, pt.Y, area.Width, area.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        Attached = true;
        return true;
    }

    public void Detach(Form form)
    {
        if (!Attached) return;
        var h = form.Handle;
        SetParent(h, IntPtr.Zero);
        SetWindowLongPtr(h, GWL_STYLE, savedStyle);
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | SWP_NOZORDER | SWP_FRAMECHANGED); // NOSIZE|NOMOVE
        if (workerW != IntPtr.Zero)
            RedrawWindow(workerW, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        Attached = false;
    }
}
