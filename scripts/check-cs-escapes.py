import sys, pathlib
VALID = set("'\"\\0abfnrtvuUx")

def scan(src):
    """Report every invalid escape in a regular C# string literal.

    Interpolation holes are entered rather than skipped: an interpolated verbatim string can
    contain an ordinary nested string, and treating the whole thing as one literal is what made
    the first version of this report false positives on code that compiles.
    """
    bad, i, n, line = [], 0, len(src), 1

    def string_body(i, line, verbatim, interpolated):
        while i < n:
            c = src[i]
            if c == '\n':
                line += 1
                if not verbatim:            # an unterminated regular literal ends at the newline
                    return i + 1, line
                i += 1; continue
            if c == '"':
                if verbatim and i + 1 < n and src[i+1] == '"':
                    i += 2; continue
                return i + 1, line
            if not verbatim and c == '\\':
                nxt = src[i+1] if i + 1 < n else ''
                if nxt not in VALID:
                    bad.append((line, repr(src[max(0, i-30):i+20])))
                i += 2; continue
            if interpolated and c == '{':
                if i + 1 < n and src[i+1] == '{':
                    i += 2; continue
                i, line = hole(i + 1, line)
                continue
            i += 1
        return i, line

    def hole(i, line):
        depth = 1
        while i < n and depth:
            c = src[i]
            if c == '\n': line += 1; i += 1; continue
            if c == '{': depth += 1; i += 1; continue
            if c == '}': depth -= 1; i += 1; continue
            if c in '"$@':
                j, line = literal(i, line)
                if j != i: i = j; continue
            if c == "'":
                i += 1
                while i < n and src[i] != "'": i += 2 if src[i] == '\\' else 1
                i += 1; continue
            i += 1
        return i, line

    def literal(i, line):
        """If a string literal starts at i, consume it. Otherwise return i unchanged."""
        j, dollar, at = i, False, False
        while j < n and src[j] in '$@':
            dollar |= src[j] == '$'; at |= src[j] == '@'; j += 1
        if j >= n or src[j] != '"': return i, line
        if not at and src.startswith('"""', j):
            k = src.find('"""', j + 3)
            if k < 0: return n, line
            return k + 3, line + src.count('\n', j, k)
        return string_body(j + 1, line, at, dollar)

    while i < n:
        c = src[i]
        if c == '\n': line += 1; i += 1; continue
        if src.startswith('//', i):
            k = src.find('\n', i); i = n if k < 0 else k; continue
        if src.startswith('/*', i):
            k = src.find('*/', i + 2)
            if k < 0: break
            line += src.count('\n', i, k); i = k + 2; continue
        if c in '"$@':
            j, line = literal(i, line)
            if j != i: i = j; continue
        if c == "'":
            i += 1
            while i < n and src[i] != "'": i += 2 if src[i] == '\\' else 1
            i += 1; continue
        i += 1
    return bad

problems = 0
for path in sys.argv[1:]:
    p = pathlib.Path(path)
    if not p.exists() or p.suffix != '.cs': continue
    for line, ctx in scan(p.read_text(encoding='utf-8')):
        print(f"{p}:{line}: invalid C# escape near {ctx}")
        problems += 1
print(f"--- {problems} problem(s) ---")
sys.exit(1 if problems else 0)
