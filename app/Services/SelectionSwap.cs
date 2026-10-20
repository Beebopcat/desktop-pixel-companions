using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace AzerothCompanion.Services;

/// <summary>A global hotkey as text, e.g. "Ctrl+Alt+1".</summary>
public readonly record struct HotkeySpec(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 1, Control = 2, Shift = 4, Win = 8, NoRepeat = 0x4000;

    public static HotkeySpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0; uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Control; continue;
                case "alt": mods |= Alt; continue;
                case "shift": mods |= Shift; continue;
                case "win": case "windows": mods |= Win; continue;
            }
            if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) vk = char.ToUpperInvariant(raw[0]);
            else if (raw.StartsWith("numpad", StringComparison.OrdinalIgnoreCase) && raw.Length == 7 && char.IsDigit(raw[6])) vk = (uint)(0x60 + (raw[6] - '0'));
            else if (raw.Length is 2 or 3 && (raw[0] is 'F' or 'f') && int.TryParse(raw[1..], out var f) && f is >= 1 and <= 24) vk = (uint)(0x6F + f);
            else if (Enum.TryParse<Key>(raw, true, out var key)) vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            else return null;
        }
        return vk == 0 || mods == 0 && !(vk is >= 0x70 and <= 0x87) ? null : new HotkeySpec(mods, vk);
    }

    public static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        parts.Add(vk switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => "NumPad" + (vk - 0x60),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            _ => key.ToString()
        });
        return string.Join('+', parts);
    }
}

/// <summary>Copies the current selection and pastes a replacement, in whatever app is in front. Text clipboard is restored afterwards.</summary>
public static class SelectionSwap
{
    public static nint Foreground => GetForegroundWindow();
    public static bool IsOwnWindow(nint hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return pid == (uint)Environment.ProcessId; }

    /// <summary>Hotkeys fire while their modifiers are still held; wait for release so Ctrl+C does not become Ctrl+Alt+C.</summary>
    public static async Task WaitForModifierRelease(int timeoutMs = 1500)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until && (Down(0x11) || Down(0x12) || Down(0x10) || Down(0x5B) || Down(0x5C))) await Task.Delay(15);
    }
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static string? TryGetText()
    {
        for (int i = 0; i < 6; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        return null;
    }

    public static bool TrySetText(string text)
    {
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        return false;
    }

    /// <summary>Sends Ctrl+C and returns the selected text, or null if nothing was copied.</summary>
    public static async Task<string?> CopySelectionAsync()
    {
        uint before = GetClipboardSequenceNumber();
        SendCombo(0x43);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(20);
            if (GetClipboardSequenceNumber() != before) return TryGetText();
        }
        return null;
    }

    public static void Paste() => SendCombo(0x56);

    private static void SendCombo(ushort letter)
    {
        var inputs = new[]
        {
            Key(0x11, false), Key(letter, false), Key(letter, true), Key(0x11, true)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } } };

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
