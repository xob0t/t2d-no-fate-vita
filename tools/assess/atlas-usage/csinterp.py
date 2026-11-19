"""A tiny concrete interpreter for one decompiled C# method (Rewind's output).

It handles the control flow the generated dispatch methods use (if/else,
switch/case, goto labels, return/break, do-while barrier loops) and integer
locals. Everything else is an opaque statement: it is recorded, not run.
Conditions it cannot evaluate fork both ways. run() returns, for one input
binding, the set of statements that some path executes.
"""
import re


class Unknown(Exception):
    pass


def _strip(line):
    return line.strip()


def parse(lines):
    """lines: method body lines (without the outer braces). Returns a tree."""
    pos = [0]

    def peek():
        while pos[0] < len(lines) and not _strip(lines[pos[0]]):
            pos[0] += 1
        return _strip(lines[pos[0]]) if pos[0] < len(lines) else None

    def take():
        s = peek()
        pos[0] += 1
        return s

    def block():
        s = take()
        assert s == '{', s
        out = []
        while peek() != '}':
            out.append(stmt())
        take()
        return ('block', out)

    def body():
        return block() if peek() == '{' else stmt()

    def stmt():
        s = peek()
        if s == '{':
            return block()
        m = re.match(r'^if \((.*)\)$', s)
        if m:
            take()
            then = body()
            els = None
            if peek() == 'else':
                take()
                els = body()
            elif peek() is not None and peek().startswith('else if '):
                lines[pos[0]] = lines[pos[0]].replace('else if ', 'if ', 1)
                els = stmt()
            return ('if', m.group(1), then, els)
        m = re.match(r'^switch \((.*)\)$', s)
        if m:
            take()
            assert take() == '{'
            sections = []          # (labels, stmts)
            while peek() != '}':
                labs = []
                while True:
                    t = peek()
                    mc = re.match(r'^case (-?\d+)u?:$', t or '')
                    if mc:
                        labs.append(int(mc.group(1))); take(); continue
                    if t == 'default:':
                        labs.append('default'); take(); continue
                    break
                stmts = []
                while True:
                    t = peek()
                    if t == '}' or re.match(r'^case -?\d+u?:$', t) or t == 'default:':
                        break
                    stmts.append(stmt())
                sections.append((labs, stmts))
            take()
            return ('switch', m.group(1), sections)
        if s == 'do':
            take()
            b = body()
            t = take()
            assert t.startswith('while'), t
            return ('loop', b)
        m = re.match(r'^while \((.*)\)$', s)
        if m:
            take()
            return ('loop', body())
        m = re.match(r'^(IL_[0-9a-f]+):$', s)
        if m:
            take()
            return ('label', m.group(1))
        m = re.match(r'^goto (IL_[0-9a-f]+);$', s)
        if m:
            take()
            return ('goto', m.group(1))
        if s.startswith('return'):
            take()
            return ('return',)
        if s == 'break;':
            take()
            return ('break',)
        if s == 'continue;':
            take()
            return ('break',)
        # simple statement, possibly spanning lines (array initialisers)
        text = take()
        depth = text.count('{') - text.count('}') + text.count('(') - text.count(')')
        while depth > 0 or not (text.endswith(';') or text.endswith('{') or text.endswith('}')):
            if peek() is None:
                break
            t = take()
            text += ' ' + t
            depth += t.count('{') - t.count('}') + t.count('(') - t.count(')')
        return ('stmt', text)

    out = []
    while peek() is not None:
        out.append(stmt())
    return ('block', out)


def compile_tree(tree):
    code = []          # ('stmt', text) | ('cj', cond, target_if_false) | ('j', target) | ('sw', expr, {v: tgt}, default) | ('ret',)
    labels = {}
    counter = [0]

    def new_label():
        counter[0] += 1
        return '@%d' % counter[0]

    def place(lab):
        labels[lab] = len(code)

    def emit(node, brk):
        k = node[0]
        if k == 'block':
            for n in node[1]:
                emit(n, brk)
        elif k == 'stmt':
            code.append(('stmt', node[1]))
        elif k == 'if':
            l_else, l_end = new_label(), new_label()
            code.append(('cj', node[1], l_else))
            emit(node[2], brk)
            code.append(('j', l_end))
            place(l_else)
            if node[3] is not None:
                emit(node[3], brk)
            place(l_end)
        elif k == 'switch':
            l_end = new_label()
            table, default = {}, l_end
            sec_labels = []
            for labs, _ in node[2]:
                L = new_label()
                sec_labels.append(L)
                for v in labs:
                    if v == 'default':
                        default = L
                    else:
                        table[v] = L
            code.append(('sw', node[1], table, default))
            for (labs, stmts), L in zip(node[2], sec_labels):
                place(L)
                for n in stmts:
                    emit(n, l_end)
                code.append(('j', l_end))
            place(l_end)
        elif k == 'loop':
            l_end = new_label()
            emit(node[1], l_end)
            place(l_end)
        elif k == 'label':
            place(node[1])
        elif k == 'goto':
            code.append(('j', node[1]))
        elif k == 'return':
            code.append(('ret',))
        elif k == 'break':
            code.append(('j', brk) if brk else ('ret',))
        else:
            raise ValueError(k)
    emit(tree, None)
    code.append(('ret',))
    return code, labels


_ID = re.compile(r'[A-Za-z_][A-Za-z0-9_.]*')


def py_expr(e):
    e = re.sub(r'\((?:uint|int|long|ulong|double)\)', '', e)
    e = re.sub(r'(\d+)(?:u|L|uL|UL)\b', r'\1', e)
    e = e.replace('&&', ' and ').replace('||', ' or ')
    e = re.sub(r'!(?!=)', ' not ', e)
    e = e.replace('true', 'True').replace('false', 'False')
    # C# ternary  a ? b : c  -> not supported (raise)
    if '?' in e:
        raise Unknown(e)
    return e


def evaluate(e, env):
    pe = py_expr(e)
    for name in _ID.findall(pe):
        if name in ('and', 'or', 'not', 'True', 'False'):
            continue
        if name not in env:
            raise Unknown(name)
    try:
        return eval(pe, {'__builtins__': {}}, dict(env))
    except Exception:
        raise Unknown(e)


ASSIGN = re.compile(r'^(?:(?:int|long|ulong|uint|double|bool|byte) )?([A-Za-z_]\w*) = (.+);$')


def run(code, labels, env0, pinned=(), max_states=20000):
    """Explore all paths for the given initial env. Returns the set of
    statement texts executed on some path. Variables in `pinned` keep their
    initial value (assignments to them are ignored)."""
    seen = set()
    executed = []
    exec_set = set()
    work = [(0, tuple(sorted(env0.items())))]
    n = 0
    while work:
        pc, envt = work.pop()
        while True:
            key = (pc, envt)
            if key in seen:
                break
            seen.add(key)
            n += 1
            if n > max_states:
                raise RuntimeError('state explosion')
            ins = code[pc]
            env = dict(envt)
            op = ins[0]
            if op == 'ret':
                break
            if op == 'stmt':
                if ins[1] not in exec_set:
                    exec_set.add(ins[1])
                    executed.append(ins[1])
                m = ASSIGN.match(ins[1])
                if m and m.group(1) not in pinned:
                    try:
                        env[m.group(1)] = evaluate(m.group(2), env)
                    except Unknown:
                        env.pop(m.group(1), None)
                    envt = tuple(sorted(env.items()))
                pc += 1
            elif op == 'j':
                pc = labels[ins[1]]
            elif op == 'cj':
                try:
                    v = evaluate(ins[1], env)
                    pc = pc + 1 if v else labels[ins[2]]
                except Unknown:
                    work.append((labels[ins[2]], envt))
                    pc += 1
            elif op == 'sw':
                try:
                    v = evaluate(ins[1], env)
                    pc = labels[ins[2].get(v, ins[3])]
                except Unknown:
                    for t in set(ins[2].values()) | {ins[3]}:
                        work.append((labels[t], envt))
                    break
            else:
                raise ValueError(op)
    return executed


def method_lines(src, header_re):
    """Body lines of the first method whose header matches header_re
    (one tab indent, braces on their own lines)."""
    m = re.search(header_re, src)
    if not m:
        raise KeyError(header_re)
    start = src.index('\n\t{\n', m.end()) + 4
    end = src.index('\n\t}\n', start)
    return src[start:end].split('\n')
