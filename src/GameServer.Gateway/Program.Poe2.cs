using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

// The instance server for the PoE2 client (Game_CLIENT=poe2; PORTING-POE2.md, wave 13). It needs none of the PoE1
// client, data or saves: it reads PoE2's opening, sends the area, then lets InstanceSessionPoe2 answer the client
// (the player after its 0x9a, echoes of its direction moves) and ends a server frame every 33 ms, without which the
// lockstep client neither runs nor draws its world.
partial class Program
{
    // Clearfell Encampment (WorldAreas G1_town, HASH16), for a ticket Auth does not know (a local test login through
    // the harness has none).
    const ushort Poe2FallbackArea = 0x3f70;

    // What the Gateway enters a PoE2 client with: Auth's area and character for the ticket, with the character's quest
    // flags (QuestFlags Ids) and activated waypoints (WorldAreas Ids).
    sealed record Poe2Ticket(ushort Area, uint Seed, ulong Instance, string Character, CharacterClass Class, int Level,
        IReadOnlyList<string> QuestFlags, IReadOnlyList<string> Waypoints);

    // Game_POE2_TRACE=1 logs every decrypted client chunk (its first 48 bytes), to map new C→S messages.
    static readonly bool Poe2Trace = Environment.GetEnvironmentVariable("Game_POE2_TRACE") == "1";

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
                var ticket = await ResolvePoe2Ticket(remote, opening.Ticket);
                var (area, seed, instance) = (ticket.Area, ticket.Seed, ticket.Instance);

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

                var row = Poe2Classes.Of(ticket.Class);
                // The client derives no life or mana from the attributes: the final values go in the Stats, full pools.
                var sheet = Poe2CharacterStats.For(row, ticket.Level);
                Console.WriteLine($"[gateway] {remote} {ticket.Character}: level {ticket.Level} {ticket.Class} ({row.ObjectType}), "
                    + $"life {sheet.MaximumLife}, mana {sheet.MaximumMana}");
                // The level start (ExperienceLevels: PoE2 0.5.5e's matches PoE1's) needs data/tables/ExperienceLevels.json;
                // without it the experience is 0, the client shows level 1 and kills level nobody up.
                var walkable = Poe2Walkability(remote, area, seed);
                var (exits, objects) = Poe2Layout(remote, area, seed);
                var spawn = Poe2Spawn(remote, opening.Ticket, area, exits, objects, walkable);
                var monsters = Poe2AreaMonsters(remote, area, objects);
                var serverObjects = Poe2AreaServerObjects(remote, area, objects);
                // The quest tables (data/poe2/quests.json): without them the flags are kept but never reach the client,
                // and the World panel knows no waypoint.
                if (Poe2QuestTables.Value.FlagCount == 0)
                    Console.WriteLine($"[gateway] {remote} no {Poe2Quests.DefaultPath}: quest flags not sent, no waypoint travel (scripts/re/poe2/quests.py)");
                Console.WriteLine($"[gateway] {remote} {ticket.Character}: {ticket.QuestFlags.Count} quest flags, waypoints " +
                    (ticket.Waypoints.Count == 0 ? "none" : string.Join(", ", ticket.Waypoints)));
                // The class's starting weapon in the main hand (data/poe2/item-types.json), swung with Default
                // Attack, and the dodge roll; the main hand's stats go in the player's stats too. The weapon is given
                // again at each entry: inventories are not saved.
                var weapon = Poe2Items.StartingWeapon(Poe2ItemTypes.Value, row.Index);
                var session = new InstanceSessionPoe2(new PlayerObjectPoe2(spawn.X, spawn.Y,
                    Life: (uint)sheet.MaximumLife, Mana: (uint)sheet.MaximumMana,
                    Stats: [.. sheet.Stats, .. Poe2StartingSkills.MainHandStats(row, weapon)],
                    Name: ticket.Character, ClassRow: row.Index, Experience: CharacterStats.ExperienceFor(ticket.Level),
                    Skills: Poe2StartingSkills.For(row, weapon), Visible: weapon is null ? null : [Poe2Items.Visible(weapon)]),
                    objectType: row.ObjectType, walkable: walkable, exits: exits, monsters: monsters,
                    levelStarts: CharacterStats.LevelStarts, onLevel: level => _ = SavePoe2Level(remote, opening.Ticket, level),
                    itemTypes: Poe2ItemTypes.Value, weapon: weapon, serverObjects: serverObjects,
                    questFlags: ticket.QuestFlags, waypoints: ticket.Waypoints, quests: Poe2QuestTables.Value, area: Poe2Areas.ByHash(area),
                    onQuestFlags: flags => _ = SavePoe2List(remote, opening.Ticket, "set_quest_flags", flags),
                    onWaypoints: list => _ = SavePoe2List(remote, opening.Ticket, "set_waypoints", list));
                var clock = Stopwatch.StartNew();
                double? leavingSince = null;
                bool transferred = false;
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
                        if (!wasInWorld && session.InWorld)
                            _ = RunPoe2Frames(Send, () => session.Tick(clock.Elapsed.TotalSeconds), closed.Token);
                    }
                    if (session.LeavingTo is not null && leavingSince is null) leavingSince = clock.Elapsed.TotalSeconds;
                    if (leavingSince is { } since && !transferred
                        && (session.LeaveAcknowledged || clock.Elapsed.TotalSeconds - since >= Poe2LeaveTimeout.TotalSeconds))
                    {
                        transferred = true;
                        await TransferPoe2(remote, opening.Ticket, area, session.LeavingTo!.Value, session.LeavingAtWaypoint, keyring, Send);
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
                    if (Poe2Trace) Console.WriteLine($"[gateway] {remote} C→S chunk {Convert.ToHexString(chunk, 0, Math.Min(got, 48))}");
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

    // Auth's area, seed, instance and character for the ticket, or a level 1 Witch named Exile in Clearfell
    // Encampment for a ticket it does not know.
    static async Task<Poe2Ticket> ResolvePoe2Ticket(string remote, uint ticket)
    {
        var resolved = await InstanceControl.CallAsync(new ControlRequest { Op = "resolve", Ticket = ticket });
        if (resolved.Ok && resolved.Area != 0)
            return new(resolved.Area, resolved.Seed == 0 ? 1 : resolved.Seed, resolved.Instance,
                string.IsNullOrEmpty(resolved.Character) ? "Exile" : resolved.Character,
                Enum.IsDefined((CharacterClass)resolved.Class) ? (CharacterClass)resolved.Class : CharacterClass.Witch,
                Math.Clamp(resolved.Level, 1, 100), resolved.QuestFlags ?? [], resolved.Waypoints ?? []);
        Console.WriteLine($"[gateway] {remote} ticket 0x{ticket:x8} not resolved ({resolved.Error ?? "unknown area"}); " +
                          "Clearfell Encampment, seed 1, instance 1");
        return new(Poe2FallbackArea, 1, 1, "Exile", CharacterClass.Witch, 1, [], []);
    }

    // The character's whole quest flag list ("set_quest_flags") or waypoint list ("set_waypoints") goes to Auth's account
    // store as soon as it changes (PORTING-POE2.md, wave 27).
    static async Task SavePoe2List(string remote, uint ticket, string op, IReadOnlyCollection<string> list)
    {
        var saved = await InstanceControl.CallAsync(new ControlRequest { Op = op, Ticket = ticket, Flags = [.. list] });
        string what = op == "set_quest_flags" ? "quest flags" : "waypoints";
        Console.WriteLine(saved.Ok ? $"[gateway] {remote} {list.Count} {what} saved"
            : $"[gateway] {remote} {what} not saved: {saved.Error ?? "no answer"}");
    }

    static readonly Lazy<Poe2Quests> Poe2QuestTables = new(() => Poe2Quests.Load());

    // A kill's new level goes to Auth's account store (and the next tickets): the character comes back at that level's
    // start. The experience into the level is not saved.
    static async Task SavePoe2Level(string remote, uint ticket, int level)
    {
        var saved = await InstanceControl.CallAsync(new ControlRequest { Op = "set_level", Ticket = ticket, Level = level });
        Console.WriteLine(saved.Ok ? $"[gateway] {remote} level {level} saved"
            : $"[gateway] {remote} level {level} not saved: {saved.Error ?? "no answer"}");
    }

    static string Poe2AreaId(ushort area) => Poe2Areas.ByHash(area)?.Id ?? $"0x{area:x4}";

    // The area's walk map, data/walkability/<id>_seed<seed>.bin (scripts/walkmaps/poe2_walkmap.py), or null: the walk
    // estimate then goes through walls.
    static Func<int, int, bool>? Poe2Walkability(string remote, ushort area, uint seed)
    {
        string id = Poe2AreaId(area);
        if (AreaLayouts.LoadCells(id, seed, out int w, out int h) is { } cells)
        {
            Console.WriteLine($"[gateway] {remote} walk map {id}_seed{seed} ({w}x{h})");
            return InstanceSessionPoe2.Walkable(cells, w, h);
        }
        Console.WriteLine($"[gateway] {remote} no walk map data/walkability/{id}_seed{seed}.bin: walks go through walls " +
                          "(capture it with scripts/walkmaps/poe2_walkmap.py)");
        return null;
    }

    // The area's exits and other server objects, from its capture (data/walkability/<id>_seed<seed>.objects.json,
    // scripts/walkmaps/poe2_walkmap.py); none without one.
    static (IReadOnlyList<AreaTransitionObjectPoe2> Exits, IReadOnlyList<Poe2Areas.PlacedObject> Objects) Poe2Layout(
        string remote, ushort hash, uint seed)
    {
        string id = Poe2AreaId(hash);
        if (Poe2Areas.ByHash(hash) is not { } area || Poe2Areas.LoadObjects(id, seed) is not { } objects)
        {
            Console.WriteLine($"[gateway] {remote} no objects for {id}_seed{seed}: no exits (capture them with scripts/walkmaps/poe2_walkmap.py)");
            return ([], []);
        }
        var notes = new List<string>();
        var exits = Poe2Areas.Exits(area, objects, notes);
        foreach (var note in notes) Console.WriteLine($"[gateway] {remote} {id}: {note}");
        Console.WriteLine($"[gateway] {remote} {id}_seed{seed}: {objects.Count} objects, exits " +
            string.Join(", ", exits.Select(e => $"({e.GridX}, {e.GridY}) → {Poe2AreaId(e.DestinationHash16)}")));
        return (exits, objects);
    }

    static readonly Lazy<IReadOnlyDictionary<string, Poe2Monsters.Variety>> Poe2Varieties = new(() => Poe2Monsters.Load());
    static readonly Lazy<IReadOnlyList<Poe2Monsters.LevelStats>> Poe2MonsterLevels = new(() => Poe2Monsters.LoadLevels());
    // Without data/poe2/item-types.json (scripts/re/poe2/item_types.py) monsters drop nothing.
    static readonly Lazy<IReadOnlyDictionary<string, Poe2Items.ItemType>> Poe2ItemTypes = new(() =>
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("Game_POE2_DROP_CHANCE"), out int chance)) Poe2Items.DropChance = chance;
        return Poe2Items.Load();
    });

    // The monsters the area's capture places, of the varieties in data/poe2/monster-varieties.json.
    static IReadOnlyList<Poe2Monster> Poe2AreaMonsters(string remote, ushort hash, IReadOnlyList<Poe2Areas.PlacedObject> objects)
    {
        string id = Poe2AreaId(hash);
        if (Poe2Varieties.Value.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} no {Poe2Monsters.DefaultPath}: no monsters");
            return [];
        }
        var notes = new List<string>();
        byte level = Poe2Areas.ByHash(hash)?.Level ?? 1;
        var monsters = Poe2Monsters.InArea(objects, Poe2Varieties.Value, level, Poe2MonsterLevels.Value, notes);
        foreach (var note in notes) Console.WriteLine($"[gateway] {remote} {id}: {note}");
        Console.WriteLine($"[gateway] {remote} {id}: {monsters.Count} monsters " +
            string.Join(", ", monsters.Select(m => $"{m.VarietyId.Split('/')[^1]} ({m.Object.GridX}, {m.Object.GridY}) life {m.Object.Life}")));
        return monsters;
    }

    static readonly Lazy<IReadOnlyDictionary<string, Poe2ServerObjects.ObjectType>> Poe2ObjectTypes = new(() => Poe2ServerObjects.Load());

    // The NPCs, waypoint and other server objects the area's capture places, of the types in
    // data/poe2/server-objects.json (the session sets their states for the player's quest flags).
    static IReadOnlyList<ServerObjectPoe2> Poe2AreaServerObjects(string remote, ushort hash, IReadOnlyList<Poe2Areas.PlacedObject> objects)
    {
        string id = Poe2AreaId(hash);
        IReadOnlyDictionary<string, Poe2ServerObjects.ObjectType> types;
        try { types = Poe2ObjectTypes.Value; }
        catch (System.Text.Json.JsonException e)
        {
            // An older file (wave 27 changed its talk entries): the area goes without NPCs rather than without a player.
            Console.WriteLine($"[gateway] {remote} {Poe2ServerObjects.DefaultPath} not readable ({e.Message}); no NPCs. " +
                              "Rerun scripts/re/poe2/server_objects.py");
            return [];
        }
        if (types.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} no {Poe2ServerObjects.DefaultPath}: no NPCs");
            return [];
        }
        var notes = new List<string>();
        var placed = Poe2ServerObjects.InArea(objects, types, notes);
        foreach (var note in notes) Console.WriteLine($"[gateway] {remote} {id}: {note}");
        Console.WriteLine($"[gateway] {remote} {id}: {placed.Count} server objects " +
            string.Join(", ", placed.Select(o => $"{o.TypePath.Split('/')[^1]} ({o.GridX}, {o.GridY})")));
        return placed;
    }

    // Where the player appears: by the area's waypoint after a waypoint travel, by the exit back to the area the ticket's
    // transfer left, else by the exit to the area's first connection (its entrance1: the way in), else the middle of
    // Clearfell Encampment.
    static (int X, int Y) Poe2Spawn(string remote, uint ticket, ushort hash, IReadOnlyList<AreaTransitionObjectPoe2> exits,
        IReadOnlyList<Poe2Areas.PlacedObject> objects, Func<int, int, bool>? walkable)
    {
        bool transferred = Poe2Areas.CameFrom.TryRemove(ticket, out var came);
        ushort from = came?.From ?? 0;
        if (!transferred && Poe2SpawnOverride is { } at)
        {
            Console.WriteLine($"[gateway] {remote} logs in at ({at.X}, {at.Y}) (Game_POE2_SPAWN)");
            return at;
        }
        if (came is { ByWaypoint: true } && Poe2Areas.WaypointArrival(objects, walkable) is { } byWaypoint)
        {
            Console.WriteLine($"[gateway] {remote} arrives at ({byWaypoint.X}, {byWaypoint.Y}), by the waypoint");
            return byWaypoint;
        }
        if (!transferred && Poe2Areas.ByHash(hash) is { Town: false, Connections: [var first, ..] } && Poe2Areas.ById(first) is { } way)
            from = way.Hash;
        if (from != 0 && Poe2Areas.Arrival(from, exits, objects, walkable) is { } arrival)
        {
            Console.WriteLine($"[gateway] {remote} arrives at ({arrival.X}, {arrival.Y}), by the exit to {Poe2AreaId(from)}");
            return arrival;
        }
        return InstanceSessionPoe2.G1TownSpawn;
    }

    // A login's cell from env Game_POE2_SPAWN ("x,y"), for testing far from an area's entrance; transfers still arrive
    // by their exit.
    static readonly (int X, int Y)? Poe2SpawnOverride =
        Environment.GetEnvironmentVariable("Game_POE2_SPAWN")?.Split(',') is [var sx, var sy]
            && int.TryParse(sx, out int x) && int.TryParse(sy, out int y) ? (x, y) : null;

    // How long the Gateway waits for the client's C→S 0x355 after a change area before it transfers anyway.
    static readonly TimeSpan Poe2LeaveTimeout = TimeSpan.FromSeconds(2);

    // Auth moves the character to the destination and issues its ticket; 0x29 sends the client there (it reconnects
    // to this Gateway with that ticket).
    static async Task TransferPoe2(string remote, uint ticket, ushort from, ushort area, bool byWaypoint, InstanceKeyring keyring,
        Func<byte[], string?, Task> send)
    {
        var r = await InstanceControl.CallAsync(new ControlRequest { Op = "transfer", Ticket = ticket, Area = area });
        if (!r.Ok)
        {
            Console.WriteLine($"[gateway] {remote} transfer to 0x{area:x4} failed: {r.Error}");
            return;
        }
        Poe2Areas.CameFrom[r.Ticket] = new(from, byWaypoint);
        string host = Environment.GetEnvironmentVariable("Game_GATEWAY_HOST") ?? "127.0.0.1";
        ushort port = ushort.TryParse(Environment.GetEnvironmentVariable("Game_GATEWAY_PORT"), out var gp) ? gp : (ushort)6112;
        await send(InstanceProtocolPoe2.ConnectInstance(host, port, r.Ticket, r.Area, keyring.Key(r.Ticket)),
            $"0x29 connect to instance {r.Instance} of area 0x{r.Area:x4} (seed {r.Seed}) at {host}:{port}, ticket 0x{r.Ticket:x8}");
    }

    // Ends a server frame every 33 ms until the connection closes (the length went with the player, 0x2f5), after the
    // session's work for that frame.
    static async Task RunPoe2Frames(Func<byte[], string?, Task> send, Func<IReadOnlyList<byte[]>> tick, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(InstanceSessionPoe2.FrameMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                foreach (var message in tick()) await send(message, DescribePoe2(message));
                await send(InstanceProtocolPoe2.FrameEnd(), null);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    static string DescribePoe2(byte[] message) => (message[0] << 8 | message[1]) switch
    {
        InstanceProtocolPoe2.LocalPlayerId => "0x24 local player",
        InstanceProtocolPoe2.CreateObjectId => $"0x2f7 object 0x{message[2] << 24 | message[3] << 16 | message[4] << 8 | message[5]:x} ({message.Length} B)",
        InstanceInventoriesPoe2.AddInventoryId => $"0xdf × {InstanceInventoriesPoe2.PlayerDefaults.Length} inventories",
        InstanceProtocolPoe2.EnterAreaId => "0x23 enter area",
        InstanceProtocolPoe2.ChangeAreaId => $"0x2f1 change area to 0x{message[3] << 8 | message[4]:x4}",
        InstanceProtocolPoe2.FrameLengthId => $"0x2f5 frame length {message[2]} ms, then 0x2f4 every frame",
        InstanceMovementPoe2.DirectionMoveEchoId => $"0x64 move echo {Convert.ToHexString(message, 13, message.Length - 13)}",
        InstanceProtocolPoe2.RemoveObjectId => $"0x2fa remove object 0x{message[2] << 24 | message[3] << 16 | message[4] << 8 | message[5]:x}",
        InstanceProgressionPoe2.ComponentUpdateId => $"0x2f9 component update ({message.Length} B)",
        InstanceNpcsPoe2.TalkId => $"0xa3 npc 0x{message[2] << 24 | message[3] << 16 | message[4] << 8 | message[5]:x} mode 0x{message[13]:x2}",
        InstanceNpcsPoe2.ShopWindowId => "0xaf buy window",
        InstanceWaypointsPoe2.WaypointsId => $"0x5a waypoints {Convert.ToHexString(message, 7, InstanceWaypointsPoe2.BitsetLength)}",
        InstanceWaypointsPoe2.OpenUiId => $"0x98 open ui kind {message[3]}",
        var id => $"0x{id:x}",
    };
}
