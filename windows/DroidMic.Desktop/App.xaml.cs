using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DroidMic.Desktop.Services;
using DroidMic.Desktop.ViewModels;
using DroidMic.Desktop.Views;
using DroidMic.Networking;
using Forms = System.Windows.Forms;

namespace DroidMic.Desktop;

public partial class App : Application
{
    private const string SingleInstanceName = "Local\\DroidMic.Desktop.SingleInstance";
    private const string ActivateEventName = "Local\\DroidMic.Desktop.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private Forms.NotifyIcon? _tray;
    private AudioHost? _host;
    private MainWindow? _window;
    private bool _trayHintShown;

    public AppSettings Settings { get; private set; } = new();
    public UiLogger Log { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, SingleInstanceName, out bool first);
        if (!first)
        {
            // Bring the running instance to the front instead of starting a second receiver.
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unexpected error: " + args.Exception.Message);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Background error: " + args.Exception.InnerException?.Message);
            args.SetObserved();
        };

        StartUiWatchdog();
        Settings = AppSettings.Load();
        Log.Info($"DroidMic {typeof(App).Assembly.GetName().Version} starting");
        _host = new AudioHost(Settings, Log);

        var vm = new MainViewModel(_host, Settings, Log);
        _window = new MainWindow(vm);
        _window.HiddenToTray += () =>
        {
            if (_trayHintShown) return;
            _trayHintShown = true;
            _tray?.ShowBalloonTip(3000, "DroidMic is still running", "DroidMic keeps receiving in the tray. Right-click the icon to exit.", Forms.ToolTipIcon.Info);
        };
        CreateTray();

        if (!Settings.StartMinimized || !Settings.MinimizeToTray) _window.Show();
        if (Settings.StartListeningOnLaunch) await vm.StartListening();
    }

    /// <summary>Logs when the UI thread stops responding, so "Not responding" reports come with evidence.</summary>
    private void StartUiWatchdog()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(1000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var op = Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
                if (op.Wait(TimeSpan.FromSeconds(2)) == System.Windows.Threading.DispatcherOperationStatus.Completed) continue;
                op.Wait();
                Log.Warn($"UI thread was blocked for {sw.Elapsed.TotalSeconds:0.0} s");
            }
        }) { IsBackground = true, Name = "UI watchdog" };
        t.Start();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open DroidMic", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Disconnect phone", null, (_, _) => _host?.Server?.DisconnectActive());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/droidmic.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "DroidMic",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        if (_host is not null)
        {
            _host.StateChanged += UpdateTrayText;
        }
    }

    private void UpdateTrayText()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_tray is null || _host is null) return;
            var st = _host.Server?.Status;
            string text = st?.Phase switch
            {
                ConnectionPhase.Streaming => $"DroidMic: receiving from {st.DeviceName}",
                ConnectionPhase.Connected => $"DroidMic: {st.DeviceName} connected",
                null => "DroidMic: not listening",
                _ => "DroidMic: waiting for phone",
            };
            _tray.Text = text.Length > 63 ? text[..63] : text;
        });
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async Task ExitAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _activateEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
