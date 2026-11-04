// PoE2 0.5.5e (PathOfExileSteam_le2.exe): logs the client's weapon requirement checks of each skill (PORTING-POE2.md
// wave 26 / 28): why the weapon's own attack (Mace Strike) is greyed while Default Attack is not.
//   python scripts/re/frida/run.py PathOfExileSteam_le2.exe --follow --log logs/weapon.log scripts/re/frida/poe2-trace-weapon-check.js
// Hooks (guarded on their first bytes):
//   0x141e859c0 (skill, out, types*): the per-skill evaluation. In: the skill id (+0x40), the caller's hand types
//               (u32 main, u32 off; 0x77 = none). Out: u8 main ok, u8 off ok, i32 status (0 usable, 2 not).
//   0x141e862c0 (type, isMain, stats, list*): one hand against the skill's requirement list. Out: bool.
//   0x14268cdb0 (out*, type): the hand type -> the row record the requirement list holds.
const m = Process.getModuleByName('PathOfExileSteam_le2.exe');
const B = ptr('0x140000000');
const at = v => m.base.add(ptr(v).sub(B));
const guard = (va, hex) => {
  const got = at(va).readByteArray(hex.length / 2);
  const s = Array.from(new Uint8Array(got)).map(b => b.toString(16).padStart(2, '0')).join('');
  if (s !== hex) throw new Error(`bytes at ${va} are ${s}, not ${hex}: another build`);
};
guard('0x141e859c0', '48895c241055565741544155');
guard('0x141e862c0', '48895c240888542410555657');
guard('0x14268cdb0', '48895c241048894c24085556');

Interceptor.attach(at('0x141e859c0'), {
  onEnter(args) {
    this.skill = args[0]; this.out = args[1];
    const types = args[2];
    this.id = this.skill.add(0x40).readU32();
    this.types = [types.readU32(), types.add(4).readU32()];
    send({check: 'skill', id: '0x' + this.id.toString(16), skill: this.skill.toString(), typesIn: this.types,
          ge: this.skill.add(0x58).readPointer().toString()});
  },
  onLeave() {
    send({check: 'skill-result', id: '0x' + this.id.toString(16), mainOk: this.out.readU8(), offOk: this.out.add(1).readU8(),
          status: this.out.add(4).readS32()});
  }
});
Interceptor.attach(at('0x141e862c0'), {
  onEnter(args) {
    this.type = args[0].toInt32(); this.main = args[1].toInt32() & 0xff; this.list = args[3];
    let req = null;
    try {
      const holder = this.list.readPointer();
      const l = holder.add(0x136).readPointer();
      if (!l.isNull()) {
        const n = l.add(8).readU32(), data = l.add(0x10).readPointer();
        req = [];
        for (let i = 0; i < Math.min(n, 8); i++) req.push(data.add(16 * i).readPointer().toString());
      }
    } catch (e) { req = 'err ' + e; }
    this.req = req;
  },
  onLeave(ret) {
    send({check: 'hand', type: this.type, main: this.main, ok: ret.toInt32() & 0xff, requirement: this.req});
  }
});
Interceptor.attach(at('0x14268cdb0'), {
  onEnter(args) { this.out = args[0]; this.type = args[1].toInt32(); },
  onLeave() { send({check: 'type-record', type: this.type, record: this.out.readPointer().toString()}); }
});
send({ready: 'poe2-trace-weapon-check'});
