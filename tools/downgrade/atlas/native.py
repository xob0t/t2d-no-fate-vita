"""The double values IL2CPP stores one by one into small double[] arrays inside the game's generated table
initialisers (AssetsPack.staticInit6..9), read from GameAssembly.dll. Some Rewind versions drop these stores (the
bounds-checked store helper isn't translated); frametable.py fills such gaps from here.

Generalised from tools/assess/atlas-usage/native_dstores.py: the method's address comes from an Il2CppDumper-style
dump.cs (the "// VA: 0x... RVA: 0x..." comment above each method), and the two store helpers are found in the
function itself instead of being passed in:

    call alloc(klass, k)          ; rbx = new double[k]
    movsd xmm2, [rip+c] | movaps xmm2, xmmN | xorps xmm2, xmm2
    xor edx, edx | mov edx, J ; mov rcx, rbx
    call STORE_D                  ; inner[J] = xmm2, per element
    mov r8, rbx ; mov edx, IDX ; mov rcx, rdi
    call STORE_REF                ; outer[IDX] = inner

STORE_D is the call target most often reached with xmm2 just set; STORE_REF the one most often reached with r8
just set.
"""
import collections
import re
import struct

import capstone
import pefile


def dump_vas(dump_path, type_name, names):
    """VA of each method `names` of class `type_name` (the short name) in an Il2CppDumper dump.cs."""
    text = open(dump_path, encoding='utf-8', errors='replace').read()
    m = re.search(r'\n(?:public |internal |private )?[^\n]*class %s\b[^\n]*\n\{' % re.escape(type_name), text)
    if not m:
        raise KeyError('class %s not in %s' % (type_name, dump_path))
    end = text.find('\n}\n', m.end())
    body = text[m.end():end]
    out = {}
    for mm in re.finditer(r'// VA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*[^\n]*?\b(\w+)\(\)', body):
        if mm.group(2) in names:
            out[mm.group(2)] = int(mm.group(1), 16)
    return out


class Native:
    def __init__(self, dll):
        self.pe = pefile.PE(str(dll), fast_load=True)
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True
        self.md.skipdata = True

    def read(self, va, n):
        return self.pe.get_data(va - self.base, n)

    def _function(self, va, limit=0x200000):
        """Instructions from va to the first ret (the initialisers are one long block)."""
        for ins in self.md.disasm(self.read(va, limit), va):
            yield ins
            if ins.mnemonic == 'ret':
                return

    def helpers(self, va):
        dstore, rstore = collections.Counter(), collections.Counter()
        recent = collections.deque(maxlen=4)
        for ins in self._function(va):
            if ins.mnemonic == 'call' and ins.operands[0].type == capstone.x86.X86_OP_IMM:
                tgt = ins.operands[0].imm
                if any(r.startswith('xmm2') for r in recent):
                    dstore[tgt] += 1
                if any(r.startswith('r8') for r in recent):
                    rstore[tgt] += 1
            recent.append(ins.op_str if ins.mnemonic in ('mov', 'movsd', 'movaps', 'movapd', 'xorps', 'xorpd', 'movq', 'lea') else '')
        if not dstore or not rstore:
            raise ValueError('no store helpers found at 0x%X' % va)
        return dstore.most_common(1)[0][0], rstore.most_common(1)[0][0]

    def double_stores(self, va, count):
        """{outer index: {inner index: value}} for the arrays stored element by element."""
        store_d, store_ref = self.helpers(va)
        xmm, edx = {}, None
        pending, out, unknown = None, {}, 0
        for ins in self._function(va):
            m, ops = ins.mnemonic, ins.op_str
            if m in ('movsd', 'movq') and ops.startswith('xmm') and 'rip' in ops:
                addr = ins.address + ins.size + ins.operands[1].mem.disp
                xmm[ops.split(',')[0]] = struct.unpack('<d', self.read(addr, 8))[0]
            elif m in ('movaps', 'movapd', 'movsd') and ops.count('xmm') == 2:
                dst, src = (s.strip() for s in ops.split(','))
                xmm[dst] = xmm.get(src)
            elif m in ('xorps', 'xorpd', 'pxor') and ops.count('xmm') == 2:
                dst, src = (s.strip() for s in ops.split(','))
                xmm[dst] = 0.0 if dst == src else None
            elif ops.startswith('xmm') and m not in ('ucomisd', 'comisd'):
                xmm[ops.split(',')[0]] = None
            if m == 'xor' and ops == 'edx, edx':
                edx = 0
            elif m == 'mov' and ops.startswith('edx, ') and re.fullmatch(r'0x[0-9a-f]+|\d+', ops[5:]):
                edx = int(ops[5:], 0)
            elif ops.startswith(('edx', 'rdx', 'dx')) and m not in ('cmp', 'test'):
                edx = None
            if m == 'call':
                tgt = ins.operands[0].imm if ins.operands[0].type == capstone.x86.X86_OP_IMM else None
                if tgt == store_d:
                    v = xmm.get('xmm2')
                    if v is None or edx is None:
                        unknown += 1
                    else:
                        pending = pending or {}
                        pending[edx] = v
                elif tgt == store_ref:
                    if pending is not None and edx is not None:
                        out[edx] = pending
                    pending = None
                    if edx == count - 1:
                        break
                for r in ('xmm0', 'xmm1', 'xmm2', 'xmm3', 'xmm4', 'xmm5'):
                    xmm.pop(r, None)
                edx = None
        return out, unknown
