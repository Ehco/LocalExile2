// PoE2 walk-map capture (Frida, loaded by poe2_walkmap.py into the PoE2 0.5.5e client before it loads an area).
//
// When the client loads an area, 0x1412ac690(ctx, provider) stores the area's walkability provider (ctx +0xf0). PoE1
// asked that provider for the grid right there (walkmaps.js hooks it); PoE2 skips the request in Clearfell Encampment
// (ctx +0xf8 is 1), so this script asks it itself: provider vtable +0x50 (0x141687dc0)(provider, out, rect) fills
//   out = { i32 w; i32 h; vector<u8> walk [+0x08, +0x10]; vector<u8> projectile [+0x20, +0x28] }
// for rect = { i32 x0, y0, x1, y1 } in grid cells. The area's size in 23-cell tiles is provider +0x68 / +0x70. Of the
// two providers set per load, the walkability one has vtable 0x14339dd90. The walk grid is one byte per cell like
// PoE1's; the PoE2 player walks on cells >= 1 (PoE1's needs 2; live 2026-11-03, InstanceSessionPoe2.Walkable).
//
// The grid goes with the area's server objects (exits, waypoint, checkpoints, monsters...), which the client does not
// create but meets while it generates the level: for every object a room places, 0x141ff3490 asks the spawn
// predicate 0x1415a16b0 (vtable [+0xa0]; call returns to 0x141ff3844) whether the client creates it, with
//   rdx = the room's object record, whose +0 is the type handle (handle +8 -> UTF-16 path) and +8 the .ao handle,
//   r14 = the room, [r14] its .arm handle (still the caller's register on entry),
//   caller rsp +0x70 = { i32 x, i32 y (grid cells, room placed and turned), f32 wx, wy, z, f32 angle (radians) }.

const IMAGE_BASE = ptr('0x140000000');
const SET_PROVIDER = '0x1412ac690', FILL = '0x141687dc0', PROVIDER_VTABLE = '0x14339dd90';
const SPAWN_PREDICATE = '0x1415a16b0', ROOM_OBJECT_CALLER = '0x141ff3844';
// The first bytes of the hooked and called code in the 0.5.5e client; nothing is hooked or called on another build.
const EXPECTED = { [SET_PROVIDER]: '488bc455535657415441554156415748', [FILL]: '48895c241848896c2420488954241056',
  [SPAWN_PREDICATE]: '48895c2408574883ec30488bfa488b0a', [ROOM_OBJECT_CALLER]: '84c0756a488d4db0e8cf90bdfe488b45' };
// Client-only objects the predicate also meets (decoration, flow fields, sound): not kept.
const NOT_SERVER = /\/Doodad[A-Za-z]*$|NonDefaultFlows|AudioTools|\/Sector$|\/RuleSet$|AreaTransitionBlockage$|AreaTransitionDoodad$/;
const objects = [];
// The provider is set before the area's terrain is finished; ask for the grid a little later.
const SETTLE_MS = 3000;

const exe = Process.enumerateModules().find(m => m.name.toLowerCase().endsWith('.exe'));
const at = va => exe.base.add(ptr(va).sub(IMAGE_BASE));
const hex = (p, n) => Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join('');

const mismatch = Object.keys(EXPECTED).find(va => hex(at(va), 16) !== EXPECTED[va]);
if (mismatch) {
  send({ type: 'refused', reason: `${exe.name} is not the PoE2 0.5.5e client (code at ${mismatch} does not match)` });
} else {
  Interceptor.attach(at(SPAWN_PREDICATE), {
    onEnter(args) {
      if (!this.returnAddress.equals(at(ROOM_OBJECT_CALLER))) return;
      try {
        const type = handlePath(args[1].readPointer());
        if (!type || NOT_SERVER.test(type)) return;
        const place = this.context.rsp.add(8 + 0x70);
        objects.push({
          type, model: handlePath(args[1].add(8).readPointer()), room: handlePath(this.context.r14.readPointer()),
          x: place.readS32(), y: place.add(4).readS32(), rotation: place.add(0x14).readFloat(),
        });
      } catch (e) {
        send({ type: 'error', message: `object: ${e}` });
      }
    },
  });
  Interceptor.attach(at(SET_PROVIDER), {
    onEnter(args) {
      const provider = args[1];
      // The level's objects come before its providers; the login scene's before its other provider, and the world
      // maps', generated while the grid settles, after.
      const placed = objects.splice(0);
      if (provider.isNull() || !provider.readPointer().equals(at(PROVIDER_VTABLE))) return;
      setTimeout(() => capture(provider, placed), SETTLE_MS);
    },
  });
  send({ type: 'ready', module: exe.name });
}

function handlePath(handle) {
  if (handle.isNull()) return null;
  const text = handle.add(8).readPointer();
  return text.isNull() ? null : text.readUtf16String();
}

function capture(provider, placed) {
  try {
    const w = provider.add(0x68).readS32() * 23, h = provider.add(0x70).readS32() * 23;
    const out = Memory.alloc(0x40), rect = Memory.alloc(16);
    rect.writeS32(0);
    rect.add(4).writeS32(0);
    rect.add(8).writeS32(w);
    rect.add(12).writeS32(h);
    new NativeFunction(at(FILL), 'pointer', ['pointer', 'pointer', 'pointer'])(provider, out, rect);
    const gw = out.readS32(), gh = out.add(4).readS32();
    const begin = out.add(8).readPointer(), n = out.add(0x10).readPointer().sub(begin).toInt32();
    if (gw <= 0 || gh <= 0 || n !== gw * gh) {
      send({ type: 'skip', w: gw, h: gh, bytes: n });
      return;
    }
    send({ type: 'grid', w: gw, h: gh, objects: placed }, begin.readByteArray(n));
  } catch (e) {
    send({ type: 'error', message: String(e) });
  }
}
