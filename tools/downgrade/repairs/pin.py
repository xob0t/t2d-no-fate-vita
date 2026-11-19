"""Fill in the hashes a new rule leaves as null, from `Repairs apply ... --pin` output.

    Repairs.exe apply <in.dll> <out.dll> --pin > pins.txt
    python -I pin.py pins.txt tools/downgrade/repairs/rules/*.cs

In each rule, e.Method("<key>", null) gets the method's hash, and the null hash arguments of its
Site, Sites, Anchor and CompileAll calls get the sites' hashes in the order the rule asked for them
(Sites and CompileAll take one per site, space-separated). Only rules whose method has pins change;
read the diff before committing: a pin says what the IL is now, not that the repair is right.
"""
import re
import sys

CALL = re.compile(r'\.(Site|Sites|Anchor|CompileAll)\(')


def main():
    pins = {}
    for line in open(sys.argv[1], encoding='utf-8'):
        m = re.match(r'PIN (.+?\)): (.*) ([0-9a-f]{8})$', line.rstrip())
        if m:
            pins.setdefault(m.group(1), []).append((m.group(2), m.group(3)))
    for path in sys.argv[2:]:
        src = open(path, encoding='utf-8').read()
        out, changed = src, 0
        for key, items in pins.items():
            head = 'e.Method("%s", null)' % key
            at = out.find(head)
            if at < 0:
                continue
            method = [h for what, h in items if what == 'method hash']
            sites = [h for what, h in items if what != 'method hash']
            out = out[:at] + 'e.Method("%s", "%s")' % (key, method[0]) + out[at + len(head):]
            end = out.find('static void', at + 1)
            end = len(out) if end < 0 else end
            body = out[at:end]
            pos, pieces = 0, []
            for call in CALL.finditer(body):
                close = body.find(', null)', call.end())
                if close < 0:
                    continue
                args = body[call.end():close]
                n = 1
                if call.group(1) in ('Sites', 'CompileAll'):
                    n = int(args.split(',')[-1].strip())
                take, sites = sites[:n], sites[n:]
                pieces.append(body[pos:close] + ', "%s")' % ' '.join(take))
                pos = close + len(', null)')
            pieces.append(body[pos:])
            out = out[:at] + ''.join(pieces) + out[end:]
            if sites:
                sys.exit('%s: %d pins left over in %s' % (key, len(sites), path))
            changed += 1
        if out != src:
            open(path, 'w', encoding='utf-8', newline='').write(out)
            print('%s: %d rule(s) pinned' % (path, changed))


if __name__ == '__main__':
    main()
