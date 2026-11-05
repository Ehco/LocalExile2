// PoE2 0.5.5e (PathOfExileSteam_le2.exe): once the player's 0x2f7 blob is read (0x141e712a0), report its grid cell
// (Positioned +0x444/+0x448), polled every 50 ms, whenever it changes, with a wall-clock time (`now`, ms). Its log is
// the client side of scripts/dev/poe2_walk_compare.py (HANDOFF-POE2.md):
//   python scripts/re/frida/run.py PathOfExileSteam_le2.exe --log track.log scripts/re/frida/poe2-track-player.js
const m = Process.getModuleByName('PathOfExileSteam_le2.exe');
const B = ptr('0x140000000');
const at = v => m.base.add(ptr(v).sub(B));
let pos = null, last = '';
const t0 = Date.now();

Interceptor.attach(at('0x141e712a0'), {
  onEnter(args) { this.obj = args[0]; },
  onLeave() {
    pos = this.obj.add(0x10).readPointer().readPointer();
    send({tracking: pos.toString()});
  }
});
setInterval(() => {
  if (pos === null) return;
  try {
    const g = pos.add(0x444).readS32() + ',' + pos.add(0x448).readS32();
    if (g !== last) { last = g; send({t: Date.now() - t0, now: Date.now(), cell: g}); }
  } catch (e) { send({err: '' + e}); pos = null; }
}, 50);
send({ready: 'poe2-track'});
