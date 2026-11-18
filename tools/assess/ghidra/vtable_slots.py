"""Resolve IL2CPP vtable slots from a dump.cs: prints slot, klass offset (0x138 + slot*0x10
 on Unity 6 / v31 x64) and the most-derived declaring method.
usage: python -I vtable_slots.py <dump.cs> <Class> [<BaseClass> ...]   (most-derived first, simple names)
"""
import re,sys
src=open(sys.argv[1],encoding='utf-8').read().split('\n')
want=sys.argv[2:]
res={}
cur=None
for i,l in enumerate(src):
    m=re.match(r'^(?:\t*)(?:public|internal|private|protected)?[\w\s]*(?:class|struct) (\S+)(?: :.*)? // TypeDefIndex',l)
    if m:
        cur=m.group(1)
        # disambiguate by namespace line above
        continue
    m=re.search(r'Slot: (\d+)',l)
    if m and cur in [w.split('.')[-1] for w in want]:
        sig=src[i+1].strip()
        if sig.startswith('['): sig=src[i+2].strip()
        res.setdefault(cur,{})[int(m.group(1))]=sig
order=[w.split('.')[-1] for w in want]
for s in sorted(set(k for c in res.values() for k in c)):
    for c in order:
        if s in res.get(c,{}):
            print(s, hex(0x138+s*16), c, res[c][s][:110]); break
