using System.Windows;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // До запуска winws и прокси: тогда они завершатся вместе с программой, даже если она упадёт
        ChildProcesses.BindToCurrentProcess();
        base.OnStartup(e);
    }
}
