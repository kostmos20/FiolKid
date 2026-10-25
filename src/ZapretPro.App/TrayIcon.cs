using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace ZapretPro.App;

/// <summary>
/// Значок в трее: цветной кружок (серый — выключено, жёлтый — подбираю, зелёный — всё работает,
/// красный — не всё). Уведомляет, только если что-то сломалось и само не починилось за 5 минут.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly TimeSpan ProblemPatience = TimeSpan.FromMinutes(5);

    private readonly AppEngine _engine;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Dictionary<AppEngine.Overall, Icon> _icons = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private bool _hintShown;
    private bool _rebootNotified;
    private DateTime? _problemSince;
    private bool _problemNotified;

    public TrayIcon(AppEngine engine, Action open, Func<Task> exit)
    {
        _engine = engine;
        foreach (var state in Enum.GetValues<AppEngine.Overall>())
            _icons[state] = MakeIcon(state);

        _toggle = new Forms.ToolStripMenuItem("Включить", null, async (_, _) =>
        {
            if (_engine.IsOn)
                await _engine.TurnOffAsync();
            else
                _engine.TurnOn();
        });
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Открыть", null, (_, _) => open()) { Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold) });
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Выйти совсем", null, async (_, _) => await exit()));

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
        };
        _icon.BalloonTipClicked += (_, _) => open();

        _engine.StatusChanged += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(Update);
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    /// <summary>Окно закрыли крестиком — один раз объясняем, что программа не выключилась.</summary>
    public void ShowHiddenHint()
    {
        if (_hintShown)
            return;
        _hintShown = true;
        _icon.ShowBalloonTip(5000, "FiolKid работает дальше",
            "Окно спрятано сюда, к часам. Чтобы открыть — щёлкни по кружочку.", Forms.ToolTipIcon.Info);
    }

    private void Update()
    {
        var state = _engine.OverallState();
        _icon.Icon = _icons[state];
        _icon.Text = state switch
        {
            AppEngine.Overall.Off => "FiolKid — выключено",
            AppEngine.Overall.Busy => "FiolKid — подбираю способ…",
            AppEngine.Overall.Ok => "FiolKid — всё работает",
            _ => "FiolKid — не всё работает",
        };
        _toggle.Text = _engine.IsOn ? "Выключить" : "Включить";

        // Драйвер zapret завис — ждать пять минут бессмысленно, само не пройдёт: говорим сразу
        var needsReboot = _engine.IsOn && _engine.Status.NeedsReboot;
        if (needsReboot && !_rebootNotified)
        {
            _rebootNotified = true;
            _icon.ShowBalloonTip(15000, "Перезагрузи компьютер",
                "Драйвер обхода блокировок завис. После перезагрузки YouTube и Discord заработают.", Forms.ToolTipIcon.Warning);
        }
        else if (!needsReboot)
            _rebootNotified = false;

        if (state != AppEngine.Overall.Problem)
        {
            _problemSince = null;
            _problemNotified = false;
            return;
        }

        _problemSince ??= DateTime.Now;
        if (!_problemNotified && DateTime.Now - _problemSince >= ProblemPatience)
        {
            _problemNotified = true;
            _icon.ShowBalloonTip(10000, "Не всё работает",
                "Само не наладилось. Открой программу, вкладку «Журнал», и пришли отчёт.", Forms.ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// Значок как у программы — фиолетовый кружок с кнопкой питания (серый, если выключено) — и точка
    /// состояния в углу. Рисуем прямо в программе: так он одинаково чёткий на любом масштабе экрана.
    /// </summary>
    private static Icon MakeIcon(AppEngine.Overall state)
    {
        Color? dotColor = state switch
        {
            AppEngine.Overall.Ok => Color.FromArgb(0x22, 0xC5, 0x5E),
            AppEngine.Overall.Busy => Color.FromArgb(0xF5, 0x9E, 0x0B),
            AppEngine.Overall.Problem => Color.FromArgb(0xEF, 0x44, 0x44),
            _ => null,
        };
        var on = state != AppEngine.Overall.Off;
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var circle = new RectangleF(1, 1, 30, 30);
            using var face = new LinearGradientBrush(circle,
                on ? Color.FromArgb(0xB3, 0x8C, 0xFF) : Color.FromArgb(0x94, 0xA3, 0xB8),
                on ? Color.FromArgb(0x4C, 0x1D, 0x95) : Color.FromArgb(0x33, 0x41, 0x55), 45f);
            g.FillEllipse(face, circle);

            using var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 8.5f, 9.5f, 15f, 15f, -50, 280);
            g.DrawLine(pen, 16f, 6.5f, 16f, 15f);

            if (dotColor is { } color)
            {
                using var rim = new SolidBrush(Color.FromArgb(0x0B, 0x11, 0x20));
                g.FillEllipse(rim, 19, 19, 13, 13);
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, 21, 21, 9, 9);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
