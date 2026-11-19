"""Recover the values IL2CPP stores into one-element double[] arrays inside
generated table initialisers (Rewind drops them: bounds-checked store helper).

Pattern per element (x64, MSVC):
    call alloc(klass, 1)          ; rbx = new double[1]
    movsd xmm2, [rip+c] | movaps xmm2, xmmN | xorps xmm2, xmm2
    xor edx, edx ; mov rcx, rbx
    call STORE_D                  ; inner[edx] = xmm2 (repeated per element)
    ...
    mov r8, rbx ; mov edx, IDX ; mov rcx, rdi
    call STORE_REF                ; outer[IDX] = inner

Usage: native_dstores.py GameAssembly.dll FUNC_VA STORE_D_VA STORE_REF_VA COUNT
Prints JSON {outer_index: {inner_index: value}} on stdout.
"""
import json, struct, sys
import capstone, pefile

def main():
    dll = sys.argv[1]
    func, store_d, store_ref = (int(a, 16) for a in sys.argv[2:5])
    count = int(sys.argv[5])
    pe = pefile.PE(dll, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    def read(va, n):
        return pe.get_data(va - base, n)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    md.skipdata = True
    xmm = {}          # reg name -> float value or None
    gpr_imm = {}      # 'edx' -> int
    pending = None    # last value stored by STORE_D
    out = {}
    unknown = 0
    va = func
    chunk = 0x200000
    data = read(va, chunk)
    for ins in md.disasm(data, va):
        m, ops = ins.mnemonic, ins.op_str
        if m in ('movsd', 'movq') and ops.startswith('xmm') and 'rip' in ops:
            reg = ops.split(',')[0]
            # compute rip-relative address
            disp = ins.operands[1].mem.disp
            addr = ins.address + ins.size + disp
            xmm[reg] = struct.unpack('<d', read(addr, 8))[0]
        elif m in ('movaps', 'movapd', 'movsd') and ops.count('xmm') == 2:
            dst, src = (s.strip() for s in ops.split(','))
            xmm[dst] = xmm.get(src)
        elif m in ('xorps', 'xorpd', 'pxor') and ops.count('xmm') == 2:
            dst, src = (s.strip() for s in ops.split(','))
            xmm[dst] = 0.0 if dst == src else None
        elif ops.startswith('xmm') and m not in ('ucomisd', 'comisd'):
            xmm[ops.split(',')[0]] = None
        if m == 'xor' and ops == 'edx, edx':
            gpr_imm['edx'] = 0
        elif m == 'mov' and ops.startswith('edx, ') and ops[5:].startswith('0x') or (m == 'mov' and ops.startswith('edx, ') and ops[5:].isdigit()):
            gpr_imm['edx'] = int(ops[5:], 0)
        elif ops.startswith(('edx', 'rdx', 'dx')) and m not in ('cmp', 'test'):
            gpr_imm['edx'] = None
        if m == 'call':
            tgt = ins.operands[0].imm if ins.operands[0].type == capstone.x86.X86_OP_IMM else None
            if tgt == store_d:
                v = xmm.get('xmm2')
                j = gpr_imm.get('edx')
                if v is None or j is None:
                    unknown += 1
                else:
                    pending = pending or {}
                    pending[j] = v
            elif tgt == store_ref:
                idx = gpr_imm.get('edx')
                if pending is not None and idx is not None:
                    out[idx] = pending
                pending = None
                if idx == count - 1:
                    break
            # calls clobber volatile xmm0-5 and edx
            for r in ('xmm0', 'xmm1', 'xmm2', 'xmm3', 'xmm4', 'xmm5'):
                xmm.pop(r, None)
            gpr_imm.pop('edx', None)
        if m == 'ret' and out:
            break
    sys.stderr.write(f'{len(out)} values, {unknown} unknown\n')
    json.dump(out, sys.stdout)

if __name__ == '__main__':
    main()
