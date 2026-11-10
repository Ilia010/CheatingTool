using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CheatingTool;

internal sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly IntPtr _iconHandle;

    public TrayIcon(Action analyze, Action toggle, Action login, Action importChrome, Action lastScreen, Action settings, Action restart, Action exit)
    {
        _icon = CreateIcon(out _iconHandle);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Анализ  (F8)", null, (_, _) => analyze());
        menu.Items.Add("Скрыть / показать  (Ctrl+Alt+H)", null, (_, _) => toggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Перенести вход из Chrome…", null, (_, _) => importChrome());
        menu.Items.Add("Войти в Gemini…", null, (_, _) => login());
        menu.Items.Add("Показать последний скриншот", null, (_, _) => lastScreen());
        menu.Items.Add("Открыть настройки…", null, (_, _) => settings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Перезапустить", null, (_, _) => restart());
        menu.Items.Add("Выход  (Ctrl+Alt+Q)", null, (_, _) => exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "CheatingTool — F8: анализ",
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => analyze();
    }

    private static Icon CreateIcon(out IntPtr handle)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var outer = new SolidBrush(Color.FromArgb(255, 225, 229, 235));
            g.FillEllipse(outer, 5, 5, 22, 22);
            using var inner = new SolidBrush(Color.FromArgb(255, 52, 58, 68));
            g.FillEllipse(inner, 12, 12, 8, 8);
        }

        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch { }

        try
        {
            if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        }
        catch { }
    }
}
