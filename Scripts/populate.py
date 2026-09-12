"""Seeds a running Baseport instance with a production-like demo workspace: a distributor's sales side against a shared warehouse.

  Locations    warehouse bins, fixed set, not referenced elsewhere
  Products     catalogue, referenced by order lines
  Customers    accounts that place sales orders
  StockLevels  Product x Location on-hand quantity, seeded with an opening balance and decremented by the shipments below
  Orders       references Customers
  OrderLines   references Orders and Products
  Shipments    goods-out against an Order and the Location it left from
  Portway      optional proxy table over the Portway demo API

Tables, fields and forms go through the admin API so validation, ApiName rules
and the generated-column DDL all run. Rows go straight into SQLite in batches,
because a quarter million HTTP posts would outlast the rest of the seed. The
one exception is stock: `StockLevels` reflects the same ledger the generator
computes in memory while seeding opening balances and shipments, kept
consistent by construction here since nothing at runtime maintains it across
tables (the Action Engine only ever writes back to the record that triggered
it).

Deterministic: the RNG is seeded, two runs produce the same database.

Run through POPULATE.sh, which supplies the environment.
"""
import argparse
import glob
import json
import os
import random
import re
import sqlite3
import sys
import time
import urllib.error
import urllib.request
from datetime import date

BASE = os.environ.get("BASE_URL", "http://localhost:5000").rstrip("/")
USER = os.environ.get("ADMIN_USER") or os.environ.get("ADMIN_USERNAME", "")
PASSWORD = os.environ.get("ADMIN_PASSWORD", "")
NEW_PASSWORD = os.environ.get("ADMIN_NEW_PASSWORD", "baseport-dev-password")
SPEC = os.environ.get("PORTWAY_SPEC", "")
TOKEN = os.environ.get("PORTWAY_TOKEN", "")

DEFAULT_DB = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                          "Source", "Baseport", "baseport.db")
VOLUMES = {"products": 80, "customers": 4_000, "orders": 40_000, "lines": 250_000,
           "shipments": 30_000}
SEED = 20260101
BATCH = 5_000
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_-"

_cookie = ""

CUSTOMERS_DOC = """The accounts that place orders.

## Finding an account

`Email` is unique across the table and is what the customer knows, it is the
practical way to find someone. `Reference` is generated here and is safe to
print on correspondence.

## Address and contact

`Address` and `Contact` are objects with a published schema, a generated
client sees the real shape instead of a string, and every member is validated
on write: `Address.Street` is required, `Address.Geo.Lat` has to be a number
between -90 and 90, `Contact.Email` has to be an address.

`City` and `Country` stay top-level. They are what the reports group by, and
only a top-level field is indexed.

`PATCH` merges an object member by member, sending
`{"Contact": {"Phone": "+31 6 1234 5678"}}` changes the phone number and leaves
the name, the address and the role alone.

## What you can do

Read and create accounts, and patch the ones that exist. Accounts are never
deleted through the API, `DELETE` is switched off for this endpoint.
"""

ORDERS_DOC = """Order headers taken through the portal.

## Identifiers

Every order has an `OrderNo` that is unique across the table and safe to
show a customer. The `id` in a response is the record's own identifier: it is
unguessable and stable, and it is what the single-record routes take.

## Totals

`Total` is the sum of the order's lines at the moment it was taken, and
`Amounts` includes the same figure split into net, VAT and gross. The lines
themselves live in `order-lines` and reference this order.

## Ship-to

`ShipTo` is a copy of the account's address as it stood when the order was
taken, not a lookup. An account that moves next year must not rewrite where
last year's goods went. `PATCH` merges it member by member, sending only
`{"ShipTo": {"City": "Breda"}}` leaves the street alone.

## What you can do

Read and create orders, and patch the ones that exist. Orders are closed by
setting `Status`, never deleted, `DELETE` is switched off for this endpoint.
"""

LINES_DOC = """The individual lines of an order.

Each line references the order it belongs to and the product it sells.
`LineTotal` is calculated from `Quantity` and `UnitPrice` on write; sending it
has no effect.
"""

SHIPMENTS_DOC = """Packing slips: what shipped against an order, and when.

Each shipment references the order it fulfils and the warehouse location it
left from. An order can have more than one shipment if it went out in
separate packages.
"""

LOCATIONS_DOC = """The warehouse's own bins: where a shipment leaves from.

A fixed set - a demo warehouse has the same handful of docks and aisles
regardless of how much stock moves through them. `Zone` is what a picker
reads off a bin label; `Code` is what the WMS scans.
"""

STOCK_LEVELS_DOC = """On-hand quantity, by product and by warehouse location.

The ledger the shipments below draw down from an opening balance. A product
with stock in three locations has three rows here, not one row with three
numbers - the same reason `OrderLines` is its own table instead of an array
on `Orders`: a location's stock needs to be queried and ruled on its own.
"""

PRODUCTS_DOC = """The catalogue order lines point at.

`Sku` is unique and is the practical way to find an article. Inactive products
stay readable so historic order lines keep resolving.

`Slug` auto-generates from `Name`. `Attributes` is a category-specific spec
sheet (thread size, bore diameter, ...) that would otherwise need a column
per possible attribute across every category, it is deliberately free-form.

`Packaging` is the opposite case: every article has a box quantity, a weight
and outer dimensions, it declares a schema and this document publishes it,
down to `Packaging.Dimensions`.
"""

FIRST = ["Anna", "Bram", "Chloe", "Daan", "Eva", "Femke", "Gijs", "Hanna", "Ivo", "Julia",
         "Koen", "Lotte", "Mees", "Noor", "Olaf", "Pien", "Quinn", "Ruben", "Sanne", "Tim",
         "Ursula", "Vera", "Wouter", "Yara"]
LAST = ["Bakker", "de Vries", "Jansen", "Visser", "Smit", "Meijer", "Mulder", "Bos",
         "Vos", "Peters", "Hendriks", "Dekker", "Brouwer", "Kok", "Willems", "Maes",
         "Claes", "Schmidt", "Weber", "Fischer", "Dubois", "Moreau", "Leroy", "Martin"]
SUFFIX = ["BV", "NV", "GmbH", "SARL", "Holding", "Group", "Trading", "Logistics"]
CITIES = {
    "Netherlands": ["Amsterdam", "Rotterdam", "Utrecht", "Eindhoven", "Groningen", "Breda", "Tilburg"],
    "Belgium": ["Brussels", "Antwerp", "Ghent", "Bruges", "Leuven"],
    "Germany": ["Berlin", "Hamburg", "Munich", "Cologne", "Frankfurt", "Stuttgart"],
    "France": ["Paris", "Lyon", "Marseille", "Lille", "Toulouse"],
}
COUNTRIES = list(CITIES)
CATEGORIES = ["Fasteners", "Bearings", "Seals", "Tooling", "Electrical", "Packaging", "Safety"]
MATERIAL = ["Steel", "Brass", "Nylon", "Alu", "Copper", "Rubber", "Ceramic", "Titanium"]
ARTICLE = ["Bolt", "Nut", "Washer", "Bushing", "Gasket", "Clamp", "Bracket", "Coupler",
           "Sleeve", "Spacer", "Pin", "Ring"]
# category-specific attrs, one JSON field instead of a column per category
CATEGORY_ATTRS = {
    "Fasteners": lambda: {"thread": random.choice(["M4", "M6", "M8", "M10", "M12"]),
                           "head": random.choice(["hex", "socket", "flange", "countersunk"]),
                           "length_mm": random.choice([10, 16, 20, 30, 40, 60])},
    "Bearings": lambda: {"bore_mm": random.choice([8, 10, 12, 15, 20, 25]),
                          "od_mm": random.choice([22, 26, 32, 42, 47]),
                          "type": random.choice(["ball", "roller", "needle"])},
    "Seals": lambda: {"id_mm": random.choice([6, 10, 15, 20]), "od_mm": random.choice([16, 22, 28, 35]),
                       "profile": random.choice(["O-ring", "lip", "quad"])},
    "Tooling": lambda: {"drive": random.choice(["1/4in", "3/8in", "1/2in"]),
                         "torque_nm": random.choice([20, 40, 80, 150])},
    "Electrical": lambda: {"voltage": random.choice(["24V", "230V", "400V"]),
                            "amperage_a": random.choice([6, 10, 16, 32]), "ip_rating": random.choice(["IP54", "IP65", "IP67"])},
    "Packaging": lambda: {"units_per_box": random.choice([50, 100, 250, 500]),
                           "recyclable": random.random() > 0.3},
    "Safety": lambda: {"standard": random.choice(["EN388", "EN166", "EN20345"]),
                        "size": random.choice(["S", "M", "L", "XL"])},
}
PRODUCT_TAGS = ["bestseller", "new", "clearance", "eco", "heavy-duty", "premium"]
CONTACT_ROLES = ["Purchasing", "Warehouse", "Finance", "Owner"]
STREETS = {
    "Netherlands": ["Dorpsstraat", "Industrieweg", "Havenkade", "Stationsplein", "Molenweg"],
    "Belgium": ["Nijverheidslaan", "Handelskade", "Vaartstraat", "Bergstraat", "Kerkstraat"],
    "Germany": ["Industriestrasse", "Bahnhofstrasse", "Hafenweg", "Lagerstrasse", "Gewerbering"],
    "France": ["Rue de l'Industrie", "Avenue du Port", "Rue des Entrepots", "Boulevard Gambetta"],
}
# rough city centres, enough for a map pin in a demo
CITY_GEO = {"Amsterdam": (52.37, 4.90), "Rotterdam": (51.92, 4.48), "Utrecht": (52.09, 5.12),
            "Eindhoven": (51.44, 5.48), "Groningen": (53.22, 6.57), "Breda": (51.59, 4.78),
            "Tilburg": (51.56, 5.09), "Brussels": (50.85, 4.35), "Antwerp": (51.22, 4.40),
            "Ghent": (51.05, 3.72), "Bruges": (51.21, 3.22), "Leuven": (50.88, 4.70),
            "Berlin": (52.52, 13.40), "Hamburg": (53.55, 9.99), "Munich": (48.14, 11.58),
            "Cologne": (50.94, 6.96), "Frankfurt": (50.11, 8.68), "Stuttgart": (48.78, 9.18),
            "Paris": (48.86, 2.35), "Lyon": (45.76, 4.84), "Marseille": (43.30, 5.37),
            "Lille": (50.63, 3.06), "Toulouse": (43.60, 1.44)}
COUNTRY_DIAL = {"Netherlands": "31", "Belgium": "32", "Germany": "49", "France": "33"}
# 1234 AB in the Netherlands, four digits in Belgium, five everywhere else
POSTCODE = {
    "Netherlands": lambda: f"{random.randint(1000, 9999)} {random.choice('ABCDEGHJKLMNPRSTVWXZ')}{random.choice('ABCDEGHJKLMNPRSTVWXZ')}",
    "Belgium": lambda: str(random.randint(1000, 9999)),
    "Germany": lambda: f"{random.randint(10000, 99999)}",
    "France": lambda: f"{random.randint(10000, 95999)}",
}
# what the tax office would charge on a shipment to each country
VAT_RATE = {"Netherlands": 0.21, "Belgium": 0.21, "Germany": 0.19, "France": 0.20}
STATUSES = ["open", "picking", "shipped", "closed", "cancelled"]
STATUS_WEIGHTS = [12, 8, 20, 55, 5]
CHANNELS = ["web", "phone", "edi", "counter"]
CHANNEL_WEIGHTS = [55, 15, 25, 5]
START = date(2024, 1, 1).toordinal()
END = date(2026, 7, 31).toordinal()

# fixed bin list, same regardless of data volume; storage_bins/shipping_bin below pick from it by zone
LOCATIONS = [
    ("RCV-01", "Receiving", "Dock 1"), ("RCV-02", "Receiving", "Dock 2"),
    ("BLK-A1", "Bulk storage", "Aisle A"), ("BLK-A2", "Bulk storage", "Aisle A"),
    ("BLK-B1", "Bulk storage", "Aisle B"), ("BLK-B2", "Bulk storage", "Aisle B"),
    ("PCK-A1", "Pick face", "Aisle A"), ("PCK-A2", "Pick face", "Aisle A"),
    ("PCK-B1", "Pick face", "Aisle B"), ("PCK-B2", "Pick face", "Aisle B"),
    ("PAK-01", "Pack", "Pack bench 1"), ("SHP-01", "Shipping", "Dock 3"),
]
ZONES = ["Receiving", "Bulk storage", "Pick face", "Pack", "Shipping"]


def call(method, path, body=None, expect_json=True):
    global _cookie
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if _cookie:
        request.add_header("Cookie", _cookie)
    try:
        with urllib.request.urlopen(request) as response:
            raw = response.read().decode()
            headers = response.headers
            # any response may re-issue the session: changing the password ends every session
            for value in headers.get_all("Set-Cookie") or []:
                if value.startswith("baseport_auth="):
                    _cookie = value.split(";")[0]
            return (json.loads(raw) if expect_json and raw else raw), headers
    except urllib.error.HTTPError as error:
        raw = error.read().decode()
        try:
            detail = json.loads(raw).get("errors", [raw])
        except json.JSONDecodeError:
            detail = [raw[:200]]
        raise SystemExit(f"  {method} {path} failed ({error.code}): {'; '.join(detail)}")
    except urllib.error.URLError as error:
        raise SystemExit(f"Could not reach {BASE}: {error.reason}. Is Baseport running?")


def _auto_detect_from_log(db_path):
    """Parse the most recent log file for the one-time admin username and password."""
    global USER, PASSWORD
    log_dir = os.path.join(os.path.dirname(db_path), "log")
    files = sorted(glob.glob(os.path.join(log_dir, "baseport-*.log")), reverse=True)
    for f in files:
        try:
            with open(f) as fh:
                text = fh.read()
        except OSError:
            continue
        matches = list(re.finditer(
            r"Seeded a one-time admin account\.\s+Username:\s+(\S+)\s+Password:\s+(\S+?)(?=\.)",
            text))
        if matches:
            USER, PASSWORD = matches[-1].group(1), matches[-1].group(2)
            print(f"  Credentials from log: {USER}")
            return
    raise SystemExit(
        "No one-time admin account found in the log files.\n"
        "Set ADMIN_USER and ADMIN_PASSWORD manually, or check log/baseport-*.log.")


def sign_in():
    global _cookie
    if not USER or not PASSWORD:
        raise SystemExit(
            "Set ADMIN_USER and ADMIN_PASSWORD, or ensure the log directory is accessible.\n"
            "A fresh instance logs a one-time admin account on first start;\n"
            "check log/baseport-*.log for 'Seeded a one-time admin account'.")

    call("POST", "/api/auth/login", {"username": USER, "password": PASSWORD})
    if not _cookie:
        raise SystemExit("Signed in but no session cookie came back.")

    # a session on the one-time password reaches the sign-in surface and nothing else
    if call("GET", "/api/auth/me")[0].get("mustChangePassword"):
        call("POST", "/api/auth/password",
             {"currentPassword": PASSWORD, "newPassword": NEW_PASSWORD})
        print(f"Admin password was one-time; set to {NEW_PASSWORD!r}")


def table(name, description, fields):
    """Returns (publicId, created). An existing table keeps its forms and records."""
    existing = next((t for t in call("GET", "/api/_admin/tables")[0] if t["name"] == name), None)
    if existing:
        print(f"  {name}: already present, skipping")
        return existing["id"], False

    pid = call("POST", "/api/_admin/tables", {"name": name, "description": description})[0]["id"]
    for field in fields:
        call("POST", f"/api/_admin/tables/{pid}/fields", field)
    print(f"  {name}: {len(fields)} fields")
    return pid, True


def publish(pid, api_name, display, namespace, doc, methods=("GET", "POST", "PATCH")):
    call("PATCH", f"/api/_admin/tables/{pid}", {
        "apiName": api_name, "apiEnabled": True, "apiDisplayName": display,
        "apiNamespace": namespace, "apiDocumentation": doc, "apiMethods": list(methods),
    })


def form(table_pid, **spec):
    spec["tableId"] = table_pid
    for key in ("configJson", "layoutJson"):
        if isinstance(spec.get(key), (dict, list)):
            spec[key] = json.dumps(spec[key])
    return call("POST", "/api/_admin/forms", spec)[0]["id"]


def layout(*groups):
    return {"rows": [{"t": "row", "cols": [{"t": "col", "w": 12, "items": list(g)}]} for g in groups]}


def short_id(length=12):
    return "".join(random.choices(ALPHABET, k=length))


def slugify(text):
    """Mirrors FieldValidation.Slugify: bulk inserts bypass RecordEngine, nothing else derives this."""
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def gs1_check_digit(digits):
    """Standard GS1 mod-10 check digit, shared by EAN/GTIN/GLN - all the same 13-digit numbering."""
    total = sum(int(d) * (3 if i % 2 == 0 else 1) for i, d in enumerate(reversed(digits)))
    return str((10 - total % 10) % 10)


def gs1_code(prefix, seq):
    """A 13-digit, correctly-checksummed demo code. `prefix` sits in GS1's restricted-circulation range
    (never assigned to a real company), so nothing generated here can collide with an actual GTIN or GLN."""
    base = f"{prefix}{seq:010d}"[:12]
    return base + gs1_check_digit(base)


class Bulk:
    """Rows straight into SQLite. _records is five stable columns; the generated
    index columns are virtual, SQLite derives them from JsonData on insert.
    UpdatedAt is written explicitly: this path bypasses EF, RecordChangeInterceptor
    never stamps it, and the column default is year 1."""

    SQL = 'INSERT INTO "_records" ("Id","TableId","JsonData","CreatedAt","UpdatedAt") VALUES (?,?,?,?,?)'

    def __init__(self, path):
        if not os.path.exists(path):
            raise SystemExit(f"No database at {path}. Point --db at the instance's baseport.db.")
        self.conn = sqlite3.connect(path, isolation_level=None)
        self.conn.execute("PRAGMA journal_mode=WAL")
        self.conn.execute("PRAGMA synchronous=NORMAL")
        self.conn.execute("PRAGMA busy_timeout=10000")
        self.batch = []
        self.written = 0

        self.conn.execute("BEGIN")
        self.indexes = self.conn.execute(
            "SELECT name, sql FROM sqlite_master"
            " WHERE type='index' AND tbl_name='_records' AND sql IS NOT NULL").fetchall()
        for name, _ in self.indexes:
            self.conn.execute(f'DROP INDEX "{name}"')

    def add(self, table_id, data, created, record_id=None):
        """record_id is for a row whose id something else already handed out, an order header its
        lines already point at. The tuple shape lives here only, a column added to _records
        is one edit instead of one per caller."""
        rid = record_id or short_id()
        self.batch.append((rid, table_id, json.dumps(data, separators=(",", ":")), created, created))
        if len(self.batch) >= BATCH:
            self.flush()
        return rid

    def flush(self):
        # commit per batch: one long-lived BEGIN would block other writers past busy_timeout
        if self.batch:
            self.conn.executemany(self.SQL, self.batch)
            self.written += len(self.batch)
            self.batch.clear()
            self.conn.execute("COMMIT")
            self.conn.execute("BEGIN")

    def close(self):
        self.flush()
        print(f"  Records: {self.written:,} rows written, rebuilding {len(self.indexes)} indexes", flush=True)
        for _, sql in self.indexes:
            self.conn.execute(sql)
        self.conn.execute("COMMIT")
        self.conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        self.conn.close()


def stamp(day):
    return f"{date.fromordinal(day)} {random.randint(6, 20):02d}:{random.randint(0, 59):02d}:{random.randint(0, 59):02d}"


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--db", default=DEFAULT_DB, help="SQLite file the instance writes to")
    parser.add_argument("--scale", type=float, default=1.0,
                        help="fraction of the full volume (1.0 = 250k order lines)")
    args = parser.parse_args()

    counts = {k: max(1, round(v * args.scale)) for k, v in VOLUMES.items()}
    counts["lines"] = max(counts["lines"], counts["orders"])

    print(f"Seeding {BASE} (db {args.db}, scale {args.scale})")
    if not USER or not PASSWORD:
        _auto_detect_from_log(args.db)
    sign_in()

    locations, fresh_loc = table("Locations", "The warehouse's own bins", location_fields())
    publish(locations, "locations", "Locations", "Warehousing", LOCATIONS_DOC, methods=("GET",))

    products, fresh_p = table("Products", "Catalogue the order lines sell from", product_fields())
    publish(products, "products", "Products", "Catalogue", PRODUCTS_DOC)

    customers, fresh_c = table("Customers", "Accounts that place orders", customer_fields())
    publish(customers, "customers", "Customers", "Accounts", CUSTOMERS_DOC)

    stock_levels, fresh_sl = table("StockLevels", "On-hand quantity by product and location",
                                    stock_level_fields(products, locations))
    publish(stock_levels, "stock-levels", "Stock levels", "Warehousing", STOCK_LEVELS_DOC, methods=("GET",))

    orders, fresh_o = table("Orders", "Order headers taken through the portal", order_fields(customers))
    publish(orders, "sales-orders", "Sales orders", "Sales", ORDERS_DOC)

    lines, fresh_l = table("OrderLines", "The lines of an order", line_fields(orders, products))
    publish(lines, "order-lines", "Order lines", "Sales", LINES_DOC)

    shipments, fresh_s = table("Shipments", "Packing slips against an order", shipment_fields(orders, locations))
    publish(shipments, "shipments", "Shipments", "Warehousing", SHIPMENTS_DOC)

    if fresh_loc:
        location_forms(locations)
    if fresh_p:
        product_forms(products)
    if fresh_c:
        customer_forms(customers)
    if fresh_sl:
        stock_level_forms(stock_levels)
    track_id = order_forms(orders) if fresh_o else None
    if fresh_l:
        line_forms(lines, track_id)
    if fresh_s:
        shipment_forms(shipments)

    if fresh_loc and fresh_p and fresh_c and fresh_sl and fresh_o and fresh_l and fresh_s:
        fill(args.db, counts, locations, products, customers, stock_levels, orders, lines, shipments)
    else:
        print("  Records: skipped, some tables already existed")

    seed_queries()
    seed_actions(orders)
    seed_portway()


def product_fields():
    return [
        {"name": "Sku", "label": "SKU", "dataType": "text", "isRequired": True,
         "isUnique": True, "isIdentifier": True, "helpText": "Printed on the packing slip."},
        {"name": "Gtin", "label": "GTIN", "dataType": "text", "isUnique": True,
         "pattern": r"^\d{13}$", "helpText": "13-digit GS1 barcode (EAN-13/GTIN-13), the identifier that travels outside this system - a PO, a carrier label, a retailer's own catalogue."},
        {"name": "Name", "label": "Description", "dataType": "text", "isRequired": True},
        {"name": "Slug", "dataType": "slug", "optionsJson": json.dumps({"sourceField": "Name"}),
         "helpText": "Auto-generated from the name; used in the storefront product URL."},
        {"name": "Category", "dataType": "select", "optionsJson": json.dumps(CATEGORIES),
         "defaultValue": CATEGORIES[0]},
        {"name": "UnitPrice", "label": "List price", "dataType": "currency", "min": 0},
        {"name": "Active", "dataType": "boolean", "defaultValue": "true"},
        {"name": "Body", "label": "Description (long)", "dataType": "richtext",
         "helpText": "Storefront copy. Sanitized on save."},
        # free-form on purpose: attributes differ per category, no one schema to declare
        {"name": "Attributes", "dataType": "json",
         "helpText": "Category-specific spec sheet, e.g. thread size or bore diameter."},
        # same shape for every article, so this one gets a schema and the API publishes it
        {"name": "Packaging", "dataType": "json", "optionsJson": json.dumps({"fields": [
            {"name": "UnitsPerBox", "label": "Units per box", "dataType": "number", "min": 1, "isRequired": True},
            {"name": "WeightKg", "label": "Weight (kg)", "dataType": "number", "min": 0},
            {"name": "Dimensions", "dataType": "json", "optionsJson": json.dumps({"fields": [
                {"name": "LengthMm", "dataType": "number", "min": 0},
                {"name": "WidthMm", "dataType": "number", "min": 0},
                {"name": "HeightMm", "dataType": "number", "min": 0},
            ]})},
        ]}), "helpText": "Box quantity, weight and outer dimensions."},
        {"name": "Tags", "dataType": "array", "helpText": "Merchandising tags."},
        {"name": "Datasheet", "dataType": "url", "helpText": "Link to the PDF spec sheet."},
    ]


def location_fields():
    return [
        {"name": "Code", "label": "Bin code", "dataType": "text",
         "isRequired": True, "isUnique": True, "isIdentifier": True,
         "helpText": "What the WMS scans."},
        {"name": "Zone", "dataType": "select", "optionsJson": json.dumps(ZONES),
         "defaultValue": ZONES[0]},
        {"name": "Description", "dataType": "text", "helpText": "What a picker reads off the bin label."},
    ]


def customer_fields():
    return [
        # dataType email replaces the hand-rolled pattern this field used before the type existed
        {"name": "Email", "label": "Email address", "dataType": "email",
         "isRequired": True, "isUnique": True, "isIdentifier": True,
         "helpText": "We use this to find your account."},
        {"name": "Name", "label": "Account name", "dataType": "text", "isRequired": True},
        # City/Country stay top-level: reports group by them, and only a top-level field is indexed
        {"name": "City", "dataType": "text"},
        {"name": "Country", "dataType": "select", "optionsJson": json.dumps(COUNTRIES),
         "defaultValue": "Netherlands"},
        {"name": "Address", "label": "Postal address", "dataType": "json", "optionsJson": json.dumps({"fields": [
            {"name": "Street", "dataType": "text", "isRequired": True},
            {"name": "PostalCode", "label": "Postal code", "dataType": "text", "max": 12},
            {"name": "Geo", "label": "Coordinates", "dataType": "json", "optionsJson": json.dumps({"fields": [
                {"name": "Lat", "dataType": "number", "min": -90, "max": 90},
                {"name": "Lon", "dataType": "number", "min": -180, "max": 180},
            ]})},
        ]}), "helpText": "Where the goods go."},
        {"name": "Contact", "label": "Primary contact", "dataType": "json", "optionsJson": json.dumps({"fields": [
            {"name": "Name", "dataType": "text", "isRequired": True},
            {"name": "Email", "dataType": "email"},
            {"name": "Phone", "dataType": "text", "pattern": r"^\+?[0-9 ()-]{6,20}$"},
            {"name": "Role", "dataType": "select", "optionsJson": json.dumps(CONTACT_ROLES)},
        ]}), "helpText": "Who to call about an order."},
        {"name": "SignedUp", "label": "Signed up", "dataType": "date"},
        {"name": "Gln", "label": "GLN", "dataType": "text", "isUnique": True,
         "pattern": r"^\d{13}$", "helpText": "13-digit GS1 Global Location Number identifying this account's ship-to party in EDI traffic."},
        {"name": "Reference", "dataType": "systemid"},
    ]


def order_fields(customers):
    return [
        {"name": "OrderNo", "label": "Order number", "dataType": "text",
         "isRequired": True, "isUnique": True, "isIdentifier": True,
         "helpText": "Printed on your confirmation email."},
        {"name": "Customer", "dataType": "reference",
         "optionsJson": json.dumps({"tableId": customers}), "isRequired": True},
        {"name": "OrderDate", "label": "Order date", "dataType": "date"},
        {"name": "Channel", "dataType": "select", "optionsJson": json.dumps(CHANNELS),
         "defaultValue": "web"},
        {"name": "Status", "dataType": "select", "optionsJson": json.dumps(STATUSES),
         "defaultValue": "open"},
        {"name": "Total", "label": "Order total", "dataType": "currency", "min": 0},
        # copied off the account when the order is taken, not a live lookup: an address that
        # changes next year must not rewrite what shipped last year
        {"name": "ShipTo", "label": "Ship to", "dataType": "json", "optionsJson": json.dumps({"fields": [
            {"name": "Street", "dataType": "text", "isRequired": True},
            {"name": "PostalCode", "label": "Postal code", "dataType": "text", "max": 12},
            {"name": "City", "dataType": "text"},
            {"name": "Country", "dataType": "select", "optionsJson": json.dumps(COUNTRIES)},
        ]})},
        {"name": "Amounts", "dataType": "json", "optionsJson": json.dumps({"fields": [
            {"name": "Net", "dataType": "currency", "min": 0},
            {"name": "Vat", "label": "VAT", "dataType": "currency", "min": 0},
            {"name": "Gross", "dataType": "currency", "min": 0},
        ]}), "helpText": "Net, VAT and gross at the moment the order was taken."},
        {"name": "Notes", "label": "Internal notes", "dataType": "longtext", "isHidden": True},
        # stamped by the seeded "Orders - stamp last update" action on every write, not by hand
        {"name": "LastTouched", "label": "Last touched", "dataType": "date", "isHidden": True},
    ]


def line_fields(orders, products):
    return [
        {"name": "LineNo", "label": "Line number", "dataType": "text",
         "isRequired": True, "isUnique": True, "isIdentifier": True},
        {"name": "Order", "dataType": "reference",
         "optionsJson": json.dumps({"tableId": orders}), "isRequired": True},
        {"name": "Product", "dataType": "reference",
         "optionsJson": json.dumps({"tableId": products}), "isRequired": True},
        {"name": "Quantity", "dataType": "number", "min": 1, "max": 9999, "defaultValue": "1"},
        {"name": "UnitPrice", "label": "Unit price", "dataType": "currency", "min": 0},
        {"name": "LineTotal", "label": "Line total", "dataType": "calculated",
         "expression": "data.Quantity * data.UnitPrice"},
    ]


def stock_level_fields(products, locations):
    return [
        {"name": "Product", "dataType": "reference",
         "optionsJson": json.dumps({"tableId": products}), "isRequired": True},
        {"name": "Location", "dataType": "reference",
         "optionsJson": json.dumps({"tableId": locations}), "isRequired": True},
        {"name": "Qty", "label": "On hand", "dataType": "number", "min": 0, "defaultValue": "0"},
    ]


def shipment_fields(orders, locations):
    return [
        {"name": "PackingSlipNo", "label": "Packing slip number", "dataType": "text",
         "isRequired": True, "isUnique": True, "isIdentifier": True},
        {"name": "Order", "dataType": "reference",
         "optionsJson": json.dumps({"tableId": orders}), "isRequired": True},
        {"name": "Location", "label": "Shipped from", "dataType": "reference",
         "optionsJson": json.dumps({"tableId": locations}), "isRequired": True},
        {"name": "ShipDate", "label": "Ship date", "dataType": "date"},
        {"name": "Tracking", "label": "Track & trace", "dataType": "text"},
        {"name": "Notes", "dataType": "text"},
    ]


def product_forms(products):
    # Slug derives server-side; Attributes/Tags are PIM data filled via the admin grid or import, not typed here
    form(products, kind="form", actions=["submit"], title="Products - Create new",
         description="A new article for the catalogue.",
         layoutJson=layout(["Sku", "Gtin", "Name"], ["Category", "UnitPrice", "Active"],
                            ["Body"], ["Datasheet"]))
    form(products, kind="form", actions=["lookup"], title="Products - Look up", isReadOnly=True,
         description="Enter a SKU or scan a barcode.",
         configJson={"matchFields": ["Sku", "Gtin"], "resultFields": ["Sku", "Gtin", "Name", "Category", "UnitPrice", "Active"],
                     "notFoundText": "No product with that SKU or barcode."})
    form(products, kind="list", title="Products - Catalogue",
         description="Every article, by description.",
         configJson={"columns": ["Sku", "Gtin", "Name", "Category", "UnitPrice", "Active"],
                     "searchFields": ["Sku", "Gtin", "Name"],
                     "sortField": "Name", "sortDir": "asc", "pageSize": 25})


def location_forms(locations):
    form(locations, kind="list", title="Locations - Overview",
         description="Every bin in the warehouse.",
         configJson={"columns": ["Code", "Zone", "Description"],
                     "searchFields": ["Code"],
                     "sortField": "Code", "sortDir": "asc", "pageSize": 25})


def customer_forms(customers):
    form(customers, kind="form", actions=["submit"], title="Customers - Create new",
         description="Tell us who you are and we will set up an account.",
         layoutJson=layout(["Name", "Email"], ["City", "Country", "SignedUp"]))
    form(customers, kind="form", actions=["lookup"], title="Customers - Search", isReadOnly=True,
         description="Enter the email address you signed up with.",
         configJson={"matchFields": ["Email"], "resultFields": ["Name", "City", "Country", "Reference"],
                     "notFoundText": "No account matches that email address."})
    form(customers, kind="list", title="Customers - Overview",
         description="Every account. Search by name or email address.",
         configJson={"columns": ["Name", "Email", "City", "Country"],
                     "searchFields": ["Name", "Email"],
                     "sortField": "Name", "sortDir": "asc", "pageSize": 25})


def order_forms(orders):
    form(orders, kind="form", actions=["submit"], title="Orders - Create new",
         description="The header. Lines are added against the order number.",
         layoutJson={"rows": [
             {"t": "row", "cols": [{"t": "col", "w": 12, "items": ["OrderNo", "Customer"]}]},
             {"t": "row", "cols": [{"t": "col", "w": 12, "items": ["OrderDate", "Channel", "Status"]}]},
             {"t": "button", "label": "Place order", "action": "submit"},
         ]})
    # returned so line_forms() can wire OrderLines - Overview's followLookup to it (the /order page embeds both)
    track_id = form(orders, kind="form", actions=["lookup"], title="Orders - Look up", isReadOnly=True,
                     description="Enter your order number.",
                     configJson={"matchFields": ["OrderNo"], "resultFields": ["OrderNo", "OrderDate", "Status", "Total"],
                                 "notFoundText": "We could not find that order."})
    form(orders, kind="list", title="Orders - Overview status open",
         description="Everything still open, newest first.",
         configJson={"columns": ["OrderNo", "OrderDate", "Channel", "Total", "Status"],
                     "searchFields": ["OrderNo"],
                     "filters": [{"field": "Status", "op": "eq", "value": "open"}],
                     # a renderer turns a bare value into markup; row data is escaped first
                     "renderers": {"Status": "'<strong>' + data.Status.toUpperCase() + '</strong>'"},
                     # targets the /order page on customers.site.com, which embeds Orders - Look up and answers ?q=
                     "actions": [{"label": "View order",
                                  "hrefExpr": "'http://127.0.0.1:8081/order?q=' + encodeURIComponent(data.OrderNo)"}],
                     "sortField": "OrderDate", "sortDir": "desc", "pageSize": 25})

    form(orders, kind="list", title="Orders - Worklist",
         description="Everything still open, for picking and packing.",
         configJson={"columns": ["OrderNo", "OrderDate", "Channel", "Total", "Status"],
                     "searchFields": ["OrderNo"],
                     "filters": [{"field": "Status", "op": "eq", "value": "open"}],
                     "renderers": {"Status": "'<strong>' + data.Status.toUpperCase() + '</strong>'"},
                     # targets /order-lines on wms.site.com; LineNo is "{OrderNo}-{n}" so an OrderNo search already narrows to it
                     "actions": [{"label": "View lines",
                                  "hrefExpr": "'http://127.0.0.1:8082/order-lines?q=' + encodeURIComponent(data.OrderNo)"}],
                     "sortField": "OrderDate", "sortDir": "desc", "pageSize": 25})

    return track_id


def line_forms(lines, track_id=None):
    form(lines, kind="form", actions=["submit"], title="OrderLines - Create new",
         description="Everything except the line total, which we calculate.",
         layoutJson={"rows": [
             {"t": "row", "cols": [{"t": "col", "w": 12, "items": ["LineNo", "Order", "Product"]}]},
             {"t": "row", "cols": [{"t": "col", "w": 12, "items": ["Quantity", "UnitPrice"]}]},
             {"t": "subtotal", "label": "Line total", "expr": "data.Quantity * data.UnitPrice",
              "format": "currency"},
             {"t": "button", "label": "Add line", "action": "submit"},
         ]})
    form(lines, kind="form", actions=["lookup"], title="OrderLines - Search", isReadOnly=True,
         description="Enter the line number from your confirmation.",
         configJson={"matchFields": ["LineNo"], "resultFields": ["LineNo", "Quantity", "UnitPrice", "LineTotal"],
                     "notFoundText": "No line with that number."})
    lines_cfg = {"columns": ["LineNo", "Quantity", "UnitPrice", "LineTotal"],
                 "searchFields": ["LineNo"],
                 # drill-down only (never browsed standalone): empty query waits, doesn't dump a quarter-million lines
                 "requireQuery": True,
                 # LineTotal is calculated, not currency, so it skips the built-in formatting; toLocaleString adds EU thousands grouping
                 "renderers": {"LineTotal": "'€ ' + Number(data.LineTotal.toFixed(0)).toLocaleString('nl-NL')"},
                 "sortField": "LineNo", "sortDir": "asc", "pageSize": 25}
    if track_id:
        # the /order page embeds both Orders - Look up and this list; re-filter live on submit
        lines_cfg["followLookup"] = track_id
    form(lines, kind="list", title="OrderLines - Overview",
         description="Every line, by line number or order number.",
         configJson=lines_cfg)


def stock_level_forms(stock_levels):
    form(stock_levels, kind="list", title="StockLevels - By location",
         description="On-hand quantity, by product and by warehouse bin.",
         configJson={"columns": ["Product", "Location", "Qty"],
                     "sortField": "Qty", "sortDir": "asc", "pageSize": 25})


def shipment_forms(shipments):
    form(shipments, kind="form", actions=["submit"], title="Shipments - Create new",
         description="Record what shipped against an order.",
         layoutJson=layout(["PackingSlipNo", "Order"], ["Location", "ShipDate", "Tracking"], ["Notes"]))
    form(shipments, kind="form", actions=["lookup"], title="Shipments - Look up", isReadOnly=True,
         description="Enter a packing slip number.",
         configJson={"matchFields": ["PackingSlipNo"],
                     "resultFields": ["PackingSlipNo", "Location", "ShipDate", "Tracking"],
                     "notFoundText": "No shipment with that packing slip number."})
    form(shipments, kind="list", title="Shipments - Overview",
         description="Every packing slip, newest first.",
         configJson={"columns": ["PackingSlipNo", "Location", "ShipDate", "Tracking"],
                     "searchFields": ["PackingSlipNo"],
                     "sortField": "ShipDate", "sortDir": "desc", "pageSize": 25})


def fill(db_path, counts, locations, products, customers, stock_levels, orders, lines, shipments):
    random.seed(SEED)  # short_id draws from the same stream, ids are stable too
    started = time.perf_counter()
    print(f"  Records: generating {sum(counts.values()):,} rows", flush=True)
    bulk = Bulk(db_path)

    # warehouse bins: fixed, written once, referenced by code from here on
    loc_id = {}
    for code, zone, desc in LOCATIONS:
        loc_id[code] = bulk.add(locations, {"Code": code, "Zone": zone, "Description": desc}, stamp(START))
    storage_bins = [code for code, zone, _ in LOCATIONS if zone in ("Bulk storage", "Pick face")]
    shipping_bin = next(code for code, zone, _ in LOCATIONS if zone == "Shipping")

    # stock ledger: (product, bin) -> qty, opening balance below minus shipments; StockLevels is this dict at the end
    catalogue = []
    home = {}
    stock = {}
    for i in range(counts["products"]):
        price = round(random.uniform(0.45, 480.0), 2)
        material = random.choice(MATERIAL)
        article = random.choice(ARTICLE)
        size = random.choice(["M4", "M6", "M8", "M10", "12mm", "18mm", "24mm"])
        name = f"{material} {article} {size}"
        category = random.choice(CATEGORIES)
        data = {
            "Sku": f"P-{i:05d}",
            "Gtin": gs1_code("20", i),
            "Name": name,
            "Slug": f"{slugify(name)}-{i:05d}",  # size/material repeat a lot, the row index makes it unique
            "Category": category,
            "UnitPrice": price,
            "Active": random.random() > 0.08,
            "Body": f"<p>{material} {article.lower()}, {size} &mdash; engineered for {category.lower()} applications.</p>"
                    f"<ul><li>Corrosion-resistant finish</li><li>ISO-compliant dimensions</li></ul>",
            "Attributes": CATEGORY_ATTRS[category](),
            "Packaging": {
                "UnitsPerBox": random.choice([1, 10, 25, 50, 100, 250]),
                "WeightKg": round(random.uniform(0.01, 12.0), 3),
                "Dimensions": {
                    "LengthMm": random.choice([20, 40, 80, 120, 200, 400]),
                    "WidthMm": random.choice([20, 40, 80, 120, 200]),
                    "HeightMm": random.choice([10, 20, 40, 80, 150]),
                },
            },
            "Tags": random.sample(PRODUCT_TAGS, k=random.randint(0, 3)),
            "Datasheet": f"https://cdn.example.test/datasheets/P-{i:05d}.pdf",
        }
        pid = bulk.add(products, data, stamp(START))
        catalogue.append((pid, price))
        home[pid] = random.choice(storage_bins)
        # opening balance: every article starts somewhere, shipments below draw it down
        stock[(pid, home[pid])] = random.choices([0, random.randint(1, 20), random.randint(20, 500)], weights=[8, 30, 62])[0]

    accounts = []
    for i in range(counts["customers"]):
        country = random.choice(COUNTRIES)
        city = random.choice(CITIES[country])
        signed = random.randint(START, END - 30)
        lat, lon = CITY_GEO[city]
        contact = f"{random.choice(FIRST)} {random.choice(LAST)}"
        data = {
            "Email": f"{random.choice(FIRST)}.{random.choice(LAST)}{i}@{random.choice(['acme', 'nova', 'delta', 'orion', 'vertex'])}.test".lower().replace(" ", ""),
            "Name": f"{random.choice(LAST)} {random.choice(SUFFIX)}",
            "City": city,
            "Country": country,
            "Address": {
                "Street": f"{random.choice(STREETS[country])} {random.randint(1, 240)}",
                "PostalCode": POSTCODE[country](),
                # jittered off the city centre so a map of the demo data is not one pin per city
                "Geo": {"Lat": round(lat + random.uniform(-0.06, 0.06), 4),
                         "Lon": round(lon + random.uniform(-0.06, 0.06), 4)},
            },
            "Contact": {
                "Name": contact,
                "Email": f"{contact.split()[0]}.{contact.split()[-1]}@example.test".lower().replace(" ", ""),
                "Phone": f"+{COUNTRY_DIAL[country]} {random.randint(100, 999)} {random.randint(100000, 999999)}",
                "Role": random.choice(CONTACT_ROLES),
            },
            "SignedUp": str(date.fromordinal(signed)),
            "Gln": gs1_code("04", i),
            "Reference": short_id(10),
        }
        accounts.append((bulk.add(customers, data, stamp(signed)), signed, country, city, data["Address"]))

    # every order gets one line, then the remainder is scattered so the fan-out varies
    per_order = [1] * counts["orders"]
    for _ in range(counts["lines"] - counts["orders"]):
        per_order[random.randrange(counts["orders"])] += 1

    shippable_orders = []  # (order_id, order_no, ship_day) - only orders that actually shipped get a packing slip
    for i, line_count in enumerate(per_order):
        account, signed, country, city, address = accounts[random.randrange(len(accounts))]
        day = random.randint(signed, END)
        order_no = f"SO-{100000 + i}"
        order_id = short_id()
        status = random.choices(STATUSES, STATUS_WEIGHTS)[0]
        total = 0.0
        order_lines = []
        for n in range(line_count):
            product, list_price = catalogue[random.randrange(len(catalogue))]
            quantity = random.choice([1, 1, 2, 2, 5, 10, 12, 25, 50, 100])
            unit_price = round(list_price * random.uniform(0.8, 1.0), 2)
            line_total = round(quantity * unit_price, 2)
            total += line_total
            order_lines.append((product, quantity))
            bulk.add(lines, {
                "LineNo": f"{order_no}-{n + 1:03d}",
                "Order": order_id,
                "Product": product,
                "Quantity": quantity,
                "UnitPrice": unit_price,
                "LineTotal": line_total,
            }, stamp(day))

        # written last so the id chosen for the lines is the id the header lands on
        net = round(total, 2)
        vat = round(net * VAT_RATE[country], 2)
        bulk.add(orders, {
            "OrderNo": order_no,
            "Customer": account,
            "OrderDate": str(date.fromordinal(day)),
            "Channel": random.choices(CHANNELS, CHANNEL_WEIGHTS)[0],
            "Status": status,
            "Total": net,
            "ShipTo": {"Street": address["Street"], "PostalCode": address["PostalCode"],
                        "City": city, "Country": country},
            "Amounts": {"Net": net, "Vat": vat, "Gross": round(net + vat, 2)},
        }, stamp(day), record_id=order_id)
        if status in ("shipped", "closed"):
            shippable_orders.append((order_id, order_no, day))
            # decremented once per order, not per packing slip: two packages still ship the lines' quantities once
            for product, quantity in order_lines:
                bin_code = home[product]
                stock[(product, bin_code)] = max(0, stock.get((product, bin_code), 0) - quantity)

    # one packing slip per shipped order, occasionally two; guarded for a tiny --scale with no shippable orders
    for i in range(counts["shipments"] if shippable_orders else 0):
        order_id, order_no, order_day = shippable_orders[random.randrange(len(shippable_orders))]
        ship_day = min(order_day + random.randint(0, 3), END)
        bulk.add(shipments, {
            "PackingSlipNo": f"PS-{100000 + i}",
            "Order": order_id,
            "Location": loc_id[shipping_bin],
            "ShipDate": str(date.fromordinal(ship_day)),
            "Tracking": f"3S{random.randint(10**11, 10**12 - 1)}",
        }, stamp(ship_day))

    for (pid, bin_code), qty in stock.items():
        bulk.add(stock_levels, {"Product": pid, "Location": loc_id[bin_code], "Qty": qty}, stamp(END))

    bulk.close()
    print(f"  Records: {counts['products']} products, {len(LOCATIONS)} locations, "
          f"{counts['customers']} customers, "
          f"{counts['orders']} orders, {counts['lines']} order lines, "
          f"{counts['shipments']} shipments, {len(stock)} stock levels "
          f"in {time.perf_counter() - started:.1f}s")


def seed_queries():
    """Two worked examples for the SQL console: the same answer against the raw
    record store and against the projected table views, the pair shows what
    the views save."""
    existing = {q["name"] for q in call("GET", "/api/_admin/queries")[0]}
    if "Revenue by country" in existing:
        print("  Queries: already present, skipping")
        return

    sql = """SELECT
    c.JsonData->>'$.Country' As Country,
    COUNT(DISTINCT o.Id) As Orders,
    ROUND(SUM(o.JsonData->>'$.Total'), 2) As Revenue
FROM _records o
INNER JOIN _tables t ON t.Id = o.TableId AND t.Name = 'Orders'
INNER JOIN _records c ON c.Id = o.JsonData->>'$.Customer'
GROUP BY Country
ORDER BY Revenue DESC"""

    call("POST", "/api/_admin/queries", {"name": "Revenue by country", "sql": sql})

    projected = """SELECT
    c.Country,
    COUNT(DISTINCT o.id) As Orders,
    ROUND(SUM(o.Total), 2) As Revenue
FROM Orders o
INNER JOIN Customers c ON c.id = o.Customer
GROUP BY c.Country
ORDER BY Revenue DESC"""

    call("POST", "/api/_admin/queries", {"name": "Revenue by country (virtual)", "sql": projected})
    print("  Queries: 2 saved queries")


def seed_actions(orders):
    """Three worked examples, one per step kind the Action Engine has (updateRecord,
    runExpression, httpRequest) - real steps against real fields, not a placeholder
    no-op. httpRequest ships disabled: an outbound call to a system this demo does not
    own would either need a live receiver nobody here runs, or a real endpoint to point
    it at - it is left as a filled-in template an operator turns on once they have one."""
    existing = {a["name"] for a in call("GET", "/api/_admin/actions")[0]}
    if "Orders - stamp last update" in existing:
        print("  Actions: already present, skipping")
        return

    call("POST", "/api/_admin/actions", {
        "tableId": orders, "name": "Orders - stamp last update", "triggerKind": "onUpdate",
        "stepsJson": json.dumps([{"type": "updateRecord", "setJson": {"LastTouched": "GETDATE()"}}]),
    })
    call("POST", "/api/_admin/actions", {
        "tableId": orders, "name": "Orders - flag large orders", "triggerKind": "onCreate",
        "stepsJson": json.dumps([{"type": "runExpression", "expr": "Total > 5000"}]),
    })
    call("POST", "/api/_admin/actions", {
        "tableId": orders, "name": "Orders - notify ERP (template, disabled)",
        "triggerKind": "onCreate", "isEnabled": False,
        "stepsJson": json.dumps([{
            "type": "httpRequest", "url": "https://example.com/erp/orders", "method": "POST",
            "bodyTemplate": {"orderNo": "OrderNo", "customer": "Customer", "total": "Total"},
        }]),
    })
    print("  Actions: 3 seeded (1 updateRecord, 1 runExpression, 1 httpRequest template, disabled)")


def seed_portway():
    if not (TOKEN and SPEC):
        print("  Portway: skipped (set PORTWAY_TOKEN to seed the proxy table)")
        return

    if any(t["name"] == "Portway Catalogue" for t in call("GET", "/api/_admin/tables")[0]):
        print("  Portway: already present, skipping")
        return

    result = call("POST", "/api/_admin/proxy/create", {
        "name": "Portway Catalogue", "specUrl": SPEC,
        "path": "/api/{env}/Products/Products", "method": "POST",
        "pathParams": {"env": "WMS"}, "token": TOKEN,
    })[0]
    proxied = result["table"]["id"]
    call("PATCH", f"/api/_admin/tables/{proxied}", {
        "description": "Live product catalogue, proxied to the Portway demo API. Nothing is stored here.",
    })
    source = "a live sample" if result["inferredFromSample"] else "the spec"
    print(f"  Portway: {result['fieldCount']} fields inferred from {source}")

    form(proxied, kind="list", title="Portway Catalogue - Browse",
         description="Read live from the Portway demo API. Nothing is stored here.",
         configJson={"columns": ["sku", "name"], "searchFields": ["name"], "pageSize": 25})
    form(proxied, kind="form", actions=["lookup"], title="Portway Catalogue - Look up", isReadOnly=True,
         description="Enter a SKU to see what Portway stores for it.",
         configJson={"matchFields": ["sku"], "resultFields": ["sku", "name"],
                     "notFoundText": "No product with that SKU."})
    form(proxied, kind="form", actions=["submit"], title="Portway Catalogue - Add",
         description="Validated here, then forwarded to Portway.",
         layoutJson=layout(["sku", "name"]))
    print("  Portway: 3 forms (proxy)")


if __name__ == "__main__":
    sys.exit(main())
