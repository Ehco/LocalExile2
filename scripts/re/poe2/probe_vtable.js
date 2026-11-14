// Finds the walkability provider's vtable at run time for a client build whose addresses moved.
//
// poe2-walkmap.js needs SET_PROVIDER, FILL and PROVIDER_VTABLE. Static scanning narrows SET_PROVIDER
// to two functions but leaves FILL ambiguous (73 candidates), so this asks the live client: hook both
// SET_PROVIDER candidates, and when one stores a pointer at [ctx+0xF0], print that object's vtable and
// its +0x50 slot. The area-load provider's vtable +0x50 IS the fill function, so the answer comes out of
// memory instead of a signature.
//
//   .venv/Scripts/python.exe scripts/re/frida/run.py PathOfExileSteam_le2.exe --follow --log logs/vtable.log scripts/re/poe2/probe_vtable.js
//
// Attach before the client loads an area; it prints one "provider" line per area load.

const IMAGE_BASE = ptr('0x140000000');
// The SET_PROVIDER candidates site_scan.py narrowed the 143-prologue search to: the functions that store
// a pointer at [ctx+0xF0] and read [ctx+0xF8].
const CANDIDATES = ['0x142585158', '0x142b76060'];
const SEEN = {};

const exe = Process.enumerateModules().find(m => m.name.toLowerCase().endsWith('.exe'));
const at = va => exe.base.add(ptr(va).sub(IMAGE_BASE));
const hex = (p, n) => { try { return Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join(''); } catch (e) { return '?'; } };

function dump(vt, ctx, via) {
  const key = vt.toString();
  if (SEEN[key]) return;
  SEEN[key] = true;
  const fill = vt.add(0x50).readPointer();
  send({
    type: 'provider',
    via,
    ctx: ctx.toString(),
    vtable: key,
    fill: fill.toString(),
    // what poe2-walkmap.js would need to verify this build
    prologue: hex(fill, 16),
    tileW: ctx.add(0x68).readU32(),
    tileH: ctx.add(0x70).readU32(),
  });
}

CANDIDATES.forEach(va => {
  try {
    Interceptor.attach(at(va), {
      onEnter(args) { this.ctx = args[0]; this.va = va; },
      onLeave(_rv) {
        try {
          const p = this.ctx.add(0xF0).readPointer();
          if (!p.isNull()) dump(p.readPointer(), this.ctx, this.va);
        } catch (e) { send({ type: 'error', va: this.va, error: String(e) }); }
      },
    });
    send({ type: 'hooked', va });
  } catch (e) { send({ type: 'hook-failed', va, error: String(e) }); }
});
