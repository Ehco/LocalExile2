using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;

// The instance server for the PoE2 client (Game_CLIENT=poe2; PORTING-POE2.md, wave 13). It needs none of the PoE1
// client, data or saves: it reads PoE2's opening, sends the area, then lets InstanceSessionPoe2 answer the client
// (the player after its 0x9a, echoes of its direction moves) and ends a server frame every 33 ms, without which the
// lockstep client neither runs nor draws its world.
partial class Program
{
    // Clearfell Encampment (WorldAreas G1_town, HASH16), for a ticket Auth does not know: Auth does not log PoE2 clients
    // in yet, so a local test login (a harness) has no ticket there.
    const ushort Poe2FallbackArea = 0x3f70;

    static bool IsPoe2Client =>
        string.Equals(Environment.GetEnvironmentVariable("Game_CLIENT"), "poe2", StringComparison.OrdinalIgnoreCase);

    static async Task<int> RunPoe2Gateway(int port, InstanceKeyring keyring)
    {
        var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("Game_BIND") ?? "127.0.0.1");
        var listener = new TcpListener(bind, port);
        listener.Start();
        Console.WriteLine($"[gateway] PoE2 client: listening on {bind}:{port}");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException e)
                {
                    // A connection reset before it was accepted: the listener goes on.
                    Console.WriteLine($"[gateway] accept failed: {e.Message}");
                    continue;
                }
                _ = HandlePoe2Async(client, keyring, cts.Token);
            }
        }
        finally { listener.Stop(); }
        Console.WriteLine("[gateway] stopped");
        return 0;
    }

    // A client that has not sent its opening by then is dropped; unread client bytes above the cap close the connection.
    static readonly TimeSpan Poe2OpeningTimeout = TimeSpan.FromSeconds(10);
    const int Poe2UnreadCap = 64 * 1024;

    static async Task HandlePoe2Async(TcpClient client, InstanceKeyring keyring, CancellationToken stop)
    {
        string remote = "?";
        string reason = "closed by the client";
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            using (client)
            {
                remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
                client.NoDelay = true;
                Console.WriteLine($"[gateway] CONNECT {remote} (PoE2)");
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                var pending = new List<byte>();
                InstanceOpeningPoe2? opening = null;
                using (var openingTime = CancellationTokenSource.CreateLinkedTokenSource(closed.Token))
                {
                    openingTime.CancelAfter(Poe2OpeningTimeout);
                    while (opening is null)
                    {
                        int n = await stream.ReadAsync(buffer, openingTime.Token);
                        if (n == 0) return;
                        pending.AddRange(buffer.AsSpan(0, n));
                        opening = InstanceOpeningPoe2.TryRead([.. pending]);
                    }
                }
                Console.WriteLine($"[gateway] {remote} PoE2 opening: ticket=0x{opening.Ticket:x8} flag={opening.Flag}");
                var (area, seed, instance) = await ResolvePoe2Ticket(remote, opening.Ticket);

                var ciphers = keyring.Ciphers(opening.Ticket);
                var gate = new SemaphoreSlim(1, 1);
                async Task Send(byte[] message, string? log)
                {
                    await gate.WaitAsync(closed.Token);
                    try
                    {
                        if (log is not null) Console.WriteLine($"[gateway] {remote} => {log}");
                        ciphers.Send.Process(message);
                        await stream.WriteAsync(message, closed.Token);
                    }
                    finally { gate.Release(); }
                }

                // 0x11 goes in plaintext; everything after it is encrypted, the client's bytes after its opening too.
                await stream.WriteAsync(InstanceProtocolPoe2.StartEncryption(), closed.Token);
                await Send(new AreaInstancePoe2(InstanceId: (uint)instance, AreaHash: area, Seed: seed, Level: 0).Encode(),
                    $"0x1e area 0x{area:x4} seed {seed} instance {instance}");

                var session = new InstanceSessionPoe2(new PlayerObjectPoe2(
                    InstanceSessionPoe2.G1TownSpawn.X, InstanceSessionPoe2.G1TownSpawn.Y, Life: 100, Mana: 30));
                var clock = Stopwatch.StartNew();
                byte[] unread = [.. pending.Skip(InstanceOpeningPoe2.Length)];
                ciphers.Receive.Process(unread);
                while (true)
                {
                    var messages = InstanceSessionPoe2.Split(unread, out int rest);
                    foreach (var message in messages)
                    {
                        bool wasInWorld = session.InWorld;
                        foreach (var answer in session.Handle(message, clock.Elapsed.TotalSeconds))
                            await Send(answer, DescribePoe2(answer));
                        if (!wasInWorld && session.InWorld) _ = RunPoe2Frames(Send, closed.Token);
                    }
                    unread = unread[rest..];
                    if (unread.Length >= 2 && !InstanceSessionPoe2.CanFrame((ushort)(unread[0] << 8 | unread[1])))
                    {
                        // No length for this id: drop the rest of the read (the client writes one message per chunk).
                        Console.WriteLine($"[gateway] {remote} C→S 0x{unread[0] << 8 | unread[1]:x4} not framed, {unread.Length} B dropped");
                        unread = [];
                    }
                    int got = await stream.ReadAsync(buffer, closed.Token);
                    if (got == 0) break;
                    byte[] chunk = buffer[..got];
                    ciphers.Receive.Process(chunk);
                    unread = [.. unread, .. chunk];
                    if (unread.Length > Poe2UnreadCap) { reason = $"{unread.Length} B of client data not framed"; break; }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            reason = e is OperationCanceledException && !stop.IsCancellationRequested ? "no opening in time" : e.GetType().Name;
        }
        catch (Exception e)
        {
            // A bad opening (InvalidDataException) or a bug: logged here, as nothing awaits this task.
            reason = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            closed.Cancel();
            Console.WriteLine($"[gateway] DISCONNECT {remote} (PoE2): {reason}");
        }
    }

    // Auth's area, seed and instance for the ticket, or Clearfell Encampment for a ticket it does not know.
    static async Task<(ushort Area, uint Seed, ulong Instance)> ResolvePoe2Ticket(string remote, uint ticket)
    {
        var resolved = await InstanceControl.CallAsync(new ControlRequest { Op = "resolve", Ticket = ticket });
        if (resolved.Ok && resolved.Area != 0) return (resolved.Area, resolved.Seed == 0 ? 1 : resolved.Seed, resolved.Instance);
        Console.WriteLine($"[gateway] {remote} ticket 0x{ticket:x8} not resolved ({resolved.Error ?? "unknown area"}); " +
                          "Clearfell Encampment, seed 1, instance 1");
        return (Poe2FallbackArea, 1, 1);
    }

    // Ends a server frame every 33 ms until the connection closes (the length went with the player, 0x2f5).
    static async Task RunPoe2Frames(Func<byte[], string?, Task> send, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(InstanceSessionPoe2.FrameMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stop)) await send(InstanceProtocolPoe2.FrameEnd(), null);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    static string DescribePoe2(byte[] message) => (message[0] << 8 | message[1]) switch
    {
        InstanceProtocolPoe2.LocalPlayerId => "0x24 local player",
        InstanceProtocolPoe2.CreateObjectId => $"0x2f7 player object ({message.Length} B)",
        InstanceInventoriesPoe2.AddInventoryId => $"0xdf × {InstanceInventoriesPoe2.PlayerDefaults.Length} inventories",
        InstanceProtocolPoe2.EnterAreaId => "0x23 enter area",
        InstanceProtocolPoe2.FrameLengthId => $"0x2f5 frame length {message[2]} ms, then 0x2f4 every frame",
        InstanceMovementPoe2.DirectionMoveEchoId => $"0x64 move echo {Convert.ToHexString(message, 13, message.Length - 13)}",
        var id => $"0x{id:x}",
    };
}
