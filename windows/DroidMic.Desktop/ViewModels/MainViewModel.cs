using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DroidMic.AudioEngine;
using DroidMic.Desktop.Services;
using DroidMic.Networking;
using DroidMic.Platform;
using DroidMic.Protocol;
using QRCoder;

namespace DroidMic.Desktop.ViewModels;

public sealed record PairedDeviceItem(string DeviceId, string Name, string Details);

public sealed partial class MainViewModel : ObservableObject
{
    private const string Green = "#3DD68C";
    private const string Amber = "#F5B841";
    private const string Red = "#FF6B6B";
    private const string Grey = "#8A94A6";

    private readonly AudioHost _host;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _fastTimer;
    private readonly DispatcherTimer _slowTimer;
    private int _slowTicks;
    private long _lastRx, _lastLost;
    private DateTime _lastRateSample = DateTime.UtcNow;

    public MainViewModel(AudioHost host, AppSettings settings, UiLogger log)
    {
        _host = host;
        _settings = settings;
        Log = log;
        Logs = log.Entries;

        _host.StateChanged += () => Application.Current?.Dispatcher.BeginInvoke(RefreshListening);
        _host.Pairings.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RefreshDevices);

        _fastTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _fastTimer.Tick += (_, _) => UpdateMeters();
        _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _slowTimer.Tick += (_, _) => SlowTick();

        LatencyPreset = settings.LatencyPreset;
        MonitorOnSpeakers = settings.MonitorOnSpeakers;
        MinimizeToTray = settings.MinimizeToTray;
        StartListeningOnLaunch = settings.StartListeningOnLaunch;
        EnableDiscovery = settings.EnableDiscovery;
        ControlPort = settings.ControlPort;
        AudioPort = settings.AudioPort;
        Fingerprint = host.Identity.Fingerprint;

        RefreshDevices();
        RefreshEndpoint();
        RefreshListening();
        _fastTimer.Start();
        _slowTimer.Start();
    }

    public UiLogger Log { get; }
    public ObservableCollection<LogEntry> Logs { get; }
    public ObservableCollection<PairedDeviceItem> PairedDevices { get; } = new();
    public ObservableCollection<string> LocalAddresses { get; } = new();
    public Array LatencyPresets { get; } = Enum.GetValues(typeof(LatencyPreset));

    [ObservableProperty] private int _selectedSection;

    // ---- Status ------------------------------------------------------------------
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private string _listeningText = "Not listening";
    [ObservableProperty] private string _connectionTitle = "Waiting for phone";
    [ObservableProperty] private string _connectionDetail = "";
    [ObservableProperty] private string _connectionBrush = Grey;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _deviceName = "—";
    [ObservableProperty] private string _deviceAddress = "—";
    [ObservableProperty] private string _streamFormat = "—";
    [ObservableProperty] private string _startError = "";
    [ObservableProperty] private bool _hasStartError;
    [ObservableProperty] private bool _hasPairedDevices;

    // ---- Meter & stats -------------------------------------------------------------
    [ObservableProperty] private double _meterPeak;
    [ObservableProperty] private double _meterRms;
    [ObservableProperty] private string _levelText = "-∞ dBFS";
    [ObservableProperty] private bool _isClipping;
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private string _bufferText = "—";
    [ObservableProperty] private string _jitterText = "—";
    [ObservableProperty] private string _lossText = "—";
    [ObservableProperty] private string _underrunText = "0";
    [ObservableProperty] private string _rttText = "—";
    [ObservableProperty] private string _qualityText = "—";
    [ObservableProperty] private string _qualityBrush = Grey;
    [ObservableProperty] private string _throughputText = "—";
    [ObservableProperty] private string _driftText = "—";
    [ObservableProperty] private string _receiverCounters = "";
    [ObservableProperty] private string _driverCounters = "";

    // ---- Microphone endpoint ---------------------------------------------------------
    [ObservableProperty] private string _micTitle = "Checking…";
    [ObservableProperty] private string _micDetail = "";
    [ObservableProperty] private string _micBrush = Grey;
    [ObservableProperty] private bool _micReady;
    [ObservableProperty] private bool _driverMissing;
    [ObservableProperty] private string _endpointName = "—";
    [ObservableProperty] private string _defaultText = "";
    [ObservableProperty] private string _selfTestResult = "";
    [ObservableProperty] private bool _selfTestRunning;
    [ObservableProperty] private string _appsUsingMic = "";

    // ---- Pairing -------------------------------------------------------------------
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private string _pin = "";
    [ObservableProperty] private string _pairingExpires = "";
    [ObservableProperty] private BitmapImage? _qrImage;
    [ObservableProperty] private string _fingerprint = "";
    [ObservableProperty] private string _primaryAddress = "";

    // ---- Settings --------------------------------------------------------------------
    [ObservableProperty] private LatencyPreset _latencyPreset;
    [ObservableProperty] private bool _monitorOnSpeakers;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startListeningOnLaunch;
    [ObservableProperty] private bool _enableDiscovery;
    [ObservableProperty] private int _controlPort;
    [ObservableProperty] private int _audioPort;
    [ObservableProperty] private string _firewallText = "";

    partial void OnLatencyPresetChanged(LatencyPreset value)
    {
        _host.Pipeline.SetPreset(value);
        _settings.LatencyPreset = value;
        _settings.Save();
    }

    partial void OnMonitorOnSpeakersChanged(bool value)
    {
        _host.SetMonitor(value);
        _settings.MonitorOnSpeakers = value;
        _settings.Save();
    }

    partial void OnMinimizeToTrayChanged(bool value) { _settings.MinimizeToTray = value; _settings.Save(); }
    partial void OnStartListeningOnLaunchChanged(bool value) { _settings.StartListeningOnLaunch = value; _settings.Save(); }
    partial void OnEnableDiscoveryChanged(bool value) { _settings.EnableDiscovery = value; _settings.Save(); }

    [RelayCommand]
    private void SavePorts()
    {
        if (ControlPort is < 1024 or > 65535 || AudioPort is < 1024 or > 65535 || ControlPort == AudioPort)
        {
            MessageBox.Show("Ports must be different numbers between 1024 and 65535.", "DroidMic");
            return;
        }
        _settings.ControlPort = ControlPort;
        _settings.AudioPort = AudioPort;
        _settings.Save();
        Log.Info("Ports changed; restart listening to apply.");
    }

    // ---- Listening -------------------------------------------------------------------

    [RelayCommand]
    public async Task StartListening()
    {
        if (await _host.StartListeningAsync())
        {
            _host.Server!.StatusChanged += _ => Application.Current?.Dispatcher.BeginInvoke(RefreshListening);
            _host.Server.Pairing.Changed += _ => Application.Current?.Dispatcher.BeginInvoke(RefreshPairing);
            _host.Server.DevicePaired += (_, name) => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Log.Info($"Paired with '{name}'");
                SelectedSection = 0;
            });
        }
        RefreshListening();
    }

    [RelayCommand]
    private async Task StopListening()
    {
        await _host.StopListeningAsync();
        RefreshListening();
        RefreshPairing();
    }

    [RelayCommand]
    private void Disconnect() => _host.Server?.DisconnectActive();

    private void RefreshListening()
    {
        IsListening = _host.IsListening;
        StartError = _host.LastStartError ?? "";
        HasStartError = StartError.Length > 0;
        ListeningText = IsListening ? $"Listening on port {_host.Server!.ControlPort}" : "Not listening";
        var st = _host.Server?.Status;
        IsConnected = st is { Phase: ConnectionPhase.Connected or ConnectionPhase.Streaming };
        IsStreaming = st?.Phase == ConnectionPhase.Streaming;
        DeviceName = st?.DeviceName ?? "—";
        DeviceAddress = st?.DeviceAddress?.ToString() ?? "—";
        StreamFormat = st?.StreamFormat ?? "—";
        RttText = st is { RoundTripMs: > 0 } ? $"{st.RoundTripMs:0} ms" : "—";

        (ConnectionTitle, ConnectionDetail, ConnectionBrush) = (IsListening, st?.Phase) switch
        {
            (false, _) => ("Not listening", "Click Start listening so your phone can connect.", Grey),
            (true, ConnectionPhase.Streaming) => ($"Receiving from {DeviceName}", "Audio from the phone is live.", Green),
            (true, ConnectionPhase.Connected) => ($"{DeviceName} connected", "Press Start Microphone on the phone to begin.", Amber),
            (true, ConnectionPhase.Authenticating) => ("Phone connecting…", "Verifying the phone's pairing.", Amber),
            _ => ("Waiting for phone", _host.Pairings.Devices.Count == 0
                ? "No phone paired yet: open 'Pair phone' to get a PIN and QR code."
                : "Open DroidMic on your phone and press Start Microphone.", Grey),
        };
        RefreshAddresses();
    }

    // ---- Meters (50 ms) -----------------------------------------------------------------

    private void UpdateMeters()
    {
        var p = _host.Pipeline;
        var stats = p.GetStats();
        bool live = stats.State != PipelineState.Idle;
        double peak = live ? p.InputMeter.PeakDb : -90;
        double rms = live ? p.InputMeter.RmsDb : -90;
        MeterPeak = DbToFraction(peak);
        MeterRms = DbToFraction(rms);
        LevelText = peak <= -89 ? "-∞ dBFS" : $"{peak:0.0} dBFS";
        IsClipping = live && peak > -0.5;
    }

    private static double DbToFraction(double db) => Math.Clamp((db + 60) / 60, 0, 1) * 100;

    // ---- Slow tick (1 s) ------------------------------------------------------------------

    private void SlowTick()
    {
        _host.Tick();
        var s = _host.Pipeline.GetStats();
        var now = DateTime.UtcNow;
        double secs = Math.Max(0.001, (now - _lastRateSample).TotalSeconds);
        long dRx = s.FramesReceived - _lastRx, dLost = s.FramesLost - _lastLost;
        _lastRx = s.FramesReceived; _lastLost = s.FramesLost; _lastRateSample = now;

        if (s.State == PipelineState.Idle)
        {
            BufferText = JitterText = LossText = LatencyText = ThroughputText = DriftText = QualityText = "—";
            QualityBrush = Grey;
        }
        else
        {
            double lossPct = dRx + dLost > 0 ? 100.0 * dLost / (dRx + dLost) : 0;
            double frameMs = s.InputRate > 0 ? 10 : 0;
            double rtt = _host.Server?.Status.RoundTripMs ?? 0;
            // Capture frame + one-way network + jitter buffer + driver queue (~15 ms) + engine period (~10 ms).
            double latency = frameMs + rtt / 2 + s.BufferMs + 15 + 10;
            BufferText = $"{s.BufferMs:0} ms (target {s.TargetMs:0})";
            JitterText = $"{s.JitterMs:0.0} ms";
            LossText = $"{lossPct:0.0} % ({s.FramesLost} total)";
            UnderrunText = s.Underruns.ToString();
            LatencyText = $"≈ {latency:0} ms";
            DriftText = $"{s.DriftPpm:+0;-0;0} ppm";
            long bytes = _host.Server?.Receiver?.Counters.Bytes ?? 0;
            ThroughputText = $"{dRx / secs * (s.InputRate / 100 * 2 + 40) * 8 / 1000:0} kbit/s";
            (QualityText, QualityBrush) = (lossPct, s.JitterMs) switch
            {
                ( < 1, < 10) => ("Excellent", Green),
                ( < 3, < 25) => ("Good", Green),
                ( < 8, _) => ("Fair", Amber),
                _ => ("Poor", Red),
            };
        }

        var c = _host.Server?.Receiver?.Counters;
        ReceiverCounters = c is null ? "" :
            $"accepted {c.Accepted} · malformed {c.Malformed} · wrong session {c.WrongSession} · wrong source {c.WrongSource} · replayed {c.Replayed} · auth failed {c.AuthFailed}";
        var ds = _host.DriverSink?.LastStatus;
        DriverCounters = ds is null ? "driver not attached" :
            $"ring {ds.Value.RingFillSamples / 48.0:0.0} ms · recording apps {ds.Value.ActiveCaptureStreams} · underrun {ds.Value.UnderrunSamples / 48000.0:0.00} s · overflow {ds.Value.OverflowSamples / 48000.0:0.00} s";
        AppsUsingMic = ds is { ActiveCaptureStreams: > 0 } ? "A Windows app is currently recording from DroidMic Microphone." : "No app is recording from DroidMic Microphone right now.";

        if (++_slowTicks % 3 == 0) RefreshEndpoint();
        if (IsPairing) RefreshPairingCountdown();
    }

    // ---- Microphone endpoint ------------------------------------------------------------

    private void RefreshEndpoint()
    {
        var info = EndpointDetector.Detect();
        DriverMissing = info.Availability == EndpointAvailability.NotInstalled;
        MicReady = info.Availability == EndpointAvailability.Active && _host.Mode == SinkMode.VirtualMicrophone;
        EndpointName = info.FriendlyName ?? "—";
        DefaultText = info.IsDefault ? "DroidMic Microphone is the Windows default microphone."
            : info.Availability == EndpointAvailability.Active ? "Not the default microphone: select it in each app, or make it the default in Sound settings." : "";
        (MicTitle, MicDetail, MicBrush) = info.Availability switch
        {
            EndpointAvailability.Active when _host.Mode == SinkMode.VirtualMicrophone =>
                ("DroidMic Microphone ready", "Windows lists the recording device and DroidMic is feeding it.", Green),
            EndpointAvailability.Active =>
                ("Endpoint present, not attached", IsListening ? "DroidMic could not open the driver's control interface. See the log." : "Start listening to feed it.", Amber),
            EndpointAvailability.Disabled =>
                ("DroidMic Microphone is disabled", "Enable it in Sound settings → More sound settings → Recording.", Amber),
            EndpointAvailability.Unavailable =>
                ("DroidMic Microphone unavailable", info.DriverInterfacePresent ? "Windows reports the endpoint as not present." : "The driver is installed but not running. Check Device Manager (code 52 = signature problem).", Red),
            _ => ("Virtual microphone not installed", "Audio can be received, but other apps can't use it until the DroidMic driver is installed.", Red),
        };
    }

    [RelayCommand]
    private async Task RunSelfTest()
    {
        SelfTestRunning = true;
        SelfTestResult = "Recording 2.5 s from DroidMic Microphone…";
        try
        {
            var r = await _host.RunSelfTestAsync();
            SelfTestResult = (r.Passed ? "PASSED: " : "FAILED: ") + r.Message;
            Log.Info("Endpoint self-test " + SelfTestResult);
        }
        catch (Exception ex)
        {
            SelfTestResult = "FAILED: " + ex.Message;
        }
        finally
        {
            SelfTestRunning = false;
            RefreshEndpoint();
        }
    }

    [RelayCommand]
    private void InstallDriver()
    {
        string dir = AppContext.BaseDirectory;
        string setup = Path.Combine(dir, "driver", "DroidMic.DriverSetup.exe");
        string inf = Path.Combine(dir, "driver", "DroidMicVAD.inf");
        if (!File.Exists(setup) || !File.Exists(inf))
        {
            MessageBox.Show("The driver package is not bundled with this build.\n\nSee docs/DRIVER_INSTALLATION.md for how to build and install it.",
                "DroidMic", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(setup, $"install \"{inf}\"") { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit();
            Log.Info($"Driver setup finished with exit code {p?.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log.Warn("Driver installation cancelled (administrator permission is required).");
        }
        RefreshEndpoint();
    }

    [RelayCommand] private void OpenSoundSettings() => WindowsShell.OpenSoundSettings();
    [RelayCommand] private void OpenRecordingDevices() => WindowsShell.OpenRecordingDevices();
    [RelayCommand] private void OpenMicPrivacy() => WindowsShell.OpenMicrophonePrivacy();
    [RelayCommand] private void OpenLogFolder() => Process.Start(new ProcessStartInfo(Path.GetDirectoryName(Log.LogFilePath)!) { UseShellExecute = true });

    // ---- Pairing -----------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenPairing()
    {
        if (!_host.IsListening) await StartListening();
        if (!_host.IsListening) return;
        _host.Server!.Pairing.Open();
        RefreshPairing();
    }

    [RelayCommand]
    private void ClosePairing()
    {
        _host.Server?.Pairing.Close();
        RefreshPairing();
    }

    private void RefreshPairing()
    {
        var info = _host.Server?.Pairing.Current;
        IsPairing = info is not null;
        Pin = info is null ? "" : $"{info.Pin[..3]} {info.Pin[3..]}";
        if (info is null)
        {
            QrImage = null;
            PairingExpires = "";
            return;
        }
        var addr = _host.PreferredAddress()?.ToString() ?? "0.0.0.0";
        string uri = DiscoveryCodec.BuildPairingUri(addr, _host.Server!.ControlPort, info.Pin, _host.Identity.Fingerprint,
            _host.Server.Options.ServerName);
        QrImage = RenderQr(uri);
        RefreshPairingCountdown();
    }

    private void RefreshPairingCountdown()
    {
        var info = _host.Server?.Pairing.Current;
        if (info is null)
        {
            if (IsPairing) RefreshPairing();
            return;
        }
        var left = info.ExpiresAt - DateTimeOffset.UtcNow;
        PairingExpires = $"Expires in {Math.Max(0, (int)left.TotalMinutes)}:{Math.Max(0, left.Seconds):00}";
    }

    private static BitmapImage RenderQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10, new byte[] { 0x10, 0x14, 0x1A }, new byte[] { 0xFF, 0xFF, 0xFF });
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        img.Freeze();
        return img;
    }

    private void RefreshAddresses()
    {
        LocalAddresses.Clear();
        foreach (var a in NetworkInfo.GetLocalAddresses())
            LocalAddresses.Add($"{a.Address}  ({a.InterfaceName})");
        PrimaryAddress = _host.PreferredAddress()?.ToString() ?? "No network";
    }

    [RelayCommand]
    private void CopyConnectionDetails()
    {
        var addrs = string.Join(", ", NetworkInfo.GetLocalAddresses().Select(a => a.Address));
        string text = $"DroidMic PC: {(_host.Server?.Options.ServerName ?? Environment.MachineName)}\n" +
                      $"Address: {addrs}\nPort: {_settings.ControlPort}\nIdentity: {_host.Identity.Fingerprint}";
        Clipboard.SetText(text);
        Log.Info("Connection details copied to the clipboard");
    }

    // ---- Devices ---------------------------------------------------------------------------

    private void RefreshDevices()
    {
        PairedDevices.Clear();
        foreach (var d in _host.Pairings.Devices.OrderByDescending(d => d.LastSeen))
            PairedDevices.Add(new PairedDeviceItem(d.DeviceId, d.Name,
                $"Paired {d.PairedAt.LocalDateTime:g} · last seen {(d.LastSeen?.LocalDateTime.ToString("g") ?? "never")}"));
        HasPairedDevices = PairedDevices.Count > 0;
    }

    [RelayCommand]
    private void RevokeDevice(PairedDeviceItem? item)
    {
        if (item is null) return;
        if (MessageBox.Show($"Remove '{item.Name}'? It will have to pair again with a new PIN.", "DroidMic",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_host.Server?.Status.DeviceId == item.DeviceId) _host.Server.DisconnectActive("revoked");
        _host.Pairings.Remove(item.DeviceId);
        Log.Info($"Removed pairing for '{item.Name}'");
    }

    // ---- Firewall ----------------------------------------------------------------------------

    [RelayCommand]
    private void CheckFirewall()
    {
        bool rule = FirewallHelper.RuleExists();
        bool pub = FirewallHelper.AnyPublicNetwork();
        FirewallText = (rule ? "Firewall rule 'DroidMic' is present (private networks)." : "No 'DroidMic' firewall rule found: Windows may block the phone.")
                       + (pub ? " Your current network is set to Public, so phones may be blocked. Set it to Private in Network settings if this is your home network." : "");
    }

    [RelayCommand]
    private void AddFirewallRule()
    {
        string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        FirewallText = FirewallHelper.AddRulesElevated(exe) ? "Firewall rules added." : "Firewall rules were not added (permission declined or failed).";
    }

    [RelayCommand] private void OpenNetworkSettings() => WindowsShell.OpenNetworkSettings();
}
