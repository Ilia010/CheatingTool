using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CheatingTool;

public partial class OverlayWindow : Window
{
    private static readonly Brush NormalBrush = Frozen(Color.FromRgb(0xE8, 0xEB, 0xEF));
    private static readonly Brush StatusBrush = Frozen(Color.FromRgb(0xBF, 0xC6, 0xCF));
    private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xD9, 0xA9, 0xA9));

    private static readonly string[] SpinnerFrames = { "|", "/", "-", "\\" };
    private static readonly FontFamily AnswerFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily BusyFont = new("Consolas");

    private readonly AppConfig _cfg;
    private readonly DispatcherTimer _spinner;
    private string _lastText = "";
    private string _busyText = "";
    private int _spinnerFrame;
    private bool _positioning;

    internal OverlayWindow(AppConfig cfg)
    {
        _cfg = cfg;
        InitializeComponent();

        MaxWidth = cfg.Overlay.Width;
        Opacity = cfg.Overlay.Opacity;
        Body.FontSize = cfg.Overlay.FontSize;
        Body.LineHeight = Math.Round(cfg.Overlay.FontSize * 1.4);
        Body.FontFamily = AnswerFont;

        _spinner = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        _spinner.Tick += (_, _) =>
        {
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
            Body.Text = SpinnerText();
        };

        SizeChanged += (_, _) => Reposition();
        Loaded += (_, _) => Reposition();
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>Удалось ли исключить окно из скриншотов (WDA_EXCLUDEFROMCAPTURE).</summary>
    public bool CaptureExclusionWorks { get; private set; }

    public bool IsHiddenByUser { get; private set; }

    public string CurrentText { get; private set; } = "";

    /// <summary>Обычный статус без анимации (подсказки, сообщения).</summary>
    public void ShowStatus(string text)
    {
        IsHiddenByUser = false;
        CurrentText = text ?? "";
        StopSpinner();
        Body.FontFamily = AnswerFont;
        Body.FontStyle = FontStyles.Italic;
        Body.Foreground = StatusBrush;
        Body.Text = text;
        Reveal();
    }

    /// <summary>Индикатор работы: крутящийся слэш + текст.</summary>
    public void ShowBusy(string text)
    {
        IsHiddenByUser = false;
        CurrentText = text ?? "";
        _busyText = CurrentText;
        _spinnerFrame = 0;

        Body.FontFamily = BusyFont;
        Body.FontStyle = FontStyles.Normal;
        Body.Foreground = StatusBrush;
        Body.Text = SpinnerText();

        _spinner.Start();
        Reveal();
    }

    public void ShowText(string text)
    {
        IsHiddenByUser = false;
        _lastText = text ?? "";
        CurrentText = _lastText;
        StopSpinner();
        Body.FontFamily = AnswerFont;
        Body.FontStyle = FontStyles.Normal;
        Body.Foreground = NormalBrush;
        Body.Text = Trim(text);
        Reveal();
    }

    public void ShowError(string message)
    {
        IsHiddenByUser = false;
        CurrentText = "⚠ " + Short(message);
        StopSpinner();
        Body.FontFamily = AnswerFont;
        Body.FontStyle = FontStyles.Normal;
        Body.Foreground = ErrorBrush;
        Body.Text = CurrentText;
        Reveal();
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            IsHiddenByUser = true;
            StopSpinner();
            Hide();
            return;
        }

        IsHiddenByUser = false;
        if (!string.IsNullOrWhiteSpace(_lastText)) ShowText(_lastText);
        else ShowStatus("F8 — анализ · Ctrl+Alt+H — скрыть");
    }

    public void CopyLastToClipboard()
    {
        if (string.IsNullOrWhiteSpace(_lastText)) return;
        try
        {
            System.Windows.Clipboard.SetText(_lastText);
        }
        catch (Exception ex)
        {
            Log.Write("clipboard: " + ex.Message);
        }
    }

    private string SpinnerText() =>
        _busyText.Length == 0 ? SpinnerFrames[_spinnerFrame] : SpinnerFrames[_spinnerFrame] + "  " + _busyText;

    private void StopSpinner()
    {
        _spinner.Stop();
        _spinnerFrame = 0;
    }

    private void Reveal()
    {
        if (!IsVisible) Show();
        UpdateLayout();
        Reposition();
        Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Loaded);
    }

    private string Trim(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= _cfg.Overlay.MaxLines) return text ?? "";

        var cut = new string[_cfg.Overlay.MaxLines];
        Array.Copy(lines, cut, cut.Length);
        return string.Join("\n", cut) + "\n…";
    }

    private static string Short(string message)
    {
        message = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (message.Length == 0) message = "ошибка";
        return message.Length > 220 ? message[..220] + "…" : message;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        int style = Native.GetWindowLong(handle, Native.GWL_EXSTYLE);
        style |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(handle, Native.GWL_EXSTYLE, style);

        if (Env.Flag("NO_CAPTURE_EXCLUSION"))
        {
            CaptureExclusionWorks = false;
            return;
        }

        CaptureExclusionWorks = Native.SetWindowDisplayAffinity(handle, Native.WDA_EXCLUDEFROMCAPTURE);
        if (!CaptureExclusionWorks) Log.Write("не удалось исключить оверлей из скриншотов");
    }

    private void Reposition()
    {
        if (_positioning) return;
        _positioning = true;

        try
        {
            var monitor = Displays.UnderCursor();
            double scale = 96.0 / monitor.Dpi;

            double maxWidth = monitor.WorkWidth * scale - 2 * _cfg.Overlay.Margin;
            double width = Math.Max(200, Math.Min(_cfg.Overlay.Width, maxWidth));
            MaxWidth = width;
            Body.MaxWidth = Math.Max(140, width - 24);

            UpdateLayout();

            double left = monitor.WorkLeft * scale + _cfg.Overlay.Margin;
            double top = (monitor.WorkTop + monitor.WorkHeight) * scale - ActualHeight - _cfg.Overlay.Margin;
            double minTop = monitor.WorkTop * scale + _cfg.Overlay.Margin;

            Left = left;
            Top = Math.Max(minTop, top);

            if (Env.Flag("DEBUG"))
            {
                Log.Write($"overlay: work={monitor.WorkLeft},{monitor.WorkTop} {monitor.WorkWidth}x{monitor.WorkHeight} dpi={monitor.Dpi} " +
                          $"actual={ActualWidth:F0}x{ActualHeight:F0} -> {Left:F0},{Top:F0}");
            }
        }
        catch (Exception ex)
        {
            Log.Write("overlay: " + ex.Message);
        }
        finally
        {
            _positioning = false;
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
