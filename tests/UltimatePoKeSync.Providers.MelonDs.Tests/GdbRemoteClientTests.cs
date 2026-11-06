using Xunit;

namespace UltimatePoKeSync.Providers.MelonDs.Tests;

/// <summary>
/// The protocol half of the melonDS provider, against a stub that behaves like the real one.
/// Every rule checked here came from a failure against melonDS 1.1, not from its source.
/// See D-039.
/// </summary>
public sealed class GdbRemoteClientTests
{
    /// <summary>A protocol test that hangs should fail, not wait forever.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheConversationOpensWithAnAcknowledgementAndAContinue()
    {
        await using var stub = new FakeGdbStub();
        await using GdbRemoteClient client = await GdbRemoteClient.ConnectAsync("127.0.0.1", stub.Port, Token);

        // A continue is answered by the next stop rather than immediately, so nothing has
        // arrived yet at this point. One read drains the pipe and puts the two in order.
        await client.ReadMemoryAsync(0x02000000, 16, Token);

        // Without the bare '+' first, melonDS swallows the packet's '$' and hangs up. The
        // fake enforces that, so merely getting here proves it was sent.
        // The continue matters just as much: connecting halts the CPU, and a provider that
        // forgets it leaves the game frozen for whoever is playing.
        Assert.Equal("c", stub.Commands[0]);
    }

    [Fact]
    public async Task MemoryComesBackAsTheBytesThatWereAskedFor()
    {
        await using var stub = new FakeGdbStub();
        await using GdbRemoteClient client = await GdbRemoteClient.ConnectAsync("127.0.0.1", stub.Port, Token);

        byte[] read = await client.ReadMemoryAsync(0x02000100, 64, Token);

        Assert.Equal(stub.MemoryAt(0x02000100, 64), read);
        Assert.Contains("m2000100,40", stub.Commands);
    }

    /// <summary>
    /// 576 bytes is what the stub says it allows, and it is wrong: the reply's own framing
    /// no longer fits its buffer and the read times out. Refusing early beats hanging.
    /// </summary>
    [Fact]
    public async Task AReadTooBigForTheStubIsRefusedBeforeItIsSent()
    {
        await using var stub = new FakeGdbStub();
        await using GdbRemoteClient client = await GdbRemoteClient.ConnectAsync("127.0.0.1", stub.Port, Token);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ReadMemoryAsync(0x02000000, GdbRemoteClient.MaximumReadLength + 1, Token));

        Assert.DoesNotContain(stub.Commands, command => command.StartsWith('m'));
    }

    [Fact]
    public async Task ARefusedReadIsAnErrorRatherThanEmptyBytes()
    {
        await using var stub = new FakeGdbStub { RefuseReads = true };
        await using GdbRemoteClient client = await GdbRemoteClient.ConnectAsync("127.0.0.1", stub.Port, Token);

        Exception? error = await Record.ExceptionAsync(
            () => client.ReadMemoryAsync(0x02000000, 32, Token));

        Assert.NotNull(error);
        Assert.Contains("E01", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A short reply is the failure that would quietly corrupt a party: half a Pokémon
    /// parses into something, and it would not look wrong until much later.
    /// </summary>
    [Fact]
    public async Task AShortReplyIsAnErrorRatherThanHalfAPokemon()
    {
        await using var stub = new FakeGdbStub { TruncateReads = true };
        await using GdbRemoteClient client = await GdbRemoteClient.ConnectAsync("127.0.0.1", stub.Port, Token);

        Exception? error = await Record.ExceptionAsync(
            () => client.ReadMemoryAsync(0x02000000, 64, Token));

        Assert.NotNull(error);
        Assert.Contains("got 32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsQueueUpRatherThanTalkingOverEachOther()
    {
        await using var stub = new FakeGdbStub();
        await using GdbRemoteClient client = await GdbRemoteClient.ConnectAsync("127.0.0.1", stub.Port, Token);

        byte[][] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i =>
                client.ReadMemoryAsync((uint)(0x02000000 + (i * 128)), 128, Token)));

        for (int i = 0; i < results.Length; i++)
        {
            Assert.Equal(stub.MemoryAt((uint)(0x02000000 + (i * 128)), 128), results[i]);
        }
    }

    /// <summary>
    /// The one that costs a player rather than a test: leaving without detaching leaves the
    /// game frozen on their screen.
    /// </summary>
    [Fact]
    public async Task LeavingDetachesSoTheGameKeepsRunning()
    {
        await using var stub = new FakeGdbStub();

        GdbRemoteClient client = await GdbRemoteClient.ConnectAsync("127.0.0.1", stub.Port, Token);
        await client.ReadMemoryAsync(0x02000000, 16, Token);
        await client.DisposeAsync();

        Assert.Equal("D", stub.Commands[^1]);
    }

    /// <summary>
    /// melonDS writes its acknowledgement and its reply separately, with Nagle's algorithm on,
    /// so the reply waits until the client acknowledges the first write. Linux delays that
    /// acknowledgement by up to 40 ms unless asked not to, and against a real SoulSilver that
    /// made every read cost three frames and held the game at 22 FPS. Measured, with the
    /// fix: 16.8 ms, one frame. See D-065.
    /// </summary>
    [Fact]
    public async Task ReadsDoNotWaitOnADelayedAcknowledgement()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the delayed acknowledgement is Linux's");

        await using var stub = new FakeGdbStub { ReplyLikeMelonDs = true };
        await using GdbRemoteClient client = await GdbRemoteClient.ConnectAsync("127.0.0.1", stub.Port, Token);

        // The kernel acknowledges the first few segments of a connection at once and only
        // starts delaying once the traffic looks interactive, so warm up before timing.
        for (int i = 0; i < 10; i++)
        {
            await client.ReadMemoryAsync(0x02000000, 16, Token);
        }

        var timings = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            await client.ReadMemoryAsync(0x02000000, 16, Token);
            timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        // There is no emulator frame to wait for here, so a read costs well under a
        // millisecond; a delayed acknowledgement costs tens of them.
        timings.Sort();
        Assert.True(timings[timings.Count / 2] < 10, $"median read took {timings[timings.Count / 2]:F1} ms");
    }

    [Fact]
    public async Task AStubThatIsNotThereFailsToConnectRatherThanHanging()
    {
        // Port 1 on loopback is not something anyone is listening on.
        await Assert.ThrowsAnyAsync<Exception>(
            () => GdbRemoteClient.ConnectAsync("127.0.0.1", 1, Token));
    }
}
