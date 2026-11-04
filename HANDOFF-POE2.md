# PoE2 port — handoff

Read this first if you are picking up the PoE2 work. It covers where things stand, how to rebuild the test setup
from nothing, how to run a live test, and what to do next. The wave-by-wave findings (addresses, message layouts,
live results) are in [PORTING-POE2.md](PORTING-POE2.md). This file points into it and does not repeat it.

Last updated 2026-11-04, during wave 27 (NPCs, waypoints, quests): committed as work in progress; see "Wave 27 in progress" below.

## Where it stands

The real PoE2 Steam client (0.5.5e, patched with our login keys) logs in to our Auth with its Steam ticket and
enters Clearfell Encampment, served by our Gateway. Characters are created on the client's create screen and chosen
on its character screen; the player enters as the saved character (name, class model, level). The player has a HUD
and inventories, the area is drawn, and the player walks. The server's estimate of where the player is stops at
walls. Walking into the camp's gate takes the player to Clearfell, and its exit back returns to the camp. Clearfell's
monsters chase the player and hit it. The player has Punch and the dodge roll on its skill bar; Punch on a monster
walks the player to it and punches; the monster loses life and dies, and the kill gives experience (the level is saved).
At 0 life the player dies, gets the death screen and can resurrect at the area's arrival point.

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
| Walkability | Clearfell Encampment and Clearfell (seed 1); the estimate stops at walls (median 2 cells off live) | `scripts/walkmaps/poe2_walkmap.py`, `AreaLayouts.LoadCells`; wave 14 |
| Exits, change of area | Town ↔ Clearfell: exits from a capture of the level generation, `0x2f1` / `0x29` transfer, arrival by the exit back. Exits are taken by walking into them | `AreaTransitionObjectPoe2.cs`, `Poe2Areas.cs`, `poe2-walkmap.js`; wave 19 |
| Monsters | Placed from the captures at the area's level: types (`0x21`), blob, wake zone. No AI, attacks or hits; life is a placeholder | `MonsterObjectPoe2.cs`, `Poe2Monsters.cs`, `scripts/re/poe2/monster_varieties.py`; wave 20 |
| Skills, skill bar, attacks | Punch and the dodge roll in the Actor; the picker's list `0x66`, slot binding `0x5c`, skill use `0x1a5` echoed with `0x1c8` for targeted uses. No damage; slots kept per session only | `SkillsPoe2.cs`, `Poe2StartingSkills.cs`, `InstanceSkillsPoe2.cs`, `scripts/re/poe2/dat.py`; wave 21 |
| Hits, monster death | Punch hits land after the approach (`0x1cf` life, `0x1d3` stats); monster life from DefaultMonsterStats × LifeMultiplier; death at 0. No loot, no action hit `0x1fb` | `InstanceCombatPoe2.cs`, `InstanceSessionPoe2.cs`, `Poe2Monsters.cs`; wave 22 |
| Monster AI, player death | Aggro, chase (MoveTo `0x1e0`, no pathing), swings (`0x1c8`) and hits on the player; the death screen `0x6d`, resurrect C→S `0x6c` (teleport `0x1dd`) | `InstanceSessionPoe2.Monsters.cs`, `Poe2Monsters.AttackOf`; wave 23 |
| Experience, levels | A kill's experience in the Player component (`0x2f9`); a new level raises and refills life and mana and is saved to Auth. The experience into a level is not saved | `InstanceSessionPoe2.Experience.cs`, `InstanceProgressionPoe2.cs`, `scripts/re/poe2/experience_levels.py`; wave 24 |
| Drops, pickup | A kill may drop currency on the ground (`0x2f7` WorldItem, 35 % or `Game_POE2_DROP_CHANCE`); a click walks to it and puts it in the main inventory (`0x2fa`, `0xde`). The inventory is not saved | `InstanceSessionPoe2.Loot.cs`, `Poe2Inventory.cs`, `Poe2Items.cs`, `scripts/re/poe2/item_types.py`; wave 25 |
| Starting weapon | The class's starting weapon in the main hand (`0xde` into Weapon1), drawn on the model, usable (Mods `+0x263` = 7) and swung with Default Attack at the weapon's values; monster damage scaled to 50 % (`Game_POE2_MONSTER_DAMAGE`). The weapon's own attack (Mace Strike) stays greyed; equipment is not saved | `Poe2StartingSkills.cs`, `InstanceSessionPoe2.Loot.cs`, `InstanceItems.cs`, `PlayerObjectPoe2.cs`, `Poe2Items.cs`; wave 26 |
| NPCs, waypoint (wave 27, in progress) | The town's NPCs, waypoint, stash and well are drawn; a click on an NPC opens its dialogue, its options play their talks, Buy opens an empty buy window; a click on the waypoint opens the World panel. No quest flags, no shop stock, no travel | `ServerObjectPoe2.cs`, `Poe2ServerObjects.cs`, `InstanceSessionPoe2.Npcs.cs`, `InstanceNpcsPoe2.cs`, `InstanceWaypointsPoe2.cs`, `scripts/re/poe2/server_objects.py`; wave 27 |

**Tests.** 643/643 pass:

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
7. **Monster varieties.** Without `data/poe2/monster-varieties.json` the Gateway logs "no monsters". Write it from
   the captures (`*.objects.json`) and the Steam bundles (`npm ci` in `Tools/ooz` first):
   ```bash
   python scripts/re/poe2/monster_varieties.py
   ```
   - Rerun it after capturing a new area. It exists here.
8. **Experience levels.** Without `data/tables/ExperienceLevels.json` the player stays level 1 and kills give nothing:
   ```bash
   python scripts/re/poe2/experience_levels.py
   ```
9. **Item types.** Without `data/poe2/item-types.json` monsters drop nothing:
   ```bash
   python scripts/re/poe2/item_types.py
   ```

10. **Server objects** (wave 27). Without `data/poe2/server-objects.json` the town has no NPCs, waypoint or stash.
    Write it from the captures and the Steam bundles (needs `.venv` and the user-local .NET for steamdump):
    ```bash
    python scripts/re/poe2/server_objects.py
    ```
    - It exists here (21 types). Rerun it after capturing a new area.

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

## Wave 27 in progress (NPCs, waypoints, quests)

Started and tested live on 2026-11-04, committed as work in progress (the wave 27 commit below); finishing it amends nothing, add the rest on top. Build passes; the 643 Core
tests pass, but none covers wave 27 yet. PORTING-POE2.md has no wave 27 section yet; the findings are below.

**Works live (Clearfell Encampment):**
- **Server objects.** 19 objects of 21 types are created with `0x2f7` after the monsters (ids
  `ServerObjectId(i) = MonsterId(monster count) + i`; dropped items come after them). Every blob is read exactly by
  the client. The minimap shows Finn, Stash, Una, the well and the waypoint; Una has her quest marker.
  - Only types with InteractionAction are sent (`server_objects.py`): an unknown type crashes the client (type
    lookup `0x141693840`), and DogTrader_Entrance and the townspeople are the client's own.
  - Blob per component: see `ServerObjectPoe2.cs` (Positioned 17 bytes, Animated, InteractionAction u8 0, NPC u8 1
    u8 0xff (reader `0x141d96820`), MinimapIcon u8 2, Stats 7 bytes, Transitionable, StateMachine (count, 0 timers,
    per state compact value + u8 2), HideoutDoodad 0 bytes in town).
  - The town places every variant of an NPC (Una, UnaAfterIronCount, ...). Their StateMachine `visible` state comes
    from the .ot defaults and `per_player_quest_flag_state_<state>_<value> = "Flag"` rules (`Poe2ServerObjects.States`).
- **Talking.** A click on an NPC sends C→S `0x1a5` with action `0xc266` and the NPC as target. The server echoes
  `0x1c8`, walks the estimate to `TalkReach` (6 cells), then sends **S→C `0xa3`**: PoE1's `0x84`. It holds the object
  header (NPC id), u8 mode (0xff = option list) and u8 player slot (the player's `+0x181`, 0 here).
  - Path: world handler `0x1421343c0`, case `0x1421349ed`, then `0x1421065a0(player, id, mode)`. Id 0 ends the
    dialogue; the dialogue state is at `+0x8150`, the NPC id at `+0x8154`.
- **Options.** C→S **`0xa1`** (4 bytes: route, option index = NPCTalk column `_1`) and **`0xa4`** (3 bytes,
  Goodbye), which are PoE1's `0x82` / `0x85`.
  - An option of category BuyItems sends **S→C `0xaf`**, PoE1's `0x8c` buy window, empty.
  - Any other option sends `0xa3` with the index as mode: the talk plays (Introduction, Clearfell, Secrets in the
    Dark tested).
  - `0xa4` after a talk sends `0xa3` 0xff (back to the options); otherwise `0xa3` with id 0 (end).
  - Categories come from `server-objects.json` `talk` ([index, NPCTalkCategory Id]).
- **Waypoint.** A click walks there, then sends **S→C `0x98`** kind 0 (PoE1's `0x7b` OpenUi with the route byte;
  reader `0x142155300`). The World panel opens.

**Missing (wave 27 scope):**
1. **Quest flags.** They are always empty: `Program.Poe2.cs` passes `new HashSet<string>()`. Needed next:
   - storage per character;
   - the PoE2 message that sets them (PoE1's SetQuestFlags; id unknown);
   - running the NPCTalk side effects (`QuestFlags1`, `Script` such as `SetQuestFlag(...)`,
     `triggerer.FinishNPCChat()`);
   - redoing the NPC states when a flag changes.
2. **Waypoint travel.**
   - The panel opens with no unlocked waypoints: PoE1's `0x4c` bitset is not mapped in PoE2.
   - The pick (PoE1's C→S `0x4d`) is not handled.
   - The town waypoint is never activated.
3. **The shop.**
   - `0xaf` sends no stock. Its line list (u16 n + n × u32) and its 0x38-byte tab elements (reader
     `0x142175980`) are unread.
   - No NPCShop data, no buy or sell transaction.
   - SellItems ("Disenchant Items") and QuestReward options do nothing; Music options are untested.
4. **Other objects.** A click on the stash, the guild stash or the healing well does nothing (`IsUsable` is NPCs and
   the waypoint only). RenlyIntro is left out: its ControlZone component is not supported.
5. **Tests and docs.**
   - Unit tests: `ServerObjectPoe2.Blob`, `Poe2ServerObjects.States/InArea/TalkCategories`, `InstanceNpcsPoe2`
     (`Talk`, `ShopWindow`, `ReadChooseTalk`, `IsCloseTalk`), `InstanceWaypointsPoe2.OpenUi`, and the session (click →
     walk → `0xa3`, `0xa1` → talk or shop, `0xa4`, waypoint → `0x98`).
   - A PORTING-POE2.md wave 27 section, from this list.
   - Then the commit (explicit paths; see `git status`: `scripts/re/ot_components.py`,
     `scripts/re/poe2/server_objects.py`, the new and edited `src/GameServer.Core/...Poe2*.cs` /
     `World/Poe2ServerObjects.cs`, `src/GameServer.Gateway/Program.Poe2.cs`, the docs).

**Known issues:**
- `TalkReach` 6 is a guess (NPC.ot `object_size` 5 + 1). The approach is a straight line, so it can stop on a wall.
- The dialogue state (`_talkingTo`) is not cleared when the player walks away or changes area. What the client sends
  then is not checked.
- Every click on the waypoint resends `0x98`.

**Ruled out (do not retry):**
- `0x87` (reader `0x142154aa0`: u8, u32, u8, a 17-byte block): read and ignored, with any mode. Its 6-byte PoE1
  layout made the client stop ("Unable to deserialise packet with pid 62466").
- `0xda`: the trade close (listener `0x140de08d0`). It crashes at `0x140de0a90` on an empty trade list.

**RE notes for what is left:**
- The world handler `0x1421343c0` has special cases `0xa3`, `0x181`, `0x1f2`, `0x1f3`, `0x1fd`, `0x1fe`, `0x2ba`,
  `0x2f7`–`0x302`. Its component table covers `0x1af`–`0x2a6`.
- The Player component (vtable `+0xc0`) takes `0x1ae`–`0x1b2`, `0x26f`, `0x270`.
- Object messages read their header with `0x141f4b800` (u32 id `+0x10`, u32 `+0x14`, u8 `+0x18`, u16 `+0x1a`).
  Its direct callers are the object-message readers.
- `NPCDialogueWindow` is named at `0x1405f3050`. `0x1405f1280` builds a conversation and logs "0-step empty
  conversation".
- `scripts/re/poe2/dat.py NPCTalk` / `NPCTalkCategory` / `NPCs` read the dialogue data. PoE2 categories: 3 Gossip,
  6 Quests, 7 QuestReward, 12 BuyItems, 13 SellItems, 21 Music.
- `Game_POE2_TRACE=1` on the Gateway logs every C→S chunk and S→C id.

## Next steps

In rough order. Each is a wave of its own.

1. **The rest of the character** (waves 16–17 did the name, class, level, life, mana and attributes):
   - why the HUD's mana starts near 32 whatever pool is sent (perhaps the second mana pool at `+0x330`);
   - the accuracy and spirit stat keys; passives and items;
   - items: saved equipment, and the weapon's own basic attack (wave 26 swings the starting weapon with Default
     Attack; Mace Strike stays greyed, PORTING-POE2.md wave 26). The character screen's preview is mapped and encoded (wave 18,
     `CharacterPreviewPoe2`, `LoginServerSession.Poe2Preview`) but has nothing to show yet. The in-game player's
     visible equipment (the Inventories component) is not mapped;
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
5. **More areas.** Wave 19 did the town and Clearfell. For each further Act 1 area:
   - visit it with `poe2_walkmap.py --area <id>` running, started in the area before;
   - add its exit rules to `Poe2Areas` (room fragments → destinations, from its `.tgr` entranceK labels).

   Also open: the exit click, the town gates not drawn, C→S `0x362`.
6. **Combat** (waves 21–24 did the skills, the attack, hits, monster death, monster AI, the player's death and
   experience, waves 25–26 drops and the starting weapon): monster pathing (the chase glides through walls), PoE2's monster damage rule. Then items, gems, passives, NPCs, waypoints and quests. PoE1's
   Gateway is the template; re-verify every id against PoE2.

## Wave index

| Wave | Content | Commit |
|---|---|---|
| 0–2 | Recon, live handshake capture | upstream (`a3da89a` and earlier) |
| — | Client static private key at SPKI+0x350 | `75ec7da` |
| 3 | Patched client accepts the local handshake | `d46d2d1` |
| 4 | Login dialect: `0x04`, `0x05`, `0x18` | `af50ce8` |
| 5 | Character list `0x15`, Play `0x0e` | `24bac9d` |
| 6 | `0x10`, `0x16`, instance assignment `0x14` | `73d8229` |
| 7 | Instance opening | `a7d5a91` |
| 8 | Dispatcher, encryption switch, area message | `ae4526e` |
| 9 | Local player, enter area, create object | `8512ad9` |
| 10 | Player entity, inventories, HUD | `6bcbdb5` |
| 11 | Lockstep server frames, world drawn | `9716c3a` |
| 12 | Direction movement `0x63` / `0x64` | `a127b4d` |
| 13 | Gateway serves the PoE2 client | `f4bdbf4` |
| 14 | Walkability, walls-aware walk estimate | `2ab0c47` |
| — | This handoff and its tools | `4c82166` |
| 15 | Auth logs the PoE2 Steam client in | `8fc43e3` |
| 16 | Character creation, several characters, the saved character in game | `414e331` |
| 17 | The class's life, mana and attributes | `d8e0479` |
| 18 | The character screen's preview blob | `d43585c` |
| 19 | Exits, change of area, Clearfell | `2ccb9c0` |
| 20 | Monsters, the wake zone, pause | `81e63c0` |
| 21 | Skills, the skill bar, a targeted attack | `a23df43` |
| 22 | Hits and monster death | `0c7e0c5` |
| 23 | Monster AI, the player's death | `c4d6390` |
| 24 | Experience | `d078b42` |
| 25 | Monster drops, the pickup | `199bdc1` |
| 26 | The starting weapon | `13f1f9a` |
| 27 | NPCs, dialogue, waypoint panel (in progress) | the commit after `13f1f9a` |
