"""Find a string (ascii and utf-16le) in the image; print VA and code xrefs."""
import sys, re
import pe2
from pe2 import rip_refs, confirm, func_bounds, fmt
for s in sys.argv[1:]:
    for enc in ('ascii', 'utf-16le'):
        for m in re.finditer(re.escape(s.encode(enc)), pe2.data):
            sva = pe2.va(m.start())
            if sva is None: continue
            print(f'{enc} {sva:x}')
            for dva, extra, tgt in rip_refs(sva, sva + 1):
                insn = confirm(dva, tgt)
                if insn:
                    fb = func_bounds(insn.address)
                    print(f'   {fmt(insn)} func={fb[0]:x}' if fb else '   ' + fmt(insn))
