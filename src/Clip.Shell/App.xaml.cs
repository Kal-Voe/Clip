using System.IO;
using System.Windows;
using Clip.Core;

namespace Clip.Shell;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _updateMenuItem;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showPaletteSignal;
    private CancellationTokenSource? _showPaletteSignalCts;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShellLog.Configure(e.Args);
        StartupRegistration.InfoLog = ShellLog.Info;
        StartupRegistration.ErrorLog = ShellLog.Error;
        ShellLog.Info("app startup");
        var trayAction = DebugArgValue(e.Args, "--tray-action");

        // The jank harness drives a player window of its own and reports a number. It deliberately
        // runs beside whatever Clip is already open, so it goes before the single-instance check.
        if (JankHarness.Requested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _ = JankHarness.RunAsync(e.Args).ContinueWith(run =>
                Dispatcher.Invoke(() => Shutdown(run.Status == TaskStatus.RanToCompletion ? run.Result : 4)));

            return;
        }

        // The open-test harness boots the real shell off screen and reports how long the palette
        // takes to open. Like the jank harness it runs beside whatever Clip is already open, so it
        // also goes before the single-instance check.
        if (OpenTestHarness.Requested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ShellLog.Mirror = line => Console.Error.WriteLine(line);
            _window = new MainWindow
            {
                PaletteSessionMode = true,
                PaletteSessionStartHidden = true,
                OpenTestOffscreen = true,
                // So --debug-search can be used to check what the palette does with a query typed
                // into one open and still there (or not) on the next.
                DebugInitialSearch = DebugSearchText(e.Args),
            };
            _window.InitializeShell();
            // Like the jank harness: a throw inside the run must exit nonzero, not hang off screen.
            _ = OpenTestHarness.RunAsync(_window).ContinueWith(run =>
                Dispatcher.Invoke(() => Shutdown(run.Status == TaskStatus.RanToCompletion ? 0 : 4)));
            return;
        }

        // Same idea as the open-test, but repeated: it opens a named page many times and reports
        // every sample so a median and a p95 can be taken. Also runs beside a live Clip.
        if (OpenBenchHarness.Requested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ShellLog.Mirror = line => Console.Error.WriteLine(line);
            _window = new MainWindow
            {
                PaletteSessionMode = true,
                PaletteSessionStartHidden = true,
                OpenTestOffscreen = true,
            };
            _window.InitializeShell();
            _ = OpenBenchHarness.RunAsync(_window, e.Args).ContinueWith(run =>
                Dispatcher.Invoke(() => Shutdown(run.Status == TaskStatus.RanToCompletion ? 0 : 4)));
            return;
        }

        _singleInstanceMutex = new Mutex(true, Clip.Watcher.Program.RichPaletteSingleInstanceMutexName, out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            // The autostart task refires every 30 minutes as a watchdog (something on this
            // machine hard-kills Clip without a trace). When Clip is already running, that
            // fire must exit without side effects - signaling would pop the palette open on
            // whoever is mid-keystroke every half hour.
            if (e.Args.Contains("--ensure-running", StringComparer.OrdinalIgnoreCase))
            {
                ShellLog.Info("ensure-running: already running; duplicate exiting quietly");
                Shutdown();
                return;
            }

            if (!string.IsNullOrWhiteSpace(trayAction))
            {
                TrayActionRequest.Save(trayAction);
            }

            var signaled = Clip.Watcher.Program.TrySignalRichPalette();
            ShellLog.Info($"another Clip shell instance is already running; exiting duplicate signaledShow={signaled} trayAction={trayAction ?? "none"}");
            Shutdown();
            return;
        }

        StartShowPaletteSignalListener();

        DispatcherUnhandledException += (_, ex) =>
        {
            ShellLog.Error(ex.Exception, "dispatcher unhandled exception");
            ex.Handled = true;
            _window?.WriteDebugSnapshot("dispatcher-exception");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exception)
            {
                ShellLog.Error(exception, "appdomain unhandled exception");
            }
            else
            {
                ShellLog.Info($"appdomain unhandled exception object={ex.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            ShellLog.Error(ex.Exception, "task unobserved exception");
            ex.SetObserved();
        };
        // WinForms (the tray icon's message pump) otherwise shows a blocking "Unhandled exception"
        // dialog for thread exceptions — e.g. transient display/clipboard "device not functioning"
        // glitches that happen in RDP/remote sessions. Log the full stack and keep running instead
        // of popping that dialog at the user.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, ex) =>
        {
            ShellLog.Error(ex.Exception, "winforms thread exception");
        };
        var paletteSession = HasArg(e.Args, "--palette-session");
        var keepWarmSession = HasArg(e.Args, "--keep-warm");
        var prewarmSession = HasArg(e.Args, "--prewarm");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _window = new MainWindow();
        var debugVisible = e.Args.Any(a => string.Equals(a, "--debug-visible", StringComparison.OrdinalIgnoreCase));
        _window.KeepOpenForDebug = debugVisible;
        _window.DebugInitialSearch = DebugSearchText(e.Args);
        _window.DebugAutoConcealMs = DebugAutoConcealMs(e.Args);
        _window.DebugOpenSettings = HasArg(e.Args, "--debug-open-settings");
        _window.DebugOpenSurface = DebugArgValue(e.Args, "--debug-open-surface");
        _window.TrayStartupAction = trayAction;
        _window.PaletteSessionMode = paletteSession;
        _window.KeepWarmSession = keepWarmSession;
        _window.PaletteSessionStartHidden = prewarmSession;
        if (!paletteSession)
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "Clip",
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
            };
            _window.UserNotificationRequested += message => _tray?.ShowBalloonTip(3000, "Clip", message, System.Windows.Forms.ToolTipIcon.Warning);
            _window.UpdateNotification += message => _tray?.ShowBalloonTip(3000, "Clip", message, System.Windows.Forms.ToolTipIcon.Info);
            _tray.DoubleClick += (_, _) => _window.ShowPalette();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open Clip", null, (_, _) => _window.ShowPalette());
            menu.Items.Add("Paste latest item", null, (_, _) => _window.PasteLatestFromTray());
            _updateMenuItem = new System.Windows.Forms.ToolStripMenuItem("Check for updates");
            _updateMenuItem.Click += (_, _) =>
            {
                if (_window.LastUpdateStatus.State == "Update available" && !string.IsNullOrWhiteSpace(_window.LastUpdateStatus.DownloadUrl))
                {
                    _window.InstallKnownUpdateFromTray();
                }
                else
                {
                    _window.CheckForUpdatesFromTray();
                }
            };
            menu.Opening += (_, _) => UpdateTrayUpdateItem();
            menu.Items.Add(_updateMenuItem);
            menu.Items.Add("Save log snapshot", null, (_, _) => _window.WriteDebugSnapshot("tray"));
            menu.Items.Add("Settings", null, (_, _) => _window.OpenSettingsFromTray());
            menu.Items.Add("Exit", null, (_, _) => Shutdown());
            _tray.ContextMenuStrip = menu;
        }

        _window.InitializeShell();
        if (!paletteSession)
        {
            // Turn on Windows' built-in clipboard history (Win+V) if it's off, so Clip can import it
            // and Windows keeps capturing even when Clip isn't running. Best-effort, off the UI thread.
            _ = Task.Run(WindowsClipboardHistory.EnsureEnabled);

            // The standalone shell IS the app now: it owns Alt+V, the tray, clipboard capture, and
            // the UI in one process. Do NOT migrate startup to the separate Clip.Watcher host — that
            // watcher+prewarm+signal split was unreliable (the hidden window got stuck and never showed).
            Dispatcher.BeginInvoke(() =>
            {
                if (_tray is not null && _window is not null)
                {
                    _tray.Icon = LoadTrayIcon();
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        ShellLog.Info("app startup complete");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShellLog.Info("app exit");
        _showPaletteSignalCts?.Cancel();
        _tray?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _showPaletteSignal?.Dispose();
        _showPaletteSignalCts?.Dispose();
        ShellLog.Shutdown();
        base.OnExit(e);
    }

    private void StartShowPaletteSignalListener()
    {
        _showPaletteSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Clip.Watcher.Program.RichPaletteShowEventName);
        _showPaletteSignalCts = new CancellationTokenSource();
        var token = _showPaletteSignalCts.Token;
        var signal = _showPaletteSignal;

        _ = Task.Run(() =>
        {
            try
            {
                var handles = new WaitHandle[] { signal, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    var index = WaitHandle.WaitAny(handles);
                    if (index != 0 || token.IsCancellationRequested)
                    {
                        break;
                    }

                    Dispatcher.BeginInvoke(new Action(() => _window?.HandleExternalShowPaletteSignal()), System.Windows.Threading.DispatcherPriority.Input);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }, token);
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = global::Clip.Shell.MainWindow.AppIconPath();
            if (File.Exists(path))
            {
                return new System.Drawing.Icon(path);
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "tray icon load failed");
        }

        return System.Drawing.SystemIcons.Application;
    }

    private static string? DebugSearchText(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--debug-search=", StringComparison.OrdinalIgnoreCase))
            {
                return arg["--debug-search=".Length..];
            }

            if (string.Equals(arg, "--debug-search", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int? DebugAutoConcealMs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--debug-auto-conceal-ms=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg["--debug-auto-conceal-ms=".Length..], out var inlineValue))
            {
                return inlineValue;
            }

            if (string.Equals(arg, "--debug-auto-conceal-ms", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out var nextValue))
            {
                return nextValue;
            }
        }

        return null;
    }

    private static string? DebugArgValue(string[] args, string name)
    {
        var inlinePrefix = name + "=";
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(inlinePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[inlinePrefix.Length..];
            }

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasArg(string[] args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateTrayUpdateItem()
    {
        if (_window is null || _updateMenuItem is null)
        {
            return;
        }

        var status = _window.LastUpdateStatus;
        if (status.State == "Update available" && !string.IsNullOrWhiteSpace(status.DownloadUrl))
        {
            _updateMenuItem.Text = $"Install latest update ({status.LatestVersion ?? "new version"} available)";
        }
        else
        {
            _updateMenuItem.Text = "Check for updates";
        }
    }
}
