"""A small SQLite-backed "legacy ERP": vendors and parts (REST), purchase orders and
their lines (OData). Seeded once, deterministically, on first start; deleting the
DB file resets it. Plain sqlite3, no ORM: four tables and four query shapes do not
need one.
"""

import random
import sqlite3
from datetime import date, datetime, timedelta

from .config import settings

SEED = 20260615
START = date(2024, 1, 1).toordinal()
END = date(2026, 7, 31).toordinal()

VENDOR_NAMES = ["Acme", "Nova", "Delta", "Orion", "Vertex", "Atlas", "Summit", "Pioneer",
                "Meridian", "Cobalt", "Granite", "Beacon"]
VENDOR_SUFFIX = ["Industrial", "Supply Co", "Manufacturing", "Trading", "Logistics", "Group"]
COUNTRIES = ["US", "DE", "NL", "FR", "GB", "CA"]
PART_MATERIAL = ["Steel", "Brass", "Nylon", "Aluminium", "Copper", "Rubber", "Ceramic", "Titanium"]
PART_ARTICLE = ["Bolt", "Nut", "Washer", "Bushing", "Gasket", "Clamp", "Bracket", "Coupler", "Sleeve", "Spacer"]
PART_CATEGORIES = ["Fasteners", "Bearings", "Seals", "Tooling", "Electrical", "Packaging"]
PO_STATUSES = ["draft", "submitted", "approved", "received", "cancelled"]
PO_STATUS_WEIGHTS = [10, 15, 25, 45, 5]

SCHEMA = """
CREATE TABLE IF NOT EXISTS vendors (
    id TEXT PRIMARY KEY, name TEXT, country TEXT, email TEXT, active INTEGER, created_at TEXT
);
CREATE TABLE IF NOT EXISTS parts (
    id TEXT PRIMARY KEY, sku TEXT, description TEXT, category TEXT,
    unit_cost REAL, in_stock INTEGER, created_at TEXT
);
CREATE TABLE IF NOT EXISTS purchase_orders (
    id TEXT PRIMARY KEY, vendor_id TEXT, order_date TEXT, status TEXT, total REAL, created_at TEXT
);
CREATE TABLE IF NOT EXISTS purchase_order_lines (
    id TEXT PRIMARY KEY, purchase_order_id TEXT, part_id TEXT,
    quantity INTEGER, unit_cost REAL, line_total REAL, created_at TEXT
);
"""

_conn: sqlite3.Connection | None = None


def connect() -> sqlite3.Connection:
    global _conn
    if _conn is None:
        _conn = sqlite3.connect(settings.db_path, check_same_thread=False)
        _conn.row_factory = sqlite3.Row
        _conn.execute("PRAGMA journal_mode=WAL")
    return _conn


def init_and_seed() -> None:
    conn = connect()
    conn.executescript(SCHEMA)
    if conn.execute("SELECT COUNT(*) FROM vendors").fetchone()[0] == 0:
        _seed(conn)


def fetch_all(table: str) -> list[dict]:
    return [dict(r) for r in connect().execute(f'SELECT * FROM "{table}"')]


def fetch_one(table: str, record_id: str) -> dict | None:
    row = connect().execute(f'SELECT * FROM "{table}" WHERE id = ?', (record_id,)).fetchone()
    return dict(row) if row else None


def insert(table: str, row: dict) -> None:
    cols = list(row)
    placeholders = ", ".join("?" for _ in cols)
    conn = connect()
    conn.execute(f'INSERT INTO "{table}" ({", ".join(cols)}) VALUES ({placeholders})', [row[c] for c in cols])
    conn.commit()


def _stamp(day: int) -> str:
    return f"{date.fromordinal(day)}T{random.randint(6, 20):02d}:{random.randint(0, 59):02d}:{random.randint(0, 59):02d}"


def _seed(conn: sqlite3.Connection) -> None:
    random.seed(SEED)

    vendors = []
    for i in range(settings.vendor_count):
        vid = f"VEN-{i:05d}"
        name = f"{random.choice(VENDOR_NAMES)} {random.choice(VENDOR_SUFFIX)}"
        vendors.append((vid, name, random.choice(COUNTRIES),
                         f"purchasing@{name.lower().replace(' ', '')}.test",
                         1 if random.random() > 0.08 else 0, _stamp(START)))
    conn.executemany("INSERT INTO vendors VALUES (?,?,?,?,?,?)", vendors)

    parts = []
    for i in range(settings.part_count):
        pid = f"PRT-{i:05d}"
        cost = round(random.uniform(0.4, 300.0), 2)
        desc = f"{random.choice(PART_MATERIAL)} {random.choice(PART_ARTICLE)} {random.choice(['M4', 'M6', 'M8', '12mm', '18mm'])}"
        parts.append((pid, f"SKU-{i:05d}", desc, random.choice(PART_CATEGORIES),
                      cost, random.randint(0, 5000), _stamp(START)))
    conn.executemany("INSERT INTO parts VALUES (?,?,?,?,?,?,?)", parts)
    conn.commit()

    orders, lines = [], []
    for i in range(settings.purchase_order_count):
        oid = f"PO-{100000 + i}"
        vendor_id = vendors[random.randrange(len(vendors))][0]
        day = random.randint(START, END)
        status = random.choices(PO_STATUSES, PO_STATUS_WEIGHTS)[0]
        total = 0.0
        for n in range(random.randint(1, 4)):
            part = parts[random.randrange(len(parts))]
            qty = random.choice([1, 2, 5, 10, 25, 50])
            unit_cost = round(part[4] * random.uniform(0.85, 1.0), 2)
            line_total = round(qty * unit_cost, 2)
            total += line_total
            lines.append((f"{oid}-{n + 1:03d}", oid, part[0], qty, unit_cost, line_total, _stamp(day)))
        orders.append((oid, vendor_id, str(date.fromordinal(day)), status, round(total, 2), _stamp(day)))

    conn.executemany("INSERT INTO purchase_orders VALUES (?,?,?,?,?,?)", orders)
    conn.executemany("INSERT INTO purchase_order_lines VALUES (?,?,?,?,?,?,?)", lines)
    conn.commit()
