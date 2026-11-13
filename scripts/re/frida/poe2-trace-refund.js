// PoE2 0.5.5e (PathOfExileSteam_le2.exe): logs the passive tree's refund path (PORTING-POE2.md wave 28): why a click
// on an allocated node marks nothing for refund.
//   python scripts/re/frida/run.py PathOfExileSteam_le2.exe --follow --log logs/refund.log scripts/re/frida/poe2-trace-refund.js
// Hooks (guarded on their first bytes):
//   0x14086d300 the S→C 0x2db reader: the gold amount parsed (+0x18 after ntohl).
//   0x140a0b420 → 0x1409ad790: the refund request builder; 0x1409e3500(out, state, node, panel, flag): the refund
//               cost of a node (out: u32 cost, u8 affordable), reads the gold at [[state+0x1b0]+0x39d0];
//               0x1409b5e50(panel, entry, flag): a pending action queued; 0x1409ac830: the Apply sender.
//   0x141e9cc00 the tree model's click allocation ("Successfully allocated"), 0x141ea0ee0 its deallocation: with a
//               backtrace, to find the screen's click handler.
//   Every other reader of the gold field (+0x39d0), with a backtrace, to see which fires on a hover or a click.
const m = Process.getModuleByName('PathOfExileSteam_le2.exe');
const B = ptr('0x140000000');
const at = v => m.base.add(ptr(v).sub(B));
const va = p => '0x' + ptr(p).sub(m.base).add(B).toString(16);
const guard = (va, hex) => {
  const got = at(va).readByteArray(hex.length / 2);
  const s = Array.from(new Uint8Array(got)).map(b => b.toString(16).padStart(2, '0')).join('');
  if (s !== hex) throw new Error(`bytes at ${va} are ${s}, not ${hex}: another build`);
};
const bt = ctx => Thread.backtrace(ctx, Backtracer.ACCURATE).slice(0, 10).map(va);
guard('0x14086d300', '48895c2408574883ec20488b');
guard('0x140a0b420', '48895c241048897424185557');
guard('0x1409ad790', '48894c240855535657415441');
guard('0x1409e3500', '48895c240848896c24185657');
guard('0x1409b5e50', '48895c241055565741544155');
guard('0x1409ac830', '48895c241888542410555657');
guard('0x141e9cc00', '48895c240848896c24105657');

Interceptor.attach(at('0x14086d300'), {
  onEnter(args) { this.o = args[0]; },
  onLeave() { send({gold0x2db: this.o.add(0x18).readU32()}); },
});
Interceptor.attach(at('0x140a0b420'), {
  onEnter(args) { send({refundCaller: 'enter', a0: args[0].toString(), a1: args[1].toString(), a2: args[2].toString(), bt: bt(this.context)}); },
  onLeave(r) { send({refundCaller: 'leave', ret: r.toString()}); },
});
Interceptor.attach(at('0x1409ad790'), {
  onEnter(args) { send({refundBuilder: 'enter', panel: args[0].toString()}); },
  onLeave() { send({refundBuilder: 'leave'}); },
});
Interceptor.attach(at('0x1409e3500'), {
  onEnter(args) {
    this.out = args[0];
    let gold = null;
    try { gold = args[1].add(0x1b0).readPointer().add(0x39d0).readU32(); } catch (e) { gold = 'unreadable'; }
    this.info = {state: args[1].toString(), node: args[2].toString(), gold, bt: bt(this.context)};
  },
  onLeave() { send({refundCost: this.out.readU32(), affordable: this.out.add(4).readU8(), ...this.info}); },
});
Interceptor.attach(at('0x1409b5e50'), {
  onEnter(args) {
    const e = args[1];
    send({queued: true, panel: args[0].toString(), node: e.readPointer().toString(), flag: args[2].toInt32() & 0xff, bt: bt(this.context)});
  },
});
Interceptor.attach(at('0x1409ac830'), {
  onEnter(args) {
    const p = args[0];
    const begin = p.add(0x3f0).readPointer(), end = p.add(0x3f8).readPointer();
    const n = end.sub(begin).toInt32() / 0x20;
    const entries = [];
    for (let i = 0; i < n && i < 16; i++) {
      const e = begin.add(i * 0x20);
      entries.push({node: e.readPointer().toString(), b18: e.add(0x18).readU8(), b19: e.add(0x19).readU8(), b1a: e.add(0x1a).readU8()});
    }
    send({apply: n, entries});
  },
});
Interceptor.attach(at('0x141e9cc00'), {
  onEnter(args) {
    let id = null;
    try { id = args[1].add(0x10).readU16(); } catch (e) {}
    send({modelAllocate: id, mode: args[2].toInt32() & 0xff, bt: bt(this.context)});
  },
  onLeave(r) { send({modelAllocateResult: r.toInt32() & 0xff}); },
});
Interceptor.attach(at('0x141ea0ee0'), {
  onEnter(args) {
    let id = null;
    try { id = args[1].readPointer().add(0x30).readU16(); } catch (e) {}
    send({modelDeallocate: id, bt: bt(this.context)});
  },
});
// The other readers of the gold field, rate-limited per function.
const readers = ['0x1409ffd50', '0x140a095f0', '0x140a12880', '0x140a13ae0', '0x140f87270', '0x140d008c0', '0x1408f0ee0',
  '0x1408f6be0', '0x140556720', '0x140557000', '0x1404c82b0', '0x140485ab0', '0x14032a5f0'];
const seen = {};
for (const f of readers) {
  Interceptor.attach(at(f), {
    onEnter() {
      const n = (seen[f] = (seen[f] || 0) + 1);
      if (n <= 3 || n % 200 === 0) send({goldReader: f, n, bt: bt(this.context)});
    },
  });
}
// The model's unallocation (0x141e9cf50(tree, node, &budget): "Successfully unallocated") and its gate
// 0x141eb0cb0(tree, node, budget, flag): the node's point cost must fit the budget the screen passes.
guard('0x141e9cf50', '48895c240848897424105748');
guard('0x141eb0cb0', '48895c240848896c24105657');
Interceptor.attach(at('0x141eb0cb0'), {
  onEnter(args) {
    let id = null;
    try { id = args[1].add(0x10).readU16(); } catch (e) {}
    this.info = {canUnallocate: id, budget: args[2].toInt32(), flag: args[3].toInt32() & 0xff, bt: bt(this.context)};
  },
  onLeave(r) { send({...this.info, result: r.toInt32() & 0xff}); },
});
Interceptor.attach(at('0x141e9cf50'), {
  onEnter(args) {
    let id = null, budget = null;
    try { id = args[1].add(0x10).readU16(); budget = args[2].readU32(); } catch (e) {}
    send({modelUnallocate: id, budget, bt: bt(this.context)});
  },
  onLeave(r) { send({modelUnallocateResult: r.toInt32() & 0xff}); },
});
send({ready: true});
