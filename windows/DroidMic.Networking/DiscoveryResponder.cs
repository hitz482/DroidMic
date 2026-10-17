using System.Net;
using System.Net.Sockets;
using DroidMic.Protocol;
using Microsoft.Extensions.Logging;

namespace DroidMic.Networking;

/// <summary>Answers LAN discovery broadcasts while the receiver is listening (protocol §1).</summary>
public sealed class DiscoveryResponder : IDisposable
{
    private readonly Func<DiscoveryReply> _replyFactory;
    private readonly ILogger _log;
    private readonly UdpClient _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DiscoveryResponder(int port, Func<DiscoveryReply> replyFactory, ILogger log)
    {
        _replyFactory = replyFactory;
        _log = log;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            if (!DiscoveryCodec.TryParseRequest(r.Buffer, out var nonce)) continue;
            var reply = _replyFactory() with { Nonce = Convert.ToBase64String(nonce) };
            try
            {
                await _udp.SendAsync(reply.ToUtf8(), r.RemoteEndPoint, ct).ConfigureAwait(false);
                _log.LogDebug("Answered discovery from {Remote}", r.RemoteEndPoint.Address);
            }
            catch (SocketException ex)
            {
                _log.LogDebug("Discovery reply failed: {Error}", ex.SocketErrorCode);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp.Dispose();
        try { _loop?.Wait(1000); } catch (AggregateException) { }
    }
}
