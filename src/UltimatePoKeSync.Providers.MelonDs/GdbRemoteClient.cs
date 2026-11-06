using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace UltimatePoKeSync.Providers.MelonDs;

/// <summary>
/// Just enough of the GDB remote serial protocol to read memory from melonDS. See D-039.
/// </summary>
/// <remarks>
/// <para>
/// A packet is <c>$payload#cc</c>, where <c>cc</c> is the low byte of the sum of the payload
/// characters, and each side acknowledges what it receives with a bare <c>+</c>.
/// </para>
/// <para>
/// Two melonDS-specific behaviours are handled here rather than by the caller, because
/// getting either wrong looks like a broken emulator. The stub waits one second after
/// accepting for a bare <c>+</c> and swallows whatever arrives first as that acknowledgement,
/// so a client that opens with a packet loses its <c>$</c> and gets hung up on. And
/// connecting halts the emulated CPU: it stays halted until a continue arrives, and stays
/// halted if the socket is simply dropped, which freezes the game for whoever is playing it.
/// </para>
/// </remarks>
internal sealed class GdbRemoteClient : IAsyncDisposable
{
    /// <summary>
    /// The stub allows half its 1152-byte buffer, but that limit does not account for the
    /// reply's own framing: 576 bytes become 1152 hex characters and time out. See D-039.
    /// </summary>
    public const int MaximumReadLength = 256;

    private static readonly byte[] Acknowledgement = "+"u8.ToArray();

    // Linux's TCP_QUICKACK, from <netinet/tcp.h>. .NET has no named option for it.
    private const int IpProtocolTcp = 6;
    private const int TcpQuickAck = 12;
    private static readonly byte[] On = BitConverter.GetBytes(1);

    private readonly SemaphoreSlim _exchange = new(1, 1);
    private readonly Socket _socket;
    private readonly byte[] _receiveBuffer = new byte[4096];

    private StringBuilder _pending = new();
    private bool _running;

    private GdbRemoteClient(Socket socket) => _socket = socket;

    public static async Task<GdbRemoteClient> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var client = new GdbRemoteClient(socket);

            // The handshake, before anything else can be said.
            await socket.SendAsync(Acknowledgement, cancellationToken).ConfigureAwait(false);
            await client.ReadAcknowledgementAsync(cancellationToken).ConfigureAwait(false);

            // Connecting halted the CPU. Release it, and remember that we did, so that
            // disposal knows whether it still has to.
            await client.SendPacketAsync("c", cancellationToken).ConfigureAwait(false);
            client._running = true;

            return client;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes, at most <see cref="MaximumReadLength"/> in one
    /// call. Longer requests are the caller's to split, because only the caller knows which
    /// parts it can do without when the emulator stops answering half way.
    /// </summary>
    public async Task<byte[]> ReadMemoryAsync(
        uint address,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumReadLength);

        string reply = await ExchangeAsync(
            $"m{address:x},{length:x}",
            cancellationToken).ConfigureAwait(false);

        if (reply.Length == 0 || reply[0] == 'E')
        {
            throw new GdbProtocolException(
                $"the stub refused a read of {length} bytes at 0x{address:X8}: "
                + (reply.Length == 0 ? "empty reply" : reply));
        }

        if (reply.Length != length * 2)
        {
            throw new GdbProtocolException(
                $"asked for {length} bytes at 0x{address:X8} and got {reply.Length / 2}");
        }

        return Convert.FromHexString(reply);
    }

    /// <summary>Sends a command and returns its reply, one exchange at a time.</summary>
    private async Task<string> ExchangeAsync(string payload, CancellationToken cancellationToken)
    {
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await SendPacketAsync(payload, cancellationToken).ConfigureAwait(false);
            return await ReadPacketAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _exchange.Release();
        }
    }

    private async Task SendPacketAsync(string payload, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.ASCII.GetBytes(payload);
        byte checksum = 0;
        foreach (byte value in body)
        {
            unchecked
            {
                checksum += value;
            }
        }

        byte[] packet = Encoding.ASCII.GetBytes(
            $"${payload}#{checksum.ToString("x2", CultureInfo.InvariantCulture)}");

        await _socket.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls one packet out of the stream, stepping over the acknowledgements between them,
    /// and acknowledges it.
    /// </summary>
    private async Task<string> ReadPacketAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string buffered = _pending.ToString();
            int start = buffered.IndexOf('$', StringComparison.Ordinal);
            int end = start < 0 ? -1 : buffered.IndexOf('#', start + 1);

            if (start >= 0 && end > start && buffered.Length >= end + 3)
            {
                string payload = buffered[(start + 1)..end];
                _pending = new StringBuilder(buffered[(end + 3)..]);

                await _socket.SendAsync(Acknowledgement, cancellationToken).ConfigureAwait(false);
                return payload;
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadAcknowledgementAsync(CancellationToken cancellationToken)
    {
        while (_pending.Length == 0)
        {
            await FillAsync(cancellationToken).ConfigureAwait(false);
        }

        // Anything other than '+' is the stub complaining, not a reason to stop: it keeps
        // talking either way, and the next packet is what actually matters.
        if (_pending[0] is '+' or '-')
        {
            _pending.Remove(0, 1);
        }
    }

    private async Task FillAsync(CancellationToken cancellationToken)
    {
        AcknowledgeImmediately();

        int read = await _socket
            .ReceiveAsync(_receiveBuffer, cancellationToken)
            .ConfigureAwait(false);

        if (read == 0)
        {
            throw new GdbProtocolException("the emulator closed the connection");
        }

        _pending.Append(Encoding.ASCII.GetString(_receiveBuffer, 0, read));
    }

    /// <summary>
    /// Asks Linux to acknowledge what arrives next at once rather than delay it. See D-065.
    /// </summary>
    /// <remarks>
    /// melonDS writes an acknowledgement and then its reply, with Nagle's algorithm on, so the
    /// reply is held back until we acknowledge the first write. Linux delays acknowledgements
    /// by up to 40 ms once traffic looks interactive, which is exactly what a stream of reads
    /// looks like: every read then cost about 44 ms where one frame, 16.7 ms, is the floor,
    /// and the game dropped to 22 FPS while being read. The kernel clears the flag after it
    /// acts on it, so it is set again before every receive. macOS and Windows have no such
    /// option and were never seen to stall this way.
    /// </remarks>
    private void AcknowledgeImmediately()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            _socket.SetRawSocketOption(IpProtocolTcp, TcpQuickAck, On);
        }
        catch (SocketException)
        {
            // Slower, not broken: reads still work, at the old cost.
        }
    }

    /// <summary>
    /// Detaches before hanging up. Without it the emulated CPU stays where our connection
    /// left it, and the game is frozen for the person holding the controller.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_running)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendPacketAsync("D", timeout.Token).ConfigureAwait(false);
                await ReadPacketAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A detach that fails is not worth an exception on the way out; the socket
                // closing gets the emulator most of the way back on its own.
            }
        }

        _exchange.Dispose();
        _socket.Dispose();
    }
}

internal sealed class GdbProtocolException(string message) : Exception(message);
