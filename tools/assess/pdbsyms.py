"""Dump a PDB's public symbols as the symbols.tsv that rewind-lab sig-build and sig-apply read.

    python -I tools/assess/pdbsyms.py <GameAssembly.pdb> <symbols.tsv>

One line per S_PUB32 record: the RVA in hex, a tab, 1 if the symbol is code (the record's code or function
flag) or 0 if not, a tab, and the decorated name as the linker wrote it. Rewind classifies helpers by MSVC
decorated names (RuntimeHelpers.Classify), so the names are not undecorated.

The PDB is read directly (MSF 7.00 container, DBI stream, symbol record stream, section headers from the
optional debug header) in plain Python. That needs nothing installed: DIA (msdia140.dll) wants COM
registration, llvm-pdbutil isn't on this machine, and only the public symbols are needed, which take about a
hundred lines to read. Sizes and layouts follow LLVM's PDB format documentation
(https://llvm.org/docs/PDB/index.html) and Microsoft's microsoft-pdb repository (cvinfo.h).
"""
import struct
import sys

MSF_MAGIC = b'Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0\0'
S_PUB32 = 0x110E
CVPSF_CODE, CVPSF_FUNCTION = 1, 2
DBI_STREAM = 3
DBG_SECTION_HEADERS = 5  # index of the section header stream in the DBI optional debug header


class Msf:
    def __init__(self, path):
        with open(path, 'rb') as handle:
            self.data = handle.read()
        if not self.data.startswith(MSF_MAGIC):
            sys.exit(f'{path}: not an MSF 7.00 PDB')
        self.block_size, _, _, directory_bytes, _, block_map = struct.unpack_from('<6I', self.data, len(MSF_MAGIC))
        directory_blocks = -(-directory_bytes // self.block_size)
        indices = struct.unpack_from(f'<{directory_blocks}I', self.data, block_map * self.block_size)
        directory = b''.join(self.block(i) for i in indices)[:directory_bytes]
        count = struct.unpack_from('<I', directory, 0)[0]
        sizes = struct.unpack_from(f'<{count}I', directory, 4)
        at = 4 + 4 * count
        self.streams = []
        for size in sizes:
            if size == 0xFFFFFFFF:  # a deleted stream
                size = 0
            blocks = -(-size // self.block_size)
            self.streams.append((size, struct.unpack_from(f'<{blocks}I', directory, at)))
            at += 4 * blocks

    def block(self, index):
        return self.data[index * self.block_size:(index + 1) * self.block_size]

    def stream(self, number):
        size, blocks = self.streams[number]
        return b''.join(self.block(i) for i in blocks)[:size]


def public_symbols(path):
    msf = Msf(path)
    dbi = msf.stream(DBI_STREAM)
    # DBI header: 64 bytes; the substream sizes give where the optional debug header starts.
    (_, _, _, _, _, _, _, symrec_stream, _, mod_info, sec_contrib, sec_map, source_info, type_server_map,
     _, optional_dbg, ec_info) = struct.unpack_from('<iIIHHHHHHiiiiiIii', dbi, 0)
    dbg_at = 64 + mod_info + sec_contrib + sec_map + source_info + type_server_map + ec_info
    dbg_streams = struct.unpack_from(f'<{optional_dbg // 2}H', dbi, dbg_at)
    headers = msf.stream(dbg_streams[DBG_SECTION_HEADERS])
    # IMAGE_SECTION_HEADER is 40 bytes; VirtualAddress is at offset 12. Segments in symbols are 1-based.
    section_rva = [struct.unpack_from('<I', headers, at + 12)[0] for at in range(0, len(headers), 40)]

    records = msf.stream(symrec_stream)
    at = 0
    while at + 4 <= len(records):
        length, kind = struct.unpack_from('<HH', records, at)
        if length < 2:
            break
        if kind == S_PUB32:
            flags, offset, segment = struct.unpack_from('<IIH', records, at + 4)
            end = records.index(b'\0', at + 14)
            name = records[at + 14:end].decode('utf-8', 'replace')
            if 1 <= segment <= len(section_rva):
                yield section_rva[segment - 1] + offset, bool(flags & (CVPSF_CODE | CVPSF_FUNCTION)), name
        at += 2 + length


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    count = code = 0
    with open(sys.argv[2], 'w', encoding='utf-8', newline='\n') as out:
        for rva, is_code, name in sorted(public_symbols(sys.argv[1])):
            out.write(f'{rva:X}\t{1 if is_code else 0}\t{name}\n')
            count += 1
            code += is_code
    print(f'{count} public symbols ({code} code) written to {sys.argv[2]}')


if __name__ == '__main__':
    main()
