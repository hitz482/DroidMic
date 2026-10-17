using System.Net;
using System.Net.Sockets;
using System.Text;
using DroidMic.AudioEngine;
using DroidMic.Networking;
using DroidMic.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DroidMic.Tests;

/// <summary>Runs the real server and the reference client over loopback.</summary>
public sealed class ServerIntegrationTests : IAsyncLifetime
{
    private sealed class FakeSinkStatus : ISinkStatusProvider
    {
        public string SinkName => "test";
        public bool SinkOk => true;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "droidmic-test-" + Guid.NewGuid().ToString("N"));
    private DroidMicServer _server = null!;
    private AudioPipeline _pipeline = null!;
    private PairingStore _pairings = null!;
    private IPEndPoint _control = null!;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

    public Task InitializeAsync()
    {
        var store = new FileSecretStore(_dir);
        _pipeline = new AudioPipeline();
        _pairings = new PairingStore(store);
        _server = new DroidMicServer(
            new ServerOptions { ControlPort = 0, AudioPort = 0, EnableDiscovery = false, BindAddress = IPAddress.Loopback, ServerName = "Test PC" },
            ServerIdentity.LoadOrCreate(store), _pairings, _pipeline, new FakeSinkStatus(), NullLogger.Instance);
        _server.Start();
        _control = new IPEndPoint(IPAddress.Loopback, _server.ControlPort);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    private async Task<byte[]> PairAsync(ReferenceClient client)
    {
        var info = _server.Pairing.Open();
        var result = await client.ConnectAsync(_control, info.Pin, null, _cts.Token);
        Assert.IsType<AuthOkMessage>(result);
        return client.PairingKey!;
    }

    private async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task PinPairing_ThenReconnectWithPairingKey_StreamsAudioIntoPipeline()
    {
        byte[] key;
        string deviceId;
        await using (var phone = new ReferenceClient { DeviceName = "Test phone" })
        {
            key = await PairAsync(phone);
            deviceId = phone.DeviceId;
            Assert.Equal(_server.Identity.Fingerprint, phone.ServerFingerprint);
            Assert.Null(_server.Pairing.Current); // PIN is single-use
        }
        Assert.Single(_pairings.Devices);
        await WaitFor(() => _server.Status.Phase == ConnectionPhase.Listening);

        await using var again = new ReferenceClient { DeviceId = deviceId, DeviceName = "Test phone" };
        Assert.IsType<AuthOkMessage>(await again.ConnectAsync(_control, null, key, _cts.Token));
        Assert.True((await again.StartStreamAsync(48000, 1, 10, _cts.Token)).Ok);
        await WaitFor(() => _server.Status.Phase == ConnectionPhase.Streaming);

        // 1 kHz sine at -6 dBFS.
        byte[] pcm = new byte[960];
        for (int f = 0; f < 20; f++)
        {
            for (int i = 0; i < 480; i++)
            {
                short s = (short)(16384 * Math.Sin(2 * Math.PI * 1000 * (f * 480 + i) / 48000.0));
                pcm[2 * i] = (byte)s; pcm[2 * i + 1] = (byte)(s >> 8);
            }
            again.SendFrame(pcm, 48000, 1);
        }
        var c = _server.Receiver!.Counters;
        try { await WaitFor(() => _pipeline.GetStats().FramesReceived == 20); }
        catch (TimeoutException) { throw new Exception($"acc={c.Accepted} mal={c.Malformed} ws={c.WrongSession} src={c.WrongSource} rep={c.Replayed} auth={c.AuthFailed} got={_pipeline.GetStats().FramesReceived} state={_pipeline.State}"); }
        Assert.InRange(_pipeline.InputMeter.PeakDb, -6.5, -5.5);

        var output = new short[480];
        Assert.True(_pipeline.Read(output));
        Assert.Contains(output, s => Math.Abs((int)s) > 10000);

        await again.SendAsync(new StreamStopMessage(), _cts.Token);
        await WaitFor(() => _pipeline.State == PipelineState.Idle);
    }

    [Fact]
    public async Task WrongPin_IsRejected_AndLocksOutAfterFiveAttempts()
    {
        var info = _server.Pairing.Open();
        string wrong = info.Pin == "000000" ? "111111" : "000000";
        for (int i = 1; i <= ProtocolConstants.MaxPinAttempts; i++)
        {
            await using var phone = new ReferenceClient();
            var r = await phone.ConnectAsync(_control, wrong, null, _cts.Token);
            var failed = Assert.IsType<AuthFailedMessage>(r);
            Assert.Equal(i < ProtocolConstants.MaxPinAttempts ? AuthFailedMessage.BadCredentials : AuthFailedMessage.LockedOut, failed.Reason);
        }
        Assert.Null(_server.Pairing.Current);
        await using var late = new ReferenceClient();
        var closed = Assert.IsType<ErrorMessage>(await late.ConnectAsync(_control, info.Pin, null, _cts.Token));
        Assert.Equal(ErrorMessage.PairingClosed, closed.Code);
        Assert.Empty(_pairings.Devices);
    }

    [Fact]
    public async Task PinPairing_WhenPairingClosed_IsRefused()
    {
        await using var phone = new ReferenceClient();
        var r = Assert.IsType<ErrorMessage>(await phone.ConnectAsync(_control, "123456", null, _cts.Token));
        Assert.Equal(ErrorMessage.PairingClosed, r.Code);
    }

    [Fact]
    public async Task UnknownOrRevokedDevice_IsRejected()
    {
        await using (var stranger = new ReferenceClient())
        {
            var r = Assert.IsType<AuthFailedMessage>(await stranger.ConnectAsync(_control, null, new byte[32], _cts.Token));
            Assert.Equal(AuthFailedMessage.UnknownDevice, r.Reason);
        }

        byte[] key;
        string id;
        await using (var phone = new ReferenceClient()) { key = await PairAsync(phone); id = phone.DeviceId; }
        Assert.True(_pairings.Remove(id));
        await using var revoked = new ReferenceClient { DeviceId = id };
        var rr = Assert.IsType<AuthFailedMessage>(await revoked.ConnectAsync(_control, null, key, _cts.Token));
        Assert.Equal(AuthFailedMessage.UnknownDevice, rr.Reason);
    }

    [Fact]
    public async Task SecondPhone_GetsBusy()
    {
        await using var first = new ReferenceClient();
        byte[] key1 = await PairAsync(first);
        await using var second = new ReferenceClient();
        var info = _server.Pairing.Open();
        var r = Assert.IsType<ErrorMessage>(await second.ConnectAsync(_control, info.Pin, null, _cts.Token));
        Assert.Equal(ErrorMessage.Busy, r.Code);
    }

    [Fact]
    public async Task ReplayedTamperedAndForeignPackets_AreDropped()
    {
        await using var phone = new ReferenceClient();
        await PairAsync(phone);
        Assert.True((await phone.StartStreamAsync(48000, 1, 10, _cts.Token)).Ok);
        var counters = _server.Receiver!.Counters;

        byte[] frame = phone.EncodeFrame(new byte[960], 48000, 1, seq: 5);
        phone.SendRaw(frame);
        await WaitFor(() => counters.Accepted == 1);
        phone.SendRaw(frame); // replay
        await WaitFor(() => counters.Replayed == 1);

        byte[] tampered = phone.EncodeFrame(new byte[960], 48000, 1, seq: 6);
        tampered[40] ^= 0xFF;
        phone.SendRaw(tampered);
        await WaitFor(() => counters.AuthFailed == 1);

        phone.SendRaw(new byte[] { 1, 2, 3 });
        await WaitFor(() => counters.Malformed == 1);

        // A packet from a different source address than the control connection.
        using var foreign = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        byte[] valid = phone.EncodeFrame(new byte[960], 48000, 1, seq: 7);
        try
        {
            await foreign.SendAsync(valid, new IPEndPoint(IPAddress.Loopback, _server.Receiver.Port));
            await WaitFor(() => counters.WrongSource == 1);
        }
        catch (SocketException)
        {
            // 127.0.0.2 not routable on this host; skip that part.
        }
        Assert.Equal(1, counters.Accepted);
        Assert.Equal(1, _pipeline.GetStats().FramesReceived);
    }

    [Fact]
    public async Task UnsupportedStreamFormats_AreRefused()
    {
        await using var phone = new ReferenceClient();
        await PairAsync(phone);
        Assert.False((await phone.StartStreamAsync(22050, 1, 10, _cts.Token)).Ok);
        Assert.False((await phone.StartStreamAsync(48000, 3, 10, _cts.Token)).Ok);
        Assert.False((await phone.StartStreamAsync(48000, 1, 7, _cts.Token)).Ok);
        Assert.True((await phone.StartStreamAsync(48000, 2, 20, _cts.Token)).Ok);
    }

    [Fact]
    public async Task SilentClient_TimesOut()
    {
        await using var phone = new ReferenceClient();
        await PairAsync(phone);
        await WaitFor(() => _server.Status.Phase == ConnectionPhase.Connected);
        await WaitFor(() => _server.Status.Phase == ConnectionPhase.Listening, ProtocolConstants.SessionTimeoutMs + 3000);
    }

    [Fact]
    public async Task GarbageOnControlPort_DoesNotCrashServer()
    {
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(_control);
            await raw.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
            await Task.Delay(100);
        }
        await using var phone = new ReferenceClient();
        await PairAsync(phone);
    }
}
