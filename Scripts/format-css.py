#!/usr/bin/env python3
"""Normalise whitespace in a CSS file without changing a single token.

Conservative formatter for the console stylesheets. It re-indents rules and
declarations, expands every rule to multi-line, and strips comments (this file
holds no comments worth keeping). Blank-line grouping, at-rules and every value
are preserved exactly as written.

Safety: the output is re-tokenised and compared against the input token by
token, ignoring comments, which are deliberately dropped. Any mismatch
(dropped, reordered or altered token) aborts the write.

Usage:
    python3 Scripts/format-css.py [FILE]            # check only, exit 1 if changes needed
    python3 Scripts/format-css.py [FILE] --write    # apply in place
"""

import argparse
import sys

BRACES_SEMI = "{};"
WHITESPACE = " \t\r\n\f"


class FormatError(Exception):
    pass


def tokenize(src):
    """Yield (kind, text, line, start, end) tokens. kind: C comment, S string, W word, P punct."""
    toks = []
    i = 0
    n = len(src)
    line = 1
    while i < n:
        c = src[i]
        if c == "\n":
            line += 1
            i += 1
            continue
        if c in WHITESPACE:
            i += 1
            continue
        if src.startswith("/*", i):
            end = src.find("*/", i + 2)
            if end == -1:
                raise FormatError("unterminated comment")
            toks.append(("C", src[i : end + 2], line, i, end + 2))
            line += src[i : end + 2].count("\n")
            i = end + 2
            continue
        if c in "\"'":
            j = i + 1
            while j < n:
                if src[j] == "\\":
                    j += 2
                    continue
                if src[j] == c:
                    break
                j += 1
            if j >= n:
                raise FormatError("unterminated string")
            toks.append(("S", src[i : j + 1], line, i, j + 1))
            line += src[i : j + 1].count("\n")
            i = j + 1
            continue
        if c in BRACES_SEMI:
            toks.append(("P", c, line, i, i + 1))
            i += 1
            continue
        j = i
        while j < n:
            ch = src[j]
            if ch in WHITESPACE or ch in BRACES_SEMI or ch in "\"'" or src.startswith("/*", j):
                break
            j += 1
        if j == i:
            raise FormatError(f"cannot tokenise at offset {i}")
        toks.append(("W", src[i:j], line, i, j))
        i = j
    return toks


def split_decl(tokens, src):
    """Split a declaration's tokens into (name, value) at the first colon."""
    for idx, (kind, text, _line, start, _end) in enumerate(tokens):
        if kind == "W" and ":" in text:
            colon = start + text.index(":")
            name = src[tokens[0][3] : colon].strip()
            value = src[colon + 1 : tokens[-1][4]].strip()
            if not name:
                raise FormatError(f"empty declaration name near line {tokens[0][2]}")
            return name, value
    raise FormatError(f"declaration without a colon near line {tokens[0][2]}")


def parse(toks, src, pos, depth):
    """Parse items until a closing brace or EOF. Returns (items, next_pos)."""
    items = []
    n = len(toks)
    while pos < n:
        kind, text, line, start, end = toks[pos]
        if kind == "P" and text == "}":
            return items, pos
        if kind == "C":
            items.append({"t": "comment", "text": text, "line": line, "last_line": line + text.count("\n")})
            pos += 1
            continue
        if kind == "P" and text == ";":
            items.append({"t": "stray", "line": line, "last_line": line})
            pos += 1
            continue
        if kind != "W":
            raise FormatError(f"unexpected {kind} token near line {line}")
        # Gather prelude until '{' or ';' or EOF.
        prelude = []
        while pos < n:
            k, t, ln, st, en = toks[pos]
            if k == "P" and t in "{};":
                break
            if k == "C":
                raise FormatError(f"comment inside a declaration near line {ln}")
            prelude.append(toks[pos])
            pos += 1
        if pos >= n:
            raise FormatError(f"unexpected end of file near line {line}")
        punct = toks[pos]
        if punct[1] == ";":
            text_ = src[prelude[0][3] : punct[4]].strip()
            items.append({"t": "stmt", "text": text_, "line": line, "last_line": punct[2]})
            pos += 1
        else:  # '{'
            children, pos = parse(toks, src, pos + 1, depth + 1)
            closing = toks[pos]
            if closing[0] != "P" or closing[1] != "}":
                raise FormatError("unbalanced braces")
            prelude_text = src[prelude[0][3] : prelude[-1][4]].strip()
            items.append({
                "t": "block",
                "prelude": prelude_text,
                "children": children,
                "line": line,
                "last_line": closing[2],
                "single_line": closing[2] == line,
            })
            pos += 1
    return items, pos


def blank_before(prev_last, item_line):
    return prev_last is not None and item_line - prev_last > 1


def emit(items, out, indent, top, prev_last):
    pad = "  " * indent
    for it in items:
        if it["t"] == "comment":
            continue
        elif it["t"] == "stray":
            out.append(pad + ";")
        elif it["t"] == "stmt":
            if top and blank_before(prev_last, it["line"]) and out and out[-1] != "":
                out.append("")
            out.append(pad + it["text"])
        elif it["t"] == "block":
            if top and blank_before(prev_last, it["line"]) and out and out[-1] != "":
                out.append("")
            if not it["children"]:
                out.append(pad + it["prelude"] + " {}")
            else:
                out.append(pad + it["prelude"] + " {")
                emit(it["children"], out, indent + 1, top=False, prev_last=None)
                out.append(pad + "}")
        prev_last = it["last_line"]
    return prev_last


def format_css(src):
    toks = tokenize(src)
    items, pos = parse(toks, src, 0, 0)
    if pos != len(toks):
        raise FormatError("trailing content after parse")
    out = []
    emit(items, out, 0, top=True, prev_last=None)
    text = "\n".join(out)
    if text:
        text += "\n"
    # Round-trip: the output must tokenise to exactly the same stream, except
    # comments, which are deliberately dropped.
    def sig(tokens):
        return [(k, v) for k, v, _l, _s, _e in tokens if k != "C"]

    if sig(tokenize(text)) != sig(toks):
        raise FormatError("formatted output no longer matches the input token stream")
    return text


def main(argv):
    ap = argparse.ArgumentParser(
        prog="format-css.py",
        description="Normalise whitespace in a CSS file without changing any token.",
    )
    ap.add_argument("file", nargs="?", default="Source/Baseport/wwwroot/app.css")
    ap.add_argument("--write", action="store_true", help="apply changes in place (default: check only)")
    args = ap.parse_args(argv)

    try:
        with open(args.file, encoding="utf-8") as fh:
            src = fh.read()
    except OSError as exc:
        print(f"error: cannot read {args.file}: {exc}", file=sys.stderr)
        return 2

    try:
        out = format_css(src)
    except FormatError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    if out == src:
        print(f"{args.file}: already formatted")
        return 0

    print(f"{args.file}: would change")
    if not args.write:
        return 1

    with open(args.file, "w", encoding="utf-8") as fh:
        fh.write(out)
    print(f"{args.file}: formatted")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
