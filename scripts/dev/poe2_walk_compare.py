"""Compares the Gateway's PoE2 walk echoes with the client's own cell (HANDOFF-POE2.md, PORTING-POE2.md wave 14).

    python scripts/dev/poe2_walk_compare.py <gateway.log> <track.log>

gateway.log is the PoE2 Gateway's console output (its "0x64 move echo" lines, local HH:MM:SS.fff times);
track.log is the --log of scripts/re/frida/poe2-track-player.js run alongside it. For each echo it prints the client's
last cell before it and the gap (the larger of |dx|, |dy|), then the median and the count of gaps of 5 cells or more.
"""
import sys, json, datetime, bisect
def ci(b,i):
    v=b[i]
    if v<0x80: return (v-0x80 if v>=0x40 else v), i+1
    if v<0xc0:
        x=((v&0x3f)<<8)|b[i+1]; return (x-0x4000 if x>=0x2000 else x), i+2
    x=((v&0x1f)<<16)|(b[i+1]<<8)|b[i+2]; return (x-0x200000 if x>=0x100000 else x), i+3
track=[]
for l in open(sys.argv[2]):
    p=json.loads(l).get('payload',{})
    if 'now' in p: track.append((p['now'], tuple(map(int,p['cell'].split(',')))))
if not track: sys.exit(f'no client cells in {sys.argv[2]}: start the tracker before the client enters the area')
# The Gateway's times have no date: take the day the client track starts.
day=datetime.date.fromtimestamp(track[0][0]/1000)
echoes=[]
for l in open(sys.argv[1]):
    if 'move echo' not in l: continue
    t=datetime.datetime.combine(day, datetime.time.fromisoformat(l[:12])).timestamp()*1000
    b=bytes.fromhex(l.split()[-1]); i=0; v=[]
    for _ in range(4):
        x,i=ci(b,i); v.append(x)
    echoes.append((t,v))
ts=[t for t,_ in track]
errs=[]
for t,v in echoes:
    k=bisect.bisect_right(ts,t)-1
    if k<0: continue
    c=track[k][1]; d=max(abs(c[0]-v[2]),abs(c[1]-v[3])); errs.append(d)
    print(f"{datetime.datetime.fromtimestamp(t/1000).time()} dir {v[:2]} echo {v[2:]} client {c} off {d}")
errs.sort(); print('n',len(errs),'median',errs[len(errs)//2],'max',errs[-1], '>=5:',sum(e>=5 for e in errs))
