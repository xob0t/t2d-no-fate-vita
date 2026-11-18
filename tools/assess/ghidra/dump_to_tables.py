"""Convert an Il2CppDumper-style dump.cs (as written by Rewind) into flat tables
that the Ghidra script ApplyDumpAndDecompile.java consumes.

usage: python -I dump_to_tables.py <dump.cs> <outdir>

writes:
  methods.tsv  va  fullname  static  ret  params(;)  shared
  types.tsv    fullname  kind  size  base
  fields.tsv   owner  name  offset  type  static
Type names are kept as written in the dump (namespace-qualified where the dump
qualifies them); the Java side resolves them against types.tsv.
"""
import os
import re
import sys

re_ns = re.compile(r'^// Namespace: (.*)$')
re_type = re.compile(
    r'^(\t*)(?:\[.*\]\s*)?((?:public|private|internal|protected|sealed|abstract|static|readonly|ref|unsafe|partial|\s)*)'
    r'(class|struct|enum|interface) ([^:/{]+?)(?: : ([^/]+?))?\s*// TypeDefIndex: \d+, Token: 0x[0-9A-F]+(?:, Size: 0x([0-9A-F]+))?')
re_field = re.compile(r'^\t+((?:public|private|internal|protected|static|readonly|volatile|const|new|\s)*)([^=;(]+?) ([^ ;=]+); // 0x([0-9A-F]+)')
re_va = re.compile(r'// VA: 0x([0-9A-F]+) RVA: 0x[0-9A-F]+(?: \(code shared by (\d+) methods\))?')
re_method = re.compile(r'^\t+((?:public|private|internal|protected|static|virtual|override|abstract|sealed|extern|new|unsafe|async|\s)*)(.*?)\s*\((.*)\) \{ \}\s*$')


def split_params(s):
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch in '<[(':
            depth += 1
        elif ch in '>])':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur.strip())
            cur = ''
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    res = []
    for p in out:
        p = re.sub(r'^\[[^\]]*\]\s*', '', p)
        p = p.split(' = ')[0]
        parts = p.rsplit(' ', 1)
        t = parts[0] if len(parts) == 2 else p
        res.append(t.replace('params ', ''))
    return res


def main():
    src, outdir = sys.argv[1], sys.argv[2]
    os.makedirs(outdir, exist_ok=True)
    ns = ''
    stack = []  # (indent, fullname)
    pending_va = None
    m_out = open(os.path.join(outdir, 'methods.tsv'), 'w', encoding='utf-8', newline='\n')
    t_out = open(os.path.join(outdir, 'types.tsv'), 'w', encoding='utf-8', newline='\n')
    f_out = open(os.path.join(outdir, 'fields.tsv'), 'w', encoding='utf-8', newline='\n')
    with open(src, encoding='utf-8') as fh:
        for line in fh:
            line = line.rstrip('\n')
            m = re_ns.match(line)
            if m:
                ns = m.group(1)
                if ns == '<global>':
                    ns = ''
                continue
            m = re_type.match(line)
            if m:
                indent = len(m.group(1))
                while stack and stack[-1][0] >= indent:
                    stack.pop()
                name = m.group(4).strip()
                full = (stack[-1][1] + '.' + name) if stack else ((ns + '.' + name) if ns else name)
                stack.append((indent, full))
                base = (m.group(5) or '').split(',')[0].strip()
                t_out.write('\t'.join([full, m.group(3), m.group(6) or '', base]) + '\n')
                pending_va = None
                continue
            if not stack:
                continue
            m = re_va.search(line)
            if m:
                pending_va = (m.group(1), m.group(2) or '1')
                continue
            if pending_va:
                m = re_method.match(line)
                if m:
                    mods, head, params = m.group(1), m.group(2), m.group(3)
                    if head.startswith('['):
                        continue
                    parts = head.rsplit(' ', 1)
                    static = '1' if 'static' in mods.split() else '0'
                    if len(parts) == 2:
                        ret, name = parts
                    else:  # constructors are printed with the type's name
                        ret, name = 'void', ('.cctor' if static == '1' else '.ctor')
                    ptypes = split_params(params)
                    m_out.write('\t'.join(['0x' + pending_va[0], stack[-1][1] + '::' + name, static, ret,
                                           ';'.join(ptypes), pending_va[1]]) + '\n')
                    pending_va = None
                    continue
            m = re_field.match(line)
            if m and '(' not in line:
                mods = m.group(1).split()
                if 'const' in mods:
                    continue
                f_out.write('\t'.join([stack[-1][1], m.group(3), '0x' + m.group(4), m.group(2).strip(),
                                       '1' if 'static' in mods else '0']) + '\n')
    for f in (m_out, t_out, f_out):
        f.close()


if __name__ == '__main__':
    main()
