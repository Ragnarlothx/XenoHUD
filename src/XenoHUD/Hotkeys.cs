using static XenoHUD.Native;

namespace XenoHUD;

/// <summary>Global hotkeys on a hidden message window, so they survive the main window changing parents.</summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    readonly Dictionary<int, string> actions = new();
    public event Action<string>? Pressed;
    public List<string> Failed { get; } = new();

    public Hotkeys() => CreateHandle(new CreateParams());

    public void Register(IReadOnlyDictionary<string, string> map)
    {
        Clear();
        int id = 1;
        foreach (var (action, combo) in map)
        {
            if (!TryParse(combo, out var mods, out var vk) || !RegisterHotKey(Handle, id, mods | 0x4000 /*NOREPEAT*/, vk))
            {
                Failed.Add($"{action}={combo}");
                continue;
            }
            actions[id++] = action;
        }
    }

    void Clear()
    {
        foreach (var id in actions.Keys) UnregisterHotKey(Handle, id);
        actions.Clear();
        Failed.Clear();
    }

    static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= 0x1; break;
                case "ctrl": case "control": mods |= 0x2; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    if (!Enum.TryParse<Keys>(raw, true, out var key)) return false;
                    vk = (uint)key;
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && actions.TryGetValue(m.WParam.ToInt32(), out var a)) Pressed?.Invoke(a);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Clear();
        DestroyHandle();
    }
}
