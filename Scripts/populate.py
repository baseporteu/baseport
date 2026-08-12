"""Seeds a running Baseport instance with a production-like demo workspace.

  Products    catalogue lookup, referenced by order lines
  Customers   accounts that place orders
  Orders      references Customers
  OrderLines  references Orders and Products
  Portway     optional proxy table over the Portway demo API

Tables, fields and forms go through the admin API so validation, ApiName rules
and the generated-column DDL all run. Rows go straight into SQLite in batches,
because a quarter million HTTP posts would outlast the rest of the seed.

Deterministic: the RNG is seeded, so two runs produce the same database.

Run through POPULATE.sh, which supplies the environment.
"""
import argparse
import json
import os
import random
import sqlite3
import sys
import time
import urllib.error
import urllib.request
from datetime import date

BASE = os.environ.get("BASE_URL", "http://localhost:5263").rstrip("/")
USER = os.environ.get("ADMIN_USER", "admin")
PASSWORD = os.environ.get("ADMIN_PASSWORD", "")
NEW_PASSWORD = os.environ.get("ADMIN_NEW_PASSWORD", "baseport-dev-password")
SPEC = os.environ.get("PORTWAY_SPEC", "")
TOKEN = os.environ.get("PORTWAY_TOKEN", "")

DEFAULT_DB = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                          "Source", "Baseport", "baseport.db")
VOLUMES = {"products": 80, "customers": 4_000, "orders": 40_000, "lines": 250_000}
SEED = 20260101
BATCH = 5_000
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_-"

_cookie = ""

CUSTOMERS_DOC = """The accounts that place orders.

## Finding an account

`Email` is unique across the table and is what the customer knows, so it is the
practical way to find someone. `Reference` is generated here and is safe to
print on correspondence.

## What you can do

Read and create accounts, and patch the ones that exist. Accounts are never
deleted through the API, so `DELETE` is switched off for this endpoint.
"""

ORDERS_DOC = """Order headers taken through the portal.

## Identifiers

Every order carries an `OrderNo` that is unique across the table and safe to
show a customer. The `id` in a response is the record's own identifier: it is
unguessable and stable, and it is what the single-record routes take.

## Totals

`Total` is the sum of the order's lines at the moment it was taken. The lines
themselves live in `order-lines` and reference this order.

## What you can do

Read and create orders, and patch the ones that exist. Orders are closed by
setting `Status`, never deleted, so `DELETE` is switched off for this endpoint.
"""

LINES_DOC = """The individual lines of an order.

Each line references the order it belongs to and the product it sells.
`LineTotal` is calculated from `Quantity` and `UnitPrice` on write; sending it
has no effect.
"""

PRODUCTS_DOC = """The catalogue order lines point at.

`Sku` is unique and is the practical way to find an article. Inactive products
stay readable so historic order lines keep resolving.
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
STATUSES = ["open", "picking", "shipped", "closed", "cancelled"]
STATUS_WEIGHTS = [12, 8, 20, 55, 5]
CHANNELS = ["web", "phone", "edi", "counter"]
CHANNEL_WEIGHTS = [55, 15, 25, 5]
START = date(2024, 1, 1).toordinal()
END = date(2026, 7, 31).toordinal()


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
            # Any response may re-issue the session: changing the password ends every session.
            for value in headers.get_all("Set-Cookie") or []:
                if value.startswith("baseport_session="):
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


def sign_in():
    global _cookie
    if not PASSWORD:
        raise SystemExit(
            "Set ADMIN_PASSWORD. A fresh instance logs a one-time admin password on first start;\n"
            "grep the console output or log/baseport-*.log for 'one-time admin password'.")

    call("POST", "/api/auth/login", {"username": USER, "password": PASSWORD})
    if not _cookie:
        raise SystemExit("Signed in but no session cookie came back.")

    # A session on the one-time password reaches the sign-in surface and nothing else.
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


class Bulk:
    """Rows straight into SQLite. _records is four stable columns; the generated
    index columns are virtual, so SQLite derives them from JsonData on insert."""

    SQL = 'INSERT INTO "_records" ("Id","TableId","JsonData","CreatedAt") VALUES (?,?,?,?)'

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

    def add(self, table_id, data, created):
        rid = short_id()
        self.batch.append((rid, table_id, json.dumps(data, separators=(",", ":")), created))
        if len(self.batch) >= BATCH:
            self.flush()
        return rid

    def flush(self):
        if self.batch:
            self.conn.executemany(self.SQL, self.batch)
            self.written += len(self.batch)
            self.batch.clear()

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
    sign_in()

    products, fresh_p = table("Products", "Catalogue the order lines sell from", product_fields())
    publish(products, "products", "Products", "Catalogue", PRODUCTS_DOC)

    customers, fresh_c = table("Customers", "Accounts that place orders", customer_fields())
    publish(customers, "customers", "Customers", "Accounts", CUSTOMERS_DOC)

    orders, fresh_o = table("Orders", "Order headers taken through the portal", order_fields(customers))
    publish(orders, "sales-orders", "Sales orders", "Sales", ORDERS_DOC)

    lines, fresh_l = table("OrderLines", "The lines of an order", line_fields(orders, products))
    publish(lines, "order-lines", "Order lines", "Sales", LINES_DOC)

    if fresh_p:
        product_forms(products)
    if fresh_c:
        customer_forms(customers)
    track_id = order_forms(orders) if fresh_o else None
    if fresh_l:
        line_forms(lines, track_id)

    if fresh_p and fresh_c and fresh_o and fresh_l:
        fill(args.db, counts, products, customers, orders, lines)
    else:
        print("  Records: skipped, some tables already existed")

    seed_queries()
    seed_portway()


def product_fields():
    return [
        {"name": "Sku", "label": "SKU", "dataType": "text", "isRequired": True,
         "isUnique": True, "isIdentifier": True, "helpText": "Printed on the packing slip."},
        {"name": "Name", "label": "Description", "dataType": "text", "isRequired": True},
        {"name": "Category", "dataType": "select", "optionsJson": json.dumps(CATEGORIES),
         "defaultValue": CATEGORIES[0]},
        {"name": "UnitPrice", "label": "List price", "dataType": "currency", "min": 0},
        {"name": "Active", "dataType": "boolean", "defaultValue": "true"},
    ]


def customer_fields():
    return [
        {"name": "Email", "label": "Email address", "dataType": "text",
         "isRequired": True, "isUnique": True, "isIdentifier": True,
         "pattern": r"^[^@\s]+@[^@\s]+\.[^@\s]+$", "helpText": "We use this to find your account."},
        {"name": "Name", "label": "Account name", "dataType": "text", "isRequired": True},
        {"name": "City", "dataType": "text"},
        {"name": "Country", "dataType": "select", "optionsJson": json.dumps(COUNTRIES),
         "defaultValue": "Netherlands"},
        {"name": "SignedUp", "label": "Signed up", "dataType": "date"},
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
        {"name": "Notes", "label": "Internal notes", "dataType": "longtext", "isHidden": True},
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


def product_forms(products):
    form(products, kind="form", actions=["submit"], title="Products - Create new",
         description="A new article for the catalogue.",
         layoutJson=layout(["Sku", "Name"], ["Category", "UnitPrice", "Active"]))
    form(products, kind="form", actions=["lookup"], title="Products - Look up", isReadOnly=True,
         description="Enter a SKU.",
         configJson={"matchFields": ["Sku"], "resultFields": ["Sku", "Name", "Category", "UnitPrice"],
                     "notFoundText": "No product with that SKU."})
    form(products, kind="list", title="Products - Catalogue",
         description="Every article, by description.",
         configJson={"columns": ["Sku", "Name", "Category", "UnitPrice"],
                     "searchFields": ["Sku", "Name"],
                     "sortField": "Name", "sortDir": "asc", "pageSize": 25})


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
    # Returned so line_forms() can point OrderLines - Overview's followLookup at it: the
    # /order page embeds both, and the list should track whatever this lookup submits.
    track_id = form(orders, kind="form", actions=["lookup"], title="Orders - Look up", isReadOnly=True,
                     description="Enter your order number.",
                     configJson={"matchFields": ["OrderNo"], "resultFields": ["OrderNo", "OrderDate", "Status", "Total"],
                                 "notFoundText": "We could not find that order."})
    form(orders, kind="list", title="Orders - Overview status open",
         description="Everything still open, newest first.",
         configJson={"columns": ["OrderNo", "OrderDate", "Channel", "Total", "Status"],
                     "searchFields": ["OrderNo"],
                     "filters": [{"field": "Status", "op": "eq", "value": "open"}],
                     # A renderer turns a bare value into markup; row data is escaped first.
                     "renderers": {"Status": "'<strong>' + data.Status.toUpperCase() + '</strong>'"},
                     # Target is the /order page bootstrap-sites.py serves on customers.site.com,
                     # which embeds "Orders - Look up" and answers a ?q= deep link on load.
                     "actions": [{"label": "View order",
                                  "hrefExpr": "'http://127.0.0.1:8081/order?q=' + encodeURIComponent(data.OrderNo)"}],
                     "sortField": "OrderDate", "sortDir": "desc", "pageSize": 25})

    form(orders, kind="list", title="Orders - Worklist",
         description="Everything still open, for picking and packing.",
         configJson={"columns": ["OrderNo", "OrderDate", "Channel", "Total", "Status"],
                     "searchFields": ["OrderNo"],
                     "filters": [{"field": "Status", "op": "eq", "value": "open"}],
                     "renderers": {"Status": "'<strong>' + data.Status.toUpperCase() + '</strong>'"},
                     # Target is the /order-lines page bootstrap-sites.py serves on wms.site.com,
                     # which embeds "OrderLines - Overview" and answers a ?q= deep link on load.
                     # LineNo is stamped "{OrderNo}-{n}" at seed time (see below), so a plain
                     # substring search on OrderNo already narrows the list to this order's lines.
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
                 # This is only ever embedded as a drill-down target (linked to from an
                 # order's own row action, never browsed standalone), so an empty query
                 # should not dump all quarter-million lines -- it should wait for one.
                 "requireQuery": True,
                 # LineTotal is a calculated field, not a currency field, so it falls
                 # outside the built-in currency formatting UnitPrice gets automatically
                 # -- the engine's expression grammar has no object-literal syntax for
                 # Intl options, so this rounds and lets toLocaleString's own thousands
                 # grouping do the rest (EU locale: '.' groups, ',' would be the decimal
                 # separator, moot here since whole euros have none).
                 "renderers": {"LineTotal": "'€ ' + Number(data.LineTotal.toFixed(0)).toLocaleString('nl-NL')"},
                 "sortField": "LineNo", "sortDir": "asc", "pageSize": 25}
    if track_id:
        # The /order page embeds both Orders - Look up and this list, so this list should
        # re-filter live whenever a visitor submits a new order number up there.
        lines_cfg["followLookup"] = track_id
    form(lines, kind="list", title="OrderLines - Overview",
         # LineNo is "{OrderNo}-{n}", so searching "SO-118153" also narrows to
         # every line on that one order -- no separate order field needed.
         description="Every line, by line number or order number.",
         configJson=lines_cfg)


def fill(db_path, counts, products, customers, orders, lines):
    random.seed(SEED)  # short_id draws from the same stream, so ids are stable too
    started = time.perf_counter()
    print(f"  Records: generating {sum(counts.values()):,} rows", flush=True)
    bulk = Bulk(db_path)

    catalogue = []
    for i in range(counts["products"]):
        price = round(random.uniform(0.45, 480.0), 2)
        data = {
            "Sku": f"P-{i:05d}",
            "Name": f"{random.choice(MATERIAL)} {random.choice(ARTICLE)} {random.choice(['M4', 'M6', 'M8', 'M10', '12mm', '18mm', '24mm'])}",
            "Category": random.choice(CATEGORIES),
            "UnitPrice": price,
            "Active": random.random() > 0.08,
        }
        catalogue.append((bulk.add(products, data, stamp(START)), price))

    accounts = []
    for i in range(counts["customers"]):
        country = random.choice(COUNTRIES)
        signed = random.randint(START, END - 30)
        data = {
            "Email": f"{random.choice(FIRST)}.{random.choice(LAST)}{i}@{random.choice(['acme', 'nova', 'delta', 'orion', 'vertex'])}.test".lower().replace(" ", ""),
            "Name": f"{random.choice(LAST)} {random.choice(SUFFIX)}",
            "City": random.choice(CITIES[country]),
            "Country": country,
            "SignedUp": str(date.fromordinal(signed)),
            "Reference": short_id(10),
        }
        accounts.append((bulk.add(customers, data, stamp(signed)), signed))

    # Every order gets one line, then the remainder is scattered so the fan-out varies.
    per_order = [1] * counts["orders"]
    for _ in range(counts["lines"] - counts["orders"]):
        per_order[random.randrange(counts["orders"])] += 1

    for i, line_count in enumerate(per_order):
        account, signed = accounts[random.randrange(len(accounts))]
        day = random.randint(signed, END)
        order_no = f"SO-{100000 + i}"
        order_id = short_id()
        total = 0.0
        for n in range(line_count):
            product, list_price = catalogue[random.randrange(len(catalogue))]
            quantity = random.choice([1, 1, 2, 2, 5, 10, 12, 25, 50, 100])
            unit_price = round(list_price * random.uniform(0.8, 1.0), 2)
            line_total = round(quantity * unit_price, 2)
            total += line_total
            bulk.add(lines, {
                "LineNo": f"{order_no}-{n + 1:03d}",
                "Order": order_id,
                "Product": product,
                "Quantity": quantity,
                "UnitPrice": unit_price,
                "LineTotal": line_total,
            }, stamp(day))

        # Written last so the id chosen for the lines is the id the header lands on.
        bulk.batch.append((order_id, orders, json.dumps({
            "OrderNo": order_no,
            "Customer": account,
            "OrderDate": str(date.fromordinal(day)),
            "Channel": random.choices(CHANNELS, CHANNEL_WEIGHTS)[0],
            "Status": random.choices(STATUSES, STATUS_WEIGHTS)[0],
            "Total": round(total, 2),
        }, separators=(",", ":")), stamp(day)))
        if len(bulk.batch) >= BATCH:
            bulk.flush()

    bulk.close()
    print(f"  Records: {counts['products']} products, {counts['customers']} customers, "
          f"{counts['orders']} orders, {counts['lines']} order lines "
          f"in {time.perf_counter() - started:.1f}s")


def seed_queries():
    """A worked example for the SQL console: it joins on names, so it survives a reseed."""
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
    print("  Queries: 1 saved query")


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
         description="Enter a SKU to see what Portway holds for it.",
         configJson={"matchFields": ["sku"], "resultFields": ["sku", "name"],
                     "notFoundText": "No product with that SKU."})
    form(proxied, kind="form", actions=["submit"], title="Portway Catalogue - Add",
         description="Validated here, then forwarded to Portway.",
         layoutJson=layout(["sku", "name"]))
    print("  Portway: 3 forms (proxy)")


if __name__ == "__main__":
    sys.exit(main())
