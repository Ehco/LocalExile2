# PoE2 port — handoff

Read this first if you are picking up the PoE2 work. It covers where things stand, how to rebuild the test setup
from nothing, how to run a live test, and what to do next. The wave-by-wave findings (addresses, message layouts,
live results) are in [PORTING-POE2.md](PORTING-POE2.md). This file points into it and does not repeat it.

Last updated 2026-11-04, after wave 17.

## Where it stands

The real PoE2 Steam client (0.5.5e, patched with our login keys) logs in to our Auth with its Steam ticket and
enters Clearfell Encampment, served by our Gateway. Characters are created on the client's create screen and chosen
on its character screen; the player enters as the saved character (name, class model, level). The player has a HUD
and inventories, the area is drawn, and the player walks. The server's estimate of where the player is stops at the
camp's walls.

| Part | State | Where |
|---|---|---|
| Login crypto (DH, DSA, Salsa20) | Works; ported unchanged from PoE1. The patched client accepts our handshake. | `ClientLayout.Poe2` in `LoginKeys.cs`; waves 1–3 |
| Login messages | Mapped and tested: `0x04`, `0x05`, `0x0e`, `0x10`, `0x11`, `0x12`, `0x13`, `0x14`, `0x15`, `0x16`, `0x18`, `0x1b` (`LoginDialect.Poe2`) | `LoginMessagesPoe2.cs`; waves 4–6, 16 |
| Auth for the PoE2 client | `Game_CLIENT=poe2`: every Steam ticket opens one local account (a default Witch on first use). Characters of PoE2's 12 classes are created in the Standard family of leagues and chosen with Play; Play goes to Clearfell Encampment. | `LoginServerSession.cs`, `Poe2Classes.cs`, `GameServer.Auth/Program.cs`; waves 15–16 |
| Instance opening, encryption, area message | Done | `InstanceOpeningPoe2.cs`, `InstanceProtocolPoe2.cs`, `AreaInstancePoe2.cs`; waves 7–8 |
| Player object, inventories, enter area | Done. The player has the saved name, class (object type) and level, and its class's base life, mana and attributes at that level (no passives or items) | `PlayerObjectPoe2.cs`, `Poe2CharacterStats.cs`, `InstanceInventoriesPoe2.cs`; waves 9–10, 16–17 |
| Lockstep frames (`0x2f5` / `0x2f4`) | Done; the world is drawn | wave 11 |
| Movement (C→S `0x63`, S→C `0x64`) | Done, with a server-side walk estimate | `InstanceMovementPoe2.cs`, `DirectionWalk.cs`; wave 12 |
| Gateway in PoE2 mode | `Game_CLIENT=poe2` | `Program.Poe2.cs`, `InstanceSessionPoe2.cs`; wave 13 |
| Walkability | Clearfell Encampment only; the estimate stops at walls (median 2 cells off live) | `scripts/walkmaps/poe2_walkmap.py`, `AreaLayouts.LoadCells`; wave 14 |

**Tests.** 572/572 pass:

```bash
dotnet test tests/GameServer.Core.Tests -c Release
```

**Git.**
- Branch `poe2-client-static-private`: the wave commits and the handoff commit, on top of `origin/main`
  (`a3da89a`, the upstream wave 1 checkpoint). The handoff commit holds this file and the tools marked "added with
  this handoff" below.
- **Nothing is pushed.** Upstream is https://github.com/Ehco/LocalExile2. It also has a `poe2-port` branch, still at
  `a3da89a`. Pushing, forking or opening a PR is the branch owner's call.

## Repository map for the PoE2 work

- **Protocol (Core, socket-free, unit-tested):**
  - `src/GameServer.Core/Protocol/Login/LoginMessagesPoe2.cs`, `LoginDialect` and the codec table switch in
    `LoginMessages.cs`, and the dialect parameter of `LoginMessageStream.cs`;
  - `src/GameServer.Core/Protocol/Instance/*Poe2.cs`;
  - `src/GameServer.Core/World/DirectionWalk.cs`.
- **Auth:** `src/GameServer.Core/Protocol/Login/LoginServerSession.cs` (answers both dialects; `SteamAccount`,
  `Poe2ClassRow`), `AccountService.LoginTrusted`, and in `src/GameServer.Auth/` the `Poe2Login` settings in
  `Program.cs` and the fixed start area of `InstanceDirectory.AssignStart`.
- **Server:** `src/GameServer.Gateway/Program.Poe2.cs` (branches off before the PoE1 startup) and
  `src/GameServer.Gateway/AreaLayouts.cs` (`LoadCells`, walk maps).
- **Tests:** `tests/GameServer.Core.Tests/*Poe2Tests.cs`, `DirectionWalkTests.cs`.
- **Live-test tools (added with this handoff):**
  - `scripts/dev/poe2-login-harness/`: the PoE2 login stand-in. It answers the handshake, token login, character
    list and Play, and sends the client to the Gateway on 6112.
  - `scripts/re/frida/poe2-track-player.js`: logs the client's own player cell.
  - `scripts/dev/poe2_walk_compare.py`: compares the Gateway's echoes with that log.
- **Reverse-engineering tools:**
  - `scripts/re/poe2/` (`pe2.py`, `fn.py`, `around.py`, `callers.py`, `bytepat.py`; added with this handoff):
    static disassembly of the PoE2 exe. The exe defaults to this machine's `G:\SteamLibrary\...` path; set
    `POE2_EXE` elsewhere.
  - `scripts/re/frida/run.py`: the Frida runner, also used for PoE1.
  - `scripts/walkmaps/poe2-walkmap.js` + `poe2_walkmap.py`: walk-map capture.
  - `scripts/re/poe2_login_keys.py`: checks the login key blobs on any build.
    - It ignores `POE2_EXE`: pass the exe path as its argument.
    - Its default is `C:\Program Files (x86)\Steam\...`.

## Setting up a machine

Once per machine. Everything generated here lives in `data/`, which git ignores. Never commit any of it.

1. **Software.**
   - .NET 10 SDK. On this machine it is user-local: `export DOTNET_ROOT="$USERPROFILE/.dotnet" PATH="$USERPROFILE/.dotnet:$PATH"`.
   - Python 3 with `frida` (17.x), `capstone` (5.x), `numpy` and `pefile`.
     - On this machine they are in the repo's `.venv` (git-ignored; frida 17.25.2, capstone 5.0.7). The Python on
       PATH has neither frida nor capstone.
     - Elsewhere: `python -m venv .venv && .venv/Scripts/pip install frida capstone numpy pefile`.
     - Every `python` command below means `.venv/Scripts/python`.
   - Steam, signed in to an account that owns PoE2, running.
2. **Pin the client build.** Everything is mapped against PoE2 0.5.5e (`poe2_production/tags/0.5.5e`, revision
   326410). The exe SHA-256 is `d46f4c27576122afc41a428eef96b3b4c75592a16827ed8c2204508ab06a455d`. A Steam update
   moves every address in PORTING-POE2.md.
   - `poe2-walkmap.js` checks the bytes of the functions it hooks and refuses to run on another build.
   - `poe2-track-player.js` does not check them, and only attaches to a process named `PathOfExileSteam_le2.exe`.
3. **Server keys and patched client.** Run from the repo root, with `<install>` the PoE2 folder (here
   `G:\SteamLibrary\steamapps\common\Path of Exile 2`):
   ```bash
   dotnet run --project src/GameServer.Auth -- keygen "<install>/PathOfExileSteam.exe"
   ```
   ```bash
   dotnet run --project src/GameServer.Auth -- patch "<install>/PathOfExileSteam.exe" data/poe2/PathOfExileSteam.patched.exe
   ```
   - Create `data/poe2/` first (`mkdir -p data/poe2`). `patch` does not create its output folder, and it reports a
     missing folder as "file not found".
   - `keygen` writes `data/login-keys.json` and refuses to overwrite it. PoE1's Auth uses the same path.
     - On a machine with a PoE1 setup, give PoE2 its own file.
     - Pass it as keygen's 3rd argument, patch's 4th and the harness's 1st.
   - `patch` changes 636 bytes, all in the server static public and the DSA `y`.
   - On this machine both files already exist (copied from the session scratch folders on 2026-11-04).
4. **Instance key secret.** `data/instance-secret.bin` is shared by the Gateway and the harness. The Gateway
   creates it on first start if it is missing. It already exists here.
5. **Redirect the login server.**
   - Add `127.0.0.1 mel.login.pathofexile2.com` to `C:\Windows\System32\drivers\etc\hosts`.
   - In `Documents\My Games\Path of Exile 2\poe2_production_Config.ini`, set `gateway_auto_select=false` and
     `gateway_id=Australia`.
   - The client then dials `127.0.0.1:21265` (the Australia entry of `gateway_list.txt`). That file is not edited.
   - Both are in place on this machine. **Undo them to play on the real servers.**
6. **Walk map of Clearfell Encampment.** Without it the Gateway logs "no walk map" and walks go through walls.
   Capture it once:
   ```bash
   python scripts/walkmaps/poe2_walkmap.py
   ```
   - Start it before the client and let the client load the camp (a normal live test, below).
   - It writes `data/walkability/G1_town_seed1.bin` + `.json` (759 × 690). It exists here.
   - The Gateway loads the walk map when the client connects. During the capture session, walks still go through
     walls; reconnect the client to use the new map.

## Running a live test

From the repo root, in separate terminals or background jobs. Logs go to `logs/`, which git ignores
(`mkdir -p logs` once).

1. **Build** before the first run and after any change. Stop Auth and the Gateway first, or MSB3027 reports a
   locked DLL.
   ```bash
   dotnet build GameServer.slnx -c Release
   ```
2. **Gateway** (port 6112):
   ```bash
   Game_CLIENT=poe2 dotnet run --project src/GameServer.Gateway -c Release --no-build > logs/gateway.log 2>&1
   ```
3. **Auth** (port 21265, the port the redirected client dials):
   ```bash
   Game_CLIENT=poe2 Game_LOGIN_PORT=21265 dotnet run --project src/GameServer.Auth -c Release --no-build > logs/auth.log 2>&1
   ```
   - Every Steam login opens the account `Game_POE2_ACCOUNT` (default `steam`). It is created on first use, with
     the character `Game_POE2_CHARACTER` (default `Exile`, a Witch) when it has none.
   - Accounts and characters go in the save database (`data/saves.db`), shared with a PoE1 setup on the same
     machine: a character name must be unique across both.
   - Auth and the Gateway share `data/instance-secret.bin`, and the Gateway resolves each ticket through Auth's
     control port (20495).
   - **Before wave 15** the login was answered by `scripts/dev/poe2-login-harness` instead. It is kept for
     experiments: run it instead of Auth with
     `INSTANCE_SECRET=data/instance-secret.bin dotnet run --project scripts/dev/poe2-login-harness -- data/login-keys.json 21265 logs/harness.log`.
     Without `INSTANCE_SECRET` it serves its own scripted instance on 6112 and clashes with the Gateway. It
     hard-codes port 6112 and ticket 1, and is not in the solution (`dotnet run` builds it).
4. **Client.** Copy the patched exe into the install folder under the name the Frida scripts expect, and launch it
   directly (PowerShell):
   ```powershell
   $dir = "G:\SteamLibrary\steamapps\common\Path of Exile 2"
   Copy-Item data\poe2\PathOfExileSteam.patched.exe "$dir\PathOfExileSteam_le2.exe"
   $env:SteamAppId = $env:SteamGameId = "2694490"
   Start-Process "$dir\PathOfExileSteam_le2.exe" -ArgumentList "--nopatch" -WorkingDirectory $dir
   ```
   - Do not use the Steam launcher stub (`PathOfExile_x64Steam.exe`): it checks GGG's signature.
   - Without `SteamAppId` / `SteamGameId` the client quits before "Steam is loaded!".
   - The client logs in by itself with the Steam ticket. It may sit on the title screen for a few minutes first.
   - On launch it presses Play by itself on the preselected (last played) character.
   - To get back to the character screen, use "Exit to Log In Screen": the client logs in again by itself.
     "Exit to Character Selection" waits for a Gateway answer that is not mapped (C→S `0x173`).
   - Create → league → class → name creates the character, plays its intro, then enters the game with it.
   - Walk with WASD (W sometimes sent nothing during testing) or by holding the mouse on the ground.
5. **Optional, check the walk estimate.** Start the tracker before step 4.
   - `--follow` makes it wait for the client process.
   - It starts tracking only when the client reads the player object, on entering the area. Attached later, it
     logs nothing.
   - Delete `logs/track.log` first. `--log` appends, and the compare script takes the date from the first entry.
   ```bash
   python scripts/re/frida/run.py PathOfExileSteam_le2.exe --follow --log logs/track.log scripts/re/frida/poe2-track-player.js
   ```
   Walk around, then:
   ```bash
   python scripts/dev/poe2_walk_compare.py logs/gateway.log logs/track.log
   ```
   Wave 14's run gave a median gap of 2 cells.

**After each test:**
- close the client;
- delete `<install>\PathOfExileSteam_le2.exe`;
- stop Auth and the Gateway (or the harness);
- delete `logs/track.log`, and `logs/harness.log` if you used the harness.

**Never commit or share the harness log.** Its raw dumps can carry the live Steam session ticket and machine ids.
Auth redacts the ticket and the machine ids in its own log.

## Rules and pitfalls

- **Line endings.** There is no `.gitattributes`. This machine's git has `core.autocrlf=true`, so the working
  tree is CRLF.
  - Most files are stored CRLF. The PoE2 C# files, `DirectionWalk.cs` and the walk-map scripts are stored LF.
  - `sed -i` silently converts a file to LF. Edit with a tool that keeps the file's endings, or renormalize with
    Python: `s.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')`.
- **Stage explicit paths, never `git add -A`.** It easily picks up a log or a local experiment.
- **No game data in git:** no keys, exes, walk maps, captures or harness logs.
  - Ignored: `data/` (except `data/schema/`), `Game*.exe`, `PathOfExile*.exe`, `*.log`, `logs/`, `*secret*`.
  - Other exes are not ignored. Check `git status` before each commit.
- **Commit messages:** conventional (`feat:`, `fix:`, `docs:`), one wave per commit, with no attribution trailers.
- **Address convention:** VAs at image base `0x140000000`. Frida scripts translate them to the live module base.
- **PoE2 differs from PoE1 where you might not expect it:**
  - the player walks on walk-map values ≥ 1 (PoE1 needs 2);
  - movement is a direction, not a target cell;
  - the walk-map request is skipped in town, so the capture asks the provider itself.

  Check against PORTING-POE2.md before copying PoE1 logic.
- **`git push` hung** on the upstream maintainer's machine; see "Repo/push convention" in PORTING-POE2.md. Ask
  before pushing anything.

## How the reverse engineering was done

This is the method used for waves 4–14, so the next wave does not start from zero:

1. **Static.**
   - Find the message class through its factory / vtable, then read its read/write functions.
   - Tools: `scripts/re/poe2/fn.py <va>` (whole function), `around.py <va>` (a call site), `callers.py <va>`
     (direct callers), `bytepat.py` (byte search).
   - PORTING-POE2.md lists the factory, dispatch switch and handler addresses for each mapped id.
2. **Dynamic.**
   - Frida with `scripts/re/frida/run.py`: hook the function, log its arguments, or call it with a
     `NativeFunction` (that is how the walk grid is read).
   - Guard each hook on the function's first bytes. A wrong address on a new build then refuses to run instead of
     crashing the client.
3. **Live.**
   - Put the new message into the Gateway (or the harness's scripted instance) and watch the client.
   - Record the result in a new wave section of PORTING-POE2.md, with addresses, layout and live result.
   - Add tests for every encoder and decoder in `tests/GameServer.Core.Tests`.

## Next steps

In rough order. Each is a wave of its own.

1. **The rest of the character** (waves 16–17 did the name, class, level, life, mana and attributes):
   - why the HUD's mana starts near 32 whatever pool is sent (perhaps the second mana pool at `+0x330`);
   - the accuracy and spirit stat keys; passives and items;
   - the `0x11` / `0x15` detail blob (31 records of `0x80` bytes, `0x1420e5c10`), so the character screen shows
     the character's gear;
   - the act and premade start states (CharacterStartStates rows 12 on);
   - optionally, the Gateway's fallback for unknown tickets (`ResolvePoe2Ticket`), once the harness is retired.
2. **Exit to Character Selection.** Its C→S `0x173` waits for a Gateway answer.
3. **Unmapped client messages.** C→S `0x1c` (every 100 ms) and `0x62`, and the ids the Gateway drops on entering:
   `0xbc`, `0x2e8`, `0x13d`, `0x14c`.
4. **Walk estimate accuracy.**
   - Map the client's collision / slide code; slanted walls still put the estimate up to ~20 cells off.
   - Make server objects (NPCs, stash) block the walk.
   - Retest in game the latest `DirectionWalk` fixes (the diagonal corner rule, the non-finite distance guard).
     They are covered by unit tests only.
5. **More areas.** Map PoE2 WorldAreas hashes to ids (only `0x3f70` = `G1_town` is known), capture their walk
   maps, and handle area transitions.
6. **Then the long tail:** NPCs, monsters, skills, loot. PoE1's Gateway is the template; re-verify every id
   against PoE2.

## Wave index

| Wave | Content | Commit |
|---|---|---|
| 0–2 | Recon, live handshake capture | upstream (`a3da89a` and earlier) |
| — | Client static private key at SPKI+0x350 | `f4e3be2` |
| 3 | Patched client accepts the local handshake | `1214844` |
| 4 | Login dialect: `0x04`, `0x05`, `0x18` | `2101701` |
| 5 | Character list `0x15`, Play `0x0e` | `34cbf83` |
| 6 | `0x10`, `0x16`, instance assignment `0x14` | `722f7de` |
| 7 | Instance opening | `7204692` |
| 8 | Dispatcher, encryption switch, area message | `3d55c2a` |
| 9 | Local player, enter area, create object | `70e8806` |
| 10 | Player entity, inventories, HUD | `0234a4f` |
| 11 | Lockstep server frames, world drawn | `375cbd1` |
| 12 | Direction movement `0x63` / `0x64` | `efb933a` |
| 13 | Gateway serves the PoE2 client | `a8a2ccc` |
| 14 | Walkability, walls-aware walk estimate | `fb4576c` |
| — | This handoff and its tools | `29d65c5` |
| 15 | Auth logs the PoE2 Steam client in | `6200d53` |
| 16 | Character creation, several characters, the saved character in game | `97628ee` |
| 17 | The class's life, mana and attributes | the commit after `97628ee` |
