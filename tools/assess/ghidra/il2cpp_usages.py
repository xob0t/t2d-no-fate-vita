"""Independent (non-Rewind) decoding of IL2CPP v31 metadata usage globals and
per-type field offsets from a Windows x64 GameAssembly.dll + global-metadata.dat.

usage: python -I il2cpp_usages.py <GameAssembly.dll> <global-metadata.dat> <types.tsv from dump_to_tables> <outdir> [TypeName ...]

writes:
  usages.tsv        va  kind  index  label     (every .data qword that decodes as an
                                                encoded metadata usage token)
  fieldoffsets.tsv  typedefIndex  typeName  fieldNo  offset   (for the requested types)
Type names come from the metadata itself (namespace + name), not from Rewind.
"""
import struct
import sys
import os
import pefile


def main():
    dll, meta, _types_tsv, outdir = sys.argv[1:5]
    want = set(sys.argv[5:])
    os.makedirs(outdir, exist_ok=True)
    pe = pefile.PE(dll, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    img = pe.get_memory_mapped_image()

    def u64(va):
        return struct.unpack_from('<Q', img, va - base)[0]

    def u32(va):
        return struct.unpack_from('<I', img, va - base)[0]

    def i32(va):
        return struct.unpack_from('<i', img, va - base)[0]

    md = open(meta, 'rb').read()
    sanity, version = struct.unpack_from('<Ii', md, 0)
    assert sanity == 0xFAB11BAF, hex(sanity)
    # v31 header: pairs of (offset, size) after sanity/version
    def hdr(i):
        return struct.unpack_from('<ii', md, 8 + i * 8)
    strlit_off, strlit_size = hdr(0)
    strlitdata_off, _ = hdr(1)
    string_off, _ = hdr(2)
    # find typeDefinitions section: in v31 the header index of typeDefinitions is 20
    # (stringLiteral, stringLiteralData, string, events, properties, methods,
    #  parameterDefaultValues, fieldDefaultValues, fieldAndParameterDefaultValueData,
    #  fieldMarshaledSizes, parameters, fields, genericParameters,
    #  genericParameterConstraints, genericContainers, nestedTypes, interfaces,
    #  vtableMethods, interfaceOffsets, typeDefinitions)
    td_off, td_size = hdr(19)

    def cstr(off):
        end = md.index(b'\0', string_off + off)
        return md[string_off + off:end].decode('utf-8', 'replace')

    # Il2CppTypeDefinition v31 size: 88 bytes (nameIndex, namespaceIndex first)
    TD_SIZE = 88
    ntd = td_size // TD_SIZE
    assert td_size % TD_SIZE == 0, (td_size, TD_SIZE)
    tdnames = []
    for i in range(ntd):
        name_i, ns_i = struct.unpack_from('<ii', md, td_off + i * TD_SIZE)
        # declaringTypeIndex is at +0x10 in v31 (byvalTypeIndex +0x8, declaringTypeIndex +0xC)
        tdnames.append((cstr(ns_i), cstr(name_i)))
    print('typedefs', ntd)

    # locate Il2CppMetadataRegistration: ... typesCount, types, methodSpecsCount, methodSpecs,
    # fieldOffsetsCount(=ntd), fieldOffsets, typeDefinitionsSizesCount(=ntd), typeDefinitionsSizes
    data = [s for s in pe.sections if s.Name.rstrip(b'\0') in (b'.data', b'.rdata')]
    reg = None
    for s in data:
        start = base + s.VirtualAddress
        n = min(s.Misc_VirtualSize, s.SizeOfRawData)
        for off in range(0, n - 64, 8):
            va = start + off
            if u64(va) == ntd and u64(va + 16) == ntd:
                p1, p2 = u64(va + 8), u64(va + 24)
                if base < p1 < base + 0x2000000 and base < p2 < base + 0x2000000:
                    reg = va - 80  # back to genericClassesCount (fieldOffsetsCount is qword 10)
                    break
        if reg:
            break
    assert reg, 'metadata registration not found'
    types_count, types_ptr = u64(reg + 48), u64(reg + 56)
    field_offsets = u64(reg + 88)
    print('metadataRegistration at 0x%X types %d' % (reg, types_count))

    def typename_of_type(tidx):
        t = u64(types_ptr + tidx * 8)
        data_ = u64(t)
        bits = u32(t + 8)
        kind = (bits >> 16) & 0xFF
        if kind in (0x11, 0x12):  # VALUETYPE / CLASS: data = typedef index
            ns, n = tdnames[data_]
            return (ns + '.' if ns else '') + n
        prim = {0x1: 'void', 0x2: 'bool', 0x3: 'char', 0x4: 'sbyte', 0x5: 'byte', 0x6: 'short', 0x7: 'ushort',
                0x8: 'int', 0x9: 'uint', 0xA: 'long', 0xB: 'ulong', 0xC: 'float', 0xD: 'double', 0xE: 'string',
                0x18: 'IntPtr', 0x1C: 'object'}
        if kind in prim:
            return prim[kind]
        if kind == 0x1D:  # SZARRAY: data = Il2CppType*
            for j in range(types_count):
                if u64(types_ptr + j * 8) == data_:
                    return typename_of_type(j) + '[]'
            return '?[]'
        if kind == 0x15:  # GENERICINST: data = Il2CppGenericClass* {Il2CppType* type; ...}
            gt = u64(data_)
            for j in range(types_count):
                if u64(types_ptr + j * 8) == gt:
                    return typename_of_type(j) + '<..>'
            return 'GenericInst'
        return 'type%02X' % kind

    def strlit(idx):
        length, dindex = struct.unpack_from('<Ii', md, strlit_off + idx * 8)
        return md[strlitdata_off + dindex:strlitdata_off + dindex + length].decode('utf-8', 'replace')

    out = open(os.path.join(outdir, 'usages.tsv'), 'w', encoding='utf-8', newline='\n')
    nstr = strlit_size // 8
    counts = {}
    for s in pe.sections:
        if s.Name.rstrip(b'\0') != b'.data':
            continue
        start = base + s.VirtualAddress
        n = min(s.Misc_VirtualSize, s.SizeOfRawData)
        for off in range(0, n, 8):
            v = u64(start + off)
            if v >> 32 or not (v & 1):
                continue
            kind = (v >> 29) & 7
            idx = (v & 0x1FFFFFFE) >> 1
            label = None
            try:
                if kind == 1 and idx < types_count:
                    label = 'TI_' + typename_of_type(idx)
                elif kind == 2 and idx < types_count:
                    label = 'TY_' + typename_of_type(idx)
                elif kind == 5 and idx < nstr:
                    label = 'STR_' + repr(strlit(idx))[:60]
                elif kind == 3:
                    label = 'MD_%d' % idx
                elif kind == 4:
                    label = 'FI_%d' % idx
                elif kind == 6:
                    label = 'MS_%d' % idx
            except Exception as e:  # noqa
                label = None
            if label:
                counts[kind] = counts.get(kind, 0) + 1
                out.write('0x%X\t%d\t%d\t%s\n' % (start + off, kind, idx, label.replace('\t', ' ').replace('\n', '\\n')))
    out.close()
    print('usages', counts)

    fo = open(os.path.join(outdir, 'fieldoffsets.tsv'), 'w', encoding='utf-8', newline='\n')
    # field counts per typedef: Il2CppTypeDefinition.field_count (uint16) at +0x44 in v31 layout (16 int32 then 8 uint16)
    for i, (ns, n) in enumerate(tdnames):
        full = (ns + '.' if ns else '') + n
        if want and full not in want and n not in want:
            continue
        fc = struct.unpack_from('<H', md, td_off + i * TD_SIZE + 0x44)[0]
        p = u64(field_offsets + i * 8)
        if not p:
            continue
        for k in range(fc):
            fo.write('%d\t%s\t%d\t0x%X\n' % (i, full, k, i32(p + k * 4)))
    fo.close()


if __name__ == '__main__':
    main()
