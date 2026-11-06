using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UltimatePoKeSync.Providers.MelonDs.Tests;

/// <summary>
/// A stand-in for melonDS's GDB stub, faithful to the parts that bite. See D-039.
/// </summary>
/// <remarks>
/// It insists on the leading acknowledgement the way melonDS does: a client that opens with
/// a packet has its <c>$</c> swallowed and gets hung up on, because that was a real bug
/// found against the real emulator, and a fake that forgives it would let it back in.
/// </remarks>
internal sealed class FakeGdbStub : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;
    private readonly byte[] _memory;
    private readonly uint _baseAddress;

    public FakeGdbStub(uint baseAddress = 0x02000000, int size = 4096)
        : this(baseAddress, [.. Enumerable.Range(0, size).Select(i => (byte)(i * 31 % 251))])
    {
    }

    /// <summary>Serves memory somebody else laid out, for the tests that need it to mean something.</summary>
    public FakeGdbStub(uint baseAddress, byte[] memory)
    {
        _baseAddress = baseAddress;
        _memory = memory;

        _listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(1);

        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        _serving = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>Every command payload received, in order, so a test can assert the protocol.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Set to make the next read fail the way a refused read does.</summary>
    public bool RefuseReads { get; set; }

    /// <summary>Set to reply with fewer bytes than were asked for.</summary>
    public bool TruncateReads { get; set; }

    /// <summary>
    /// Set before connecting to write each acknowledgement and its reply separately, from a
    /// socket that keeps Nagle's algorithm on, which is what melonDS does. The second write
    /// then waits for the client to acknowledge the first, and a client that lets the kernel
    /// delay that acknowledgement pays for it on every read. See D-065.
    /// </summary>
    public bool ReplyLikeMelonDs { get; set; }

    public byte[] MemoryAt(uint address, int length) =>
        _memory.AsSpan((int)(address - _baseAddress), length).ToArray();

    private async Task ServeAsync()
    {
        Socket client;
        try
        {
            client = await _listener.AcceptAsync(_stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using (client)
        {
            client.NoDelay = !ReplyLikeMelonDs;

            var buffer = new byte[4096];
            var pending = new StringBuilder();

            // melonDS reads exactly one byte as the handshake before it will parse anything.
            int first = await client.ReceiveAsync(buffer.AsMemory(0, 1), _stopping.Token);
            if (first != 1 || buffer[0] != '+')
            {
                return; // hung up on, exactly like the real thing
            }

            await client.SendAsync("+"u8.ToArray(), _stopping.Token);

            while (!_stopping.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await client.ReceiveAsync(buffer, _stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (read == 0)
                {
                    return;
                }

                pending.Append(Encoding.ASCII.GetString(buffer, 0, read));

                while (TryTake(pending, out string payload))
                {
                    Commands.Add(payload);

                    string? reply = Answer(payload);
                    if (reply is null)
                    {
                        continue;
                    }

                    byte[] framed = Frame(reply);
                    if (ReplyLikeMelonDs)
                    {
                        await client.SendAsync(framed.AsMemory(0, 1), _stopping.Token);
                        await client.SendAsync(framed.AsMemory(1), _stopping.Token);
                    }
                    else
                    {
                        await client.SendAsync(framed, _stopping.Token);
                    }
                }
            }
        }
    }

    private string? Answer(string payload)
    {
        if (payload == "c")
        {
            return null; // a continue is answered by the next stop, not immediately
        }

        if (payload == "D")
        {
            return "OK";
        }

        if (payload.StartsWith('m'))
        {
            if (RefuseReads)
            {
                return "E01";
            }

            string[] parts = payload[1..].Split(',');
            uint address = Convert.ToUInt32(parts[0], 16);
            int length = Convert.ToInt32(parts[1], 16);
            if (TruncateReads)
            {
                length /= 2;
            }

            return Convert.ToHexString(MemoryAt(address, length)).ToLowerInvariant();
        }

        return string.Empty; // the protocol's "I do not know that one"
    }

    private static bool TryTake(StringBuilder pending, out string payload)
    {
        string buffered = pending.ToString();
        int start = buffered.IndexOf('$', StringComparison.Ordinal);
        int end = start < 0 ? -1 : buffered.IndexOf('#', start + 1);

        if (start < 0 || end < 0 || buffered.Length < end + 3)
        {
            payload = string.Empty;
            return false;
        }

        payload = buffered[(start + 1)..end];
        pending.Clear();
        pending.Append(buffered[(end + 3)..]);
        return true;
    }

    private static byte[] Frame(string payload)
    {
        byte checksum = 0;
        foreach (byte value in Encoding.ASCII.GetBytes(payload))
        {
            unchecked
            {
                checksum += value;
            }
        }

        return Encoding.ASCII.GetBytes($"+${payload}#{checksum:x2}");
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        try
        {
            await _serving;
        }
        catch (Exception)
        {
            // Shutting down a fake is not a test result.
        }

        _listener.Dispose();
        _stopping.Dispose();
    }
}
