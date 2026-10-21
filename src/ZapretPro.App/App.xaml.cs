using System.Windows;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private MainWindow? _main;

    public AppEngine Engine { get; private set; } = null!;
    public TrayIcon Tray { get; private set; } = null!;

    /// <summary>Выход по-настоящему (из меню трея), а не сворачивание окна.</summary>
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Второй экземпляр запустил бы второй winws и второй VPN поверх первых
        _singleInstance = new Mutex(true, @"Local\FiolKid", out var first);
        if (!first)
        {
            MessageBox.Show("FiolKid уже запущен — его значок (кружочек) возле часов.", "FiolKid");
            Shutdown();
            return;
        }

        // До запуска winws и прокси: тогда они завершатся вместе с программой, даже если она упадёт
        ChildProcesses.BindToCurrentProcess();

        Engine = new AppEngine();
        _main = new MainWindow(Engine);
        Tray = new TrayIcon(Engine, ShowMain, ExitAsync);

        // --tray: запуск при входе в Windows (этап 6) — сразу в трей, без окна
        if (!e.Args.Contains("--tray"))
            _main.Show();

        if (Engine.WasEnabled)
            Engine.TurnOn();
    }

    public void ShowMain()
    {
        if (_main is null)
            return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public async Task ExitAsync()
    {
        if (IsExiting)
            return;
        IsExiting = true;
        _main?.Hide();
        // remember: false — при следующем запуске программа включится, если была включена
        await Engine.TurnOffAsync(remember: false);
        Tray.Dispose();
        Shutdown();
    }

    /// <summary>Выключение или перезагрузка Windows: успеть снять VPN и драйвер.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        IsExiting = true;
        Task.Run(() => Engine.TurnOffAsync(remember: false)).Wait(TimeSpan.FromSeconds(15));
    }
}
