# ruff: noqa: UP031
"""A small reader for the IL of Rewind's recovered assemblies, enough to evaluate the game's straight-line table
initialisers (AssetsPack.staticInit2 and staticInit5..13, Assets' static constructor).

It decodes method bodies with dnfile, resolves the tokens the tables use (fields, methods, types, user strings,
field RVA blobs) and runs a stack machine over code without branches. Values it cannot know (Rewind's opaque
expressions, calls into the game) are Unknown; arrays are Python lists that start as None per element, so a
caller can tell an element nobody stored from one stored as zero.
"""

import struct

import dnfile

# Operand sizes. -1: switch (a count and that many 4-byte targets).
_ONE = {
    0x0E: 1,
    0x0F: 1,
    0x10: 1,
    0x11: 1,
    0x12: 1,
    0x13: 1,
    0x1F: 1,
    0x20: 4,
    0x21: 8,
    0x22: 4,
    0x23: 8,
    0x27: 4,
    0x28: 4,
    0x29: 4,
    0x45: -1,
    0x6F: 4,
    0x70: 4,
    0x71: 4,
    0x72: 4,
    0x73: 4,
    0x74: 4,
    0x75: 4,
    0x79: 4,
    0x7B: 4,
    0x7C: 4,
    0x7D: 4,
    0x7E: 4,
    0x7F: 4,
    0x80: 4,
    0x81: 4,
    0x8C: 4,
    0x8D: 4,
    0x8F: 4,
    0xA3: 4,
    0xA4: 4,
    0xA5: 4,
    0xC2: 4,
    0xC6: 4,
    0xD0: 4,
    0xDD: 4,
    0xDE: 1,
}
_ONE.update(dict.fromkeys(range(43, 56), 1))
_ONE.update(dict.fromkeys(range(56, 69), 4))
_TWO = {
    0x06: 4,
    0x07: 4,
    0x09: 2,
    0x0A: 2,
    0x0B: 2,
    0x0C: 2,
    0x0D: 2,
    0x0E: 2,
    0x12: 1,
    0x15: 4,
    0x16: 4,
    0x19: 1,
    0x1C: 4,
    0x1D: 4,
}
BRANCHES = set(range(0x2B, 0x46)) | {0xDD, 0xDE}

_NAMES = {
    0x00: 'nop',
    0x14: 'ldnull',
    0x15: 'ldc.i4.m1',
    0x1F: 'ldc.i4.s',
    0x20: 'ldc.i4',
    0x21: 'ldc.i8',
    0x22: 'ldc.r4',
    0x23: 'ldc.r8',
    0x25: 'dup',
    0x26: 'pop',
    0x28: 'call',
    0x2A: 'ret',
    0x6F: 'callvirt',
    0x72: 'ldstr',
    0x73: 'newobj',
    0x7E: 'ldsfld',
    0x80: 'stsfld',
    0x8D: 'newarr',
    0xA4: 'stelem',
    0xD0: 'ldtoken',
    0x11: 'ldloc.s',
    0x13: 'stloc.s',
    0xFE0C: 'ldloc',
    0xFE0E: 'stloc',
}
_NAMES.update({0x16 + k: 'ldc.i4.%d' % k for k in range(9)})
_NAMES.update({0x06 + k: 'ldloc.%d' % k for k in range(4)})
_NAMES.update({0x0A + k: 'stloc.%d' % k for k in range(4)})
_NAMES.update(
    {
        op: 'stelem.' + n
        for op, n in zip(range(0x9B, 0xA3), ('i', 'i1', 'i2', 'i4', 'i8', 'r4', 'r8', 'ref'), strict=False)
    }
)
_NAMES.update(dict.fromkeys((103, 104, 105, 106, 107, 108, 109, 110, 209, 210, 211, 212, 224), 'conv'))


class Unknown:
    """A value the evaluator can't know. Arrays made by Rewind's opaque allocations are Unknown too; an
    InitializeArray on one gives it a blob (.blob)."""

    def __init__(self, what=''):
        self.what = what
        self.blob = None

    def __repr__(self):
        return f'Unknown({self.what})'


class Ins:
    __slots__ = ('arg', 'name', 'offset', 'op')

    def __init__(self, offset, op, arg):
        self.offset, self.op, self.arg = offset, op, arg
        self.name = _NAMES.get(op, f'0x{op:X}')

    def __repr__(self):
        return f'IL_{self.offset:04X} {self.name} {self.arg!r}'


def _compressed(data, pos):
    b = data[pos]
    if b & 0x80 == 0:
        return b, pos + 1
    if b & 0xC0 == 0x80:
        return ((b & 0x3F) << 8) | data[pos + 1], pos + 2
    return ((b & 0x1F) << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3], pos + 4


class Assembly:
    def __init__(self, path):
        self.pe = dnfile.dnPE(str(path))
        self.t = self.pe.net.mdtables
        self.types = {}  # full name -> TypeDef row index (1-based)
        self.field_owner = {}  # Field row index -> type full name
        self.method_owner = {}
        rows = self.t.TypeDef.rows
        for i, r in enumerate(rows, 1):
            full = (str(r.TypeNamespace) + '.' if str(r.TypeNamespace) else '') + str(r.TypeName)
            self.types[full] = i
            for f in r.FieldList:
                self.field_owner[f.row_index] = full
            for m in r.MethodList:
                self.method_owner[m.row_index] = full
        self.field_rva = {fr.Field.row_index: fr.Rva for fr in (self.t.FieldRva.rows if self.t.FieldRva else [])}

    # --- metadata -----------------------------------------------------------------------------------------
    def methods(self, type_name, name):
        r = self.t.TypeDef.rows[self.types[type_name] - 1]
        return [m.row_index for m in r.MethodList if str(m.row.Name) == name]

    def method(self, type_name, name):
        found = self.methods(type_name, name)
        if len(found) != 1:
            raise KeyError('%s.%s: %d methods' % (type_name, name, len(found)))
        return found[0]

    def field(self, row_index):
        return self.field_owner.get(row_index, '?'), str(self.t.Field.rows[row_index - 1].Name)

    def _typeref_name(self, table, index):
        if table == 0x01:
            r = self.t.TypeRef.rows[index - 1]
            return (str(r.TypeNamespace) + '.' if str(r.TypeNamespace) else '') + str(r.TypeName)
        if table == 0x02:
            r = self.t.TypeDef.rows[index - 1]
            return (str(r.TypeNamespace) + '.' if str(r.TypeNamespace) else '') + str(r.TypeName)
        if table == 0x1B:
            return 'typespec#%d' % index
        return '?'

    def token(self, tok):
        """(kind, value) for a metadata token: ('field', (type, name, row)), ('method', (type, name, nparams,
        has_this, returns)), ('type', name), ('string', text)."""
        table, index = tok >> 24, tok & 0xFFFFFF
        if table == 0x04:
            owner, name = self.field(index)
            return 'field', (owner, name, index)
        if table == 0x70:
            return 'string', self.pe.net.user_strings.get(index).value
        if table in (0x01, 0x02, 0x1B):
            return 'type', self._typeref_name(table, index)
        if table == 0x06:
            r = self.t.MethodDef.rows[index - 1]
            return 'method', (self.method_owner.get(index, '?'), str(r.Name), *self._sig(r.Signature))
        if table == 0x0A:
            r = self.t.MemberRef.rows[index - 1]
            cls = r.Class
            owner = (
                self._typeref_name({'TypeRef': 1, 'TypeDef': 2, 'TypeSpec': 0x1B}.get(cls.table.name, 0), cls.row_index)
                if cls.table is not None
                else '?'
            )
            return 'method', (owner, str(r.Name), *self._sig(r.Signature))
        if table == 0x2B:
            r = self.t.MethodSpec.rows[index - 1]
            m = r.Method
            code = 0x06 if m.table.name == 'MethodDef' else 0x0A
            return self.token((code << 24) | m.row_index)
        return 'other', tok

    @staticmethod
    def _sig(blob):
        data = blob.value if hasattr(blob, 'value') else bytes(blob)
        conv = data[0]
        pos = 1
        if conv & 0x10:
            _, pos = _compressed(data, pos)
        count, pos = _compressed(data, pos)
        return count, bool(conv & 0x20), data[pos] != 0x01

    def blob(self, field_row, size):
        return self.pe.get_data(self.field_rva[field_row], size)

    # --- code ---------------------------------------------------------------------------------------------
    def body(self, method_row):
        rva = self.t.MethodDef.rows[method_row - 1].Rva
        head = self.pe.get_data(rva, 12)
        if head[0] & 3 == 2:
            size, start = head[0] >> 2, rva + 1
        else:
            size = struct.unpack_from('<I', head, 4)[0]
            start = rva + (head[1] >> 4) * 4
        return self.decode(self.pe.get_data(start, size))

    @staticmethod
    def decode(code):
        out, pos = [], 0
        while pos < len(code):
            at = pos
            op = code[pos]
            pos += 1
            if op == 0xFE:
                op2 = code[pos]
                pos += 1
                n = _TWO.get(op2, 0)
                op = 0xFE00 | op2
            else:
                n = _ONE.get(op, 0)
            if n == -1:
                count = struct.unpack_from('<I', code, pos)[0]
                arg = struct.unpack_from('<%di' % count, code, pos + 4)
                pos += 4 + 4 * count
            elif n == 0:
                arg = None
            else:
                raw = code[pos : pos + n]
                pos += n
                if op == 0x22:
                    arg = struct.unpack('<f', raw)[0]
                elif op == 0x23:
                    arg = struct.unpack('<d', raw)[0]
                elif op == 0x21:
                    arg = struct.unpack('<q', raw)[0]
                elif n == 1:
                    arg = struct.unpack('<b', raw)[0] if op == 0x1F or 0x2B <= op <= 0x37 or op == 0xDE else raw[0]
                elif n == 2:
                    arg = struct.unpack('<H', raw)[0]
                else:
                    arg = struct.unpack('<i', raw)[0]
            out.append(Ins(at, op, arg))
        return out

    def run(self, method_row, statics=None):
        """Evaluate a straight-line method. Returns (statics, markers): statics maps (type, field) to the values
        stored there (the dict passed in is updated), markers counts calls into Rewind's __Rewind helpers."""
        statics = {} if statics is None else statics
        stack, locs = [], {}
        markers = 0
        for ins in self.body(method_row):
            op = ins.op
            if op in BRANCHES or op == 0x45:
                raise ValueError(f'IL_{ins.offset:04X}: a branch; this evaluator runs straight-line code only')
            if op == 0x00:
                continue
            if 0x16 <= op <= 0x1E:
                stack.append(op - 0x16)
            elif op == 0x15:
                stack.append(-1)
            elif op in (0x1F, 0x20, 0x21, 0x22, 0x23):
                stack.append(ins.arg)
            elif op == 0x14:
                stack.append(None)
            elif op == 0x72:
                stack.append(self.token(ins.arg)[1])
            elif 0x06 <= op <= 0x09:
                stack.append(locs.get(op - 0x06))
            elif 0x0A <= op <= 0x0D:
                locs[op - 0x0A] = stack.pop()
            elif op in (0x11, 0xFE0C):
                stack.append(locs.get(ins.arg))
            elif op in (0x13, 0xFE0E):
                locs[ins.arg] = stack.pop()
            elif op == 0x25:
                stack.append(stack[-1])
            elif op == 0x26:
                stack.pop()
            elif op in (0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0xD1, 0xD2, 0xD3, 0xD4, 0xE0):
                v = stack.pop()
                if isinstance(v, int):
                    bits, signed = {
                        0x67: (8, 1),
                        0x68: (16, 1),
                        0x69: (32, 1),
                        0x6A: (64, 1),
                        0xD1: (16, 0),
                        0xD2: (8, 0),
                        0x6D: (32, 0),
                        0x6E: (64, 0),
                        0xD3: (64, 1),
                        0xE0: (64, 0),
                    }.get(op, (0, 0))
                    if bits:
                        v &= (1 << bits) - 1
                        if signed and v >> (bits - 1):
                            v -= 1 << bits
                    elif op in (0x6B, 0x6C):
                        v = float(v)
                stack.append(v)
            elif op == 0x8D:
                n = stack.pop()
                stack.append([None] * n if isinstance(n, int) else Unknown('newarr'))
            elif op == 0xA4 or 0x9B <= op <= 0xA2:
                v, i, a = stack.pop(), stack.pop(), stack.pop()
                if isinstance(a, list) and isinstance(i, int):
                    a[i] = v
            elif op == 0x7E:
                kind, (owner, name, _) = self.token(ins.arg)
                stack.append(statics.get((owner, name), Unknown(owner + '.' + name)))
            elif op == 0x80:
                kind, (owner, name, _) = self.token(ins.arg)
                statics[(owner, name)] = stack.pop()
            elif op == 0xD0:
                kind, val = self.token(ins.arg)
                stack.append(('handle', val[2]) if kind == 'field' else Unknown('token'))
            elif op in (0x28, 0x6F, 0x73):
                kind, (owner, name, nparams, has_this, returns) = self.token(ins.arg)
                args = [stack.pop() for _ in range(nparams + (1 if has_this and op != 0x73 else 0))][::-1]
                if owner == '__Rewind':
                    markers += 1
                if owner == 'System.Runtime.CompilerServices.RuntimeHelpers' and name == 'InitializeArray':
                    arr, handle = args
                    self._initialize(arr, handle[1])
                if op == 0x73 or returns:
                    stack.append(Unknown(owner + '.' + name))
            elif op == 0x2A:
                break
            else:
                raise ValueError(f'IL_{ins.offset:04X}: opcode {ins.name} not handled')
        return statics, markers

    def _initialize(self, arr, field_row):
        if isinstance(arr, Unknown):
            arr.blob = field_row
            return
        n = len(arr)
        size = self._blob_size(field_row)
        if size == 4 * n:
            vals = struct.unpack('<%di' % n, self.blob(field_row, size))
        elif size == 8 * n:
            vals = struct.unpack('<%dd' % n, self.blob(field_row, size))
        else:
            raise ValueError('InitializeArray: %d bytes for %d elements' % (size, n))
        arr[:] = list(vals)

    def _blob_size(self, field_row):
        """The size of a <PrivateImplementationDetails> field's struct (ClassLayout of its __StaticArrayInitTypeSize
        type, also in the type's name)."""
        sig = self.t.Field.rows[field_row - 1].Signature
        data = sig.value if hasattr(sig, 'value') else bytes(sig)
        # FIELD 0x06, then VALUETYPE 0x11 or CLASS 0x12 and a TypeDefOrRef coded index
        coded, _ = _compressed(data, 2)
        tag, index = coded & 3, coded >> 2
        if tag == 0:
            for cl in self.t.ClassLayout.rows:
                if cl.Parent.row_index == index:
                    return cl.ClassSize
            name = str(self.t.TypeDef.rows[index - 1].TypeName)
            if name.startswith('__StaticArrayInitTypeSize='):
                return int(name.split('=')[1])
        raise ValueError('blob size of field %d' % field_row)

    def blob_ints(self, field_row):
        size = self._blob_size(field_row)
        return list(struct.unpack('<%di' % (size // 4), self.blob(field_row, size)))
