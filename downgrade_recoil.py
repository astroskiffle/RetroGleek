#!/usr/bin/env python3
"""
Regenerates RECOIL.cs for RetroGleek from a checkout of https://github.com/pfusik/recoil (MIT licence).

  1. translates recoil.fu to C# with the `fut` translator (must be on PATH, or set the FUT env var),
  2. rewrites the output to C# 5 (the language level of the C# compiler built into Windows):
       - removes `#nullable enable`, nullable annotations (`byte[]?`) and the null-forgiving `!`
       - turns expression-bodied members (`int F() => x;`) into block bodies
     Only CODE is rewritten; string literals, char literals and comments are never touched
     (the script verifies this token by token),
  3. lowers RECOIL's 128-million-pixel allocation cap to 16 million (every occurrence),
  4. appends the exact extension tables from RECOIL's formats.xml (`RetroFormats`):
       Extensions - every <ext> (including those with attributes), HTML entities unescaped
       Generic    - extensions RECOIL itself marks skip="uwp" on every occurrence (too generic to register as
                    file associations: .cs .obj .map .raw .img .a ...). RetroGleek never lists these automatically.
       Arbitrary  - extensions marked fake="true" on every occurrence (made-up, not real conventions).

Usage:   python3 downgrade_recoil.py <recoil-checkout-dir> <output RECOIL.cs>
Verify:  compile the result with a Roslyn csc using  -langversion:5  (see THIRD-PARTY-NOTICES.txt).
"""
import os, re, subprocess, sys, tempfile

OLD_CAP, NEW_CAP, EXPECTED_CAP_SITES = "134217728", "16777216", 6

def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    recoil_dir, out_path = os.path.abspath(sys.argv[1]), sys.argv[2]
    fut = os.environ.get("FUT", "fut")
    with tempfile.TemporaryDirectory() as tmp:
        raw_path = os.path.join(tmp, "RECOIL.cs")
        subprocess.check_call([fut, "-o", raw_path, "-I", recoil_dir, "-n", "Recoil", os.path.join(recoil_dir, "recoil.fu")])
        src = open(raw_path, encoding="utf-8").read()

    # --- split into code and protected tokens (strings, chars, comments) -------------------------
    protected = re.compile(r'("(?:[^"\\\n]|\\.)*"|\'(?:[^\'\\\n]|\\.)*\'|//[^\n]*|/\*.*?\*/)', re.S)
    parts = protected.split(src)                      # odd indexes are protected tokens
    for i in range(0, len(parts), 2):
        p = parts[i]
        p = p.replace("#nullable enable", "")
        p = re.sub(r'(?<=[\w\]>])\?(?=\s+\w)', '', p)         # byte[]? x  ->  byte[] x
        p = re.sub(r'(?<=[\w\]>])\?(?=[,>)\[])', '', p)       # Foo?, Foo?> ...
        p = re.sub(r'(?<=[\w\)\]])!(?=[;,)\.\s\[])', '', p)   # null-forgiving operator
        parts[i] = p
    text = "".join(parts)

    member = re.compile(r'^(\t+)((?:(?:public|internal|protected|private|static|virtual|override|sealed|abstract)\s+)*)'
                        r'([\w<>\[\]\?,\. ]+?)\s+(\w+)\((.*)\)\s*=>\s*(.+);\s*$')
    out, converted = [], 0
    for ln in text.split("\n"):
        if "=>" in ln and "NewArray" not in ln:
            m = member.match(ln)
            if m:
                ind, mods, rtype, name, args, expr = m.groups()
                body = expr + ";" if rtype.strip() == "void" else "return " + expr + ";"
                ln = "%s%s%s %s(%s) { %s }" % (ind, mods, rtype, name, args, body)
                converted += 1
        out.append(ln)
    text = "\n".join(out)

    # --- hardening: allocation caps ---------------------------------------------------------------
    sites = text.count(OLD_CAP)
    if sites != EXPECTED_CAP_SITES:
        sys.exit("expected %d occurrences of %s, found %d - RECOIL changed, review before regenerating" % (EXPECTED_CAP_SITES, OLD_CAP, sites))
    text = text.replace(OLD_CAP, NEW_CAP)

    # --- self-checks -------------------------------------------------------------------------------
    before = protected.findall(src)
    after = protected.findall(text)
    if before != after:
        sys.exit("a string literal or comment changed during the rewrite - aborting")
    leftovers = [l for l in text.split("\n") if "#nullable" in l or re.search(r'\)\s*=>\s*[^{(]', l) and "NewArray" not in l]
    if leftovers:
        sys.exit("unconverted C# 6+ syntax remains, e.g.: " + leftovers[0].strip())

    # --- exact extension list ------------------------------------------------------------------------
    import html
    xml = open(os.path.join(recoil_dir, "formats.xml"), encoding="utf-8-sig").read()
    version = re.search(r'<formats version="([^"]+)"', xml).group(1)
    seen = {}                                          # ext -> [every occurrence is skip=uwp, every occurrence is fake]
    for attrs, e in re.findall(r"<ext([^>]*)>([^<]+)</ext>", xml):
        e = html.unescape(e).strip().lower()
        skip, fake = 'skip="uwp"' in attrs, 'fake="true"' in attrs
        cur = seen.setdefault(e, [True, True])
        cur[0] = cur[0] and skip
        cur[1] = cur[1] and fake
    def table(items):
        return ",\n".join('\t\t".%s"' % e.replace('\\', '\\\\').replace('"', '\\"') for e in sorted(items))
    def hashset(name, items, doc):
        return ("\t// %s\n\tinternal static readonly System.Collections.Generic.HashSet<string> %s =\n"
                "\t\tnew System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)\n\t{\n%s\n\t};\n") % (doc, name, table(items))
    exts = sorted(seen)
    generic = [e for e in exts if seen[e][0]]
    arbitrary = [e for e in exts if seen[e][1]]
    ext_class = ("\n\n// Extension tables from RECOIL %s's formats.xml. Generated by tools/downgrade_recoil.py.\n"
                 "internal static class RetroFormats\n{\n%s\n%s\n%s}\n") % (
        version,
        hashset("Extensions", exts, "every extension RECOIL decodes"),
        hashset("Generic", generic, 'extensions RECOIL itself marks skip="uwp" (too generic to register as file associations)'),
        hashset("Arbitrary", arbitrary, 'extensions RECOIL marks fake="true" (made-up, not real conventions)'))

    try:
        commit = subprocess.check_output(["git", "-C", recoil_dir, "rev-parse", "--short", "HEAD"], stderr=subprocess.DEVNULL).decode().strip()
    except Exception:
        commit = "unknown"
    header = ("// RECOIL %s (Retro Computer Image Library), Copyright (c) 2009-2026 Piotr Fusik, MIT License (see THIRD-PARTY-NOTICES.txt).\n"
              "// This is a MODIFIED copy for RetroGleek, generated by tools/downgrade_recoil.py from recoil.fu at commit %s:\n"
              "//   - rewritten to C# 5 syntax (no nullable annotations, no expression-bodied members) so it builds with the\n"
              "//     C# compiler that ships with Windows; strings and comments are byte-identical to the translator's output\n"
              "//   - allocation cap lowered from %s to %s pixels/bytes at every site\n"
              "//   - RetroFormats tables appended (Extensions / Generic / Arbitrary, from formats.xml)\n") % (version, commit, OLD_CAP, NEW_CAP)
    open(out_path, "w", encoding="utf-8", newline="\n").write(header + text.rstrip("\n") + ext_class)
    print("wrote %s  (RECOIL %s @ %s): %d expression-bodied members converted, %d cap sites lowered, %d extensions (%d generic, %d arbitrary)" %
          (out_path, version, commit, converted, sites, len(exts), len(generic), len(arbitrary)))

main()
