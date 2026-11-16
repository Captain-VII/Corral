"""Régénère translations/fr.json et en.json sans SDK .NET (Mac, Linux).

Portage exact de StringExtractor (tests/Corral.Tests/TranslationTests.cs) : même extraction des appels
Tr("français", "anglais"), même tri ordinal, même échappement JSON. Le test TranslationTests reste la référence :
s'il échoue en CI après ce script, c'est ce portage qu'il faut corriger.

    python3 tools/update-translations.py
"""
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read_literal(code, p):
    """Texte d'un littéral "…" ou $"…" (échappements décodés, trous {…} gardés tels quels), et position après."""
    n = len(code)
    while p < n and code[p].isspace():
        p += 1
    interp = p < n and code[p] == '$'
    if interp:
        p += 1
    if p >= n or code[p] != '"':
        return None, p
    p += 1
    sb = []
    while p < n:
        c = code[p]
        p += 1
        if c == '"':
            return ''.join(sb), p
        if c == '\\' and p < n:
            e = code[p]
            p += 1
            sb.append({'n': '\n', 't': '\t', 'r': '\r', '0': '\0'}.get(e, e))
        elif interp and c == '{' and p < n and code[p] != '{':
            # Trou : jusqu'à l'accolade fermante de même niveau, en sautant les chaînes qu'il contient
            sb.append('{')
            depth = 1
            while p < n and depth > 0:
                h = code[p]
                p += 1
                if h == '"':
                    sb.append(h)
                    while p < n and code[p] != '"':
                        if code[p] == '\\':
                            sb.append(code[p])
                            p += 1
                        sb.append(code[p])
                        p += 1
                    if p < n:
                        sb.append(code[p])
                        p += 1
                    continue
                if h == '{':
                    depth += 1
                elif h == '}':
                    depth -= 1
                sb.append(h)
        elif interp and c in '{}' and p < n and code[p] == c:
            sb.append(c + c)  # {{ ou }} : accolade littérale, gardée échappée dans la clé
            p += 1
        else:
            sb.append(c)
    return None, p


def expect(code, p, ch):
    while p < len(code) and code[p].isspace():
        p += 1
    if p >= len(code) or code[p] != ch:
        return False, p
    return True, p + 1


def from_source(code):
    found = {}
    i = 0
    while (i := code.find('Tr(', i)) >= 0:
        start = i
        i += 3
        if start > 0 and (code[start - 1].isalnum() or code[start - 1] in '_.'):
            continue
        fr, p = read_literal(code, i)
        if fr is None:
            continue
        ok, p = expect(code, p, ',')
        if not ok:
            continue
        en, p = read_literal(code, p)
        if en is None:
            continue
        ok, p = expect(code, p, ')')
        if not ok:
            continue
        found.setdefault(fr, en)
        i = p
    return found


def utf16(s):
    """Clé de tri de StringComparer.Ordinal : unités UTF-16."""
    b = s.encode('utf-16-be')
    return [int.from_bytes(b[j:j + 2], 'big') for j in range(0, len(b), 2)]


def encode(s):
    """Comme JavaScriptEncoder.UnsafeRelaxedJsonEscaping."""
    r = ['"']
    for c in s:
        o = ord(c)
        if c in '"\\':
            r.append('\\' + c)
        elif c in '\n\r\t\b\f':
            r.append({'\n': '\\n', '\r': '\\r', '\t': '\\t', '\b': '\\b', '\f': '\\f'}[c])
        elif o < 0x20 or o == 0x7f or 0x80 <= o < 0xa0 or o in (0xad, 0x2028, 0x2029, 0xfeff) or 0xd800 <= o <= 0xdfff:
            r.append('\\u%04X' % o)
        elif o > 0xffff:  # hors BMP (emoji) : paire de substitution
            o -= 0x10000
            r.append('\\u%04X\\u%04X' % (0xd800 + (o >> 10), 0xdc00 + (o & 0x3ff)))
        else:
            r.append(c)
    r.append('"')
    return ''.join(r)


def extract(src):
    files = []
    for d, _, names in os.walk(src):
        if {'obj', 'bin'} & set(d.split(os.sep)):
            continue
        files += [os.path.join(d, f) for f in names if f.endswith('.cs')]
    strings = {}
    # Ordre des fichiers comme la CI Windows (séparateur \) : en cas de doublon, la première traduction rencontrée gagne
    for f in sorted(files, key=lambda f: utf16(f.replace(os.sep, '\\'))):
        with open(f, encoding='utf-8-sig') as fh:
            for k, v in from_source(fh.read()).items():
                strings.setdefault(k, v)
    return strings


def dump(pairs):
    return '{\n' + ',\n'.join(f'  {encode(k)}: {encode(v)}' for k, v in pairs) + '\n}\n'


def main():
    strings = extract(os.path.join(ROOT, 'src', 'Corral'))
    assert len(strings) > 400, f'seulement {len(strings)} textes trouvés'
    keys = sorted(strings, key=utf16)
    out = os.path.join(ROOT, 'translations')
    with open(os.path.join(out, 'en.json'), 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(dump((k, strings[k]) for k in keys))
    with open(os.path.join(out, 'fr.json'), 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(dump((k, k) for k in keys))
    print(f'{len(strings)} textes')


if __name__ == '__main__':
    sys.exit(main())
