"""Helpers that parse the literal tables in Rewind's recovered C#."""
import re

NAME_B = r'(?<![A-Za-z0-9_])'   # word boundary before an identifier


def method_body(src, name):
    m = re.search(r'\n\t(?:public|private|internal)[^\n]*\b' + re.escape(name) + r'\(\)\n\t\{\n', src)
    if not m:
        raise KeyError(name)
    end = src.index('\n\t}\n', m.end())
    return src[m.end():end]


def num(expr):
    expr = expr.strip().rstrip(',')
    expr = expr.replace('(int)(uint)', '')
    if not re.fullmatch(r'[-0-9.E+/ ()]+', expr):
        raise ValueError(expr)
    return eval(expr, {'__builtins__': {}})


def split_items(body):
    """Split a { a, b, c } list body by commas (no nested braces)."""
    return [x for x in (s.strip() for s in body.replace('\n', ' ').split(',')) if x]


def jagged_initializer(body, typ):
    """new T[n][] { new T[k] { ... }, ... } -> list of lists.
    Elements written as new T[k] without a body are zero-filled (only valid
    for methods without gap markers)."""
    start = body.index('new %s[' % typ)
    out = []
    pat = re.compile(r'new %s\[(\d+)\](\s*\{([^{}]*)\})?' % typ)
    for m in pat.finditer(body, body.index('{', start)):
        k = int(m.group(1))
        if m.group(3) is None:
            out.append([0] * k)
        else:
            out.append([num(x) for x in split_items(m.group(3))])
    return out


def jagged_any(body, typ, n):
    if re.search(r'new %s\[%d\]\[\]\s*\{' % (typ, n), body):
        return jagged_initializer(body, typ)
    return jagged_statements(body, typ, n)


def jagged_statements(body, typ, n):
    """Statement form:
         array[i] = new T[k] { ... };
         array[i] = new T[k];                       (zero-filled)
         T[] arrayN = new T[k]; arrayN[j] = v; ... array[i] = arrayN;
    int temporaries start zero-filled; double temporaries start as gaps (None)
    because Rewind drops the bounds-checked double stores; the native
    extractor fills those."""
    out = [None] * n
    for m in re.finditer(NAME_B + r'array\[(\d+)\] = new %s\[(\d+)\]\s*\{([^{}]*)\};' % typ, body):
        out[int(m.group(1))] = [num(x) for x in split_items(m.group(3))]
    for m in re.finditer(NAME_B + r'array\[(\d+)\] = new %s\[(\d+)\];' % typ, body):
        out[int(m.group(1))] = [0] * int(m.group(2))
    fill = 0 if typ == 'int' else None
    tmps = {m.group(1): [fill] * int(m.group(2))
            for m in re.finditer(r'%s\[\] (array\d+) = new %s\[(\d+)\];' % (typ, typ), body)}
    for m in re.finditer(NAME_B + r'(array\d+)\[(\d+)\] = (-?[0-9.E+/ ]+);', body):
        tmps[m.group(1)][int(m.group(2))] = num(m.group(3))
    for m in re.finditer(NAME_B + r'array\[(\d+)\] = (array\d+);', body):
        out[int(m.group(1))] = tmps[m.group(2)]
    return out


def static_ints(src, prefix_re):
    """NAME = 123; assignments (static constructor)."""
    return {m.group(1): int(m.group(2))
            for m in re.finditer(r'\n\t\t(%s[A-Za-z0-9_]*) = (-?\d+);' % prefix_re, src)}
