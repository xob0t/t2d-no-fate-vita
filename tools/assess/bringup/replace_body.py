"""Replace the body of one method in a C# file (Rewind's output, one member per line header): the
first line containing <signature> starts the member, its body is the brace block after it.

    python -I tools/assess/bringup/replace_body.py <file.cs> <signature substring> <new body>
        [--note <text>] [--log <hacks.tsv>]

<new body> is the C# between the braces ('-' reads it from stdin). With --log, a row (file,
signature, note) is appended to a TSV, the probe's record of its local hacks. Fails if the signature
matches no line or more than one.
"""
import argparse
import sys
from pathlib import Path


def body_span(text, start):
    """(open, close) indexes of the first top-level brace block at or after start, skipping strings,
    chars and comments."""
    i, depth, open_at = start, 0, None
    n = len(text)
    while i < n:
        c = text[i]
        if c == '/' and text.startswith('//', i):
            i = text.index('\n', i)
            continue
        if c == '/' and text.startswith('/*', i):
            i = text.index('*/', i) + 2
            continue
        if c == '"':
            verbatim = i > 0 and text[i - 1] == '@'
            i += 1
            while i < n:
                if verbatim and text[i] == '"' and text.startswith('""', i):
                    i += 2
                    continue
                if not verbatim and text[i] == '\\':
                    i += 2
                    continue
                if text[i] == '"':
                    break
                i += 1
        elif c == "'":
            i += 1
            while text[i] != "'":
                i += 2 if text[i] == '\\' else 1
        elif c == '{':
            if depth == 0:
                open_at = i
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return open_at, i
        elif c == ';' and depth == 0:
            raise ValueError('member has no body')
        i += 1
    raise ValueError('unbalanced braces')


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('file', type=Path)
    ap.add_argument('signature')
    ap.add_argument('body')
    ap.add_argument('--note', default='')
    ap.add_argument('--log', type=Path)
    a = ap.parse_args()
    text = a.file.read_text(encoding='utf-8-sig')
    lines = text.splitlines(keepends=True)
    hits = [i for i, line in enumerate(lines) if a.signature in line]
    if len(hits) != 1:
        sys.exit(f'{a.file}: signature {a.signature!r} matches {len(hits)} lines')
    start = sum(len(line) for line in lines[:hits[0]])
    open_at, close_at = body_span(text, start)
    body = sys.stdin.read() if a.body == '-' else a.body
    indent = lines[hits[0]][:len(lines[hits[0]]) - len(lines[hits[0]].lstrip())]
    new = '{\n' + ''.join(indent + '\t' + line + '\n' for line in body.strip('\n').splitlines()) + indent + '}'
    a.file.write_text(text[:open_at] + new + text[close_at + 1:], encoding='utf-8')
    if a.log:
        with open(a.log, 'a', encoding='utf-8') as f:
            f.write(f'{a.file.as_posix()}\t{a.signature}\t{a.note}\n')
    print(f'{a.file}: replaced the body at line {hits[0] + 1}')


if __name__ == '__main__':
    main()
