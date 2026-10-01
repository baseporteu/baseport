#!/usr/bin/env python3
"""Two minimal mock sites for testing Baseport embeds the way they actually get
used: pasted onto someone else's domain, loaded cross-origin, spread across more
than one page, behind the sidebar chrome a real customer or ops portal actually
has. The embed is rethemed only through its --baserow-* properties. Standard
library only, reads form ids straight out of baseport.db (no admin login needed).

    customers.site.com  the sales-facing site: a "My orders" list that links out
                         to a dedicated order page, plus place-order and sign-up
    wms.site.com         the ops-facing site: the open-orders worklist

    python3 Scripts/bootstrap-sites.py
    python3 Scripts/bootstrap-sites.py --baseport-url http://localhost:5000
"""

import argparse
import sqlite3
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

DEFAULT_DB = Path(__file__).resolve().parent.parent / "Source" / "Baseport" / "baseport.db"

SITES = {
    "customers.site.com": {
        "port": 8081,
        "heading": "Acme Direct",
        "tagline": "Customer Self-Service",
        "stamp": "AD",
        "accent": "#ffc72c",
        "pages": {
            "/": {
                "title": "My orders",
                "forms": [
                    ("My orders", ["Orders - Overview status open", "Open orders"]),
                    ("Place an order", ["Orders - Create new", "Place an order"]),
                    ("Become a customer", ["Customers - Create new", "Become a customer"]),
                ],
            },
            "/order": {
                "title": "View order",
                "forms": [
                    ("View order", ["Orders - Look up", "Track your order"]),
                    ("Order lines", ["OrderLines - Overview", "Order lines"]),
                ],
            },
        },
    },
    "wms.site.com": {
        "port": 8082,
        "heading": "Warehouse Ops",
        "tagline": "Fulfillment Worklist",
        "stamp": "WO",
        "accent": "#ff6a13",
        "pages": {
            "/": {
                "title": "Open orders worklist",
                "forms": [
                    ("Open orders worklist", ["Orders - Worklist", "Orders - Overview status open"]),
                ],
            },
            "/order-lines": {
                "title": "Order lines",
                "forms": [
                    ("Order lines", ["OrderLines - Overview", "Order lines"]),
                ],
            },
            "/catalogue": {
                "title": "Product catalogue",
                "forms": [
                    ("Product catalogue", ["Products - Catalogue", "Catalogue"]),
                ],
            },
            "/stock": {
                "title": "Stock on hand",
                "forms": [
                    ("Stock on hand", ["StockLevels - By location", "Products - Stock"]),
                ],
            },
            "/shipments": {
                "title": "Shipments",
                "forms": [
                    ("Shipments", ["Shipments - Overview"]),
                ],
            },
        },
    },
}


def find_form_id(db_path: Path, candidates: list[str]) -> str | None:
    if not db_path.exists():
        return None
    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        for title in candidates:
            row = conn.execute("SELECT Id FROM _forms WHERE Title = ? LIMIT 1", (title,)).fetchone()
            if row:
                return row[0]
    finally:
        conn.close()
    return None


def render_page(hostname: str, spec: dict, page_path: str, baseport_url: str, db_path: Path) -> str | None:
    page = spec["pages"].get(page_path)
    if page is None:
        return None

    blocks = []
    for label, candidates in page["forms"]:
        form_id = find_form_id(db_path, candidates)
        body = (
            f"<script src='{baseport_url}/embed.js?id={form_id}'></script>"
            if form_id else
            "<p class='missing'>Not found. Run POPULATE.sh, or check the title in SITES matches your seed.</p>"
        )
        blocks.append(f"<section class='sheet' aria-label='{label}'><div class='card'><div class='perf' aria-hidden='true'></div>{body}</div></section>")

    current = " aria-current='page'"
    nav_items = "".join(
        f"<li><a href='{path}'{current if path == page_path else ''}>{p['title']}</a></li>"
        for path, p in spec["pages"].items()
    )

    return f"""<!doctype html>
<html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>{spec['heading']} &middot; {page['title']}</title>
<link rel='preconnect' href='https://fonts.googleapis.com'>
<link rel='preconnect' href='https://fonts.gstatic.com' crossorigin>
<link href='https://fonts.googleapis.com/css2?family=Oswald:wght@400;500;600;700&display=swap' rel='stylesheet'>
<style>
:root {{
    --ink: #1a1a1a;
    --muted: #6b6b66;
    --paper: #ffffff;
    --paper-dim: #f7f6f2;
    --rule: #d8d5cc;
    --accent: {spec['accent']};
    --display: 'Oswald', 'Arial Narrow', sans-serif;
    --lift: 0 3px 10px rgb(0 0 0 / .07);
    color-scheme: light;
    accent-color: var(--ink);
    scrollbar-color: var(--rule) var(--paper-dim);
}}

*, *::before, *::after {{ box-sizing: border-box; }}
::selection {{ background: var(--accent); color: var(--ink); }}
:focus-visible {{ outline: 2px solid var(--ink); outline-offset: 3px; }}

body {{
    margin: 0;
    min-height: 100vh;
    display: flex;
    background: var(--paper-dim);
    color: var(--ink);
    font-family: system-ui, -apple-system, 'Segoe UI', sans-serif;
    line-height: 1.5;
}}

.stub {{
    width: 15rem;
    flex-shrink: 0;
    position: relative;
    padding: 2rem 1.5rem;
    background: var(--paper);
}}

.stub::after {{
    content: '';
    position: absolute;
    top: 0;
    right: -5px;
    bottom: 0;
    width: 10px;
    background: radial-gradient(circle at 5px 8px, var(--paper-dim) 3px, transparent 3.5px) 0 0 / 10px 16px repeat-y;
}}

.brand {{ display: flex; align-items: center; gap: .875rem; margin-bottom: 2.5rem; }}

.stamp {{
    width: 52px;
    height: 52px;
    flex-shrink: 0;
    display: grid;
    place-items: center;
    position: relative;
    border: 2px solid var(--ink);
    border-radius: 50%;
    transform: rotate(-6deg);
    font: 700 1.05rem/1 var(--display);
    letter-spacing: .02em;
}}

.stamp::before {{
    content: '';
    position: absolute;
    inset: 4px;
    border: 1px dashed var(--ink);
    border-radius: 50%;
    opacity: .45;
}}

.brand-name {{ font: 700 1rem/1.15 var(--display); text-transform: uppercase; letter-spacing: .02em; }}

.footnote, .brand-tagline {{
    font: 400 .6875rem/1.5 var(--display);
    text-transform: uppercase;
    letter-spacing: .05em;
    color: var(--muted);
}}

.stub ul {{ list-style: none; margin: 0; padding: 0; }}

.stub a {{
    display: block;
    padding: .65rem .5rem;
    border-bottom: 1px dashed var(--rule);
    color: var(--ink);
    text-decoration: none;
    font: 500 .875rem/1.2 var(--display);
    text-transform: uppercase;
    letter-spacing: .015em;
    transition: background-color .15s ease-out;
}}

.stub a:hover {{ background: var(--paper-dim); }}
.stub a[aria-current] {{ background: var(--accent); font-weight: 700; }}

.footnote {{ margin-top: 2.5rem; }}

main {{ flex: 1; min-width: 0; padding: 2.5rem 2rem 4rem; }}
.well {{ max-width: 56rem; margin: 0 auto; }}

h1 {{
    margin: 0 0 2.5rem;
    font: 700 clamp(1.5rem, 4vw, 2rem)/1.1 var(--display);
    text-transform: uppercase;
    letter-spacing: .01em;
    text-wrap: balance;
}}

h1::after {{
    content: '';
    display: block;
    width: 3.5rem;
    height: 4px;
    margin-top: .75rem;
    background: var(--accent);
}}

.sheet {{ margin-bottom: 2rem; filter: drop-shadow(var(--lift)); }}

.card {{
    position: relative;
    padding: 0 1.5rem 1.75rem;
    background: var(--paper);
    border: 2px solid var(--ink);
    clip-path: polygon(0 0, calc(100% - 16px) 0, 100% 16px, 100% 100%, 0 100%);
}}

.card::before {{
    content: '';
    position: absolute;
    top: -2px;
    right: -2px;
    width: 18px;
    height: 18px;
    background: linear-gradient(to top right, transparent calc(50% - 2px), var(--ink) calc(50% - 2px) calc(50% + 1px), transparent calc(50% + 1px));
}}

.perf {{
    height: 8px;
    margin: 1.25rem -1.5rem 1.5rem;
    background:
        radial-gradient(circle at 6px 4px, var(--paper-dim) 2px, transparent 2.5px) 0 0 / 12px 8px repeat-x,
        var(--accent);
}}

.missing {{ margin: 0; color: var(--muted); }}

.baserow-embed {{
    --baserow-border: var(--ink) !important;
    --baserow-muted: var(--muted) !important;
    --baserow-accent: var(--accent) !important;
    --baserow-accent-fg: var(--ink) !important;
    --baserow-radius: 0 !important;
}}

@media (max-width: 800px) {{
    body {{ flex-direction: column; }}
    .stub {{ width: auto; }}
    .stub::after {{ display: none; }}
    .brand {{ margin-bottom: 1.5rem; }}
    .footnote {{ display: none; }}
    main {{ padding: 1.5rem 1.25rem 3rem; }}
}}

@media (prefers-reduced-motion: reduce) {{
    .stub a {{ transition: none; }}
}}
</style>
</head><body>
<nav class='stub' aria-label='{spec['heading']}'>
    <div class='brand'>
        <span class='stamp' aria-hidden='true'>{spec['stamp']}</span>
        <div>
            <div class='brand-name'>{spec['heading']}</div>
            <div class='brand-tagline'>{spec['tagline']}</div>
        </div>
    </div>
    <ul>{nav_items}</ul>
    <p class='footnote'>{hostname}<br>Mock site for embed testing</p>
</nav>
<main><div class='well'>
<h1>{page['title']}</h1>
{''.join(blocks)}
</div></main>
</body></html>"""


def make_handler(hostname: str, spec: dict, baseport_url: str, db_path: Path):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            page_path = urlsplit(self.path).path
            html = render_page(hostname, spec, page_path, baseport_url, db_path)
            if html is None:
                self.send_response(404)
                self.send_header("Content-Type", "text/plain; charset=utf-8")
                self.end_headers()
                self.wfile.write(b"No such page on this mock site.")
                return
            page = html.encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(page)))
            self.end_headers()
            self.wfile.write(page)

        def log_message(self, fmt, *args):
            print(f"  [{hostname}] {self.address_string()} {fmt % args}")

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--db", type=Path, default=DEFAULT_DB, help="baseport.db to read form ids from")
    parser.add_argument("--baseport-url", default="http://localhost:5000", help="where Baseport itself is running")
    args = parser.parse_args()

    servers = []
    for hostname, spec in SITES.items():
        handler = make_handler(hostname, spec, args.baseport_url, args.db)
        server = ThreadingHTTPServer(("127.0.0.1", spec["port"]), handler)
        servers.append(server)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        base = f"http://127.0.0.1:{spec['port']}"
        pages = ", ".join(base + path for path in spec["pages"])
        print(f"{hostname:20} -> {pages}  (embeds served from {args.baseport_url})")

    print("\nCtrl+C to stop both.")
    try:
        threading.Event().wait()
    except KeyboardInterrupt:
        print("\nStopping.")
        for server in servers:
            server.shutdown()


if __name__ == "__main__":
    main()