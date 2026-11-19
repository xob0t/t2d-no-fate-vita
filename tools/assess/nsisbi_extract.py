"""Extract the files of a Unity 6 installer without running it.

    python -I tools/assess/nsisbi_extract.py <installer.exe> <out dir> [--var N] [--list]

Unity 6's Windows installers are built with NSISBI, the NSIS fork for installers over 2 GB, and 7-Zip can't
open them (it reports "Is not archive"). The format differs from NSIS 3 Unicode only in a few places, found by
reading the files:

- The first header ("NullsoftInst") has a 64-bit length of the data after it, and flags with bits 0x70 set.
- Every data block starts with a 64-bit size whose top bit says it is compressed, then a 3-byte field (the
  chunk length), then that many bytes of raw LZMA stream (5 property bytes and the data, ending with an end
  marker). Files over 2 MiB come as several such chunks, one after another; a zero length ends the block.
- Install entries are 9 32-bit words (opcode and 8 parameters), not 7; EW_EXTRACTFILE's data position is
  parameters 2 and 3 (low and high word).

The header block's entries are walked in order, as the installer runs them: EW_CREATEDIR with its "set output
path" flag sets the current folder, and EW_EXTRACTFILE writes a file there. Only files under the folder held in
one variable are written (--var, default 31: $INSTDIR's copy that Unity's module installers extract into); that
folder is the output root. Every entry is written, whatever conditions the script attaches to it, so a file that
two branches extract is written twice, the later one winning. Paths with '..' or a drive are refused.

Check the installer's Authenticode signature before using this: it trusts the file's structure.
"""
import argparse
import lzma
import os
import struct
import sys

FIRST_HEADER_MAGIC = b'\xef\xbe\xad\xdeNullsoftInst'
EW_CREATEDIR, EW_EXTRACTFILE = 11, 20
ENTRY_WORDS = 9
NS_LANG, NS_SHELL, NS_VAR, NS_SKIP = 1, 2, 3, 4
COMPRESSED = 1 << 63


def lzma_block(data, at):
    """(decompressed bytes, offset after the block) for the data block at 'at'."""
    (size,) = struct.unpack_from('<Q', data, at)
    body = at + 8
    if not size & COMPRESSED:
        return data[body:body + size], body + size
    size &= ~COMPRESSED
    end = body + size
    out = []
    at = body
    while at < end:
        # One chunk (at most 2 MiB uncompressed): a 24-bit length, then that many bytes of raw LZMA stream
        # (5 property bytes, then the data, with an end marker).
        length = data[at] | data[at + 1] << 8 | data[at + 2] << 16
        stream = at + 3
        if length == 0:  # a zero length ends the block
            at = stream
            continue
        props = data[stream]
        (dict_size,) = struct.unpack_from('<I', data, stream + 1)
        filters = [{'id': lzma.FILTER_LZMA1, 'dict_size': dict_size, 'lc': props % 9, 'lp': props // 9 % 5, 'pb': props // 45}]
        decoder = lzma.LZMADecompressor(lzma.FORMAT_RAW, filters=filters)
        out.append(decoder.decompress(data[stream + 5:stream + length]))
        if not decoder.eof:
            sys.exit(f'LZMA chunk at {at:#x} did not end where its length says')
        at = stream + length
    if at != end:
        sys.exit(f'block at {body - 8:#x}: chunks overrun the block')
    return b''.join(out), end


class Header:
    def __init__(self, raw):
        self.raw = raw
        blocks = struct.unpack_from('<16I', raw, 4)  # (offset, count) for pages, sections, entries, strings, ...
        self.entries_at, self.entry_count = blocks[4], blocks[5]
        self.strings_at = blocks[6]

    def entries(self):
        for i in range(self.entry_count):
            yield struct.unpack_from(f'<{ENTRY_WORDS}I', self.raw, self.entries_at + 4 * ENTRY_WORDS * i)

    def string(self, offset):
        """A UTF-16 string; variables come back as '$V<n>', language strings and shell folders as '$L<n>', '$S<n>'."""
        out, at = [], self.strings_at + 2 * offset
        while True:
            (c,) = struct.unpack_from('<H', self.raw, at)
            at += 2
            if c == 0:
                return ''.join(out)
            if c in (NS_LANG, NS_SHELL, NS_VAR, NS_SKIP):
                (v,) = struct.unpack_from('<H', self.raw, at)
                at += 2
                if c == NS_SKIP:
                    out.append(chr(v))
                else:
                    out.append({NS_LANG: '$L', NS_SHELL: '$S', NS_VAR: '$V'}[c] + str((v & 0x7F) | ((v & 0x7F00) >> 1)))
            else:
                out.append(chr(c))


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('installer')
    parser.add_argument('out')
    parser.add_argument('--var', type=int, default=31, help='the variable holding the output root (default 31)')
    parser.add_argument('--list', action='store_true', help='list the files without writing them')
    args = parser.parse_args()

    with open(args.installer, 'rb') as handle:
        data = handle.read()
    first = data.find(FIRST_HEADER_MAGIC)
    if first < 4:
        sys.exit(f'{args.installer}: no NSIS first header')
    start = first - 4
    header_size, data_size = struct.unpack_from('<IQ', data, start + 20)
    raw, data_start = lzma_block(data, start + 36)
    if len(raw) != header_size:
        sys.exit(f'header: {len(raw)} bytes, expected {header_size}; not the NSISBI layout this script knows')
    header = Header(raw)

    root = f'$V{args.var}'
    out_root = os.path.abspath(args.out)
    current = None
    written = skipped = total = 0
    for entry in header.entries():
        op, parms = entry[0], entry[1:]
        if op == EW_CREATEDIR and parms[1] == 1:
            current = header.string(parms[0])
        elif op == EW_EXTRACTFILE:
            name = header.string(parms[1])
            position = parms[2] | parms[3] << 32
            # A name that starts with a variable is a full path of its own (the installer's plugin DLLs).
            full = name if name.startswith('$') else (current or '') + '\\' + name
            if not full.startswith(root + '\\'):
                skipped += 1
                continue
            relative = full[len(root):].lstrip('\\')
            parts = relative.split('\\')
            if any(p in ('', '.', '..') or ':' in p or '$' in p for p in parts):
                sys.exit(f'refusing path {relative!r}')
            content, _ = lzma_block(data, data_start + position)
            total += len(content)
            written += 1
            if args.list:
                print(f'{len(content):>12}  {relative}')
                continue
            target = os.path.join(out_root, *parts)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, 'wb') as handle:
                handle.write(content)
    print(f'{written} files, {total:,} bytes {"listed" if args.list else "written to " + out_root}; '
          f'{skipped} files outside {root} skipped', file=sys.stderr)


if __name__ == '__main__':
    main()
