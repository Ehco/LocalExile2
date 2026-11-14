# Important Notice
It's important to note that LocalExile2 is not an official Grinding Gear Games product, and it is not affiliated with or endorsed by Path of Exile 2 or Grinding Gear Games. LocalExile2 does not in any case sponsor nor support illegal public servers. If you use this project to run an illegal public server and not for testing and learning it is your own personal choice.

This project is for fun and education only. It is intended for local-gameplay with no internet much like MMO Solutions (TrinityCore, AzerothCore, MangOS).
Everything here is made by Claude Opus 5.5. You can pull-request and are encouraged to. I'm running only on two pro subs
and Im broke and unemployed, so help out folks.

[Join Discord](https://discord.gg/pd2Fd5M6Hd)

# To get started [Humans]
- Open up a Frontier Model (such as Claude Opus 5.5) and tell them to set-up this project on your machine. 
- Tell them to read all the Agents/ folder and all the MD files, and then
  [HANDOFF-POE2.md](HANDOFF-POE2.md) — that is the PoE2 handoff, and for this fork it is the file that matters.

# LocalExile2
A local, from-scratch server for the **Path of Exile 2** Steam client. It is the PoE2 fork of
[LocalExile](https://github.com/LocalExile/LocalExile), which serves Path of Exile 1: the crypto, the GGPK/bundle/`.dat`
readers, the account store and most of the test suite are shared, and everything that differs per game sits behind the
`Poe2` suffix (`*Poe2.cs`) or a `Game_CLIENT=poe2` branch. Written in C# (.NET 10) to serve the client's own network
protocol. You play on your own machine against servers running on 127.0.0.1.

This project is not affiliated with or endorsed by Grinding Gear Games. It contains **no game files, keys or game data**.
You need your own installed copy of the client, and everything the servers need is generated locally from it.

## Status — it logs in and plays on the current client

The whole session is served by these servers: Steam login → character screen → camp → walking, and on the build before
the 2026-11-13 hotfix, the rest of the gameplay below as well.

| Stage | State | Seen live |
|---|---|---|
| Steam login: DH key exchange, Salsa20, token login, leagues | works | **2026-11-13 client** |
| Character list, character detail, Play | works | **2026-11-13 client** |
| Instance ticket, `0x14` instance info, Gateway opening, area load | works | **2026-11-13 client** |
| Player object, 121 inventories, lockstep frames (`0x2f5`/`0x2f4`), walking | works | **2026-11-13 client** |
| Camp NPCs, dialogue, quest flags, waypoint travel | works | 0.5.5f (2026-11-10) |
| Skills and the skill bar, Punch, the dodge roll, hits, monster death | works | 0.5.5f |
| Monster chase and swings, player death and resurrect | works | 0.5.5f |
| Town ↔ Clearfell through the exits, experience and levels | works | 0.5.5f |
| Passive tree: allocate, saved, stats in the sheet | works (the refund click does not go through yet) | 0.5.5f |
| Skill gems: the gem window's cut, the gem's skill | works; its ground hits are not seen live yet | 0.5.5f |
| Flasks, inventory moves, equipment drops | written, **not** seen live yet | — |

Login keys are generated from whichever client is installed and `patch` re-detects the layout and verifies what it
wrote before saving, so a Steam hotfix does not silently produce a broken client: the 2026-11-13 build is patched and
plays. The message ids are translated per build (`Poe2WireIds`: everything from `0x2f1` up is 3 higher on 0.5.5f and
that also held for the hotfix). Details: [PORTING-POE2.md](PORTING-POE2.md), waves 34–38.

### What it does not do yet

- **The game data is generated per machine, and a fresh checkout has none of it.** The client still logs in and plays,
  but against an empty world: walks go through walls (no walk map), no monsters, no NPCs, no exits, no quests, no
  drops, no skills from gems. The Gateway names the missing file when you enter an area. Generating it needs a live
  client, Frida and the Steam bundles — [HANDOFF-POE2.md](HANDOFF-POE2.md), "Setting up a machine".
- **Every address is PoE2 0.5.5e's.** Only the message ids are per build; the Frida scripts and the walk-map capture
  still pin 0.5.5e, so they refuse a newer exe (the capture checks the bytes it hooks) until they are re-pointed.
- **Written but not tested live:** flasks, inventory moves, equipment drops, `set_progress`. The handoff lists them
  with the wave that added each.

## Showcase

These recordings are from the shared PoE1 server (the gameplay layer this fork inherits); there is no PoE2 recording in
the repository yet.

https://github.com/user-attachments/assets/e20dd43d-4453-40d4-8f81-eb902a87817f

Items, NPCs, Stash, Waypoints [(youtube)](https://youtu.be/-QDRFEWCmpU)

https://github.com/user-attachments/assets/fead9d6a-b2ce-4908-9e73-ccaaae78a08a

Mobs, Loot, RF, Level/XP, Passive Tree (better quality showcase is on [youtube](https://www.youtube.com/watch?v=rfnCaXQaxBo))

https://github.com/user-attachments/assets/ebb49e70-93aa-464f-a6cd-c6c130dd8c5a

Character select, Lioneye's Watch, taking The Coast exit,
and `/set_speed` in The Coast, all against the local servers.

## Getting Started

**Full guide: [HANDOFF-POE2.md](HANDOFF-POE2.md).** It covers the PoE2 setup, the generated data, the live-test
procedure and a troubleshooting table, written for people and for AI agents.

In short (Windows, .NET 10 SDK, your own PoE2 Steam client):

```powershell
dotnet build GameServer.slnx -c Release

# server keys and a patched client, straight from the installed exe
New-Item -ItemType Directory -Force data\poe2 | Out-Null
dotnet run --project src/GameServer.Auth -c Release -- keygen "<install>\PathOfExileSteam.exe"
dotnet run --project src/GameServer.Auth -c Release -- patch "<install>\PathOfExileSteam.exe" data\poe2\PathOfExileSteam.patched.exe

# the two servers
$env:Game_CLIENT="poe2"; dotnet run --project src/GameServer.Gateway -c Release --no-build   # 6112
$env:Game_CLIENT="poe2"; $env:Game_LOGIN_PORT="<port>"; dotnet run --project src/GameServer.Auth -c Release --no-build

# the client: patched exe in, launched directly (the Steam launcher stub checks GGG's signature)
Copy-Item data\poe2\PathOfExileSteam.patched.exe "<install>\PathOfExileSteam_le2.exe"
$env:SteamAppId = $env:SteamGameId = "2694490"
Start-Process "<install>\PathOfExileSteam_le2.exe" -ArgumentList "--nopatch" -WorkingDirectory "<install>"
```

Two things that cost time if you miss them:

- **`<port>` is not fixed.** It is the port on the `127.0.0.1` entry of `<install>\gateway_list.txt` — that file is what
  the client dials, and each machine has its own. On the machine this was verified on the entry is
  `America;...;127.0.0.1:21262`, so Auth ran with `Game_LOGIN_PORT=21262`; other setups use 21265. Read your own file.
  Point the login hostname at it too: add `127.0.0.1 mel.login.pathofexile2.com` to
  `C:\Windows\System32\drivers\etc\hosts`, and set `gateway_auto_select=false` plus `gateway_id=<that region>` in
  `Documents\My Games\Path of Exile 2\poe2_production_Config.ini`. Undo both to play on the real servers.
- Every Steam login opens one local account (default `steam`) and, on first use, one character (default `Exile`, a
  Witch). They live in `data/saves.db`. Auth and the Gateway share `data/instance-secret.bin`, which the Gateway
  creates on first start.

## Ports

| Project | Role | Port |
|---|---|---|
| `GameServer.Auth` | login server; also generates keys and patches the client to trust them | the `gateway_list.txt` port (21262 or 21265), 127.0.0.1:20495 control |
| `GameServer.Gateway` | instance (game) server | 6112 |
| `GameServer.Patch` | local patching server, so the client never contacts the real patch servers; also edits the GGPK gateway list | 12995, http 8080 |
| `GameServer.Host` + `GameServer.DevClient` | dev account service (register accounts, create characters) | 7000 |
| `GameServer.Core` | protocol, crypto, accounts, instance registry | |
| `GameServer.Data` | GGPK / bundle / `.dat` readers | |
| `GameServer.Storage` | JSON account store | |

The PoE2 side lives in `src/GameServer.Core/Protocol/**/*Poe2.cs` (login dialect, crypt, instance messages, wire ids),
`src/GameServer.Gateway/Program.Poe2.cs` (the instance server) and `src/GameServer.Core/World/Poe2*.cs` (areas, monsters,
items, quests, passives, gems). Data generators are in `scripts/re/poe2/`, the walk-map capture in `scripts/walkmaps/`.

## Tests

```powershell
dotnet test tests/GameServer.Core.Tests -c Release   # 779 pass
dotnet test tests/GameServer.Data.Tests -c Release   # 34 pass
```

`GameServer.Gateway.Tests` needs a LocalExile checkout with `data/tables`; without one its 2538 tests fail before they
start. That is an environment gap, not a code failure.

The rules for working here (line endings, what never goes in git, one wave per commit) are in [AGENTS.md](AGENTS.md).

## Contributing

People and AI agents are both welcome. Start with [AGENTS.md](AGENTS.md): the rules, then
[HANDOFF-POE2.md](HANDOFF-POE2.md) (where the PoE2 port stands, how to rebuild a machine),
[Agents/Status.md](Agents/Status.md) (what works) and [Agents/Roadmap.md](Agents/Roadmap.md) (what to build next).
[Agents/docs/](Agents/docs/README.md) is the PoE1 protocol reference and still describes PoE1's client; PoE2's
differences are in [PORTING-POE2.md](PORTING-POE2.md).
