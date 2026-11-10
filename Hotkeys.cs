using System;
using System.Windows.Interop;

namespace CheatingTool;

internal sealed class Hotkeys : IDisposable
{
    private const int VkF8 = 0x77;
    private const int VkH = 0x48;
    private const int VkC = 0x43;
    private const int VkQ = 0x51;

    private readonly HwndSource _source;

    public event Action? Analyze;
    public event Action? ToggleOverlay;
    public event Action? CopyAnswer;
    public event Action? Exit;

    public bool AnalyzeOk { get; }

    public Hotkeys()
    {
        var parameters = new HwndSourceParameters("cheatingtool-hotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(Hook);

        AnalyzeOk = Native.RegisterHotKey(_source.Handle, 1, 0, VkF8);
        Native.RegisterHotKey(_source.Handle, 2, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VkH);
        Native.RegisterHotKey(_source.Handle, 3, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VkC);
        Native.RegisterHotKey(_source.Handle, 4, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VkQ);

        if (!AnalyzeOk)
            Log.Write("hotkey: F8 не зарегистрирована (занята другой программой)");
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case 1: Analyze?.Invoke(); handled = true; break;
            case 2: ToggleOverlay?.Invoke(); handled = true; break;
            case 3: CopyAnswer?.Invoke(); handled = true; break;
            case 4: Exit?.Invoke(); handled = true; break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try
        {
            for (int id = 1; id <= 4; id++) Native.UnregisterHotKey(_source.Handle, id);
            _source.Dispose();
        }
        catch { }
    }
}
