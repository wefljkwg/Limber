using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace Limber;

/// <summary>A key with modifiers, set by the player in the Hotkeys tab (read from the game's own key state).</summary>
[Serializable]
public sealed class Hotkey
{
    public int Key { get; set; }        // Windows virtual-key code; 0 = not set
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    private bool _wasDown;

    public bool IsValid => Key > 0 && Key < 256;

    /// <summary>Pressed just now (this check, not the last), with exactly these modifiers.</summary>
    public bool PressedNow(IKeyState keys)
    {
        if (!IsValid || !keys.IsVirtualKeyValid((VirtualKey)Key))
        {
            _wasDown = false;
            return false;
        }
        // The game's key state, or (when another plugin, e.g. Brio, already took the key away from the game) the
        // keyboard itself, as long as the game window is the one in front.
        bool gameWindow = GameWindowFocused();
        bool Down(VirtualKey k) => keys[k] || (gameWindow && (GetAsyncKeyState((int)k) & 0x8000) != 0);
        bool down = Down((VirtualKey)Key) && Down(VirtualKey.CONTROL) == Ctrl && Down(VirtualKey.SHIFT) == Shift && Down(VirtualKey.MENU) == Alt;
        bool pressed = down && !_wasDown;
        _wasDown = down;
        return pressed;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private static readonly uint OwnProcess = (uint)Environment.ProcessId;

    private static bool GameWindowFocused()
    {
        var window = GetForegroundWindow();
        return window != 0 && GetWindowThreadProcessId(window, out var process) != 0 && process == OwnProcess;
    }

    public string Label
    {
        get
        {
            if (!IsValid)
                return "(none)";
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            var name = Enum.GetName(typeof(VirtualKey), Key) ?? $"Key {Key}";
            if (name.StartsWith("KEY_", StringComparison.Ordinal))
                name = name[4..];
            parts.Add(name);
            return string.Join("+", parts);
        }
    }

    private static readonly HashSet<VirtualKey> NotSettable =
    [
        VirtualKey.CONTROL, VirtualKey.SHIFT, VirtualKey.MENU, VirtualKey.LCONTROL, VirtualKey.RCONTROL,
        VirtualKey.LSHIFT, VirtualKey.RSHIFT, VirtualKey.LMENU, VirtualKey.RMENU,
        VirtualKey.LBUTTON, VirtualKey.RBUTTON, VirtualKey.MBUTTON, VirtualKey.XBUTTON1, VirtualKey.XBUTTON2,
        VirtualKey.ESCAPE,
    ];

    /// <summary>A key that's down now (not a modifier, mouse button or Esc), or 0.</summary>
    public static int AnyKeyDown(IKeyState keys)
    {
        foreach (var k in keys.GetValidVirtualKeys())
            if (!NotSettable.Contains(k) && keys[k])
                return (int)k;
        return 0;
    }
}
