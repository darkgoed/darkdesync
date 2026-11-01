using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Services;

/// <summary>
/// Global keyboard hook service for hotkey detection.
/// One configured pass-through hotkey, physical key edge detection and optional focus checking.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService> _logger;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc; // prevent GC
    private int _dropHotkeyVirtualKey = 0x20;     // VK_SPACE (Drop default)


    private IntPtr _targetWindowHandle = IntPtr.Zero;
    private volatile bool _enabled;
    private readonly HashSet<int> _heldKeys = new();

    public event Action? HotkeyPressed;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
    }

    public void SetHotkey(int virtualKeyCode)
    {
        _dropHotkeyVirtualKey = virtualKeyCode;
        _logger.LogInformation("Global hotkey set to VK: 0x{Key:X2}", virtualKeyCode);
    }


    public void SetTargetWindow(IntPtr hwnd)
    {
        _targetWindowHandle = hwnd;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero)
            return;

        _hookProc = HookCallback;
        _hookId = SetHook(_hookProc);
        _enabled = true;

        _logger.LogInformation("Keyboard hook installed. Global key: 0x{Key:X2}", _dropHotkeyVirtualKey);
    }

    public void Uninstall()
    {
        _enabled = false;
        _heldKeys.Clear();

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _logger.LogInformation("Keyboard hook uninstalled");
        }
    }

    public static string GetKeyName(int virtualKeyCode)
    {
        return virtualKeyCode switch
        {
            0x20 => "Espaço",
            0x54 => "T", 0x47 => "G", 0x46 => "F", 0x58 => "X",
            0x43 => "C", 0x56 => "V", 0x51 => "Q", 0x45 => "E",
            0x52 => "R", 0x5A => "Z", 0x42 => "B",
            0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
            0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
            0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
            0x14 => "Caps Lock",
            0x09 => "Tab",
            _ when virtualKeyCode >= 0x41 && virtualKeyCode <= 0x5A =>
                ((char)virtualKeyCode).ToString(),
            _ => $"VK 0x{virtualKeyCode:X2}"
        };
    }

    public static Dictionary<string, int> GetAvailableKeys()
    {
        return new Dictionary<string, int>
        {
            ["Espaço"] = 0x20,
            ["T"] = 0x54,
            ["G"] = 0x47,
            ["F"] = 0x46,
            ["X"] = 0x58,
            ["C"] = 0x43,
            ["V"] = 0x56,
            ["B"] = 0x42,
            ["Q"] = 0x51,
            ["E"] = 0x45,
            ["R"] = 0x52,
            ["Z"] = 0x5A,
            ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
            ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
            ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
            ["Caps Lock"] = 0x14,
            ["Tab"] = 0x09,
        };
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            int flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & 0x10) == 0)
            {
                bool down = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)0x0104;
                bool up = wParam == (IntPtr)0x0101 || wParam == (IntPtr)0x0105;
                if ((down || up) && AcceptKeyTransition(vkCode, down) &&
                    (_targetWindowHandle == IntPtr.Zero || IsTargetForeground()))
                {
                    try { HotkeyPressed?.Invoke(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Hotkey subscriber failed; key remains pass-through"); }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // One physical press per pulse, even if held longer than a complete cycle.
    internal bool AcceptKeyTransition(int key, bool down)
    {
        if (!down) { _heldKeys.Remove(key); return false; }
        return _heldKeys.Add(key) && key == _dropHotkeyVirtualKey;
    }

    private bool IsTargetForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == _targetWindowHandle)
            return true;

        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        GetWindowThreadProcessId(_targetWindowHandle, out uint targetPid);
        return foregroundPid == targetPid && targetPid != 0;
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
            GetModuleHandle(curModule.ModuleName), 0);
    }

    public void Dispose()
    {
        Uninstall();
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
