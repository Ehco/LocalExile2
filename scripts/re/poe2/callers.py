"""Direct E8 call sites of each target, with the function holding each one (virtual calls do not show up).

    python scripts/re/poe2/callers.py <hex va>...
"""
import sys

import numpy as np

from pe2 import BASE, _text, _tv, _tvs, func_bounds

n = _tvs - 5
e8 = np.nonzero(_text[:n] == 0xE8)[0]
rel = (_text[e8 + 1].astype(np.int64) | _text[e8 + 2].astype(np.int64) << 8 | _text[e8 + 3].astype(np.int64) << 16 | _text[e8 + 4].astype(np.int64) << 24)
rel = np.where(rel >= 1 << 31, rel - (1 << 32), rel)
dst = BASE + _tv + e8 + 5 + rel
for a in sys.argv[1:]:
    t = int(a, 16)
    for i in np.nonzero(dst == t)[0]:
        site = BASE + _tv + int(e8[i]); fb = func_bounds(site)
        print(f'{t:x} <- {site:x} func={fb[0]:x}' if fb else f'{t:x} <- {site:x}')
