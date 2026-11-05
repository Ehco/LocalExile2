# LocalExile2 — porting the LocalExile PoE1 server emulator to Path of Exile 2

Goal: a local private server for the **Path of Exile 2** Steam client
(`PathOfExileSteam.exe`, installed at
`C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\` at recon time; from wave 3 on,
`G:\SteamLibrary\steamapps\common\Path of Exile 2\`), keeping the
real client as the renderer — the approach proven by
[LocalExile](https://github.com/LocalExile/LocalExile) for the PoE 3.29.3b client.

## Status snapshot (wave 0 — recon complete, baseline validated)

| Item | State |
|---|---|
| LocalExile baseline clone | done (depth 1, upstream main) |
| .NET 10 SDK | installed via winget |
| `dotnet build GameServer.slnx -c Release` | **passes** |
| `dotnet test tests/GameServer.Core.Tests` | **519/519 pass** |
| PoE2 client recon (login crypto) | **done — see findings** |
| PoE2 client recon (DH groups) | **done** — groups unchanged (finding 5) |
| PoE2 client recon (login key blobs) | **done** — all three mapped (findings 4, 6, 7) |
| Login handshake vs patched real client | **done** — client accepts our signed `0x0002`, C→S decrypts (wave 3) |
| PoE2 login message set | in progress — `0x04`/`0x05`/`0x0e`/`0x10`/`0x11`/`0x12`/`0x13`/`0x14`/`0x15`/`0x16`/`0x18`/`0x1b` mapped; Play reaches the instance server (wave 6); characters are created and chosen (wave 16) |
| PoE2 gateway/instance protocol | in progress — the client enters Clearfell Encampment with a player entity, inventories and the in-game HUD, the area and the player are drawn once server frames run (wave 11), and the player walks when the server echoes its direction moves (wave 12); the Gateway serves all of it with `Game_CLIENT=poe2` (wave 13), and its walk estimate stops at the camp's walls (wave 14); Auth logs the Steam client in and sends it there (wave 15), and the player is the saved character: name, class model and level (wave 16), with its class's life, mana and attributes (wave 17) |

## Recon findings (2026-10-30, client build of 2026-10-08 install)

1. **Gateway list is a plain file.** `gateway_list.txt` (UTF-16) ships in the
   install dir: `<region>;...;<login host>:<port>;<speedtest host>;false`,
   e.g. `dal.login.pathofexile2.com:21262`. Unlike PoE1 there is no baked-in
   ggpk gateway list to patch — redirect via this file / hosts + DNS.
   Patch server (also plain strings in the exe): `us.patch.pathofexile2.com:13060`.
2. **Login crypto is the same family.** CryptoPP present; `Salsa20`, DH and
   DSA/SHA-1 strings all in the binary.
3. **The DSA verify key blob is findable and structurally identical.** The
   X.509 SubjectPublicKeyInfo DER prefix
   `308203423082023506072a8648ce380401` occurs **exactly once**
   (file offset `0x2f0b650` in the shipping exe) and parses as
   DSA p=2048 / q=224 — the same size class as PoE1, so
   `ClientImage.VerifyKeyLength == 0x346` still holds and
   `ClientImage.FindUnique` works **unchanged**.
4. **Only the server static public moved.** PoE1 layout (see
   `src/GameServer.Core/Protocol/Login/LoginKeys.cs`):
   verify key `+0x000`, client static private `+0x350` (384 B),
   server static public `+0x4d0` (384 B). In PoE2 the client static private
   is still at `+0x350`, but it is a short exponent: 350 zero bytes, then
   34 bytes (`0a400f5d…9d3a`, 268 bits). `+0x4d0` holds unrelated .rdata
   pointers (input-device name strings); the server static public is at
   `-0x180` (finding 6). The DER-prefix locator ports as-is.
5. **The DH groups are intact — as CryptoPP hex-string literals.** A raw-byte
   search misses them; a numeric-literal scan finds four `0x…` string constants
   clustered in .rdata (`0x350f330`–`0x350f860`): static P `cd8fd4b7…` (768 hex
   chars), static Q `66c7ea5b…`, ephemeral Q `62e8fffb…`, ephemeral P
   `c5d1fff6…` — byte-identical to `LoginGroups.cs`. **LoginGroups.cs ports
   unchanged.** (`scripts/re/find_private_key.py` maps the whole string
   neighborhood: `RSA`, `PublicElement`, `SubgroupOrder…`.)
6. **Server static public located and verified.** The 384-byte blob at file
   `0x2f0b4d0` (VA `0x142f0c6d0`) — immediately *before* the SPKI, vs PoE1's
   `+0x4d0` after — satisfies `y^q ≡ 1 (mod P)` in the static group
   (`scripts/recon/scan_dh_groups.py` + inline verification).
7. **Login crypto code path traced** (rip-relative xrefs to the key region,
   function bounds from `.pdata`, capstone disassembly), same shape as PoE1's
   [re/008](Agents/docs/re/008-login-handshake.md):
   - `0x1420e7410` (login connect) is the only code that touches the key
     blobs. It copies the 0x346-byte SPKI into a string, then calls the
     crypto-object ctor `0x14219dd90(obj /*0x780 B*/, SPKI+0x350,
     SPKI-0x180, &spki)`, then `GenerateEphemeralKeyPair` (vtable `+0x90` of
     `obj+0x2b0`) and writes msg `0x0002`.
   - The ctor parses the group strings into `obj+0xb0` (static) and
     `obj+0x1b0` (ephemeral), and stores the raw pointers: client static
     private at `obj+0x750`, server static public at `obj+0x758`.
   - `0x14219ef90` calls `DH2::Agree` (vtable `+0x98` of `obj+0x2b0`) with
     `[obj+0x750]` as the static private, `[obj+0x758]` as the static other
     public and the server's ephemeral from the message, `validate = 1`.
     Crypto++ reads a private key as `PrivateKeyLength()` = byte length of q
     (384) big-endian bytes, so the leading zeros are padding.
   - The 268-bit size is exactly Crypto++'s exponent cap,
     `2·DiscreteLogWorkFactor(3072) = 268` bits, which a key made by
     `GeneratePrivateKey` on this group has. The blob is not a hex or decimal
     string literal (the only long numeric literals are the four group
     strings), and nothing writes it at runtime (it is in .rdata).
   `ClientLayout.Poe2` now maps it, so `GameServer.Auth probe`/`keygen`/`patch`
   work on the PoE2 exe. `scripts/re/poe2_login_keys.py` reproduces the blob
   checks and the xref list on any build.

**Implication:** the login-stack port is now close to mechanical: identical
crypto family, identical DH groups, identical key blobs (the server static
public moved), DER-prefix locator unchanged. The handshake against the
patched real client and the Salsa20 key-material layout are now confirmed
(wave 3); the open item is the per-version packet/message set.

## Next waves (in order)

1. **DH group recovery.** Disassemble `PathOfExileSteam.exe` (Ghidra 12.1.x +
   JDK, or objdump from msys64 mingw64) around the CryptoPP DH2 / login init;
   extract the static (3072-bit) and ephemeral (1024-bit) group constants and
   verify `p = 2q+1`. Update `LoginGroups.cs` (or load from JSON per client
   version) and the `ClientImage` offsets from wave-0 findings.
2. **Client patcher bring-up.** Run `GameServer.Auth keygen/patch` against the
   PoE2 exe (expects a patched `ClientImage` layout); confirm the client
   accepts a locally signed login handshake.
3. **Redirection.** Point `gateway_list.txt` login entry at 127.0.0.1 (ports
   configurable); stand up the patch-server stub so the client never calls
   `us.patch.pathofexile2.com:13060`.
4. **Login flow.** Account creation + character list. Expect PoE2-specific
   message set changes (new message ids; PoE2 has 12 character classes and
   different league/leagueless structures).
5. **Gateway (instance server) bring-up.** Zone load, player object, movement,
   visibility. PoE1's gateway message catalog is the template; each id must be
   re-verified against PoE2 traffic (sniff live sessions with a Salsa-constant
   memory scan, cf. the exileSniffer approach).
6. **Data layer.** PoE2 uses the same `Bundles2`/`.index.bin` container family;
   `GameServer.Data` readers need PoE2 `.dat` schema updates. (There is already
   local tooling at `C:\PoEPeek\PoeBundlePeek` that reads PoE2 bundles.)
7. **Simulation.** Hideout/town instance flow first (fixed layouts, seed 1),
   then monsters, loot, skills — the long tail.

## Environment notes

- Build: `dotnet build GameServer.slnx -c Release`
  (.NET 10 SDK; on the wave 3+ machine it is user-local in `~/.dotnet`, see HANDOFF-POE2.md).
- Tests: `dotnet test tests/GameServer.Core.Tests -c Release`.
- PoE2 install: see header. `logs/Client.txt` confirms the account plays on the
  live servers — when sniffing, avoid touching the live account; prefer a
  throwaway login.
- Client version pinning: record the exe SHA-256
  (`d46f4c27576122afc41a428eef96b3b4c75592a16827ed8c2204508ab06a455d` at recon
  time) and treat every Steam update as a protocol change.

## Repo/push convention

`git push` hangs on this machine — publish via the GitHub REST Git Data API
(blobs → tree → commit → ref) using the PAT for user `Ehco` in
`C:\Users\Administrator\.git-credentials`. Never print the token.

## Wave 1.5 — live capture against the real client (2026-10-31)

`scripts/re/capture_login_session.py` + `login_capture_stub2.py` captured the
real client's first login message by redirecting `gateway_list.txt` (UTF-16LE,
gateway named `America` to match `gateway_id=America` in
`poe2_production_Config.ini`) to a local stub:

- **The client connects at the login screen and speaks FIRST**, before any
  credentials: C→S `0002 | 0080 | <128 B ephemeral DH public> | 0000` — 134
  bytes, byte-for-byte the same framing as PoE1's login handshake.
- The static DH half is still in play (no static public on the wire), so the
  client static private key must be in the binary — raw 384-byte blob at
  SPKI+0x350 (recon finding 7).
- The stub's dummy S→C reply (right framing, wrong key/sig) was accepted at
  the transport level; the client stays connected awaiting a valid DSA
  signature (`data/login-capture2.txt` has the full dump).

**Consequence:** `LoginKeys`/`LoginHandshake`/`LoginCrypto` port as-is once the
PoE2 `ClientImage` offsets are derived — the wire format of the first exchange
is unchanged.

## Wave 2 — live handshake exchange captured (2026-10-31, post-restart run pending)

`login_capture_stub2.py` (server-speaks-first) + the LOG IN click:

- The PoE2 client **connects to the gateway at the login screen** (no click
  needed) and immediately sends its 134-byte C→S `0x0002` — framing identical
  to PoE1 (`0002 | 0080 | <128 B ephemeral DH public> | 0000`). **Client
  speaks first**, same as PoE1.
- The stub's dummy S→C `0x0002` reply (right framing, dummy key+sig) got
  **no response** — the client stayed connected ~90 s, sent nothing more, then
  the connection dropped on idle timeout. So either the S→C framing differs
  in PoE2 or the client silently discarded the unverifiable signature. The
  code path in recon finding 7 matches PoE1's, where a failed DSA check
  ("General Rhoa Failure #3") ends the handshake without a reply, so silent
  discard is the expected result.

**Consequence:** the login handshake is close to PoE1's; the port's next
concrete deliverable is a *real* S→C `0x0002`. Both inputs are now mapped
(`ClientLayout.Poe2`): `GameServer.Auth keygen` reads the client static
private and the DSA domain, and `patch` swaps the verify key `y` and the
server static public at SPKI-0x180. Wave 3 does exactly that.

## Wave 3 — handshake accepted by the patched client (2026-11-03)

Setup: `GameServer.Auth keygen` + `patch` on the PoE2 exe (636 bytes differ, all inside
the server static public at SPKI-0x180 and the DSA `y`), a small harness that
answers the client's `0x0002` with the repo's unchanged `ServerHandshake` and
decrypts everything the client sends next with the resulting ciphers.
Redirect: hosts maps `mel.login.pathofexile2.com` to 127.0.0.1 and
`gateway_id=Australia`, so the client dials port 21265 (no `gateway_list.txt`
edit needed).

Launching the patched copy: the Steam launcher stub (`PathOfExile_x64Steam.exe`)
checks GGG's Authenticode signature, so run the patched copy directly from the
install dir with `--nopatch`. Without `SteamAppId=2694490` (and
`SteamGameId`) in the environment the client quits before
`Steam is loaded!`; with them it starts normally.

Result (client build `d46f4c27…`, `poe2_production/tags/0.5.5e`, revision 326410):

- The client connects at the login screen, sends its 134-byte `0x0002`, and
  **accepts the locally signed S→C `0x0002`** (190 B) — it carries on instead
  of going silent as it did with the dummy reply in wave 2.
- **C→S decrypts with the PoE1 key derivation unchanged.** The proof is the
  heartbeat: after the first message the client sends a 2-byte message every
  10 s, and every one decrypts to `0001` (the field-less `0x01` that PoE1
  already lists, probably a keepalive). A wrong key or IV split would give
  random bytes.
- **First message is `0x05` (token login, the Steam path)**, 229 bytes with a
  159-byte token. Wave 4 maps its PoE2 layout from the writer.
- No credentials are needed: on Steam the client logs in with the ticket by
  itself.

Don't commit the harness log: the token is a live Steam session ticket and
ids A/B identify the machine.

## Wave 4 — login accepted, client reaches character select (2026-11-03)

Static mapping of the PoE2 login message classes (`PathOfExileSteam.exe`,
same build), then a live run that answers the token login.

- **S→C factory `0x14214fbe0`** (ids 1–0x47) has the same id set as PoE1's
  `0x14219c350`, plus `0x16` (class `0x143509e58`, layout not mapped). The
  dispatch is the switch at `0x1420e82b0` (on `msg+8`, ids 2–0x47).
- **S→C `0x04` gained a leading u32** (vtable `0x143509ed8`, read
  `0x14214dbe0`): `u32, u16 result, wstr, bytes[32], u8 flags0, u8 flags1,
  blob`. Dispatch case 4 tests the u16 at `+0x14` (0 = state 3, `+0xee0`) and on
  success appends a 0x78-byte record to the vector at `login+0xef0` holding
  the u32, the blob and the flag bits (flags0 bits 0–7, flags1 bits 0–1).
- **C→S `0x05` keeps PoE1's fields but splits the token** (vtable
  `0x143506370`, write `0x1420e3670`, sender `0x1420e7f80`): `u16 token
  length, u8 realm|0x40, bytes[32] id B, bytes[32] id A, u8, token bytes`.
  The capture parses cleanly: realm byte `0x40` (realm 0), last u8 0,
  159-byte token. (Wave 3's guess of "token first" was wrong: only the length
  comes first.)
- **C→S `0x18` (character list request) gained a u32** (sender
  `0x140f03f10`, u32 class `0x143506270`): it echoes the `0x04` u32 from the
  record at `login+0xef0`, and is sent only in state 3 with that record
  present.
- Unchanged and checked: `0x06`/`0x07` (writer `0x1420e3240`), `0x03` still
  sends version 2 (sender `0x1420e7c70`), wstr reader `0x141cfb540`, blob
  reader `0x14087cd70`, and `0x1b`'s outer shape (`raw64, vec32, u8`, so an
  empty league list works).

Code: `LoginDialect.Poe2` in `LoginCodec`/`LoginMessageStream` swaps in
`LoginResultPoe2`, `TokenLoginPoe2` and the u32 `0x18`
(`LoginMessagesPoe2.cs`, tests in `LoginMessagePoe2Tests.cs`).

Live run: the harness decoded the client's `0x05` with the PoE2 dialect and
answered `0x04` (u32 0, result 0, flags0 `0x20`) plus an empty `0x1b`. The
client accepted both, left the login screen for the character-select scene
(no UI yet) and sent `0018 00000000`, then kept up the 10 s keepalive while it
waited for the character list.

Wave 5 maps the character list the client waits for here.

## Wave 5 — character list and Play (2026-11-03)

- **S→C `0x15` (character list)** (vtable `0x143509f98`, write `0x14214df90`,
  read `0x14214e210`): `u32, u8 count + entries, u8 selected, u8 slots, u16,
  u16, blob, compact count + (u16, compact int) pairs, u32, u32`. PoE1 was
  `u8 count + entries, u8, u8, blob`.
  - The leading u32 must equal the `0x04` u32: dispatch `0x1420e86b4` looks
    the login record up by it and drops the list otherwise.
  - When the selected index points at an entry, the blob is parsed into 31
    records of 0x80 bytes (PoE1: 17), the equipment preview (wave 18), and
    the blob plus the remaining fields go to the listeners at `login+0xd08`.
    The last u32 is a hash-map key (a missing key gives an empty result), so
    zeros are safe. An empty blob is fine.
  - The compact ints are the instance protocol's: PoE2's signed encoder
    `0x141cfc050` matches `InstanceProtocol.CompactInt` byte for byte, and
    `LoginReader` now reads both forms.
- **Entry (0x58 bytes, write `0x142716570`)**: PoE1's entry plus a u8 (wire
  position 4) and two u16s (positions 10–11): `wstr name, wstr league, u8,
  u8 (new), u8 level, u32, u8 class, u16, u16, u16 (new), u16 (new), u8, u8`.
  Live: level 12 with class byte 3 rendered as **"Probe — Level 12 Duelist —
  Hideout — Standard League"** with Play / Create / Delete enabled. The client
  takes the max level into `login+0xee8`, as PoE1 does.
- **C→S `0x0e` (Play)** (vtable `0x143506470`, write `0x1420e47e0`, sender
  `0x1420e9ff0`): `u8 flags, compact count, count × (u32, u32)`. Flags are
  the realm byte (`0x143a0c068`) in bits 0–6 and a bool argument in bit 7.
  PoE1 sent `u8 realm, u32 index`. Captured after Play: `000E 00 01
  00000000 00000000` (one pair (0, 0)); the client then showed "Contacting
  Server..." — waiting for the instance assignment `0x14`, as in PoE1.
- **C→S `0x10` is new and not mapped.** The client sends it by itself right
  after `0x15`: `0010 00 00000000`. Its writer sets the id indirectly; the
  id-immediate scans that found the others found nothing.
- Seen while searching, not in the codec yet: `0x0c` (delete, sender
  `0x140ed80a0`) now builds `wstr, u8, u32` (PoE1: `wstr, u8`), and the
  classes used for `0x2a`/`0x2b` and `0x3a`/`0x42` write a u32 after their
  u8.

Code: `CharacterListPoe2`, `CharacterEntryPoe2`, `PlayPoe2` and
`LoginReader.CompactUInt/CompactInt`, in the PoE2 dialect.

Next: map C→S `0x10`, then PoE2's S→C `0x14` (instance assignment, read
`0x14214ee60`), which Play waits for. Then wire the PoE2 dialect into
`LoginServerSession`/`GameServer.Auth` (which local account a Steam token
maps to is an open design choice).

## Wave 6 — 0x10, 0x16 and the instance assignment 0x14 (2026-11-03)

- **C→S `0x10`** (vtable `0x143506350`, write `0x1420e4b70`): `u32 record id,
  u8 index` (wave 5 read the capture the wrong way round; the bytes are all
  zero either way).
  - Path: the character screen (`0x140f08950`) queues (record id, key of its
    per-character cache at `screen+0x2b8`) through `0x1420ea450` when that
    character's cached entry is missing or flagged dirty (`+0x3d`). The login
    update `0x1420e6d40` pops the queue on a timer and sends `0x10` only if
    the login record with that id (the `0x04` u32) exists.
  - The id was set through a register (`mov ebx, 0x10`), which is why the
    id-immediate scans missed it. Found by listing message vtables whose
    writer emits exactly 5 field bytes.
  - Live: `0010 00000000 00` now decodes as `CharacterDetailRequestPoe2`.
    The client goes on without an answer.
- **S→C `0x16`** (vtable `0x143509e58`, write `0x14214ddd0`, read
  `0x14214de80`): `u32, u16, u16`. Dispatch `0x1420e8f26` hands all three to
  the listeners at `login+0x288`. New in PoE2 and probably the answer to
  `0x10`, which is not verified. It is in the codec as the shape-named
  `U32U16U16`.
- **S→C `0x14`** (read `0x14214ee60`, dispatch case at `0x1420e9078`) is
  unchanged from PoE1's `InstanceInfo`: `str host, u16 port, u32 address,
  u16 area hash, u32 ticket, bytes[64] key, bytes[8], u8`. The area hash is
  looked up through `0x1423e3f10`.
  - Live: Play was answered with `InstanceInfo("127.0.0.1", 6112,
    0x7f000001, 0x3f70, ticket 1, key, zeros, 0)`, as
    `LoginServerSession.InstanceAssignment` builds it. The client showed the
    **Clearfell Encampment** loading screen and connected to
    `127.0.0.1:6112`.
  - `0x3f70` is the HASH16 of `G1_town` in PoE2's
    `data/balance/worldareas.datc64`. The early columns (Id, Name, Act,
    IsTown, HasWaypoint, Connections, AreaLevel, HASH16) still parse with
    `scripts/re/worldareas.py`.
- **Instance opening (C→S, plaintext)**, captured:
  `000F 00000001 000E 12BEA056029C953E6AE9B7C8 7F000001 00000000 04`, which
  is 29 bytes. PoE1's `InstanceProtocol.TryReadOpening` expects 30.
  - The ticket and address are the ones `0x14` gave, in PoE1's positions.
  - The tail is a single byte `0x04` where PoE1 sent realm `0x40` and a flag
    byte.
  - The client then waits on the loading screen for the server.

Tooling notes:

- The Steam install has loose `Bundles2/*.bundle.bin` files and no
  `Content.ggpk`, so `GameFileSystem.Open`/`ggdump` can't read it. Parsing
  `Bundles2/_.index.bin` with `GameIndex.Parse` and reading each bundle from
  disk works, with bundle names relative to the install root.
- PoE2's index needs the Node `ooz-wasm` decoder (`npm ci` in `Tools/ooz`);
  `ManagedOodleDecoder` fails on it. (`Tools/ooz/node_modules` is ignored by
  the `node_modules/` rule in `.gitignore`.)

Code: `CharacterDetailRequestPoe2` (`0x10`) and `U32U16U16` (`0x16`) in the
PoE2 dialect; `0x14` reuses `InstanceInfo`.

Next: the PoE2 instance opening (29 bytes; what the last byte means), then
the instance server's reply (done in wave 7). In parallel, wire the PoE2 dialect into
`LoginServerSession`/`GameServer.Auth`; which local account a Steam token
maps to is an open design choice.

## Wave 7 — the instance opening (2026-11-03)

- **Where it is sent from.** The instance tick `0x142120cf0` logs "Connect
  time to instance server" and then calls `0x14211fac0(this, byte
  [0x14458c75d])`, PoE2's version of PoE1's `0x142148500`. That function
  sets the state (`+0x2270`) to 2, sends `0x0F` then `0x0E`, and flushes with
  `0x142198110`.
- **`0x0F`** (vtable `0x1434e5c08`): u32 ticket, from the 0x14 info at
  `+0x34`. Unchanged from PoE1.
- **`0x0E`** (vtable `0x1434e5798`, size 0x38, write `0x141f373a0`, read
  `0x141f37490`):

  | Field | Size | Source |
  |---|---|---|
  | digest | bytes[8] | hash over the 8-byte constant at `0x142f0bc40`, the 0x14 `Data8` (info `+0x78`) and the time |
  | time | u32 | `0x142c28850`, Unix seconds |
  | address | u32 | the 0x14 info at `+0x30` |
  | u32 | u32 | always 0 |
  | packed | u8 | bits 0–3: the function's byte argument (global `0x14458c75d`, 4 live); bits 4, 5, 6, 7: bools at `+0x2c`, `+0x2d`, `+0x2f`, `+0x2e` (all 0) |

  - That is 29 bytes with `0x0F`. PoE1 sent `bytes[12], u32, u32, u8 realm
    0x40, u8 flag`.
  - PoE2 dropped the realm byte and folded the flag into the low nibble.
  - The capture fits exactly: time `0x6AE9B7C8` is 3 s after the login
    server's `0x1b` time. So PoE1's "12-byte digest" was probably also 8
    bytes of hash plus the time (not re-checked on PoE1).
- **Live, 2026-11-03** (two runs): `InstanceOpeningPoe2.TryRead` parsed both
  openings. Each had ticket 1, the address from 0x14, flag 4, all bools 0,
  and the client clock in the time field.
- **The server's answer is not verified.**
  - We answered with PoE1's plaintext `00 11`. The client kept the connection
    open and sent nothing on it. PoE1's client also waits there for the
    area message.
  - We then sent PoE1's `0x1d` (`AreaInstance` for `0x3f70`), encrypted with
    `InstanceKeyring.Ciphers(ticket)`. The client logged nothing after
    "Connect time to instance server" (PoE1 logs "Generating level …") and
    exited 33 s later. Both sockets closed and no dump was written.
  - So one or more of these is wrong for PoE2: `0x11` as the encryption
    switch, the instance keying, or `0x1d`'s id or layout.

Code: `InstanceOpeningPoe2` (`TryRead`, `Length` = 29) next to
`InstanceProtocol`; `InstanceOpeningPoe2Tests`.

Next (done in wave 8): map PoE2's instance S→C dispatcher (PoE1: the vtable at `this+8`,
slot 0), especially the `0x11` case (which Salsa20 pair it installs, and
from which key bytes) and the area message (PoE1 `0x1d`). Then retry the
probe.

## Wave 8 — instance dispatcher, encryption switch and area message (2026-11-03)

- **Dispatcher.** The instance S→C dispatcher is `0x1421220f0`, found through
  its log strings. It reads the id from `msg+8`; ids above `0x12f` take a
  separate path. Otherwise `id - 0xd` indexes a byte table at `0x14212593c`
  and then dword targets at `0x1421258bc`.
  - Ids with their own case: `0x0d, 0x11, 0x18–0x1a, 0x1d–0x25, 0x28, 0x29,
    0x5a, 0x6b, 0x6d, 0xa5, 0xa6, 0xb2–0xb4, 0xc6, 0xc7, 0xca, 0xce, 0xdc,
    0xeb, 0x100`. The other 213 ids go to the default.
- **`0x11`** (case `0x1421221be`) switches to encryption as in PoE1, with
  two changes.
  - **It now carries one byte.** After a bare `00 11` the client took our
    first encrypted byte as that field and decrypted from the second.
    - It then reported "Unable to deserialise packet with pid 6628. Last PID
      was 17" (and pid 40109 on a second run, with another key).
    - Re-encrypting our bytes offline with the logged keys reproduces both
      pids exactly at that one-byte offset, and no other offset or key
      variant matches.
    - We send `00 11 00`. The case itself only uses constants.
  - **The keying is unchanged.** The cipher pair comes from `0x14219f500(info
    + 0x38, flag 1)`, the same constructor and flag the login connection
    uses (`0x1420e8388`). `info + 0x38` is a raw copy of the 0x14 key: login
    dispatch `0x1420e91dd` copies `msg+0x3c` to it, and Data8 follows at
    `+0x78`. So `InstanceKeyring.Ciphers(ticket)` works as is.
  - The side channel (`0x14212ac80` → `0x1421c52f0`) is PoE1's HNC: the key
    XORed with the 64 bytes at `0x142f10980`, then the same six byte swaps.
- **The area message moved from `0x1d` to `0x1e`** (class `0x1435079d8`,
  write `0x14211cb00`, read `0x14211d340`, case `0x14212269f`, which logs
  "Client-Safe Instance ID" and "Generating level").
  - PoE2's `0x1d` handler compares a u16 at `+0x18` with clocks, so it is
    probably PoE1's `0x1c` pong, shifted by one.
  - **Layout:** PoE1's `AreaInstance` fields keep their wire order, with
    struct offsets 0xc higher. New fields:
    - a u8 (`+0x10`) first;
    - a u32 (`+0x24`) after the old `B28`;
    - a u16 (`+0x1e`) when flag `+0x2d` bit 3 is set;
    - two lists at the end: a u8 count of 8-byte items (`+0x100`) and a
      vec16<u16> (`+0x118`).
  - League stats are now behind flag `+0x2d` bit 7. Terrain and three lists
    are behind `+0x2e` bits 0–3.
- **Live, 2026-11-03:** we sent `00 11 00`, then `AreaInstancePoe2(id 1,
  0x3f70, seed 1, level 0)` encrypted with `InstanceKeyring.Ciphers(1)`.
  - The client logged **"Client-Safe Instance ID = 1"** and **"Generating
    level 15 area "G1_town" with seed 1"**.
  - It then sent its first encrypted message, which decrypts to a sane id:
    `009A 003533F5 D53A64F181` (11 bytes). This is probably PoE1's `0x7d`
    "area generated", but its layout is not mapped.
  - The client stays on the Clearfell Encampment loading screen. The server
    still has to send the player and enter-area messages (PoE1: `0x22` +
    `0x2b9`, then `0x21`).

Code: `InstanceProtocolPoe2.StartEncryption`, `AreaInstancePoe2` (id
`0x1e`, flag-gated parts off), tests in `InstanceOpeningPoe2Tests`.

Next: map C→S `0x9a`, then find PoE2's ids and layouts for the local player
entity and enter area (dispatcher cases `0x22`/`0x23`/`0x24` are the
likely shifted ones), and get past the loading screen.

## Wave 9 — local player, enter area and create object (2026-11-03)

- **Almost every instance message now starts with a u8 after its id.** We
  saw it on `0x11`, `0x1e`, C→S `0x9a`, and on the writers of `0x1f`–`0x25`.
  - The dispatcher's default case (`0x142125628`) uses this byte (`msg+0x10`)
    to route the message. It picks the local player in `instance+0x48` whose
    slot byte (`+0x181`) matches, or the first one if none does.
  - It then hands the message to `0x142106bb0`, the per-player dispatcher for
    ids ≥ `0x3a`.
  - **Local players** are PoE2's local co-op. They are created at connection
    time, with slot 0 (ctor `0x142103a60`, called from `0x141628d20`). Each
    holds a name (`+0x160`), a slot (`+0x181`) and an entity id (`+0x184`).
- **The S→C factory is `0x142170500`.** It maps `id - 0xd` straight to a
  dword table at `0x1421731dc`, for ids up to `0x36b`. A second factory,
  `0x142186c20`, builds the world group, PoE1's `0x1a8`+ ids.
  - Compared with PoE1's instance group, PoE2 ids are shifted by +1 and gain
    the leading u8:
    - `0x1f` = PoE1 `0x1e` (u32, str);
    - `0x20` = PoE1 `0x1f` (u8, u32);
    - `0x21` = PoE1 `0x20`;
    - `0x24` = PoE1 `0x22`/`0x23` (u32).
  - New: `0x22` = u8, then a u8 count of (u8 slot, wstr name). It assigns
    slots to local players by name.
- **C→S `0x9a`** is PoE1's `0x7d` with the u8: `00`, u32 BE tile hash, u32 BE
  doodad hash. The live values equal the client's "Tile hash: 892597717" and
  "Doodad hash: 979693953" log lines.
- **S→C `0x24`** (local player; write `0x141f3ce30`; case `0x142123014`) is
  u8 route, u8 slot, u32 entity id.
  - It sets the matching local player's `+0x184`.
  - Through `0x142133390` it also stores the id in the world's per-slot
    arrays (`+0x608`, `+0x630`).
- **S→C `0x23` is enter area** (class `0x14350b338`, write `0x142157e80`,
  read `0x1421582b0`, case `0x14212306d`). It holds PoE1 `0x21`'s fields,
  moved into one entry per local player:
  - u8 route;
  - compact u32 (`+0x1f8`);
  - u8 with three bits (`+0x1fc`/`+0x1fd`/`+0x1fe`);
  - u8 count of entries, each: two stash tab lists (`0x142176240`, u16 count
    first), a u8 count of u32 pairs, a wstr and bytes[3] (PoE1's flags);
  - u8 count of a second list (`+0x110`).
  - The case ends by setting the instance state (`+0x2270`) to **3**.
- **S→C `0x2f7` is create object**, PoE1's `0x2b9` (class `0x14350c4f0`,
  write `0x141f6c540`). Its handler is `0x1421343c0`, which logs "Unknown
  object type serialized by server".
  - The entity header (`0x141f4b740`) gained a u8: u32 id, u32, u8, u16.
  - Then a u32 object type, and the component blob behind a **compact**
    length (PoE1 used a u16).
  - The handler looks the id up in the world's `+0x630` array (filled by
    `0x24`). It then finds the local player with that `+0x184`, which raises
    event `0xe794fcda`, as in PoE1.
  - It only runs while the world is active (`+0x84a`, PoE1 `+0xb7a`).
    Otherwise messages queue at `+0x6b0`.
  - Activation is `0x14212fd90` (world vtable `0x1435083e8` slot `0x278`). It
    sets `+0x84a`, replays the queue through `0x1421343c0` and frees it. It
    is called from `0x1416820a0`, slot `0x220` of the vtables `0x14339d1a0`
    and `0x1430e1b08`.
- **Live, 2026-11-03:** we answered `0x9a` with `0024 00 00 00001000`, then a
  minimal `0x23` (one empty entry): `0023 00 00 00 01 0000 0000 00 0000
  000000 00`.
  - About 14 s later the client crashed: access violation at `0x1401506fe`,
    reading a null object at `+0x248`.
  - The crash dump's stack holds the return address `0x1421232cb`, right after
    case `0x23` sets state 3.
  - So `0x23` is accepted and moves the client into the game. It then needs
    the player entity, just as PoE1 crashed on `0x21` without one.

Code: `InstanceProtocolPoe2.ReadAreaGenerated`, `SetLocalPlayer`, `EnterArea`,
`CreateObject` (header and length only). Tests are in `InstanceWorldPoe2Tests`.

Next: the PoE2 player entity. Send `0x2f7` with the character's object type
before `0x23`, and map PoE2's component blob for a character. PoE1's order
was traced live with Frida ([04](Agents/docs/protocol/04-player-entity.md));
PoE2's components and order will differ.

## Wave 10 — player entity, inventories and the in-game HUD (2026-11-03)

- **The client spawns the player and shows the HUD.** Tested live with this
  sequence after `0x9a`:
  1. `0x24` local player;
  2. `0x2f7` for `Metadata/Characters/Int/IntFour`, with a 242-byte blob;
  3. 121 × `0xdf` inventories;
  4. `0x23` with a player record.

  The client logs "[LOADING SCREEN] (Clearfell Encampment) Duration = 1.8
  seconds" and stays connected. It sends C→S `0x1c` (u8 + u32) every 100 ms
  and once `0062 00 01`. The HUD shows Life 100, Shield 50, the weapon sets
  and the skill bar. With the position at (200, 200) the minimap marker sits
  inside the encampment. **The 3D world is not drawn yet:** the screen stays
  grey.
- **Player blob.** The object type key is the PoE1 MurmurHash2 of the path:
  `IntFour` → `0xa7000ea0`, found by the type lookup `0x141693840`.
  - PoE2 has no per-class `.ot`; `Character.ot` lists Stats, Positioned,
    Pathfinding, Buffs, Life, Animated, Player, PlayerClass, Inventories and
    Actor, plus client-side Render, Targetable and BuffOrbs.
  - The blob reader is `0x141e712a0`: u8 flags (bit0 → `+0x8d|1`, bit1 →
    `+0x8d|4`), base data (`0x141e715a0`), each component's vtable `+0x98`
    deserializer, then tasks (u8 count × u16 id + `0x141e7dd60`).
  - Live Frida trace of the component order and sizes (zero blob, life > 0):

    | Component | Deserializer | Bytes |
    |---|---|---|
    | Positioned | `0x141dd89a0` | 17: i32 x, i32 y, f32 rotation, u16, **three** flag bytes (PoE1: two) |
    | Stats | `0x142181160` | 13 |
    | Pathfinding | — | 0 |
    | Buffs | `0x141db5080` | 2 |
    | Life | `0x141d8c260` | 125 |
    | Animated | `0x141da66c0` | 1 |
    | Player | `0x141dcf4a0` | 39 |
    | PlayerClass | — | 0 |
    | Inventories | `0x1416e6d70` | 2 |
    | Actor | `0x1416d8210` | 40 (48 when life is 0) |

  - Life: pool `+0x1a8` (life), base `+0x200`, pool `+0x240` (energy shield),
    base `+0x298`, pools `+0x2e0` and `+0x330`, three (u32, i16) at `+0x380`,
    one more at `+0x398`, flags u8. A pool is PoE1's u32 current, u32, u16,
    u32, u32; a base is 14 bytes.
  - Current life ≤ 0 sends the object down the death path (`0x141d87120`),
    which crashed. Mana is not in the third, fifth or sixth pool: values put
    at blob offsets 98, 116 and 134 left the mana orb at 0.
- **S→C `0xdf` = add inventory**, PoE1's `0xc0` (class `0x1431064e0`, read
  `0x140ca2c20`).
  - Inventory messages now go through the per-local-player dispatcher
    `0x142106bb0`: `0xde` changes an inventory (PoE1 `0xbf`), `0xdf` adds one
    and `0xe0` removes one (PoE1 `0xc1`).
  - `0xdf` is u8 route, u8 manager (`0x142115810`), u32 BE inventory id, then
    the inventory object (`0x1425aa790`). That object is unchanged from PoE1:
    u8 kind (`+0x140`), u8 row (`+0x144`), u8 width, u8 height, u8 flags,
    compact item count (`0x1425aaa50`). Flag bits 3 and 4 add a u64 and four
    u32.
  - PoE1's two trailing bytes are gone.
  - Without inventories, the UI built on entering throws "Invalid inventory
    id". The throw is in `0x14042daa0`, after the lookup `0x141cf1b70` (a
    sorted vector at manager `+0x168`) fails.
  - As on PoE1, we register an empty inventory for every Inventories row
    (id = row + 1). `data/balance/inventories.datc64` (0.5.5e) has 121 rows
    with PoE1's columns.
- **The `0x23` second list is one record per local player.** It is required.
  The per-player handler `0x14210c170` takes the record whose u32 matches the
  player's entity id. Without one, it returns before setting up the weapon
  sets.
  - Record layout: u32 BE entity id; u8 (bits 0–5 entry index, bit 6 →
    `+7`, bit 7 → `+6`); u8 flags (bit 0 → `+8`, bit 1 → `+9`, bit 2 →
    `+0xa`); u8 `+5`; three u8 rows of
    `Data/Balance/CharacterShapeshiftBasicSkills.dat` (`0x141f4d170`, `0xff`
    = none), copied to the local player's `+0x7980`; u16 BE count of u16.
  - The weapon inventory panels map rows 2/3, 14/15 and 79/80 to sets 0/1/2
    (`0x142606af0`). They read the set's row without a null check
    (`0x14049849b`), so all three rows must be valid. The table has exactly
    three rows; we send 0, 1, 2.

Code: `InstanceInventoriesPoe2` (`AddInventory`, `PlayerDefaults`),
`PlayerObjectPoe2` (position, life, energy shield; everything else zero), and
`InstanceProtocolPoe2.EnterArea(params uint[] localPlayerIds)` with the player
records. Tests are in `InstanceInventoriesPoe2Tests`, `PlayerObjectPoe2Tests`
and `InstanceWorldPoe2Tests`.

Next:
- why the world stays grey (a missing message, or a Positioned/Render flag
  such as PoE1's "shown" bits);
- mapping Player (name, class, experience), Stats and mana;
- C→S `0x1c` and `0x62`.

## Wave 11 — server frames: the world is drawn (2026-11-03)

The grey world was the lockstep network mode, not the player entity.

- **Cause.** On localhost the client's Network Mode `auto` picks lockstep, as
  PoE1's did (`networking_type=auto` in `poe2_production_Config.ini`). A
  lockstep client advances its world only by the frames the server ends, so
  without them nothing ran.
  - Nothing was drawn: the view stayed the fog colour behind the HUD, with a
    thin vertical line at the centre.
  - Nothing advanced: mana did not regenerate.
  - The minimap and the HUD were still shown.
- **The frame messages moved by +0x3f.** PoE1's `0x2b5` / `0x2b6` are built
  by a second, world-group message factory (`0x142186c20`):
  - `0x2f4`, id only (class `0x143506450`): PoE1's frame end `0x2b5`.
  - `0x2f5`, a u8 at `+0x10` (class `0x14350c330`, write `0x141f3d870`, read
    `0x141f3d8d0`): PoE1's frame length `0x2b6`.
- **Live test.** After `0x23`, the harness sent `0x2f5 21` (33 ms), then one
  `0x2f4` every 33 ms. Clearfell Encampment and the Witch model were drawn,
  and mana regenerated.
  - This holds with the plain 242-byte blob: object flags 0, Animated 0. The
    wake-zone flag (blob byte 0 bit 1) and an AO path in Animated are not
    needed.
- **Mana is the third Life pool**, at blob offset 98 (component `+0x2e0`).
  The HUD showed the 30 sent there.
- **Positioned `+0x440`.** The value `0x7fffffff` is a sentinel, not a bad
  height: "take the height from the terrain" (`0x141de5510`).

Code:
- `InstanceProtocolPoe2.FrameLength` / `FrameEnd` (`0x2f5` / `0x2f4`).
- `PlayerObjectPoe2` gains `Mana`.
- Tests are in `InstanceWorldPoe2Tests` and `PlayerObjectPoe2Tests`.

Next:
- **Movement.** A click on the ground sends no PoE1-style move. It sends C→S
  `0x63`: 10 bytes `0063 00 BF81 BF81 000000` on the click, then
  `0063 00 0000 0000 00`. The lockstep moves need an answer from the server.
- Mapping Player (name, class, experience) and Stats; life and mana maxima
  currently come from defaults (HUD 100/28, 30/34).
- C→S `0x1c` (every 100 ms) and `0x62`.

## Wave 12 — direction movement (2026-11-03)

PoE2 does not send PoE1's "use Move at a cell" (`0x25`, or lockstep `0x18e`)
for a click on the ground. While the button is held it sends the direction
from the player to the cursor, and a zero direction on release. In lockstep
the client only sends it; the player walks once the server echoes it.

- **C→S `0x63`** (write `0x141f3a6e0`, sender `0x142113a1d`): u8 slot (the
  local player's `+0x181`, PoE2's route byte), then four signed compact ints
  (PoE1's `CompactInt`; client writer `0x141cfc050`), then u8.
  - dx, dy: a vector of length about 180 (±127 on a diagonal, ±180 on an
    axis); 0, 0 = stop.
  - x, y: the player's cell (Positioned `+0x444/+0x448`). It is sent only
    outside lockstep (network mode `+0x661` ≠ 2); in lockstep it is 0, 0.
  - u8: 1 when stat `0x5746` is non-zero.
  - Outside lockstep the sender also starts the walk itself
    (`0x141e4fb40`).
  - While the button is held, the client steers around obstacles: each turn
    is a new `0x63`.
- **S→C `0x64`** (world group: factory `0x142186c20`, class `0x14350c250`,
  read `0x141f6b260`; handler `0x1421497c0`, case at `0x142149852`):
  - Layout: the object header (`0x141f4b800`: u32 object id, u32, u8, u16,
    big-endian), dx, dy, x, y as signed compact ints, then u8 bool.
  - A zero direction stops the object.
  - Otherwise, if (x, y) is not the object's cell, the client first puts it
    there. In predictive mode it does so only from 46 cells away (squared
    distance ≥ `0x844`); closer, it bends the direction instead. Then it
    stores the direction (`+0x400`) and walks (`0x141e4fb40`).
  - So x, y must be the server's idea of the player's cell. Sending a stale
    cell teleports the player back.
- **S→C `0x65`** (class `0x14350c150`): the object header alone. Its case
  resets `+0x8b0`/`+0x8c8`; it is not mapped further.
- **Live test.** The harness answered every `0x63` with a `0x64` carrying the
  same direction. The cell came from a walk estimate: 37 cells/s along the
  previous direction, PoE1's measured speed.
  - Frida (Positioned `+0x444/+0x448` every 100 ms): the client walks between
    echoes at about 3.7 cells per 100 ms along an axis, so the estimate holds.
  - Corrections at each new echo were 1–2 cells.

Code: `InstanceMovementPoe2` with `ReadDirectionMove` (`0x63`),
`DirectionMoveEcho` (`0x64`) and `TryReadCompactInt` (the reader for
`InstanceProtocol.CompactInt`). Tests are in `InstanceMovementPoe2Tests`.

Next:
- A server-side walker that follows the area's walkable grid, so the echoed
  cell matches the client's path around obstacles.
- Wiring frames, `0x63`/`0x64` and the PoE2 player into the Gateway.
- Player (name, class, experience) and Stats; C→S `0x1c` and `0x62`.

## Wave 13 — the Gateway serves the PoE2 client (2026-11-03)

`Game_CLIENT=poe2` runs the Gateway as the PoE2 instance server
(`Program.Poe2.cs`). It branches before the PoE1-only startup, so it needs no
PoE1 client (HNC mask), data tables or saves. Per connection it:

1. reads `InstanceOpeningPoe2`;
2. asks Auth to resolve the ticket. Auth does not log PoE2 clients in yet, so
   for a ticket Auth does not know it falls back to Clearfell Encampment, seed
   1, instance 1, and logs that;
3. sends `0x11` in plaintext, then the encrypted area `0x1e`;
4. hands each decrypted client message to `InstanceSessionPoe2`;
5. once the player is in the world, ends a server frame (`0x2f4`) every
   33 ms. Every encrypted write goes through one gate, since the Salsa20
   stream must see bytes in wire order.

`InstanceSessionPoe2` (Core, tested without sockets):
- **On the client's `0x9a`:** sends `0x24`, the `0x2f7` player (IntFour, the
  242-byte blob, standing in cell 200,200), the 121 inventories, `0x23`, and
  `0x2f5` 33 ms. The frames start from there.
- **On each `0x63`:** answers with `0x64` at the cell `DirectionWalk` puts the
  player in.
  - `DirectionWalk` is a straight line from the cell centre at 37 cells/s.
  - When the client reports its own cell (outside lockstep), the walk
    restarts from it.
- **Framing (`Split`):** the client messages of known length are `0x1c`,
  `0x5c`, `0x62`, `0x9a`, `0x17a`, `0x221`, `0x2e6`, `0x357` and `0x63`.
  - A message cut off at the end of a read waits for the next read.
  - Any other id drops the rest of the read; the client writes one message
    per chunk. Seen on entering: `0xbc`, `0x2e8`, `0x13d`.

**Live test (2026-11-03).** The harness ran only the login, with the
Gateway's keyring (`INSTANCE_SECRET`, the Gateway's `Game_INSTANCE_SECRET` file). The client connected to the
Gateway on 6112, entered Clearfell Encampment, and walked around the camp on
the Gateway's `0x64` echoes.

Limits:
- No PoE2 walkability grid, so the walk estimate ignores walls. A player held
  against a wall is put through it at the next echo.
- The character's class, name, level and stats are not used: every player is
  the IntFour template.
- Auth does not log PoE2 clients in (the dialect is not wired into
  `LoginServerSession`); a Steam token still needs mapping to a local
  account.

## Wave 14 — walkability (2026-11-03)

The server's walk estimate now uses the client's walk map, so a player held
against a wall stays there instead of being echoed through it.

**Where the grid comes from.** PoE1's walk-map hook (right after the
provider call in `0x1410ddb90`) has a twin in PoE2:
- `0x1412ac690(ctx, provider)` stores the area's walkability provider at
  ctx `+0xf0`, then asks it for the grid.
- In Clearfell Encampment the request is skipped (ctx `+0xf8` is 1), so
  the hook never fires.
- The capture asks the provider itself. Of the two providers set per load,
  the walkability one has vtable `0x14339dd90`. Its vtable `+0x50`
  (`0x141687dc0`) is `(provider, out, rect)`:
  - `rect` is `{x0, y0, x1, y1}` in grid cells;
  - `out` is filled with `{i32 w, h; vector walk; vector projectile}`, as
    in PoE1;
  - the area's size in 23-cell tiles is provider `+0x68` / `+0x70`.
- G1_town is 33 × 30 tiles, a 759 × 690 grid.

`scripts/walkmaps/poe2_walkmap.py` (with `poe2-walkmap.js`) waits for the
client, hooks the provider setter and asks for the grid 3 s after the area
load. It writes `data/walkability/<area>_seed<seed>.bin` and `.json`, the
PoE1 walk-map format. The client does not tell which area it loads, so
`--area` / `--seed` name it (default `G1_town`, seed 1). The files are game
data and stay out of git (`data/` is ignored).

**The PoE2 player walks on values ≥ 1** (`InstanceSessionPoe2.Walkable`).
PoE1's player needs 2. Live:
- of 230 client cells sampled every 100 ms, 56 were on a 1 and none on a 0;
- a held walk stopped where the 1s end.

**Spawn.** The old spawn (200, 200) is a 0, outside the camp: the client
only stood there because the server put it there. The spawn is now
(296, 262), the walkable cell farthest from any blocked one (43 cells), as
PoE1 picks its town spawns.

**`DirectionWalk`** steps the estimate a quarter cell at a time:
- A step into a cell the player cannot walk on is refused, and so is a
  diagonal step between two blocked cells (a wall corner). When that
  happens, the walk slides along the wall on the free axis, the larger
  component first, at full speed (a held diagonal walk ran on along x at
  ~3.7 cells per 100 ms). With no free axis, it waits against the wall
  while the key is held.
- A walker standing on a blocked cell (a spawn or an echo off the walkable
  ground) walks freely until it is back on walkable ground.
- Without a walk map, the Gateway logs it and the walk goes through walls,
  as before.

**Live check (2026-11-03).** 23 echoes were compared with the client's own
cell, polled every 50 ms with wall-clock times:
- the median gap is 2 cells; 4 echoes were 10–20 cells off;
- 3 of those 4 are slides along slanted walls: the client follows the
  wall's slope, for example moving slightly +y on a (+1, −1) walk;
- the 4th comes from the client turning gradually after a direction
  change.

An offline replay of the same walks gave the same median and the same
outliers for every slide model tried:
- rotations of ±45° and ±90°;
- a tangent from the nearby blocked cells;
- the best of 8, 16 or 32 directions.

So the axis slide stays until the client's collision code is mapped.

Limits:
- Slides along slanted walls and turns after a direction change still put
  the estimate up to ~20 cells off. The stop echo then snaps the client to
  it.
- Objects the server creates (NPCs, the stash) do not block the estimate;
  the client may walk around them.
- Only Clearfell Encampment has an id for its walk map until Auth serves
  PoE2 areas.

## Wave 15 — Auth logs the PoE2 client in (2026-11-04)

`Game_CLIENT=poe2` now also runs Auth for the PoE2 Steam client, so live tests
no longer need the login harness. Auth listens on `Game_LOGIN_PORT=21265`
(the port the redirected client dials).

- **Login dialect.** The connection decodes with `LoginDialect.Poe2`.
  `LoginServerSession` answers the PoE2 messages next to the PoE1 ones (their
  types differ: `TokenLoginPoe2`, `U32Message 0x18`, `PlayPoe2`).
- **Steam login.** The client sends only its Steam ticket, which cannot be
  checked offline. Every ticket opens one local account, `Game_POE2_ACCOUNT`
  (default `steam`), created on first use with a random password
  (`AccountService.LoginTrusted`). The answer is the PoE2 `0x04` (record id 0,
  result 0, verified flag) and an empty `0x1b`: only the outer shape of `0x1b`
  is checked on PoE2, not its entries.
- **Default character.** PoE2's character creation (`0x12`) is not mapped, so
  an account with no character gets one: `Game_POE2_CHARACTER` (default
  `Exile`), a level 1 Witch. If the name is taken by another account, Auth logs
  it and the character screen stays empty.
- **Character list `0x15`.** Echoes the `0x18` record id. The class byte is
  the row of PoE2's `Characters.datc64` (0.5.5e, 12 rows): Marauder 0, Witch 1
  (`IntFour`), Ranger 2, Duelist 3, Shadow 4, Templar 5, then Warrior,
  Sorceress, Huntress, Mercenary, Monk, Druid (6–11). Row 3 showed "Duelist"
  in wave 5. PoE2 has no Scion; `Poe2ClassRow` gives it the Witch.
- **Play `0x0e`.** The first u32 of the first pair is taken as the
  character's index (a guess: only one character was tested). Auth's instance
  registry puts the character in Clearfell Encampment (`0x3f70`, seed 1) and
  issues the ticket, so the Gateway resolves it through Auth instead of
  falling back.
- `0x10` (character details) stays unanswered, as before.
- The token and machine ids are redacted in Auth's log. Before login, bytes
  Auth cannot decode are counted, not dumped, since they may hold the ticket.
- Auth creates the Steam account at startup and stops if its name is invalid
  (3–32 letters, digits, `_` or `-`). It warns when `Game_BIND` is not a
  loopback address: anyone reaching the port then logs in without a password.
- Two logins at once create the default character once
  (`AccountService.EnsureCharacter`).

**Live test (2026-11-04).** Auth (`Game_CLIENT=poe2 Game_LOGIN_PORT=21265`)
and the Gateway, no harness:
- the client logged in by itself;
- it listed "Exile", level 1, and pressed Play by itself (the only
  character is preselected);
- Auth issued the ticket for instance 1 of `0x3f70`, and the Gateway resolved
  it ("not resolved" never logged);
- the client entered Clearfell Encampment and the player walked (150 `0x64`
  echoes).

Limits:
- One account for every Steam ticket: a server for one player.
- The character still plays as the IntFour template, whatever its class,
  name or level.
- Every PoE2 login starts in Clearfell Encampment.

## Wave 16 — character creation, several characters, the saved character in game (2026-11-04)

The PoE2 client now creates characters, chooses between them, and enters the
game as the character it chose: its name, class (model) and level come from
the save.

- **Classes.** `CharacterClass` keeps PoE1's values 0–6 (Characters.dat
  IntegerIds) and appends PoE2's own classes: Warrior 7, Sorceress 8,
  Huntress 9, Mercenary 10, Monk 11, Druid 12. `Poe2Classes` maps each class to
  its row of PoE2's `Characters.datc64` (0.5.5e, 12 rows), its object type and
  its default CharacterStartStates Id:

  | Row | Class | Object type | Start state |
  |---|---|---|---|
  | 0 | Marauder | `Metadata/Characters/Str/StrFour` | `StrFourDefault` |
  | 1 | Witch | `Metadata/Characters/Int/IntFour` | `IntFourDefault` |
  | 2 | Ranger | `Metadata/Characters/Dex/DexFour` | `DexFourDefault` |
  | 3 | Duelist | `Metadata/Characters/StrDex/StrDexFour` | `StrDexFourDefault` |
  | 4 | Shadow | `Metadata/Characters/DexInt/DexIntFour` | `DexIntFourDefault` |
  | 5 | Templar | `Metadata/Characters/StrInt/StrIntFour` | `StrIntFourDefault` |
  | 6 | Warrior | `Metadata/Characters/Str/StrFourb` | `StrFourBDefault` |
  | 7 | Sorceress | `Metadata/Characters/Int/IntFourb` | `IntFourBDefault` |
  | 8 | Huntress | `Metadata/Characters/Dex/DexFourb` | `DexFourBDefault` |
  | 9 | Mercenary | `Metadata/Characters/StrDex/StrDexFourb` | `StrDexFourBDefault` |
  | 10 | Monk | `Metadata/Characters/DexInt/DexIntFourb` | `DexIntFourBDefault` |
  | 11 | Druid | `Metadata/Characters/StrInt/StrIntFourb` | `StrIntFourBDefault` |

  CharacterStartStates rows 0–11 are these defaults; their Characters key
  (the u64 at row byte 8) is the row number. Rows 12 on are act and premade
  states (`WitchActTwo`, `WarriorGamepadPremade`, …), not mapped: creating
  with one is refused as an invalid class.
- **Leagues `0x1b`.** The create screen needs one: the sender of `0x12`
  throws when no league is selected. A PoE2 league entry is PoE1's without
  the trailing `RawC4`, with one more byte `C2` (`+0xc2`) after `C1` (reader
  `0x142716e10`; outer message unchanged). Auth sends the Standard family
  without Ruthless (Standard, Hardcore, SSF Standard, SSF Hardcore). The
  screen showed the Standard card with its Solo Self-Found and Hardcore boxes.
- **C→S `0x12` create** (vtable `0x143506430`, write `0x1420e4cc0`, sender
  `0x1420e9ce0`, create screen `0x140ece810`): UTF-8 `Str` name, UTF-8 `Str`
  league, u32 login record id, u32 0, u32 0, `WStr` start state, 32-byte
  password digest. PoE1 had UTF-16 name and league and two u32s.
- **S→C `0x13` result** (vtable `0x143509e98`): u16 result, bool tracking,
  u32 login record id (new; it only picks the record for the tracking POST).
  On 0, the create screen (listener `0x140ed2ab0`, then `0x140ecee50`) looks
  the new character up by exact name in the last `0x15` (skipping entries
  whose `B51` has bit 3) and sends Play with it. As on PoE1, Auth sends the
  list first, then `0x13`.
- **Play `0x0e`.** One pair per local player slot (the screen has two):
  `A` is the character's index in the `0x15` list, `B` the login record id
  (`0x1420e9ff0` looks the record up by `B`, then bounds-checks `A`). Wave 15's
  guess was right.
- **C→S `0x10` is answered by S→C `0x11`**, not `0x16`.
  - `0x11` (writer `0x14214e6b0`, reader `0x14214e7d0`): u32 record id,
    u8 index, then the tail of `0x15` for one character (u16, u16, u16-counted
    blob, compact pair list, u32, u32).
  - Dispatch `0x1420e8cc6` decodes the blob into 31 records of `0x80` bytes
    and hands it to the listeners at `login+0xd08`. The character screen's
    listener `0x140f0c090` caches the entry and clears its dirty flag, which
    ends the `0x10` requests.
  - Auth answers with an empty detail, for the last `0x18` record and an
    existing index only. The blob (the character's appearance) is not mapped.
  - `0x16` goes to `login+0x288`, where the screen's listener `0x140f0b340`
    only passes it to its two slot panels: a UI notice. Not sent.
- **The player object from the save.** The Gateway takes the name, class and
  level Auth resolved for the ticket. It creates the player as the class's
  object type (`StrFourb` for a Warrior). It writes the start of the Player
  component (`0x141dcf4a0`), after Animated at blob offset 160:
  - u32-counted UTF-16 name;
  - u8 Characters row (compared with PlayerClass `+0x158`);
  - u32 experience (`0x141dd1360` derives the level, `+0x204`).

  The rest of the component stays zero:
  - u32, u32, u16, u16;
  - two flag sets (`0x141deebf0`, u16 count);
  - 5 raw bytes;
  - two u16 dat keys;
  - u8 flags;
  - u8, u8;
  - a u8 count of u32;
  - a u8 count (`0x141deecb0`).

  An empty name gives PoE1's 39 bytes.
- **Experience.** PoE2 0.5.5e's `ExperienceLevels.datc64` matches PoE1's
  (level 2 at 525, 3 at 1760, 100 at 4 250 334 444). The Gateway takes the
  level's start from `data/tables/ExperienceLevels.json`, as PoE1 does.
  Without that file the experience is 0 and the client shows level 1, which
  is right for new characters.

**Live test (2026-11-04).** Auth and the Gateway, `Game_CLIENT=poe2`:
- the client logged in. The `0x1b` leagues decoded; `0x10` got its `0x11`.
- It pressed Play on "Exile" by itself. The character panel showed "Exile,
  Level 1 Witch".
- "Exit to Log In Screen" brought back the character screen.
- Create → Standard → Warrior → "Brakka":
  - Auth decoded `0x12` (`StrFourBDefault`) and sent the list, then `0x13`;
  - the client played the new character's intro, then sent Play `(1, 0)`;
  - the Gateway created the player as `StrFourb`;
  - the character panel showed "Brakka, Level 1 Warrior", with the Warrior's
    portrait and model.
- Back on the character screen, both characters were listed. Selecting
  "Exile" and pressing Play sent `(0, 0)` and entered the Witch.

Limits:
- "Exit to Character Selection" sends C→S `0x173` (3 bytes) to the Gateway
  and waits for an answer we do not know. "Exit to Log In Screen" works.
- The `0x11` detail is empty: the character screen shows each class's default
  look, not the character's gear.
- Only the class defaults are mapped among the start states.
- PoE1 and PoE2 share the save database. PoE1's create request refuses the
  PoE2 classes, but a PoE2 character is not hidden from a PoE1 client that
  logs into the same account (the Steam account has a random password, so
  this takes a dev password reset).
- Every PoE2 character still starts in Clearfell Encampment, with the
  Gateway's fixed life, mana and an empty skill bar (wave 17 sends the
  class's life and mana).

## Wave 17 — the class's life, mana and attributes (2026-11-04)

The player now enters with its class's base life, mana and attributes at its
level, instead of fixed pools and zero stats.

- **The Stats component is PoE1's stats list** (`0x142181160`): a compact
  count of (compact key, compact value) pairs, each set by `0x1426acb60`, then
  12 bytes (sent as zeros). `PlayerObjectPoe2.Stats` fills the list;
  `BlobWriter.Stats` writes it as for PoE1.
- **Stat keys** (client key = PoE2 Stats.datc64 row + 1, checked live on the
  character panel): `maximum_life` `0xef`, `maximum_mana` `0xf0`, `strength`
  `0x233`, `intelligence` `0x236`, `dexterity` `0x239`.
- **The client derives none of them.** These are live results from
  2026-11-04, level-1 Witch:

  | Stats sent | Panel |
  |---|---|
  | none | Life 28, Mana 34, attributes 0 |
  | the three attributes only (Str 15, Dex 8, Int 9) | those attributes, still Life 28, Mana 34 |
  | `maximum_life` 77 and `maximum_mana` 55 with the attributes | exactly those values |

  The HUD's maximums follow the panel. So, as on PoE1, the server sends the
  final values.
- **The rules** (`Poe2CharacterStats`), before passives, items and buffs:
  - **Life:** Characters BaseMaxLife (16, every class) + `life_per_level`
    (12) × level + 2 per Strength.
  - **Mana:** BaseMaxMana (30) + `mana_per_level` (4) × level + 2 per
    Intelligence.
  - **Attributes:** the class row's BaseStrength, BaseDexterity and
    BaseIntelligence (Characters row bytes 68, 72, 76). The class's own
    attribute is 15 and the others 7; a hybrid class has 11 in each of its
    two and 7 in the third. The "Fourb" classes have the same attributes as
    their "Four" counterparts.
  - **Where the numbers come from:**
    - `life_per_level`, `mana_per_level`, and the `*_per_level` attributes
      (all 0) are in `Metadata/Characters/Character.ot`'s Stats.
    - The per-attribute bonuses are the client's own attribute tooltips.
    - With zero attributes at level 1, the rules give the client's own
      default, 28 and 34.

  Dexterity's +8 accuracy per point is not sent, because the accuracy stat
  key is not mapped.
- **The Gateway** sends the class's sheet at the saved level, with the life
  and mana pools set to their maximums, and logs it
  (`Exile: level 1 Witch (…IntFour), life 42, mana 64`).

**Live test (2026-11-04).** Auth and the Gateway ran with `Game_CLIENT=poe2`:

| Character | Panel | HUD |
|---|---|---|
| "Exile", level 1 Witch | Life 42, Mana 64, Str 7, Dex 7, Int 15 | Life 42/42 |
| "Brakka", level 1 Warrior | Life 58, Mana 48, Str 15, Dex 7, Int 7 | Life 58/58 |

Limits:
- **Mana starts low.** The HUD's mana starts near 32 (32/64, 32/48) whatever
  pool is sent, then regenerates to full in about 20 seconds. Life starts at
  the pool sent. Something else sets the starting mana: perhaps the second
  mana pool at `+0x330`, sent as zeros.
- **No accuracy, evasion or spirit.** Accuracy and evasion are left to the
  client (the panel shows its own 5% evasion), and spirit is 0.
- **No passives or items.** Characters have no passive tree or gear yet.

## Wave 18 — the character screen's preview blob (2026-11-04)

The Data blob of `0x15` (for its selected entry) and `0x11` is what the
character screen shows the character wearing. It is now mapped and encoded
(`CharacterPreviewPoe2`). Auth does not fill it yet, because PoE2 characters
have no items: the saves hold none, and CharacterStartStates names no
starting gear.

- **Decoder `0x1420e5c10`.** The blob is read as 31 records of `0x80` bytes,
  one per visual slot, each by `0x1426c41a0`. A blob that ends early throws,
  so all 31 records are sent. An empty blob gives an empty preview
  (`0x1420eb1e0`).
- **A record** is PoE1's `VisibleItem` record (the in-game visible
  equipment) without its slot byte, and with three trailing bytes instead of
  one:

  | Field | Reader → record offset |
  |---|---|
  | u8 type: 0 = empty, and nothing follows; else the ItemClasses row + 1 | → `+0` |
  | skin: u16 ItemVisualIdentity HASH16, u32 MTXTypeGameSpecific HASH32, u8 | `0x1426c3d80` → `+0x18` |
  | effect: u16 ItemVisualEffect HASH16, u32 MTXTypeGameSpecific HASH32, u8 | `0x1426c3f50` → `+0x40` |
  | u8 ItemStances row (17-byte rows) | `0x1426c4fd0` → `+8` |
  | u16 ItemVisualIdentity HASH16 (the item's own look) | `0x1426c4c50` → `+0x68` |
  | u8, u8, u8 | → `+0x78`, `+0x79`, `+0x7a` |

  A present record is 21 bytes. The hashes are looked up by key: a missing
  key gives an empty reference.
- **Visual slots.** A record's position is the visual slot of an equipment
  inventory: the Inventories.datc64 u32 at row byte 23 (31 for none).

  | Slot | Inventory |
  |---|---|
  | 0 | Weapon1 |
  | 1 | Offhand1 |
  | 2 | Weapon2 |
  | 3 | Offhand2 |
  | 4 | Weapon3 |
  | 5 | Offhand3 |
  | 6 | BodyArmour1 |
  | 8 | Helm1 |
  | 9 | Gloves1 |
  | 10 | Boots1 |
  | 14 | Ring1 |
  | 15 | Ring2 |
  | 16 | Belt1 |
  | 17 | Amulet1 |

  No inventory uses the other slots up to 30. They are probably for
  cosmetics only.
- **Data used for the test.**
  - BaseItemTypes (PoE2): the ItemClasses key is at row byte 8, the
    ItemVisualIdentity key at row byte 124.
  - ItemVisualIdentity: HASH16 is at row byte 40, as on PoE1.
  - ItemStances rows: Wand 2, Staff 5, TwoHandMace 7, Shield 9, Spear 10,
    Crossbow 12, Talisman 19.
- **Auth.** `LoginServerSession.Poe2Preview` (character → visible items)
  fills the `0x15` selected entry's blob and each `0x11`. Auth leaves it
  unset, so the blobs stay empty, as before.

**Live test (2026-11-04).** Auth set the hook for the test only, then the
experiment was removed:

| Character | Items sent | Character screen |
|---|---|---|
| "Exile", Witch | Withered Wand in slot 0 (type 9, look `0x8a43`, stance 2) | holds a glowing wand in her right hand; before, she was empty-handed |
| "Brakka", Warrior | Felled Greatclub in slot 0 (type 18, look `0x0153`, stance 7) | holds the club |
| "Brakka", Warrior | the club, plus Rusted Greathelm in slot 8 (type 26, `0x1ff7`) and Rusted Cuirass in slot 6 (type 25, `0xb16e`) | also wears the helm and the plate cuirass |

Limits:
- **Not filled yet.** Nothing fills the preview until characters have items.
  The in-game player's visible equipment (the PoE2 Inventories component,
  sent as 2 zero bytes) is not mapped either.
- **Unknown fields.** The three trailing bytes and the u8 after each
  reference are sent as zeros; what they do is not known.

## Wave 19 — leaving the town: exits, the change of area and Clearfell (2026-11-04)

The player walks out of Clearfell Encampment into Clearfell (G1_2), arrives by
its exit back to the town, and walks back. Each area's exits come from a capture
of the client's own level generation.

- **The exit object**, `Metadata/MiscellaneousObjects/AreaTransition`
  (`AreaTransitionObjectPoe2`). The client's readers consumed it exactly, live:

  | Part | Bytes | Reader / note |
  |---|---|---|
  | object flags | u8 `0x02` | bit 1 → `+0x8d` bit 2; create object (`0x1421343c0`) then puts the object in the wake zone (`0x141e73d70`, PoE1's separate `0x1a9`). Without it the exit is neither drawn nor on the map |
  | base data count | u8 0 | |
  | Positioned | 17 | i32 x, i32 y, f32 rotation, u16, three u8 (the player's layout; grid position at component `+0x444`/`+0x448`) |
  | Animated | u8 bit 0, then `WStr32` .ao | |
  | AreaTransition (`0x1416e2dc0`) | 4 or 5 | u8 state (7 bits + top bit), u8 variant 2 + u16 WorldAreas HASH16 (`0x141d9bca0`), then a compact index **only** when a flag of the area is set (`[[object +8] +0x78] +0x98`, byte `+0x14`) and the state is 0 |
  | Stats | 7 | compact count, u32, u8, u8 |
  | ObjectMagicProperties (`0x141dc8190`) | 4 | |
  | MinimapIcon (`0x141d91260`) | u8 | bit 1 shown (`+0x39`, object `+0x8d` \|= 8), bit 2 `+0x38`, bit 0 → u16 icon, bit 3 → variant |
  | InteractionAction (`0x141d81480`), tasks | u8, u8 | |

  The index: live, the town's exits read one and Clearfell's read none. A sent
  index in Clearfell shifts every later component, and the client stops on
  "Buffer Underflow" (thrown via `0x1421bffd0`; found by tracing each
  component reader's stream position). The Gateway sends it in towns only.
- **The change of area.**
  1. S→C `0x2f1` (`ChangeArea`): u8 route, u16 destination HASH16, u8. The
     client shows the loading screen and answers C→S `0x355` (3 bytes), then a
     C→S `0x362` of 868 bytes (PoE1's `0x2cb` analogue; not framed, dropped).
  2. The Gateway asks Auth's control `transfer` (ticket, area) for the
     destination ticket, on `0x355` or after 2 s.
  3. S→C `0x29` (`ConnectInstance`, reader `0x142152020`): PoE1's `0x27` with
     the route byte first and one more u8 last. u8 route, str host, u16 port,
     u32 IPv4 (big-endian), u32, u16 area HASH16, u32 ticket, u8, u8, 64-byte
     instance key, 8 bytes, u8. The client opens a new instance connection with
     the ticket ("Got Instance Details from login server").
  4. The Gateway serves the new connection the destination area. It remembers
     which area the ticket left (`Poe2Areas.CameFrom`) to place the arrival.
- **Taking an exit.** Clicking an exit sends nothing yet (the client is in WASD
  mode, and the exits are not targetable). The session takes the exit when the
  walk estimate comes within 6 cells of it (`ExitReach`), once the player has
  been out of reach of every exit (an arrival is beside one).
- **Where the exits are: a capture of the level generation.** The client
  generates each level itself and meets every object its rooms place, server
  ones included. For each, `0x141ff3490` asks the spawn predicate `0x1415a16b0`
  (vtable `[+0xa0]`, call returning to `0x141ff3844`) whether the client creates
  it:
  - `rdx` = the room's object record: `+0` type handle, `+8` .ao handle (a
    handle's `+8` points to its UTF-16 path);
  - `r14` = the room; `[r14]` is its .arm handle (still the caller's register on
    entry);
  - caller `rsp + 0x70` = i32 x, i32 y in grid cells (the room placed and
    turned), f32 world x, y, z, f32 angle in radians.

  `scripts/walkmaps/poe2_walkmap.py` now also writes
  `<area>_seed<seed>.objects.json`: exits, arrival markers, waypoint,
  checkpoints, monsters, chests. The level's objects come before its
  walkability provider. The login scene's come before its other provider, and
  the world maps' (generated while the grid settles) after. So the script keeps
  the objects met since the previous provider. Decoration, flow fields and sound
  objects are left out.
- **Where each exit leads.** A level's graph (`.tgr`) labels the node of each
  exit room `entranceK`: the area's K-th WorldAreas connection.
  - Clearfell's `felling_*.tgr`: `town_N` is entrance1 (G1_town),
    `mud_entrance` entrance2 (G1_3), `exit_woods` entrance3 (G1_4).
  - The town's: entrance1 by the lumbermill (G1_1), entrance2–4 by its three
    gates (G1_2).

  The capture names the room and model, not the node, so `Poe2Areas` maps a
  fragment of either to the destination. Only the town and Clearfell have rules.
- **Arrival.** Each exit room has an `AreaTransition_Auto` marker 14–18 cells
  from the exit: the arrival by that exit. Without a marker (the town's gates),
  the arrival is the first walkable cell 8–20 cells from the exit. A login into a
  non-town area arrives by its entrance1.
- **Act 1 areas** (WorldAreas: id `+0`, Connections `+22`, HASH16 at row byte
  42) are in `Poe2Areas.Act1`. The walk maps and captures are named by their id.

Clearfell, seed 1:

| Object | Grid | Room |
|---|---|---|
| exit to G1_town (generic_cylinder) | (1001, 474) | `lumbermill_exteriorN.arm` |
| its arrival marker | (1001, 460) | |
| exit to G1_4 The Grelwood | (280, 999) | `exit_woods.arm` |
| exit to G1_3 Mud Burrow (generic_cylinder_vert) | (655, 1406) | `mud_entrance_woods_2.arm` |
| waypoint | (585, 1344) | `mud_entrance_woods_2.arm` |
| camp: 3 CourtGuardZombie, CampsiteChest | around (1140, 1360) | `campsite_03.arm` |

**Live test (2026-11-04).**
- Walking into the town's north gate sends `0x2f1`. The client loads Clearfell
  (instance 2, seed 1) and arrives at (1001, 460), beside an exit drawn and
  labelled "Clearfell Encampment".
- Walking into that exit returns to the town at (317, 197), by the north gate.
- The Clearfell walk map (1656 × 1656) was captured on the way.

Limits:
- Only the town and Clearfell are captured. Each further area needs one visit
  with the capture running, and its exit rules in `Poe2Areas`.
- The layouts are those of seed 1 (Auth's seed). Another seed needs its own
  capture.
- Exits are taken by walking into them; the click is not mapped.
- The town's gates are still not drawn.
- C→S `0x362` is not framed.

## Wave 20 — monsters, and the wake zone outside towns (2026-11-04)

Clearfell's camp has its three CourtGuardZombies, drawn where the level puts
them. Outside towns the player is drawn and updated too: it had been frozen
there since wave 19.

- **The wake zone.** Outside towns, the client draws and updates only the
  objects in its wake zone. Create object (`0x1421343c0`) puts an object there
  when bit 1 of its flags byte is set (`+0x8d` |= 4, then `0x141e73d70`). Towns
  wake everything, so the player (flags 0) worked in the camp.
  - Symptom in Clearfell: the player was not drawn, the camera did not follow,
    and mana did not regenerate.
  - The world itself ran: its frame step `0x142130840` was called 30 times a
    second and emptied its message queue (Frida).
  - Wave 19 found the same bit for the exits. The player, the exits and the
    monsters now all send `0x02`.
- **Pause** (no effect on the freeze, kept because it matches PoE1).
  - C→S `0x1ff` (4 bytes, `0x142129330` / `0x1421293a0`) is PoE1's `0x1d4` with
    the route byte: u8 route, then u8 1 = pause or 0 = resume. The client counts
    its requests (instance `+0x2734`) and sends only the first pause and the
    last resume, where `0x141003e60` allows a pause (not in towns). It sends one
    pair while it loads Clearfell.
  - S→C `0x1fe` (world group, class `0x14350c430`, read `0x142184420`) is
    PoE1's pause state `0x1d3` behind the world message header: u8 flags, bit 0
    → `+0x20`, bit 1 → `+0x21`.
  - The session answers each request with its state. `0x1ff` and C→S `0x23d`
    (4 bytes; sent by `0x140f55790` outside towns: u8 route, u8 local player
    slot, u8 2) are now framed.
- **Monster types: S→C `0x21`** (read `0x14215aac0`, write `0x14215a9e0`). It is
  PoE1's `0x20` with the route byte:
  - u8 route, u16 count and u16 preload (big-endian);
  - count × raw little-endian u16 MonsterVarieties row;
  - count × u8 level.

  The client registers `"<variety Id>@<level>"` for each pair. Create object
  `0x2f7` then names that path's hash. `InstanceProtocolPoe2.MonsterTypes` and
  `MonsterObjectPoe2.TypePath` build them.
- **The monster blob** (`MonsterObjectPoe2`). Its components are the base
  type's .ot chain: Positioned first, then the sections parent-first. A
  CourtGuardZombie has 14 (Frida, component vtables in deserialize order):

  | Component | Bytes |
  |---|---|
  | object flags, base data count | u8 `0x02`, u8 0 |
  | Positioned | 17: i32 x, i32 y, f32 rotation, u16 team, three u8 |
  | Stats | 7: compact count, u32, u8, u8 |
  | ObjectMagicProperties | 4 |
  | BaseEvents, Pathfinding | 0 |
  | Buffs | 2 |
  | Life | 107: pool (life) + 14, pool + 14, pool, 3 × 6, 6, u8 (the player's is longer) |
  | Animated | u8 bit 0, then `WStr32` .ao |
  | Inventories | 2 |
  | Actor | 40 |
  | Monster, Functions | 0 |
  | Transitionable (`0x142180490`) | u8 state 1..num_states (7 bits) |
  | StateMachine (`0x14214cc50` → `0x141dfa4a0`) | u8 shared-state count, u8 shared-timer count, per state a compact value and a u8 |
  | tasks | u8 |

  The StateMachine throws when its count is not the type's. CourtGuardZombie's
  .ot has one shared state, PoE2's `define_shared_checkpoint_state = "dead;"`.
  Live, each zombie blob was read 310/310 (188 bytes and its 59-character
  model). Other components are not written yet: `Poe2Monsters.InArea` leaves
  such varieties out with a note.
- **Which monsters.** The area captures (wave 19) list the monsters the level
  places. `data/poe2/monster-varieties.json` lists the varieties the server
  can create: MonsterVarieties row, components and shared states.
  `scripts/re/poe2/monster_varieties.py` writes it:
  - the varieties are the captured `Metadata/Monsters/` types that are
    MonsterVarieties Ids (row size 1030: Id `+0`, BaseMonsterTypeIndex `+72`);
  - each base type's .ot chain is merged with `ot_components.py`, which now
    also reads `define_shared_checkpoint_state`;
  - the files come from the Steam bundles through
    `scripts/re/poe2/steamdump` (moved into the repository from the session
    scratch).
- **Area level.** The monsters take the area's level, WorldAreas' i32
  AreaLevel at row `+38` (`Poe2Areas.Area.Level`): the town 15, Clearfell 2.
  These match the client's "Generating level N area" log lines.
- **Testing far from an entrance.** The Gateway's `Game_POE2_SPAWN=x,y` puts a
  login at that cell. Transfers still arrive by their exit.
  - Auth's `Game_POE2_START_AREA=<hash16 hex>` logs straight in to that area.
  - Clearfell's camp: `Game_POE2_START_AREA=9875`, `Game_POE2_SPAWN=1154,1297`.

**Live test (2026-11-04).**
- Login to Clearfell: the player is drawn at the town exit, and its mana fills.
- With the spawn by the camp, the three zombies stand around its fire, at
  (1116, 1371), (1158, 1364) and (1158, 1326).
- Walking back to the town and out again still works.

Limits:
- The monsters stand still: no AI, no attacks, no hits (waves 21–23).
- Their life is a placeholder 30. Their stats (MonsterVarieties' life,
  damage, level scaling) are not read.
- Only varieties made of the base components, Transitionable and StateMachine
  are written. Two varieties are in the captures so far.

## Wave 21 — skills, the skill bar and a targeted attack (2026-11-04)

The player has Punch and the dodge roll. Punch shows in the skill picker, can be
put on a slot of the skill bar, and a left click on a monster with it makes the
player walk to the monster and punch. Nothing is hit yet (wave 22).

- **The Actor's skill list** (`ActorSkillsPoe2`, `SkillsPoe2.cs`). The Actor
  body (`0x142147300`) reads it on create through `0x142149510`, right after its
  flags byte. A record (`0x142188d50`) is:
  - u32 skill id, u8 stats source (0 = the Actor's stats);
  - a GrantedEffectsPerLevel reference (u8, u32 row) and a
    GrantedEffectStatSetsPerLevel reference (u8, u32 row);
  - u8 flags, u8 supports, u32 × 3, u8;
  - a compact stats list (the skill's own stats), u8.

  The list ends with three empty lists (PoE1's soul budgets, a new u8 list,
  cooldowns). Skill ids share the manager with the type's basic actions
  (ActionTypes HASH16 & 0x3fff), so the server's start at `0x102`.
- **The starting skills** (`Poe2StartingSkills`, read with the new
  `scripts/re/poe2/dat.py`):
  - Punch: GrantedEffects MeleeUnarmedPlayer, GEPL and GESSPL row 40. Its stats
    are the class's unarmed weapon (Characters WeaponSpeed 606 ms, MinDamage 2,
    MaxDamage 5/6/8, MaxAttackDistance), sent as final values in the player's
    stats and the skill's;
  - the dodge roll: Characters StartingSkill, GEPL 899, GESSPL 905.
- **The skill picker's list: S→C `0x66`** (reader `0x142153f60`, game-state
  case `0x142106d28`). Without it the picker is empty whatever the Actor has.
  - u8 route, u8 merge (0 replaces the list `0x1421386c0`; else updates
    `0x142138af0`), u8 count;
  - count × 64-byte entries: u32 id, u8, five bytes of slot bits (bit n = bar
    slot n; slot 0 is the left mouse button), u8 × 3, 3 bytes, 24, 24.

  The SkillsList UI (`0x140c6b060`) keeps an entry when its id is in the
  Actor's skill manager and its GrantedEffects is not hidden (+0x6e). It groups
  them by the id's high bits: skills, the `+0x318` group, actions. `0x255`,
  thought to be the bar in wave 20, is a one-time 112-byte copy into
  `game+0x80c8`.
- **Putting a skill on a slot: C→S `0x5c`** (8 bytes, sent by `0x142112ea4`):
  u8 route, u8, u8 slot, u32 skill id. The client binds it locally
  (`0x142138c90`), but the bar shows the icon only after the server's `0x66`
  merge with the new slot bits. The session keeps the slots for the session
  and answers with each changed skill's mask (the old skill of the slot loses
  its bit).
- **Skill use: C→S `0x1a5`** (built by `0x14210faa0`), PoE1's `0x24`/`0x25` in
  one message:
  - u8 route, u32 skill id, u16 flags (`0x0400`), u8 sequence (`0xff`),
    u8 options;
  - options bit 3: a u32 target object id follows;
  - i32 x, i32 y: absolute with a target, else relative to the player.

  19 bytes, or 23 with a target. Live: Space sends the dodge roll with a
  relative cell; a click on a zombie sends Punch with its id and cell.
- **The echo: S→C `0x1c8`** (read `0x141f69830`), PoE1's action started
  `0x1ab`: object header (u32 id, u32, u8, u16), u16 flags (bit `0x80`: a u32
  target id follows), compact actor x, y, compact target x, y, u32 skill id,
  u16 sequence (`0xffff`: the client starts it), u8. As in PoE1, an attack on
  an object waits for it; the session answers a targeted use of a granted skill
  from the player's estimated cell.
- C→S `0x67`/`0x68` (the client toggling entry flags, `0x1421134a0` /
  `0x1421132f0`) and `0x173`, `0xbc`, `0x13d`, `0x1ac` are framed but not
  answered.

**Live test (2026-11-04).**
- Punch shows in the picker of the left mouse slot and binds; its icon appears.
- A click on the Rotting Guard by Clearfell's fire: the player walks to it and
  punches (the animation plays). The dodge roll rolls on Space.

Limits:
- No damage, no monster reaction (wave 22).
- The slots are not saved between sessions.
- The dodge roll is not echoed; the client runs it by itself.
- Untargeted uses (a punch at the ground) get no answer.

## Wave 22 — hits and monster death (2026-11-04)

Punching a Rotting Guard takes its life down; at 0 it dies and can no longer be
targeted. Loot needs items on the ground, so it moves to the items wave.

- **Where object messages go.** The world handler (`0x1421343c0`, which also
  runs create object) sends ids `0x1af`–`0x2a6` through a jump table to the
  object's components (index tables `0x14213647c` / `0x142136440`):

  | Ids | Component | Handler |
  |---|---|---|
  | `0x1c5` | remove from the world (`0x141e717a0`) | — |
  | `0x1c6` / `0x1c7` | enter / leave the wake zone | `0x141e73d70` / `0x141e740a0` |
  | `0x1c8`–`0x1ce`, `0x1d8`–`0x1dc`, `0x1f9`–`0x1fc`, `0x222`, `0x223`, `0x295` | Actor | `0x1421497c0` |
  | `0x1cf`–`0x1d1`, `0x296` | Life | `0x141d8ea60` |
  | `0x1d2`, `0x2a4`–`0x2a6` | Animated | `0x141da8a40` |
  | `0x1d3`–`0x1d7` | Stats | `0x1421820a0` |
  | `0x1eb`–`0x1ed` | Buffs | `0x141db52d0` |
  | `0x1dd`–`0x1ea` (most) | Positioned | `0x141dda7c0` |

  PoE1's ids `0x1a8`+ map to `0x1c5`+ with gaps: action started `0x1ab` →
  `0x1c8`, action target `0x1ad` → `0x1cb`, life `0x1b1` → `0x1cf`, stats
  `0x1b3` → `0x1d3`. The action hit (PoE1 `0x1af`) is `0x1fb`: header, u16
  sequence, u32 target, u16 flags, u8 kind, then a u16-length opaque blob
  (`0x141f4d500`); not sent yet (the blood and the impact sound).
- **Life: S→C `0x1cf`** (read `0x141f6cb20`). Object header, u8 flags (pool in
  bits 0–2; bit 7 compact current, bit 6 u32 → `+0x24`, bit 5 u32 → `+0x28`,
  bit 4 u16 → `+0x2e`), u8 (`+0x30`, passed to the death slot), then the fields.
  - Pools: 0 life `+0x1a8`, 1 `+0x200`, 2 energy shield `+0x240`, 3 `+0x298`,
    4 mana `+0x2e0`, 5 `+0x330`.
  - The store `0x141d88f20` copies every field whether its bit is set or not,
    so the server sends all of them (0, as in the blob).
  - Life going from ≥ 1 to ≤ 0 calls the owner's death slot (vtable `+0x130`);
    back above 0, its revive slot (`+0x158`).
- **Stats: S→C `0x1d3`** (read `0x141f6de60`): PoE1's `0x1b3` with a u8 after the
  header, then compact count and (compact key, compact value) pairs. At death
  the server sets `hide_mini_life_bar` (`0x2733`): as in PoE1, the mini life
  bar otherwise keeps its last sliver.
- **Monster life.** `monster_varieties.py` now also writes each variety's
  combat columns (Name, Life/Damage/ExperienceMultiplier, AttackSpeed,
  attack distances, MovementSpeed, ObjectSize) and
  `data/poe2/default-monster-stats.json` (DefaultMonsterStats by level: life,
  damage, experience, accuracy, evasion, armour). A Rotting Guard at level 2
  has 20 × 100 % = 20 life. The monster's Stats component now carries
  `maximum_life`, which the life bar needs for its fraction.
- **The hit** (`InstanceSessionPoe2`). A targeted use of an attack skill
  (`AttackPoe2.Of`: the skill's main-hand damage, attack time and reach) on a
  live monster starts an approach: the player walks at the walk speed until
  within reach, the swing starts once the previous one has ended, and the hit
  lands at half the attack time. The session's `Tick` then rolls the damage
  and sends `0x1cf`. A move before the hit cancels it, the walk estimate placed
  where the approach had got.

**Live test (2026-11-04).** The Rotting Guard by Clearfell's fire: the first
punch took its bar to about 70 %; it died on the fourth (2–8 per punch), its
bar hidden, and could no longer be targeted.

Limits:
- No loot (items on the ground are the items wave), no action hit `0x1fb`
  (blood, sound), no flinch.
- No experience (wave 24). Monsters do not fight back (wave 23).
- Monsters do not move, so the approach aims at their placed cell.

## Wave 23 — monsters fight back, the player's death (2026-11-04)

The area's monsters now chase the player, attack it in reach and take its
life; at 0 the client shows the death screen and "Resurrect at Checkpoint"
revives the player. Code: `InstanceSessionPoe2.Monsters.cs`.

- **Monster attack data.** `monster_varieties.py` adds MaxAgroRange, the type's
  DamageSpread and the variety's first GrantedEffects row (its attack) with
  that effect's GrantedEffectsPerLevel / GrantedEffectStatSetsPerLevel rows by
  level. `Poe2Monsters.AttackOf` builds the attack: the level's Damage ×
  DamageMultiplier, ± DamageSpread, the variety's AttackSpeed as the attack
  time, MaximumAttackDistance as the reach.
  - A level 2 Rotting Guard: 10.26 × 120 % = 12 (10–15) per hit every 2.5 s.
  - PoE1's server (and Path of Building) also multiply the damage by the attack
    time in seconds. For PoE2 that gave 25–37 per hit against a 58-life
    Warrior, so it is left out until PoE2's rule is known.
  - The attack is the first entry of the monster's Actor skill list (id
    `0x102`, the same layout as the player's), so the client knows the skill
    its actions name.
- **AI** (`MonsterTick`, every frame; each monster decides every 0.25 s).
  - Aggro within MaxAgroRange cells (as PoE1 reads it; 105 for the Rotting
    Guard); a hit from the player provokes a monster from any distance.
  - Out of reach (MaximumAttackDistance + 2 cells), it walks to the edge of
    its reach on the line to the player, at MovementSpeed cells/s; the order is
    renewed once the target point has moved 3 cells.
  - In reach and free, it swings: S→C `0x1c8` with skill `0x102`, flags
    `0x0408` and the player as the target. The hit lands at half the attack
    time if the player is still within reach + 3 cells, and sends `0x1cf` on
    the player.
  - The server estimates each monster's cell along its walk;
    the player's approach now aims at that cell.
  - The monsters wait 2 s after the player enters (the client is still
    loading), start a pack 0.25 s apart, and stop while the client has the
    game paused (C→S `0x1ff`: loading, the Esc menu). On resume their timers
    move on by the pause.
- **The chase is a MoveTo.** PoE1's chase (`0x1c8` with the Move action
  `0x2909`, the client pathing) left PoE2's monsters standing. S→C `0x1e0`
  (Positioned, read `0x141f6d540`) makes them glide to the point: object
  header, u32 x, u32 y, u32 height marker (`0x7fffffff`: terrain), u32 float
  duration, three u8 (0).
- **The death screen: S→C `0x6d`** (read `0x142154590`). Found from the
  `death_screen` panel:
  - the panel's constructor `0x1403af980` is built by the HUD at
    `0x140fcf1ec` into HUD `+0xa88`; its setup is `0x1403b0f60`;
  - HUD vtable `+0x5c0` (`0x140fee6d0`) calls the setup from a task
    (`0x141016e40`) queued by in-game `0x141010240`;
  - that function is a listener at instance `+0x12f0`, fired by the instance
    dispatcher's case `0x12` (`0x142123e6f`), which is id `0x6d`.

  Format: u8 (`+0x10`), u8 flags (bit 0 → `+0x1a`, bit 1 → `+0x1b`, bit 2 →
  `+0x18`, bit 3 → `+0x19` = hide, bit 4 → `+0x1c`). The panel takes (bit 2,
  bit 0, bit 1). With flags 0 it shows "You have Died" and "Resurrect at
  Checkpoint".
- **Resurrect: C→S `0x6c`** (4 bytes; live `00 6c 00 00` from "Resurrect at
  Checkpoint", followed by an unframed `0x178`, 8 bytes). As PoE1's server, the
  session answers:
  - the death screen hidden (`0x6d` with bit 3);
  - full life (`0x1cf` from 0 runs the client's revive);
  - a teleport to where the player entered: S→C `0x1dd` (Positioned, read
    `0x141f6d1c0`): object header, u32 x, u32 y, u32 height marker, u32
    float z;
  - every monster calms down (no aggro, 2 s wait), and the living ones heal
    to full.
- A dead player's moves and skill uses get no answer.
- **An attack at the ground is echoed** (`0x1c8` toward the player's cell plus
  the use's relative x, y). Unanswered, the client sent no further skill use.
  The dodge roll still gets no answer; the client runs it alone.

**Live test (2026-11-04).** Spawned by Clearfell's fire (`SPAWN=1154,1297`),
next to three Rotting Guards:
- they glided to the player and their swings took its life (58 → 0 in about
  6 s for the three);
- "You have Died" showed with "Resurrect at Checkpoint", and the click
  revived the player at full life;
- seven punches in a row while being hit killed one guard.

Limits:
- No experience (wave 24), no loot (items).
- The chase glides straight through walls: MoveTo has no pathing. Whether the
  client animates the walk is not checked.
- Damage, aggro range and speed units are PoE1's reading of the columns, not
  checked against PoE2.
- No action hit `0x1fb`, no stun, no flinch, no player regeneration.
- The "in town" button and hardcore death are not handled.

## Wave 24 — experience (2026-11-04)

A kill gives the player experience; the client's experience bar and its level
follow. Code: `InstanceSessionPoe2.Experience.cs`, `InstanceProgressionPoe2.cs`.

- **The experience is a component update: S→C `0x2f9`** (world group, read
  `0x141f6c8d0`). PoE1's `0x2ba` has no PoE2 counterpart.
  - Format: object header, compact length, then that many bytes: the
    component's index in the object's list, then that component's update.
  - The world handler (case at `0x142136123`) picks the component by that
    first byte and calls its vtable `+0xb0` with the rest.
  - A player's components: Positioned 0, Stats 1, Pathfinding 2, Buffs 3,
    Life 4, Animated 5, Player 6.
- **The Player update** (reader `0x141dd0b90`) starts with u16 flags, read raw
  (little-endian):
  - bit 0: u32 experience, raw. It goes to the same setter as at the
    player's creation (`0x141dd1360`), which derives the level;
  - bit 1, bit 3: flags (`0x141deebf0`);
  - bit 2: u16 at `+0x200`; bit 6: u16 at `+0x202`;
  - bit 4: more fields; high byte bit 0: 5 raw bytes.

  So the experience is `02f9`, header, `07`, `06 01 00`, u32 LE total.
- **The gain** is PoE1's rule (`Progression.MonsterExperience`):
  - DefaultMonsterStats' Experience at the monster's level × the variety's
    ExperienceMultiplier, with the level penalty, capped at the start of the
    last level;
  - every monster counts as normal rarity;
  - it goes to the killer only (no range rule; there is one player).
  - A level 2 Rotting Guard gives 18 (multiplier 100 %).
- **The level table**: `scripts/re/poe2/experience_levels.py` writes
  `data/tables/ExperienceLevels.json` from PoE2's ExperienceLevels. Its 100 rows
  match PoE1's (level 2: 525). Without it the experience is 0 and kills level
  nobody up.
- **A new level**:
  - sends the class's new maximum life and mana (`0x1d3`, `Poe2CharacterStats`)
    and refills both (`0x1cf`, life pool and mana pool);
  - the Gateway saves it to Auth (`set_level`), so the character comes back at
    that level's start. The experience into the level is not saved.

**Live test (2026-11-04).** A level 1 Warrior killed a Rotting Guard in
Clearfell. The bar's tooltip read "Level 1 (3%)", "Current Exp: 18 / Next
Level: 525".

Limits:
- The level-up (30 kills at level 1) is checked by unit tests only: the new
  maximums, the refill and the save to Auth.
- The experience into a level is lost when the player leaves the area or logs
  out.
- No rarity, no party share, no PoE2 death penalty.

## Wave 25 — monster drops and the pickup (2026-11-04)

A kill can drop a currency item where the monster fell; a click on its label walks
the player there and puts it in the main inventory. Code:
`InstanceSessionPoe2.Loot.cs`, `WorldItemObjectPoe2.cs`, `Poe2Inventory.cs`,
`Poe2Items.cs`, `scripts/re/poe2/item_types.py`.

- **The item data is PoE1's** (reader `0x1421ecfc0`, wire `0x1421ecef0`):
  - u32 type HASH32, u16 flags (bit 9 → one u64), then each component's data
    through its vtable `+0x28`, in the .it chain's order;
  - on the wire: u16 big-endian length, then that.
  - Base (vtable `0x1435736b0`, read `0x1425a0e50`, as PoE1): u16 visual
    HASH16, u8, u8 (`+0xc7` corrupted), u16 flags, u8; the rest hangs on flags.
  - Usable (vtable `0x143573580`): its read is `ret`, no data.
  - Stack (vtable `0x143574c88`, read `0x1425cd230`): u16 LE count.
  - Currency is Base, Usable, Stack (Item → AbstractCurrency →
    StackableCurrency).
- **The item types**: `scripts/re/poe2/item_types.py` writes
  `data/poe2/item-types.json`: the 568 currency rows players see, with HASH32,
  grid size, drop level, ItemVisualIdentity HASH16, components and CurrencyItems
  stack size. Scroll of Wisdom is `AF0B8850`, visual 2083, stack 40.
- **The ground item** is `Metadata/MiscellaneousObjects/WorldItem`, created with
  `0x2f7`:
  - .ot: Positioned, BaseEvents, Animated, WorldItem, InteractionAction (+ client
    Render, Targetable);
  - Positioned is the PoE2 player's (17 bytes); object flags `0x02` put it in the
    wake zone, as the exits;
  - WorldItem (vtable `0x1434d86b8`, read `0x141e02860`): u32 (non-zero → one
    more u32), u8 flags, u32 visual override (0: the item's), then the item data.
- **The drop** is our own rule, not PoE2's: 35 % per kill
  (`Game_POE2_DROP_CHANCE`, 0–100), then Scroll of Wisdom 60, Orb of
  Transmutation 25, Orb of Augmentation 15 among those whose drop level the
  monster reaches.
- **The pickup click** is a skill use, C→S `0x1a5`:
  - skill `0x2000C266`, flags `0x0400`, options `08`, the item's entity id as
    target, then x, y;
  - live: `01A5 00 2000C266 0400 FF 08 00001007 00000483 00000519`.
  - The server echoes it at once (`0x1c8`, as PoE1's server does), the client
    walks, and on arrival (2 cells away by the walk estimate) the server sends
    the removal and the inventory change. A move cancels it.
- **Remove object: S→C `0x2fa`**, the object header only (read `0x141f4b800`;
  PoE1's `0x2bb`).
- **Inventory change: S→C `0xde`** (read `0x142158b20`, body `0x1427174f0`):
  - u8 route, u8 manager, u32 inventory id, u32 revision;
  - u32 count + the removed item ids;
  - u32 count + per added item (`0x1425aae20`): u32 id, u8 x, u8 y, wire item;
  - u8 flag.
  - The main inventory is id 1, 12 × 5. Revisions start at 1.
  - Stacks of the same type grow first: a grown stack is removed and added
    again under a new id. With no room the item stays on the ground.

**Live test (2026-11-04).** With `Game_POE2_DROP_CHANCE=100` a Rotting Guard
dropped a Scroll of Wisdom with its label. A click on it: the server sent
`0x2fa` and `0xde`, the label went away, and the inventory showed the scroll at
the first cell, "Stack Size: 1/40". An Orb of Augmentation's label and tooltip
("Stack Size: 1/30") were checked earlier.

Limits:
- The inventory lives in the session: it is lost on leaving the area or
  logging out, and not in the save database.
- Only currency drops. No gold (it needs its own counter), no rarity, no
  equipment, no moving items in the inventory, no dropping them.

## Wave 26 — the starting weapon (2026-11-04)

A class with a starting weapon has it in the main hand, sees it on its model and
swings it. Code: `Poe2StartingSkills.cs`, `InstanceSessionPoe2.Loot.cs`,
`InstanceItems.cs`, `PlayerObjectPoe2.cs`, `Poe2Items.cs`,
`CharacterPreviewPoe2.cs`, `Program.Poe2.cs`, `scripts/re/poe2/item_types.py`.

- **The weapons**: `item_types.py` adds each class's Characters StartWeapons row
  to `data/poe2/item-types.json` (580 types): ItemClasses row, ItemStances row,
  WeaponTypes values (damage, attack time, reach, critical), the classes that
  start with it, its basic attack (ActiveSkills `player_melee_*`, GEPL and GESSPL
  rows) and its WieldableClasses row. The Warrior's Wooden Club: class 12,
  6–10 damage, 690 ms, reach 13, Mace Strike rows 400, wieldable 5.
- **In the main hand**: S→C `0xde` into the Weapon1 inventory (Inventories row 2,
  2 × 4), sent right after the empty inventories (`0xdf`), before enter area.
  The player's Inventories records (`VisibleItem`) show it on the model:
  visual slot 0, the ItemVisualIdentity, the stance, type = ItemClasses row + 1
  (`0x1425afac0`; `0x77` none).
- **Usable**: Mods `+0x263`, the u8 right after the level requirement. 0 makes
  the client treat the item as unusable: its popup says "You cannot use this
  item" (`0x1425d1300` → `0x1425d39b0` → `[+0x1964]` → ClientStrings 328, fetched
  by `0x1423bf2b0` with id − 1), and a worn weapon's slot is drawn red. An item
  without Mods gets 7 there (`0x1425d15ff`); we send 7. Verified live: the slot
  is no longer red.
- **The hand weapon types**:
  - stat key 3 `main_hand_weapon_type` / key 4 `off_hand_weapon_type` take the
    WieldableClasses row (0 unarmed, 5 one hand mace). It indexes the client's
    per-weapon bool tables at `0x142f65698` (two-handed?), `+0x20` (one-handed),
    `+0x40`, `+0x60`, `+0x80` (melee);
  - the Actor component's hand records: `+0x2c8` main / `+0x2cc` off, three
    records of stride `0x28` read by the Actor body (`0x142147300`) as u8, u8,
    WStr32; defaults at `+0x924` / `+0x928`. We send the weapon's type
    (ItemClasses row + 1, 13 for the club) in the first.
- **The skill's weapon check** (skill constructor `0x141e848f0`):
  - `+0x188` = stat `0x97f` skill_number_of_triggers ≤ 1;
  - `+0x18c` / `+0x191` / `+0x192` from `0x141e859c0`: stat `0x4795`
    active_skill_use_parent_weapon_type, else the caller's types →
    `0x141e85dc0` → per hand `0x141e862c0`;
  - `+0x193` from stats `0x64e`, `0x71a` skill_unusable, `0x97f`, `0x3f87`;
  - the skill button (`0x140c67b90`, 4th argument the current mana) greys the
    skill when `+0x18c` ≠ 0, `+0x190` = 0, `+0x188` = 0 or `+0x193` ≠ 0, among
    other flags. "[Not enough mana]" is only the label: the cost struct
    (`0x141e87300`: mana 0x2ac2, life 0x3333, ES 0x3659, rage 0x3356…) is 0.
- **Which attack**:
  - Mace Strike (`player_melee_1hmace`, ActiveSkillWeaponRequirement 2 "One Hand
    Mace", WieldableClasses [5]) stays greyed with all of the above sent. Its
    data equals Punch's but for the requirement. Not solved. Leads not yet
    followed:
    - which of the Actor's hand records the check reads depends on
      `[comp+0x210]` → `[+0xd8]` → … → `[+0x158]+0x20` and on the skill's `+0xdc`
      byte; the defaults at `+0x924` / `+0x928` may be the ones used;
    - other flags of the skill button (`[rsp+0x41]`, `[rsp+0x42]`) and `+0x193`;
    - the re-evaluation callers of `0x141e859c0`: `0x141e89b60`, `0x141d82bc0`,
      `0x141e88940`;
    - stats not sent yet: `0x532c` has_main_hand_weapon_stats, `0x532d`
      has_off_hand_weapon_stats, `0x390e` skill_disabled_by_stat.
  - Punch (`player_melee_unarmed`, an Unarmed skill) is offered but the client
    never sends it with a weapon in hand.
  - Default Attack (`player_melee`, GrantedEffects MeleePlayer, GEPL and GESSPL
    row 0; requirement 36 "Any Melee Weapon and Unarmed", WieldableClasses
    3, 8, 4, 9, 5, 10, 0, 16, 14, 1, 17, 20, 26) works: the picker shows it lit,
    the client sends `0x1a5` and its hits land. We give it, with the weapon's
    values, in Punch's slot; unarmed classes keep Punch.
- **Monster damage scale**: three Rotting Guards killed the Warrior in 4 s at the
  data's damage. Our own playability rule, not the game's: monster hit damage is
  scaled to 50 % (`Poe2Monsters.DamageScalePercent`, env
  `Game_POE2_MONSTER_DAMAGE`). No PoE2 rule lowering it was found: the zombie's
  GESSPL 26777 (StatSet 1334 MeleeAtAnimationSpeed) has BaseMultiplier 0.

**Live test (2026-11-04).** The Warrior entered Clearfell with the Wooden Club in
the main hand (tooltip normal, slot not red). With Punch in the slot the clicks
on a Rotting Guard sent nothing. With Default Attack bound from the picker, the
clicks sent `01A5 … 0400 FF 08 …`, two Rotting Guards died (`0x1d3`, and their
drops) and the third was hurt. At 50 % a Rotting Guard hits for about 6.

Limits:
- Bow and crossbow classes get Default Attack too, which their weapons do not
  meet, and there are no projectiles; wand and staff classes (no weapon values)
  keep Punch with the weapon in hand, which the client will likely refuse. Only
  the Warrior was tested.
- The weapon is given again at each entry; equipment is not saved and cannot be
  moved.
- The weapon's own basic attack (Mace Strike etc.) is not usable: fixed in the
  wave 26 follow-up below (after wave 27).

## Wave 27 — NPCs, dialogue, quest flags and waypoints (2026-11-04)

The town's NPCs, waypoint, stash and well exist; a click on an NPC opens its
dialogue and its options set the character's quest flags, which the client
keeps and the NPCs' states follow; the waypoint activates, opens the World panel
and travels. Code: `ServerObjectPoe2.cs`, `Poe2ServerObjects.cs`,
`InstanceNpcsPoe2.cs`, `InstanceQuestsPoe2.cs`, `InstanceWaypointsPoe2.cs`,
`Poe2Quests.cs`, `InstanceSessionPoe2.Npcs.cs`, `InstanceSessionPoe2.Quests.cs`,
`Program.Poe2.cs`, `InstanceDirectory.cs`, `scripts/re/poe2/server_objects.py`,
`scripts/re/poe2/quests.py`.

- **Server objects.** `scripts/re/poe2/server_objects.py` writes
  `data/poe2/server-objects.json`: the types the captures place under
  `Metadata/NPC/` and `Metadata/MiscellaneousObjects/` with an
  InteractionAction (21 in Clearfell Encampment), their .ot components
  (`ot_components.py`), StateMachine shared states and defaults, the
  `per_player_quest_flag_state_<state>_<value> = "Flag"` rules, and the
  NPCTalk rows of the type's NPCs row.
  - 19 objects are created with `0x2f7` after the monsters
    (`ServerObjectId(i) = MonsterId(monster count) + i`; dropped items come
    after them). Every blob is read exactly. The minimap shows Finn, Stash,
    Una, the well and the waypoint; Una has her quest marker.
  - An unknown type crashes the client (type lookup `0x141693840`):
    DogTrader_Entrance (extends "nothing") and the townspeople (Doodads) are
    the client's own and are not sent.
  - Blob per component (`ServerObjectPoe2.Blob`): Positioned 17 bytes,
    Animated (u8 1 + WStr32 .ao), InteractionAction u8 0, NPC u8 1 u8 0xff
    (reader `0x141d96820`), MinimapIcon u8 2, Stats 7 bytes, Transitionable
    u8, StateMachine (u8 count, u8 0 timers, per state compact value + u8 2),
    BaseEvents / Pathfinding / Functions nothing, HideoutDoodad nothing
    outside a hideout.
  - The town places every variant of an NPC (Una, UnaAfterIronCount, ...);
    the `visible` state of each follows the player's flags, so the client
    shows the one for its progress.
- **Talking.** A click on an NPC is C→S `0x1a5` with skill `0x2000C266` and
  the NPC as target (as a pickup). The server echoes `0x1c8`, walks the
  estimate to `TalkReach` (6 cells), then sends **S→C `0xa3`**: PoE1's `0x84`.
  Object header (the NPC), u8 mode (0xff = the option list), u8 player slot
  (the local player's `+0x181`, 0).
  - World handler `0x1421343c0`, case `0x1421349ed`, then
    `0x1421065a0(player, id, mode)`. Id 0 ends the dialogue; the dialogue
    state is at `+0x8150`, the NPC id at `+0x8154`.
  - The client builds the option list itself from NPCTalk and the player's
    quest flags: an option needs all of its column `_3` flags and none of its
    `_4` flags (PoE1's QuestFlag1 / QuestFlag2). That is why the flags must
    reach the client.
- **Options.** C→S **`0xa1`** (4 bytes: route, NPCTalk column `_1`, the
  option's index among the NPC's rows) and **`0xa4`** (3 bytes: Goodbye, Esc,
  or Continue at the end of a talk), PoE1's `0x82` / `0x85`.
  - BuyItems answers **S→C `0xaf`**, PoE1's `0x8c` buy window, still empty.
  - Any other option sets its start flags (`QuestFlags1`, and the Script's
    `SetQuestFlag(...)`, delays ignored), then plays its talk: `0xa3` with the
    index as mode. `0xa4` then sets the finish flag (`QuestFlag2`; `NEVER_SET`
    is none) and sends `0xa3` 0xff (back to the options); `0xa4` on the option
    list sends `0xa3` with id 0 (end).
  - A Script with `triggerer.FinishNPCChat()` ends the dialogue at once (`0xa3`
    id 0) and sets both flag columns (Una's "Use Ring", the Music options).
  - The server refuses an option the flags exclude. Without the table's row
    it plays the talk anyway.
- **Quest flags.** PoE2 has no message of its own, as PoE1 has none: the set
  goes in a Player component update, **S→C `0x2f9`** component 6
  (`InstanceQuestsPoe2.PlayerQuestFlags`), u16 LE flags bit 1 (`0x0002`), then
  the whole set. Reader `0x141dd0b90` (wave 24's) takes the set into a
  temporary and hands it to the setter `0x141dd2ad0`; bit 3 is the second set
  (`+0x260`), the high byte's bit 0 five raw bytes (`+0x278`).
  - A flag set (`0x141deebf0`, PoE1's `0x141e83340`): u16 LE block count,
    then per block u8 block (row >> 6) and u64 LE bits. A flag is a
    QuestFlags.datc64 row (6252 in 0.5.5e). The creation blob carries the two
    sets after the experience and four scalars (`PlayerObjectPoe2.QuestFlags`);
    the session sends the first again anyway, after the area's objects.
  - Storage: Auth's account store, on the PoE2 character (`Character.QuestFlags`,
    `Waypoints`): control ops `set_quest_flags` / `set_waypoints` (the whole
    list) and both lists in `resolve`. The Gateway saves at each change.
  - Entering an area sets its WorldAreas `FlagOnEntered` flags
    (`Visited-G1_town`, `Visited-G1_2`): Una's "Home" gossip needs
    `Visited-G1_2`.
  - When a flag changes an NPC's states, the object is removed (`0x2fa`) and
    created again (`0x2f7`) with the new states; the NPC whose dialogue is
    open waits for the dialogue's end.
  - `scripts/re/poe2/quests.py` writes `data/poe2/quests.json`: the flag Ids in
    row order, the waypoint areas (below) and each area's entry flags.
- **Waypoints.** The character's activated waypoints are **S→C `0x5a`**
  (class `0x142153b40`, reader `0x142153c00`), PoE1's `0x4c` with the route
  byte: u8 route, u32 BE, 22 raw bytes.
  - The instance dispatcher (`0x1421220f0`, switch base 0xd, byte table
    `0x14212593c`) case `0x142123878` copies the 22 bytes to instance
    `+0x2318`, calls the listeners at `+0xff0` with the u32 and the bitset,
    and compares the current area's bit before and after (`0x141f68b30`):
    turned on by a later `0x5a`, the client plays the waypoint's activation
    (`0x142121fe0`, state 2). Areas of the kinds in mask `0x4109008`
    (`0x141f68d10`) count as always on.
  - Bit order (`0x141f68930`, built once, at most 0xaf = 175 entries): the
    WorldAreas rows (stride 0x2e0) with HasWaypoint (`+0x15`) whose `+0x1b4`
    is 1 (a town), 2 (an area) or 31; column `_45` of the extracted table.
    0.5.5e has 103: G1_town bit 0, G1_2 bit 1, G1_4 bit 2 ...
  - The server activates a town's waypoint on arrival and any waypoint on a
    click or within 25 cells (PoE1's rules), saves the list and sends `0x5a`
    again.
  - A click on the waypoint then sends **S→C `0x98`** kind 0 (PoE1's `0x7b`
    OpenUi with the route byte; reader `0x142155300`), its first u32 the
    waypoint object: the World panel opens.
  - A pin sends **C→S `0x5b`** (10 bytes; sender `0x142110920`, vtable
    `0x1434e6548`, writer `0x141f396f0`, from the pin click `0x140bb4a70`):
    u8 route, u16 BE destination HASH16 (the pin's WorldAreas row `+0x2a`),
    u32 BE (the panel's `+0x340`, the waypoint `0x98` named), u8 Ctrl. PoE1's
    `0x4d`.
  - The server checks the destination's waypoint is activated and not this
    area's, then changes area as for an exit (`0x2f1`, the `0x29` transfer);
    the new connection arrives 8–20 cells from the destination's waypoint
    object (`Poe2Areas.WaypointArrival`; `CameFrom` remembers `ByWaypoint`).
    Ctrl (PoE1's instance chooser) travels too.

**Live test (2026-11-04).** In Clearfell Encampment the 19 objects appeared
with their minimap icons. Una: Introduction, Clearfell and Secrets in the Dark
played; Buy or Sell items opened an empty buy window; Goodbye closed it. The
quest flags went out with `0x2f9` after the objects and after each option; Auth
saved them (6 after the tour); after a relog Una's Introduction had changed
state in her list. The camp's waypoint was lit on arrival, its pin lit on the
World panel; in Clearfell the waypoint lit up on approach (`0x5a` bit 1,
"2 waypoints saved"); the pin click sent `0x5b` both ways (`005B 00 9875
00001006 00` from the camp, `005B 00 3F70 00001007 00` from Clearfell), the
transfer arrived 8 cells from the destination's waypoint. Two fixes came out
of it:
- Clearfell's arrival markers (AreaTransition_Auto) sit in the exit tiles'
  blocked border, 14 cells outside the walkable ground: the player arrived
  "off the map", the server's estimate walked through walls from there (a
  blocked start cell walks freely) and every echo pulled the client back. A
  marker off the walkable ground now gives way to a walkable cell 8–20 cells
  from the exit (`Poe2Areas.Arrival`): the gate now lands on (993, 482).
- The World panel hid the camp's pin, and the map's border, while the
  character lacked `Visited-G1_1`: MapPins reveal a pin when any of its
  `QuestFlags1` is set (the camp's and the border's need The Riverbank
  visited; Clearfell's needs `Visited-G1_town`). Auth enters new characters
  in the camp, so entering an area now also grants the flags the game expects
  there (`quests.json` "granted": the area's WorldAreas `RequiredFlags`,
  `MillerDefeated` in Act 1, and in a town the `FlagOnEntered` of the act's
  first areas, those connecting to the town alone: `Visited-G1_1`).

Ruled out (do not retry):
- `0x87` (reader `0x142154aa0`: u8, u32, u8, a 17-byte block) as the talk
  message: read and ignored with any mode; its 6-byte PoE1 layout stopped the
  client ("Unable to deserialise packet with pid 62466").
- `0xda` as the buy window: the trade close (listener `0x140de08d0`), which
  crashes at `0x140de0a90` on an empty trade list.

Limits:
- No shop: `0xaf` sends no stock (its line list u16 n + n × u32 and its
  0x38-byte tabs, reader `0x142175980`, are unread), no NPCShop data, no buy,
  sell or Disenchant. QuestReward options do nothing; Music options end the
  dialogue (their `Script2` interlude is the client's). Left for a wave of
  its own.
- The Script's `ForceInteractWithNPC`, `DirtyTransition` and delays are
  ignored; `DirtyStateMachine` is covered by the state recomputation.
- A click on the stash, the guild stash or the healing well does nothing.
  RenlyIntro's ControlZone component is not written.
- `TalkReach` 6 is a guess (NPC.ot `object_size` 5 + 1); the approach is a
  straight line and can stop on a wall. The dialogue state is not cleared
  when the player walks away. Every click on the waypoint resends `0x98`.
- Travel is possible between the areas with a capture only (the town and
  Clearfell); the destination's waypoint must have been activated.

## Wave 26 follow-up — the weapon's own attack usable (2026-11-04)

Mace Strike, the Wooden Club's own attack, is offered, binds and hits. Code:
`Poe2StartingSkills.cs` (`WeaponAttackId`, `WeaponSetOneSource`),
`InstanceSkillsPoe2.WeaponRecords`, `InstanceSessionPoe2.EnterWorld`,
`scripts/re/frida/poe2-trace-weapon-check.js`.

- **What was wrong.** Two things: the server did not grant the weapon's attack at
  all since wave 26 (only Default Attack), and once granted the client judged
  it with the wrong hands. Traced live with Frida (the new script hooks the
  per-skill evaluation `0x141e859c0`, the per-hand check `0x141e862c0` and the
  type lookup `0x14268cdb0`): every skill was evaluated with hand types
  **37, 119** whatever the Actor blob held, that is Unarmed (ItemClasses row 36
  + 1) and none (`0x77`).
- **The weapon check**, now mapped:
  - hand types are **ItemClasses row + 1**, `0x77` none (`0x1425afac0` on an
    ItemClasses foreign key; `0x14268cdb0` maps a type to the WieldableClasses
    row of that ItemClasses row);
  - `0x141e859c0(skill, out, types)` → `0x141e85dc0` (the skill's
    ActiveSkillTypes rules, both hands) → `0x141e862c0(type, isMain, stats,
    requirement)` per hand: with a requirement list (the
    ActiveSkillWeaponRequirement's WieldableClasses rows, at the holder's
    `+0x136`) the hand's row must be in it; stats `0x4c09`, `0x26ca`, `0x26cb`,
    `0x52a7`, `0x65a9`, `0x53e7`/`0x53e8`, `0xf80`/`0xf81`, `0x56f8`, `0x1f22`
    bend the rule. The result is two bools (main, off) and a status (0 usable,
    2 not: `+0x18c`, which the skill button greys);
  - **which hands**: the skill's `+0xdc` byte, the skill record's second byte
    (wave 21 called it the stats source): bits 7–6 the weapon set, bits 5–0
    the Actor hand record. Set 1 or 2 reads the Actor's record (`+0x2c8` +
    `0x28` × record: u32 main, u32 off, name); **set 0 reads the Actor's
    defaults `+0x924` / `+0x928`** (37, `0x77`). Our skills had 0, hence the
    unarmed verdict; Default Attack passed anyway because its requirement
    (36, "Any Melee Weapon and Unarmed") accepts the empty off hand.
- **S→C `0x324`** (world group, vtable `0x14350c450`, reader `0x141f6b3a0`;
  the Actor handler `0x1421497c0`, case `0x14214b15e`): the Actor's three
  weapon records again. Object header, u8 (passed to the Actor's vtable
  `+0x3d0`), then 3 × (u8 main type, u8 off type, wstr: u16 BE unit count +
  UTF-16). The server sends it after the skill list with (13, `0x77`) twice
  (the current hands and weapon set I) and an empty set II; the trace then
  showed types 13, 119.
- **The fix**: the weapon's attack is granted as a third skill (`0x104`, the
  item type's GEPL/GESSPL rows, the weapon's values) with source byte `0x40`
  (weapon set I, record 0), and `0x324` follows the skill list.

**Live test (2026-11-04).** Mace Strike lit in the picker, bound to the left
button (`0x5c` skill `0x104`); clicks on Rotting Guards sent `0x1a5` with
`0x104`, the hits landed (`0x1cf`) and a guard died (`0x1d3`, `0x2f9`). Before
the source byte the trace read 37/119 and the button said "[Not enough mana]"
at full mana (a decoy label, as in wave 26).

Limits:
- Weapon set II and the weapon swap are not handled; Default Attack keeps
  source 0 (it works through the empty off hand).
- The creation blob's first record still sends off hand 0 (the client resets
  it to `0x77`); `0x324` is what the client keeps.
- C→S `0x16` (7 bytes) appears while the skill picker is open; unframed, it
  is dropped.
- Frida cannot attach to a client started from an elevated PowerShell
  ("unable to access process"); start it from a normal one.


## Wave 28 — the passive skill tree (2026-11-04)

The tree screen (P) shows the character's allocated nodes, allocates and refunds
them, and the nodes' stats reach the character sheet. Code:
`InstancePassivesPoe2.cs` (S→C `0x3a`, C→S `0x3c` / `0x3e` / `0x48`),
`InstanceSessionPoe2.Passives.cs`, `Poe2Passives.cs` (the nodes and their
stats), `Poe2CharacterStats.For(row, level, modifiers)`, the Auth op
`set_passives` (`Character.Passives`), `scripts/re/poe2/passives.py`.

- **Data.** `passives.py` writes `data/poe2/passives.json` from
  PassiveSkills.datc64: one entry per row with a PassiveSkillGraphId (8783),
  the id the client allocates and the server lists; its stats as (Stats Id,
  client key = Stats row + 1, StatNValue), SkillPointsGranted and the
  IsKeystone / IsNotable / IsAttribute / IsJewelSocket flags. An attribute node
  ("Attribute", IsAttribute) carries no stat: the client sends the generic
  node chosen (generic_attribute_strength / dexterity / intelligence, rows
  300–302, graph ids `0x66b9` / `0x3a4f` / `0xdebe`) and that node's stat
  counts. The 11 IsMultipleChoice ascendancy nodes and their 36 options are
  not handled.
- **S→C `0x3a`** (reader `0x142152800`, class size `0xb8`; the per-player
  dispatcher case `0x142107efd` hands it to the tree at `0x142108058` →
  `0x141e9b870`): u8 route; u16 BE quest points (`+0xa8` → tree `+0x2c8`,
  added to the level's), u16 BE (`+0xaa` → `+0x2d8`), u16 BE weapon set
  points (`+0xac` → `+0x2cc`, both sets); u8 level (`+0xaf` → `+0x2c4`); u8
  class (`+0xae` → `+0x2b8`); a flag set (u16 BE count of (u8 block, u64);
  sent empty); u8 flags (bit 1: no lists follow); then five lists:
  - `+0x18`, u16 BE n × (u16 BE node, u8 weapon set): the allocated nodes,
    sorted, diffed against the tree's `+0x1a8` (new nodes go through
    `0x141ea0a90(tree, record, set)`, dropped ones through `0x141ea0ee0`);
  - `+0x30` and `+0x48`, u16 BE n × raw u16 (`0x1421749d0`): unused here;
  - `+0x60`, u16 BE n × raw u32 (node | u16 << 16): copied into the tree's
    `+0x208` list (not the choices; sent empty);
  - `+0x78`, **u8** n × raw u32 (node | chosen node << 16, sorted by node):
    the attribute choices; it replaces the tree's `+0x1d8` list, which
    `0x141eb1690` searches by node to find a node's chosen record.
  The points shown are (level − 1) × 1 (`[+0x168]->+0x10`) + `+0x2c8` +
  the nodes' SkillPointsGranted (`+0x2d0`) − the allocated count
  (`0x141ea1580`). Live with `Game_POE2_PASSIVE_POINTS=10` at level 5:
  "Skill Points: 14", "Weapon Set: 40 (40)" with 40 in `+0xac`.
- **The client's checks** (`0x141e9e100` after every tree message →
  `0x141e9d510`, predicate `0x141e9dca0` per allocated node): the node must be
  reachable from the class start in the connectivity bytes built by
  `0x141ebb3a0` (one byte per node, bit = weapon set), an ascendancy node must
  match the class, and an attribute node (record `+0x1b3` non-null) must have
  a chosen record in `+0x1d8` that is one of its options (`0x141eb1120`).
  Otherwise "Removing N invalid/unconnected passive skills" (log hashes
  `f4ab7330` / `f4ab733c`) and the node is undone. A click allocates through
  `0x141e9cc00` with the chosen record ("Successfully allocated passive
  skill", `f4ab5ad5`), so the server is told only on Apply.
- **C→S** (sender `0x1409ac830`): `0x3c` allocate, 9 bytes: u8 route, u16 BE
  node, u8, u16 BE chosen node or 0, u8 (`003C 00 0F60 00 0000 00`, `003C 00
  3455 00 66B9 00`), one per node of the path, sent on Apply; `0x3e` refund,
  6 bytes: u8 route, u16 BE node, u8; `0x48` refund all, 3 bytes. `0x46` /
  `0x47` (`0x360` / `0x361`, 7 and 5 bytes) are built by the same screen and
  not mapped. The client checks the connection itself; the server checks the
  points, keeps the node with its choice and sends the whole tree again (the
  client undoes a click the tree comes back without). A node the server
  already has costs no point when asked for again (the client re-sends what it
  had dropped).
- **Stats.** `Poe2CharacterStats.For(row, level, modifiers)` folds
  base_strength / dexterity / intelligence, additional_all_attributes,
  base_maximum_life, maximum_life_+%, base_maximum_mana and maximum_mana_+%
  into the sheet (life = (16 + 12 × level + 2 × strength + flats) ×
  (100 + %) / 100, mana likewise with intelligence) and sends every other stat
  summed by key in the player's Stats update (`0x1d3`); the maximums move,
  the pools are not refilled.
- **Saving.** The allocations (node | choice << 16) go to Auth with
  `set_passives` at every change and come back in the ticket; the sheet on
  entering counts them.
- **Gold and the refund.** A refund costs gold: `Data/Balance/GoldRespecPrices.dat`
  (100 rows, level → cost: 15 at level 1, 19, 25, 31, 39 ... 10 129 at 100;
  `passives.py` writes it to `data/poe2/gold-respec-prices.json`). The
  character's gold is **S→C `0x2db`** (reader `0x14086d300`, class `0x20`;
  dispatcher `0x1420ee490`, case `0x1420f14b6`): u8 route, u32 BE amount,
  stored at `+0x39d0` of the player state (`[[state+0x1b0]+0x39d0]`, what the
  script function `GEAL::GetCurrentGoldAmount`, `0x14190a830`, returns; the
  inventory panel shows it). The tree's Apply bar (`0x140a12880`) shows the
  pending refunds' total against it, and the request builder `0x1409ad790` →
  `0x1409e3500(out, state, node, panel, flag)` (u32 cost, u8 affordable)
  queues a refund only while the gold covers it. The Gateway sends `0x2db`
  after the tree on entering (`Game_POE2_GOLD`, the same at each entry, not
  saved) and again after each refund; a refund (`0x3e`) or refund all (`0x48`)
  costs the level's price per node and is refused whole (tree and gold sent
  back unchanged) when the gold is short.

**Live test (2026-11-04).** Normal nodes allocate, stay allocated after Apply,
are saved and reloaded, and the character sheet moves (Melee Damage, Armour,
Life Regeneration); an Attribute node chosen as Strength gives +5 Strength
and +10 life. Before the lists were told apart: nodes in `+0x30` stayed
unallocated; choices in `+0x60` left every attribute node "invalid" and the
nodes behind it unconnected. The gold shows (500 in the inventory, parsed by
`0x14086d300` per Frida), but **a click on an allocated node still does
nothing**: traced with `scripts/re/frida/poe2-trace-refund.js`, neither the
model's can-unallocate (`0x141eb0cb0(tree, node, budget, flag)`, vtable slot
after `0x141e9cf50` unallocate at `0x1434dcf98`) nor the cost `0x1409e3500`
nor the queue `0x1409b5e50` is called: the screen rejects the click before the
model. Only the Apply bar's refresh `0x140a12880` ran (from `0x140a1b0d0`).
Open: what the screen checks first (a tree mode as PoE1's respec mode at an
NPC, a flag in the `0x3a` flag set, or the `+0xaa` refund points); a node
allocated and undone in the same Apply batch does unallocate client-side
("Successfully unallocated", hash `f4ab5b1d`, no message).

Limits:
- Quest passive points are a Gateway knob (`Game_POE2_PASSIVE_POINTS`, added to
  every character); PoE2's quest rewards (+2 per quest) are not granted.
- The refund is implemented server-side but not reachable from the client yet
  (above). The refund points field (`+0xaa`) is sent as 0, the weapon set
  points as 0 and the per-node weapon set byte as 0.
- Jewel sockets, ascendancies, the IsMultipleChoice nodes and PoE2's
  "allocated by a jewel" stats are out.

## Wave 29 — experience and gold saved, gold piles (2026-11-05)

Written without a live test (the client was closed for the night): unit-tested
only, to be checked in game first thing. Code: `Character.Experience` /
`Character.Gold` (`Models.cs`), `AccountService.SetProgress`, the control op
`set_progress` (`InstanceControl.cs`, `InstanceDirectory.cs`),
`InstanceSessionPoe2.Experience.cs` (`ProgressChanged`),
`InstanceSessionPoe2.Loot.cs`, `Poe2Items.RollGold`, `Program.Poe2.cs`.

- **Saving.** The character's total experience and gold go to Auth together
  (`set_progress`, the Gateway's `SavePoe2Progress`) whenever either moves: a
  kill, a gold pile taken, a passive refund. `resolve` returns both; on entering
  the player's experience is the saved total (at least the level's start) and
  its gold the saved amount (`Game_POE2_GOLD` only for a character with none).
  This closes "the experience into a level is not saved" from wave 24.
- **Gold piles.** A kill also rolls a gold pile (`Poe2Items.RollGold`: 50 %,
  3 + 2 × level to 8 + 4 × level coins, our own numbers), a WorldItem of
  `Metadata/Items/Currency/GoldCoin` (BaseItemTypes row 1943, "Gold", stack
  up to 10 000) dropped where the monster fell, after the currency roll on the
  same generator. A click walks to it as to any item; on arrival the pile
  leaves the ground (`0x2fa`) and its stack goes to the character's gold
  (`0x2db`), not to the inventory. Nothing drops without the item type in
  `data/poe2/item-types.json`.

To check live: the gold pile's look on the ground (the client may draw gold
with its own object type rather than a WorldItem), the pickup (PoE2 picks gold
up by walking over it: the client may send something other than the item
click `0x1a5` / `0x2000c266`), the gold label after the pickup, and the
experience and gold after a relog.

## Wave 30 — skill gems (2026-11-05)

Written without a live test: the data, the server model and the message
layouts come from the tables and the disassembly; the client's reactions are
to be checked in game (list at the end). Code: `Poe2Gems.cs`,
`InstanceGemsPoe2.cs`, `InstanceSessionPoe2.Gems.cs`, `SkillsPoe2.cs`
(additional stat sets), `Poe2Inventory.Take`, `Poe2Items.RollUncutGem`, the
Auth op `set_gems` (`Character.Gems`), `scripts/re/poe2/gems.py`,
`item_types.py` (the gems), `cs_sigs.py`.

- **PoE2's gems.** A skill gem is an item (`Metadata/Items/Gems/SkillGem*`,
  641 bases; .it chain Base, SkillGem, Quality, Sockets for an active gem, Base,
  SkillGem, Quality for a support) that sits in the skill slots inventory
  (Inventories row 62 `SkillSlots1`, type 41, 15 × 1; `DefaultAttackSkills1`
  94 and `AscendancySkills1` 95 beside it) and grants its effect's skill. It is
  made by cutting an **uncut gem**: `SkillGemUncut1`…`20` (BaseItemTypes
  4902+, `UncutGems.datc64` tier = the gem level; Base, Usable, Stack),
  `SupportGemUncut`, `ReservationGemUncut` (spirit). SkillGems: GemType 0
  skill, 1 support (its effect IsSupport), 2 spirit; GemColour 1 red, 2 green,
  3 blue, 4 white; MinLevelReq; the attribute percents; GemEffects (the first
  is the regular gem) → GrantedEffect → ActiveSkill, StatSet and
  AdditionalStatSets. `gems.py` writes `data/poe2/gems.json` (1188 gems, 47
  uncut): per gem level the GEPL row and the GESSPL rows (StatSet first, then
  the additional ones, by (StatSet, GemLevel)). Rolling Slam: base 589, GEPL
  6113, GESSPL 7797 + 7837 (first slam) + 7877 (second slam).
- **The Actor skill record** takes one more stat-set reference per additional
  stat set and one more (empty) stats list each (`GrantedSkillPoe2.
  AdditionalStatSetRows`, wave 21's format); the earlier skills had one set.
- **The gem window** is the client's "SkillCrafter" (strings
  `SkillCrafterWindow`, `GemcuttingNew/…`, `UncutGemCannotBeCraftedError`,
  `Data/Balance/SkillCraftingData.dat`, `UncutGems.dat`). Its confirm
  (`0x140a4ca20`) sends **C→S `0x5f`** (vtable `0x1434e5198`, writer
  `0x141f388f0`: 2 1 4 4 4): u8 route, u32 BE the selected entry's `+0x8c`,
  u32 BE the uncut gem's inventory (`+0x424`, looked up with `0x141cf1b70`),
  u32 BE its item (`+0x428`); with a target it calls `0x142113800` → **`0x60`**
  (writer `0x141f39f80`: 2 1 4 4 4 4 1): inventory, item, target, the entry,
  u8. **Open:** what `+0x8c` is (`Poe2Gems.Resolve` tries the BaseItemTypes
  row, the HASH32, the SkillGems row and the GemEffects row, in that order) and
  the field order on the wire (the builders' store order is assumed).
- **The server** (`CutGem`): the uncut gem leaves the inventory (`0xde`), the
  gem becomes an item of the skill slots (`0xde` to inventory 63; SkillGem
  data as PoE1's: i32 experience, u32 variant, u8 flags, u16 — not verified on
  the PoE2 reader), its skill joins the Actor (**S→C `0x2f9` to component 8,
  the Actor: u8 flags `0x08` then the whole skill list**, the update path of
  `0x142149510`, wave 21; not seen live) and the picker (`0x66` merge). Skill
  ids: `0x112` + slot. The gems are saved (`set_gems`, row | level << 16 |
  slot << 24) and come back as skills on entering. Supports and spirit gems
  are refused (no socket model yet). **A new character starts with its class's
  gem socketed**: Characters `StartSkillGem` (Rolling Slam for the Warrior
  `StrFourb`, Spark `IntFourb`, Lightning Arrow `DexFour`, Whirling Slash
  `DexFourb`, Unearth `IntFour`, Fragmentation Rounds `StrDexFourb`, Falling
  Thunder `DexIntFourb`, Volcano `StrIntFourb`; the four `Four` Str classes
  have unused ones), `gems.json`'s "starting"; a saved character without
  gems gets it too, and it is saved. Kills drop uncut gems (4 %, the highest
  tier the level reaches); `startingUncutGem` is a test knob only.
- **`scripts/re/poe2/cs_sigs.py`**: the catalogue of every C→S message
  builder (callers of the send helper `0x14211fa00`, 514 sites): id, vtable,
  builder, the writer slot (the one that starts with `movzx ecx, word ptr
  [rcx+8]; call htons`) and its writes. That is how `0x5f` / `0x60` were
  found; the catalogue (`cs.txt`) is the map for the inventory, flask and item
  messages.

To check live: the gem window opens on the uncut gem and lists gems; what
`0x5f` carries (the Gateway logs the chunk with `Game_POE2_TRACE=1`); whether
the client accepts the skill gem item in inventory 63 and the Actor update
(the picker should offer Rolling Slam); the uncut gem item in the inventory;
the uncut drops on the ground.

## Wave 31 — inventory moves and the saved inventory (2026-11-05)

Written without a live test. Code: `InstanceItemsPoe2.cs`,
`InstanceSessionPoe2.Items.cs`, `Poe2Inventory` (`Find`, `Remove`, `Restore`,
`Place`), `SavedItem` (`Models.cs`), the Auth op `set_items`.

- **The messages** come from the C→S catalogue (`cs_sigs.py`), by the shape of
  PoE1's `0x28` / `0x2a` plus the route byte:
  - **C→S `0x39`, pick up** (19 − 1 = 18 bytes; built by `0x142113b90` from the
    inventory panel's click record `{u32 inventory, u32 item, u32, u16}`,
    writer `0x141f36d30`): u8 route, u32 BE inventory id (the panel's
    `+0x6f0`), u32 BE item id, u32 BE, u8 `0xff`, u8, u8;
  - **C→S `0x38`, place** (19 bytes; `0x142113c90(session, inventory, x, y,
    u16)`, writer `0x141f36fc0`, from `0x14047d990` with the panel's
    `+0x6f0`): u8 route, u32 BE inventory id, u32 BE x, u32 BE y, u8 (1 with
    Shift: one of the held stack, as PoE1's `+0x1d`), u8 `0xff`, u8, u8.
  The byte order inside each is the builders' store order, not seen on the
  wire; the ids are confirmed by their callers (the inventory panel
  `0x14047d5f0` / `0x14047d990`, the gem panel `0x140aa14a3` "SkillGem",
  `0x140a87400`).
- **The server** keeps the cursor as PoE2's `Cursor1` inventory (row 12, id
  13): a pick-up removes the item from its inventory (`0xde`) and adds it to
  the cursor (`0xde`); a placement puts the held item at the cell (free cell;
  a stack of its type grows, the rest stays held; another item is swapped
  onto the cursor; two items under it or a cell off the grid refuse), with
  Shift one of the stack. Only the main inventory (id 1) and the cursor are
  moved: the equipment, the flasks and the skill slots refuse for now (an
  equipment move would need the model's Inventories component and the
  weapon records again).
- **Saving.** The main inventory and the cursor go to Auth after each change
  (`set_items`, `SavedItem`: inventory, cell, type HASH32, stack) and come
  back on entering (`0xde` after the starting weapon; what was held returns
  to the inventory where it fits; unknown types are dropped). Mods are not
  kept (nothing dropped has any yet). This closes "the inventory is not
  saved" from wave 25.

To check live: the two ids and their field order (the Gateway's trace shows
the chunks; a wrong order shows as a refused move), a swap, a stack merge,
Shift-click, and the inventory after a relog. The gem panel's `0x39`
(`0x140aa14a3`) on a socketed gem is not answered: the skill list cannot
remove a skill.

## Wave 32 — flasks (2026-11-05)

Written without a live test. Code: `InstanceFlasksPoe2.cs`,
`InstanceSessionPoe2.Flasks.cs`, `Poe2Items.FlaskValues` / `CreateFlask`,
`Poe2Inventory.Replace`, `item_types.py` (the flasks).

- **The data.** `Flasks.datc64` (31 rows: Type 1 life / 2 mana, LifePerUse,
  ManaPerUse, RecoveryTime in tenths of a second) and `ComponentCharges.datc64`
  (MaxCharges, PerCharge, by base item): Lesser Life Flask
  (`Metadata/Items/Flasks/FourFlaskLife1`, BaseItemTypes 2623) recovers 50
  life over 3 s, 60 charges, 10 a use; Lesser Mana Flask (`FourFlaskMana1`,
  2632) 50 mana over 2 s. The .it chain is Base, Quality, LocalStats,
  Charges, Mods, Usable, Flask (1 × 2); `item_types.py` writes them with a
  `flask` object. The flask bar is Inventories row 11 `Flask1` (type 10, 5 ×
  2), id 12.
- **C→S `0x70`, use a flask** (11 bytes; the flask bar `0x14047775d` →
  `0x142110150(session, inventory, item)`, writer `0x141f38ae0` "2 1 4 4"):
  u8 route, u32 BE the bar's inventory id (`+0x6f0`), u32 BE the flask's item
  id (the builder looks the item up by id in the inventory, `0x141cf1b70`).
  PoE1's `0x70` named a slot in 6 bytes.
- **The server** gives a new character the two flasks full (0xde on
  entering, after the inventory), spends a use's charges (the flask sent
  again, 0xde) and recovers the amount over the flask's time in the ticks
  (0x1cf life, or the mana pool 4; the server now keeps a mana value, which
  nothing spends yet); a flask without the charges does nothing. Every kill
  gives each flask 1 charge (ours; PoE2's number is a flask stat) and
  resurrecting refills them. The flasks are saved with the inventory (their
  cell and type; they come back full).

To check live: the id and its two fields; whether the client expects the
Charges component as PoE1's signed compact (the item would not parse
otherwise) and shows the recovery; the flask bar's slots (PoE2 shows two).

## Wave 33 — equipment drops (2026-11-05)

Written without a live test. Code: `Poe2Items.RollGear` / `IsGearBase` /
`ArmourValues`, `InstanceSessionPoe2.Loot.cs`, `item_types.py` (the
equipment).

- **The bases.** `item_types.py` now covers `Metadata/Items/Weapons/`,
  `Armours/`, `Rings/`, `Amulets/`, `Belts/` and `Quivers/` (3612 item types
  in all) with every weapon's WeaponTypes values and WieldableClasses row and
  every armour's ArmourTypes values (`armour`, `evasion`, `energyShield`), the
  item class and its name. PoE2's own bases are the "Four…" files
  (FourOneHandMace1 Wooden Club, FourBodyStr1 Rusted Cuirass, FourBow1 Crude
  Bow ...); the others under the same folders are PoE1's, dev items
  (Keyblade) or skill-made ones (Energy Blade), so `IsGearBase` keeps the
  "Four" ones.
- **The drop** is ours, not PoE2's: 12 % per kill, one base among those whose
  DropLevel the monster's level reaches, each as likely, normal rarity, item
  level the monster's, level requirement the base's. It goes out as the
  currency does (a WorldItem, the item data from the base's .it chain: Base,
  Mods, Weapon or Armour, Sockets ...; the Armour component's u8 roll is 0),
  is picked up into the inventory at its grid size, moved and saved like the
  rest.
- **Not done:** mods (magic and rare items): `dat.py` cannot read PoE2's
  `Mods.datc64` with the schema it has (rows are 693 bytes, the schema 661:
  the four "StatNValue" i32 columns at +126 are really min/max pairs, Stat1
  5–8 for Strength1, so Stat3/Stat4's ranges add 16 bytes and the spawn
  weight tags sit at +158, not +142; 16 more bytes are missing further on),
  so every drop is white; **equipping** (a move into Weapon1, Body
  Armour ...) is refused by wave 31's rule: it needs the model's Inventories
  component update, the weapon records (`0x324`) and the attack's values
  again, which are the next step; attribute requirements are not checked.

To check live: a dropped armour's look on the ground and in the inventory
(the Armour component), a weapon's tooltip values, the inventory after a
relog with gear in it.

## Wave 30 follow-up — the gem window live, attacks at the ground (2026-11-05)

Live test of wave 30 with `Game_POE2_UNCUT_GEM=1` (an Uncut Skill Gem
(Level 1) in the inventory). Code: `InstanceItems.cs` (the SkillGem data),
`InstanceGemsPoe2.ActorSkills` / `Hand` / `HandOf`, `Poe2Items.Load`,
`InstanceSessionPoe2.Slams.cs`, `InstanceSkillsPoe2.ReadAim`,
`Poe2Gems.Combat`, `gems.py` (the combat values).

- **The cut request** came as `005F 00 3AA94A61 00000001 00000001`: the key
  is the chosen gem's item **HASH32** (Rolling Slam's), then the main
  inventory and the uncut gem's item id, in the builder's store order.
  `Poe2Gems.Resolve` finds it by hash.
- **Three crashes, three fixes:**
  - "An unexpected disconnection occurred" on entering: `item-types.json`
    repeats some ids (BaseItemTypes has duplicate Ids), so its dictionary
    threw; `Poe2Items.Load` keeps the first of each id.
  - "Buffer underflow" when cutting, with the Actor update first sent as the
    whole create list: an update's skill change (`0x14218b780`, flags bit 3)
    is u8 added, u8 removed, the added records, the removed u32 ids, then
    u8 n × (u32, u32, u32) and u8 c × u8, both sent empty.
  - "Buffer underflow" again, and on entering with a socketed gem: PoE2's
    **SkillGem** item component (reader `0x1421e7240`, vtable `0x1435157d8`)
    is 16 bytes, i32 experience, u32 (`+0x34`), u32 (`+0x30`), u8 flags
    (`+0x10`), u8 (`+0x38`), u16 (a support index, `0x1421e8490`); we sent
    PoE1's 11. Found by listing the item component readers by their read
    pattern.
- **Greyed "[Not enough mana]" at full mana** once cut: the decoy label of
  wave 26. The gem's skill had source byte 0, so the client judged Rolling
  Slam (a mace skill) with the Actor's default hands, unarmed. A gem's skill
  now takes the starting skills' hand: the main hand's stats and source
  `0x40` (weapon set I) with a weapon, 0 unarmed. Rolling Slam then lit up
  and played (the roll and both slams) on use.
- **Attacks at the ground.** Rolling Slam is used at the ground: `0x1a5`
  without a target, the aim relative to the player (here (28, 48)). The
  server echoed `0x1c8` but hit nothing, so the slams were harmless. Now a
  ground attack queues its hits (`InstanceSessionPoe2.Slams.cs`):
  - the data (`gems.py`, a fourth element per level in `gems.json`): the
    main stat set's `total_attack_time_+_ms` (Rolling Slam 1000) and
    `attack_maximum_action_distance_+` (20), and the hits: the main set's,
    or the additional sets' when the main one has `base_deal_no_damage`
    (Rolling Slam: First Slam, GESSPL BaseMultiplier −2500 = 75 %,
    `active_skill_base_area_of_effect_radius` 14; Second Slam, 5000 = 150 %,
    `active_skill_base_secondary_area_of_effect_radius` 18);
  - ours: hit k of n lands at (k + 1)/(n + 1) of the attack time; the first
    centre is the weapon's reach towards the aim, the later ones the reach
    plus the action distance plus 3 cells of roll, where the player ends;
    radii are game units / (250 / 23) per cell plus 2 cells of slack; each
    monster inside takes the attack's roll times the multiplier.
  - A basic attack at the ground (Default Attack, Mace Strike) hits once in
    front of the player the same way.
- **C→S `0x1a8`** (15 bytes: u8 route, i32 BE x, i32 BE y, u32 BE skill id)
  comes every ~70 ms while the button of an action is held: the client's
  re-aim, relative to the player. Framed now; the server keeps the use's
  aim (during a roll the relative values shrink as the player moves, which
  the server does not model).

Not done: the hits are not tested live yet; a ground attack's hits are not
cancelled by a move or a dodge; the skill's own damage stats (added damage,
"more" multipliers other than BaseMultiplier) are not read; a gem used on a
target object still hits that one only, at the weapon's damage.
