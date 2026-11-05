// PoE2 login stand-in for live tests (HANDOFF-POE2.md): Auth does not speak the PoE2 login dialect yet, so this answers
// the patched client's handshake, token login 0x05 (0x04 result 0 + an empty 0x1b), character list request 0x18 (one
// "Probe" character, 0x15) and Play (0x14 to 127.0.0.1:6112, Clearfell Encampment, seed 1). Logs every decoded message,
// the token redacted. Never commit its log: raw dumps can carry the Steam session ticket and machine ids (wave 3).
// With INSTANCE_SECRET (the Gateway's keyring file) the Gateway serves the instance on 6112 and this only logs in;
// without it, it serves a scripted instance itself (AFTER_9A steps, the wave 9-12 experiments).
// usage: harness <keys.json> <port> <log>
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

var keys = LoginServerKeys.FromJson(File.ReadAllText(args[0]));
int port = int.Parse(args[1]);
string logPath = args[2];

void Log(string line)
{
    string s = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
    Console.WriteLine(s);
    lock (logPath) File.AppendAllText(logPath, s + Environment.NewLine);
}

static string Describe(ILoginMessage m) => m is TokenLoginPoe2 t
    ? $"0x05 TokenLoginPoe2 {{ Realm = 0x{t.Realm:x2}, B69 = {t.B69}, Token = <{t.Token.Length} bytes redacted> }}"
    : LoginMessageFormat.Describe(m);

// INSTANCE_SECRET: the Gateway's keyring file; then the Gateway serves the instance on 6112 and this only logs in.
string? secret = Environment.GetEnvironmentVariable("INSTANCE_SECRET");
var keyring = secret is null ? InstanceKeyring.Random() : InstanceKeyring.LoadOrCreate(secret);
const ushort G1Town = 0x3f70; // PoE2 WorldAreas G1_town (Clearfell Encampment), HASH16 from data/balance/worldareas.datc64
var instance = new TcpListener(IPAddress.Loopback, 6112);
if (secret is null) instance.Start();
if (secret is null) _ = Task.Run(async () =>
{
    while (true)
    {
        var c = await instance.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            Log($"instance connect {c.Client.RemoteEndPoint}");
            try
            {
                using var owned = c;
                var s = c.GetStream();
                var pending = new List<byte>();
                byte[] b = new byte[4096];
                InstanceOpeningPoe2? opening = null;
                while (opening is null)
                {
                    int got = await s.ReadAsync(b);
                    if (got == 0) { Log("instance closed before the opening"); return; }
                    pending.AddRange(b.AsSpan(0, got));
                    opening = InstanceOpeningPoe2.TryRead([.. pending]);
                }
                Log($"instance opening {opening with { Digest = [] }} digest={Convert.ToHexString(opening.Digest)} flag={opening.Flag}");
                var ciphers = keyring.Ciphers(opening.Ticket);
                await s.WriteAsync(InstanceProtocolPoe2.StartEncryption());
                Log("instance S->C 0x0011 + u8 0 (start encryption, PoE2)");
                byte[] area = new AreaInstancePoe2(InstanceId: 1, AreaHash: G1Town, Seed: 1, Level: 0).Encode();
                Log($"instance S->C 0x1e AreaInstancePoe2 (encrypted) {Convert.ToHexString(area)}");
                ciphers.Send.Process(area);
                await s.WriteAsync(area);
                byte[] rest = [.. pending.Skip(InstanceOpeningPoe2.Length)];
                if (rest.Length > 0) { ciphers.Receive.Process(rest); Log($"instance C->S (decrypted, after opening) {Convert.ToHexString(rest)}"); }
                var sendLock = new SemaphoreSlim(1, 1);
                async Task SendEnc(byte[] m)
                {
                    await sendLock.WaitAsync();
                    try { ciphers.Send.Process(m); await s.WriteAsync(m); }
                    finally { sendLock.Release(); }
                }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                double px = 200, py = 200, since = 0, speed = double.Parse(Environment.GetEnvironmentVariable("WALK_SPEED") ?? "37");
                int dirX = 0, dirY = 0;
                int k, total = 0;
                while ((k = await s.ReadAsync(b).AsTask().WaitAsync(TimeSpan.FromMinutes(5))) > 0)
                {
                    total += k;
                    byte[] chunk = b[..k];
                    ciphers.Receive.Process(chunk);
                    if (total < 65536 && !(k <= 8 && chunk[0] == 0 && chunk[1] == 0x1c))
                        Log($"instance C->S (decrypted) {k} B: {Convert.ToHexString(chunk[..Math.Min(k, 256)])}");
                    if (InstanceMovementPoe2.ReadDirectionMove(chunk, out _) is { } mv)
                    {
                        double now = clock.Elapsed.TotalSeconds;
                        double len = Math.Sqrt((double)dirX * dirX + (double)dirY * dirY);
                        if (len > 0) { px += dirX / len * speed * (now - since); py += dirY / len * speed * (now - since); }
                        since = now; dirX = mv.Dx; dirY = mv.Dy;
                        byte[] echo = InstanceMovementPoe2.DirectionMoveEcho(0x1000, mv.Dx, mv.Dy, (int)Math.Round(px), (int)Math.Round(py));
                        Log($"instance 0x63 {mv} -> S->C 0x64 at ({Math.Round(px)}, {Math.Round(py)}) {Convert.ToHexString(echo)}");
                        await SendEnc(echo);
                    }
                    if (InstanceProtocolPoe2.ReadAreaGenerated(chunk) is { } hashes)
                    {
                        Log($"instance area generated tile={hashes.Tile} doodad={hashes.Doodad}");
                        string plan = Environment.GetEnvironmentVariable("AFTER_9A") ?? "24,23";
                        foreach (var step in plan.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (step == "inv")
                            {
                                var all = InstanceInventoriesPoe2.PlayerDefaults
                                    .SelectMany(sl => InstanceInventoriesPoe2.AddInventory(InstanceInventoriesPoe2.InventoryId(sl.Row), sl)).ToArray();
                                Log($"instance S->C {InstanceInventoriesPoe2.PlayerDefaults.Length} x 0xdf inventories (encrypted) {all.Length} B");
                                await SendEnc(all);
                                continue;
                            }
                            if (step == "frames")
                            {
                                ushort lenId = Convert.ToUInt16(Environment.GetEnvironmentVariable("FRAME_LEN_ID") ?? "2f5", 16);
                                ushort endId = Convert.ToUInt16(Environment.GetEnvironmentVariable("FRAME_END_ID") ?? "2f4", 16);
                                await SendEnc([(byte)(lenId >> 8), (byte)lenId, 33]);
                                Log($"instance S->C frame length 0x{lenId:x} 33 ms, then 0x{endId:x} every 33 ms");
                                _ = Task.Run(async () =>
                                {
                                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
                                    try { while (await timer.WaitForNextTickAsync()) await SendEnc([(byte)(endId >> 8), (byte)endId]); }
                                    catch (Exception ex) { Log($"frames stopped: {ex.GetType().Name}"); }
                                });
                                continue;
                            }
                            byte[] m = step switch
                            {
                                "24" => InstanceProtocolPoe2.SetLocalPlayer(0x1000),
                                "23" => InstanceProtocolPoe2.EnterArea(0x1000),
                                "2f7" => InstanceProtocolPoe2.CreateObject(0x1000,
                                    InstanceEntities.ObjectTypeHash(Environment.GetEnvironmentVariable("OBJ_TYPE") ?? "Metadata/Characters/Int/IntFour"),
                                    Environment.GetEnvironmentVariable("BLOB_HEX") is { } bh ? Convert.FromHexString(bh)
                                        : new byte[int.Parse(Environment.GetEnvironmentVariable("BLOB_LEN") ?? "2048")]),
                                _ => throw new InvalidOperationException(step),
                            };
                            Log($"instance S->C {step} (encrypted) {m.Length} B {Convert.ToHexString(m[..Math.Min(m.Length, 96)])}");
                            await SendEnc(m);
                        }
                    }
                }
                Log("instance closed");
            }
            catch (Exception ex) { Log($"instance: {ex.GetType().Name}: {ex.Message}"); }
        });
    }
});

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Log($"harness2 listening on 127.0.0.1:{port}, instance on 6112");
while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => Handle(client));
}

async Task Handle(TcpClient client)
{
    var peer = client.Client.RemoteEndPoint;
    Log($"connect {peer}");
    try
    {
        using var owned = client;
        var stream = client.GetStream();
        byte[] hello = new byte[134];
        await stream.ReadExactlyAsync(hello);
        if (!KeyExchange.TryDecode(hello, out var kx, out _)) { Log("not a 0x0002 key exchange"); return; }
        var (reply, ciphers) = new ServerHandshake(keys).Respond(kx!);
        await stream.WriteAsync(reply);
        Log("handshake done");

        async Task Send(params ILoginMessage[] msgs)
        {
            foreach (var m in msgs)
            {
                byte[] wire = LoginMessage.Encode(m);
                Log($"S->C {LoginMessageFormat.Describe(m)} ({wire.Length} B)");
                ciphers.Send.Process(wire);
                await stream.WriteAsync(wire);
            }
        }

        var incoming = new LoginMessageStream(LoginDirection.ClientToServer, LoginDialect.Poe2);
        byte[] buf = new byte[65536];
        int n;
        while ((n = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromMinutes(10))) > 0)
        {
            byte[] chunk = buf[..n];
            ciphers.Receive.Process(chunk);
            bool wasDesynced = incoming.Desynced;
            foreach (var m in incoming.Push(chunk))
            {
                Log($"C->S {Describe(m)}");
                if (m is TokenLoginPoe2)
                    await Send(new LoginResultPoe2(0, 0, "", new byte[32], LoginServerSession.FlagVerified, 0, []),
                               new LeagueList(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), [], false, false));
                else if (m is U32Message { MessageId: 0x18 } req)
                    await Send(new CharacterListPoe2(req.Value,
                        [new CharacterEntryPoe2("Probe", "Standard", 0, 0, 12, 0, 3, 0, 0, 0, 0, 0, 0)],
                        0, 6, 0, 0, [], [], 0, 0));
                else if (m is PlayPoe2)
                    await Send(new InstanceInfo("127.0.0.1", 6112, 0x7f000001, G1Town, 1, keyring.Key(1), new byte[8], 0));
            }
            if (incoming.Desynced)
                Log((wasDesynced ? "" : $"DESYNC: {incoming.Error}\n      ") + $"raw C->S {n} B: {Convert.ToHexString(chunk)}");
        }
        Log($"{peer} closed");
    }
    catch (Exception ex) { Log($"{peer}: {ex.GetType().Name}: {ex.Message}"); }
}
