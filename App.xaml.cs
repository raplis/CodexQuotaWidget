using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace CodexQuotaWidget;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private const int ShowWidgetMessage = 0x8001;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Local\\CodexQuotaWidget.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            var handle = FindWindow(null, "Codex 额度");
            if (handle != IntPtr.Zero) { ShowWindow(handle, 9); SetForegroundWindow(handle); SendMessage(handle, ShowWidgetMessage, IntPtr.Zero, IntPtr.Zero); }
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex(); _mutex?.Dispose(); base.OnExit(e);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}
