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
| `dotnet test tests/GameServer.Core.Tests` | **415/415 pass** |
| PoE2 client recon (login crypto) | **done — see findings** |
| PoE2 client recon (DH groups) | **done** — groups unchanged (finding 5) |
| PoE2 client recon (login key blobs) | **done** — all three mapped (findings 4, 6, 7) |
| PoE2 gateway/instance protocol | not started |

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
public moved), DER-prefix locator unchanged. Open items: confirming the
handshake against the patched real client, the Salsa20 key-material layout
(`km = SHA-512(Z_static || Z_ephemeral)`, IV split), and the per-version
packet/message set.

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
server static public at SPKI-0x180. Next: patch a copy of the exe, serve the
login with those keys, and check that the client's first encrypted message
decrypts.
