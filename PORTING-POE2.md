# LocalExile2 — porting the LocalExile PoE1 server emulator to Path of Exile 2

Goal: a local private server for the **Path of Exile 2** Steam client
(`PathOfExileSteam.exe`, installed at
`C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\`), keeping the
real client as the renderer — the approach proven by
[LocalExile](https://github.com/LocalExile/LocalExile) for the PoE 3.29.3b client.

## Status snapshot (wave 0 — recon complete, baseline validated)

| Item | State |
|---|---|
| LocalExile baseline clone | done (depth 1, upstream main) |
| .NET 10 SDK | installed via winget |
| `dotnet build GameServer.slnx -c Release` | **passes** |
| `dotnet test tests/GameServer.Core.Tests` | **452/452 pass** |
| PoE2 client recon (login crypto) | **done — see findings** |
| PoE2 client recon (DH groups) | **done** — groups unchanged (finding 5) |
| PoE2 client recon (login key blobs) | **done** — all three mapped (findings 4, 6, 7) |
| Login handshake vs patched real client | **done** — client accepts our signed `0x0002`, C→S decrypts (wave 3) |
| PoE2 login message set | in progress — `0x04`/`0x05`/`0x0e`/`0x10`/`0x14`/`0x15`/`0x16`/`0x18` mapped; Play reaches the instance server (wave 6) |
| PoE2 gateway/instance protocol | in progress — opening, encryption switch and area message work live; client generates the area and waits on the loading screen (wave 8) |

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
  (dotnet at `C:\Program Files\dotnet\dotnet.exe`).
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
    records of 0x80 bytes (PoE1: 17), presumably the equipment preview, and
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
  `ManagedOodleDecoder` fails on it. `Tools/ooz/node_modules` is not in
  `.gitignore`.

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
