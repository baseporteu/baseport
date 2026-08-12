"""A small, honest subset of OData v4 query options: $filter, $top, $skip, $select,
$orderby, $count. Shared by the OData routes and by the REST /parts endpoint, which
also happens to speak $filter/$top -- some real REST APIs do.

Deliberately not supported: $filter's `or` and parentheses, and cross-field
`$expand`. Baseport itself only ever sends a single `eq`/`contains` condition and
`$top`, so a full boolean-expression parser buys nothing a real caller here would
use; adding one on spec alone is exactly the kind of speculative surface ponytail
says to skip. `and`-chains of simple conditions cover everything realistic.
"""

import re
from typing import Any

_FUNCTION_RE = re.compile(r"^(contains|startswith|endswith)\(([\w/]+),\s*'([^']*)'\)$", re.IGNORECASE)
_SIMPLE_RE = re.compile(r"^([\w/]+)\s+(eq|ne|gt|lt|ge|le)\s+(.+)$", re.IGNORECASE)


def _parse_value(raw: str) -> Any:
    raw = raw.strip()
    if raw.startswith("'") and raw.endswith("'"):
        return raw[1:-1].replace("''", "'")
    if raw.lower() == "true":
        return True
    if raw.lower() == "false":
        return False
    try:
        return float(raw) if "." in raw else int(raw)
    except ValueError:
        return raw


def _condition(clause: str):
    clause = clause.strip()
    m = _FUNCTION_RE.match(clause)
    if m:
        fn, field, term = m.group(1).lower(), m.group(2), m.group(3)
        term = term.lower()
        if fn == "contains":
            return lambda row: term in str(row.get(field, "")).lower()
        if fn == "startswith":
            return lambda row: str(row.get(field, "")).lower().startswith(term)
        return lambda row: str(row.get(field, "")).lower().endswith(term)

    m = _SIMPLE_RE.match(clause)
    if m:
        field, op, raw = m.group(1), m.group(2).lower(), m.group(3)
        value = _parse_value(raw)

        def cmp(row: dict) -> bool:
            actual = row.get(field)
            if actual is None:
                return False
            try:
                a, b = (float(actual), float(value)) if isinstance(value, (int, float)) else (str(actual), str(value))
            except (TypeError, ValueError):
                a, b = str(actual), str(value)
            return {"eq": a == b, "ne": a != b, "gt": a > b, "lt": a < b, "ge": a >= b, "le": a <= b}[op]

        return cmp

    raise ValueError(f"Unsupported $filter clause: '{clause}'")


def apply_filter(records: list[dict], expr: str | None) -> list[dict]:
    if not expr:
        return records
    predicates = [_condition(c) for c in re.split(r"\s+and\s+", expr, flags=re.IGNORECASE)]
    return [r for r in records if all(p(r) for p in predicates)]


def apply_orderby(records: list[dict], expr: str | None) -> list[dict]:
    if not expr:
        return records
    terms = [t.strip() for t in expr.split(",") if t.strip()]
    # Stable sort applied least-significant term first, so the first term wins ties.
    for term in reversed(terms):
        parts = term.split()
        field = parts[0]
        descending = len(parts) > 1 and parts[1].lower() == "desc"
        records = sorted(records, key=lambda r: (r.get(field) is None, r.get(field)), reverse=descending)
    return records


def apply_select(records: list[dict], expr: str | None) -> list[dict]:
    if not expr:
        return records
    fields = [f.strip() for f in expr.split(",") if f.strip()]
    return [{f: r.get(f) for f in fields} for r in records]


def apply_top_skip(records: list[dict], top: int | None, skip: int | None) -> list[dict]:
    start = skip or 0
    end = start + top if top is not None else None
    return records[start:end]


def odata_error(code: str, message: str) -> dict:
    return {"error": {"code": code, "message": message}}


def _demo():
    rows = [
        {"name": "Alice", "amount": 30, "category": "Fasteners"},
        {"name": "Bob", "amount": 10, "category": "Seals"},
        {"name": "Charlie", "amount": 20, "category": "Fasteners"},
    ]
    assert [r["name"] for r in apply_filter(rows, "category eq 'Fasteners'")] == ["Alice", "Charlie"]
    assert [r["name"] for r in apply_filter(rows, "amount gt 15")] == ["Alice", "Charlie"]
    assert [r["name"] for r in apply_filter(rows, "contains(name, 'ali')")] == ["Alice"]
    assert [r["name"] for r in apply_filter(rows, "category eq 'Fasteners' and amount gt 25")] == ["Alice"]
    assert [r["name"] for r in apply_orderby(rows, "amount")] == ["Bob", "Charlie", "Alice"]
    assert [r["name"] for r in apply_orderby(rows, "amount desc")] == ["Alice", "Charlie", "Bob"]
    assert [r["name"] for r in apply_top_skip(rows, top=1, skip=1)] == ["Bob"]
    assert apply_select(rows, "name")[0] == {"name": "Alice"}
    assert odata_error("NotFound", "x")["error"]["code"] == "NotFound"
    print("odata.py: all checks passed")


if __name__ == "__main__":
    _demo()
