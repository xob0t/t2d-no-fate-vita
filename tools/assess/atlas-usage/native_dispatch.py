"""Resolve IL2CPP switch jump tables that Rewind left as __call_indirect.

For a jump table of the form
    cmp REG, MAX ; ja default
    lea r8, [rip - X]                      ; image base
    movzx eax, byte ptr [r8 + REG + BYTES] ; index -> slot
    mov ecx, dword ptr [r8 + rax*4 + DWORDS]
    add rcx, r8 ; jmp rcx
it lists, per case value, the methods the target block calls (named from
Il2CppDumper's dump.cs) and the integer immediates loaded into argument
registers / stack slots before each call.

Usage: native_dispatch.py GameAssembly.dll dump.cs BYTES_RVA DWORDS_RVA COUNT BASE
       (BASE is added to the table index to get the case value)
Prints JSON {case_value: [{"call": name, "imms": [...]}, ...]}.
"""
import json, re, struct, sys
import capstone, pefile


def load_names(dump):
    names = {}
    cls = None
    va = None
    for line in open(dump, encoding='utf-8', errors='replace'):
        m = re.match(r'^(?:public |internal |private |protected )*(?:sealed |abstract |static )*(?:class|struct) (\S+)', line)
        if m:
            cls = m.group(1)
            continue
        m = re.search(r'// VA: 0x([0-9A-F]+)', line)
        if m:
            va = int(m.group(1), 16)
            continue
        if va is not None:
            m = re.search(r'\b([A-Za-z_<>.$0-9`]+)\(', line)
            if m:
                meth = m.group(1)
                names.setdefault(va, '%s::%s' % (cls, '.ctor' if meth == cls.split('.')[-1] else meth))
            va = None
    return names


def main():
    dll, dump = sys.argv[1], sys.argv[2]
    bytes_rva, dwords_rva, count, base = (int(x, 0) for x in sys.argv[3:7])
    pe = pefile.PE(dll, fast_load=True)
    ib = pe.OPTIONAL_HEADER.ImageBase
    names = load_names(dump)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    idx = pe.get_data(bytes_rva, count)
    out = {}
    for i in range(count):
        slot = idx[i]
        off = struct.unpack('<I', pe.get_data(dwords_rva + 4 * slot, 4))[0]
        tgt = ib + off
        calls = []
        imms = {}
        pc = tgt
        steps = 0
        while steps < 400:
            data = pe.get_data(pc - ib, 16)
            ins = next(md.disasm(data, pc), None)
            if ins is None:
                break
            steps += 1
            m, ops = ins.mnemonic, ins.op_str
            mm = re.match(r'^(edx|r8d|r9d|ecx|dword ptr \[rsp \+ 0x[0-9a-f]+\]), (0x[0-9a-f]+|\d+)$', ops)
            if m == 'mov' and mm:
                imms[mm.group(1)] = int(mm.group(2), 0)
            if m == 'xor' and ops in ('edx, edx', 'r8d, r8d', 'r9d, r9d'):
                imms[ops.split(',')[0]] = 0
            mi = re.match(r'^dword ptr \[r\w+ \+ 0xe8\], (0x[0-9a-f]+|\d+)$', ops)
            if m == 'mov' and mi:               # GameObject.image = imm (inlined ctor)
                calls.append(dict(call='store GameObject.image', imms=[int(mi.group(1), 0)]))
            if m == 'call':
                op = ins.operands[0]
                if op.type == capstone.x86.X86_OP_MEM:   # virtual call, e.g. changeImage
                    calls.append(dict(call='vcall+0x%x' % op.mem.disp, imms=sorted(set(v for k, v in imms.items() if k == 'edx'))))
                if op.type == capstone.x86.X86_OP_IMM:
                    # args: param k (after `this`) -> edx, r8d, r9d, [rsp+0x20], [rsp+0x28], ...
                    order = ['edx', 'r8d', 'r9d'] + ['dword ptr [rsp + 0x%x]' % (0x20 + 8 * j) for j in range(8)]
                    args = {order.index(k): v for k, v in imms.items() if k in order}
                    calls.append(dict(call=names.get(op.imm, hex(op.imm)),
                                      imms=sorted(set(v for v in imms.values() if 0 < v < 2048)),
                                      args=args))
                imms = {}
            if m == 'jmp' or m == 'ret':
                break
            pc += ins.size
        out[base + i] = dict(target=hex(tgt), calls=calls)
    json.dump(out, sys.stdout, indent=0)


if __name__ == '__main__':
    main()
