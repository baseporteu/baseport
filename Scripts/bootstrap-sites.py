#!/usr/bin/env python3
"""Two minimal mock sites for testing Baseport embeds the way they actually get
used: pasted onto someone else's domain, loaded cross-origin, spread across more
than one page, behind the sidebar chrome a real customer or ops portal actually
has. Styled with Pico CSS (classless: it styles bare <input>/<button>/<table>
directly, which is exactly what embed.js renders - no override CSS needed to
make a Baseport embed look native here). Standard library only, reads form ids
straight out of baseport.db (no admin login needed).

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
        "logo_svg": """<svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#696cff" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"></path>
            <polyline points="3.27 6.96 12 12.01 20.73 6.96"></polyline>
            <line x1="12" y1="22.08" x2="12" y2="12"></line>
        </svg>""",
        "theme_css": """
            --pico-primary: #696cff;
            --pico-primary-background: #696cff;
            --pico-primary-border: #696cff;
            --pico-primary-underline: rgba(105, 108, 255, .5);
            --pico-primary-hover: #5f61e6;
            --pico-primary-hover-background: #5f61e6;
            --pico-primary-hover-border: #5f61e6;
            --pico-primary-hover-underline: #5f61e6;
            --pico-primary-focus: rgba(105, 108, 255, .375);
            --pico-primary-inverse: #fff;
        """,
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
        "logo_svg": """<svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#ffab00" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"></path>
            <polygon points="12 11 12 17 17 14"></polygon>
        </svg>""",
        "theme_css": """
            --pico-primary: #ffab00;
            --pico-primary-background: #ffab00;
            --pico-primary-border: #ffab00;
            --pico-primary-underline: rgba(255, 171, 0, .5);
            --pico-primary-hover: #e69a00;
            --pico-primary-hover-background: #e69a00;
            --pico-primary-hover-border: #e69a00;
            --pico-primary-hover-underline: #e69a00;
            --pico-primary-focus: rgba(255, 171, 0, .375);
            --pico-primary-inverse: #1a1a1a;
        """,
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

    # Pico styles bare <input>/<select>/<button>/<table> by tag, which is exactly what embed.js renders -
    # unlike the Bootstrap version this replaced, no override CSS is needed to make an embed look native here.
    blocks = []
    for label, candidates in page["forms"]:
        form_id = find_form_id(db_path, candidates)
        body = (
            f"<script src='{baseport_url}/embed.js?id={form_id}'></script>"
            if form_id else
            "<p><em>Not found -- run POPULATE.sh, or check the title in SITES matches your seed.</em></p>"
        )
        # Avoid duplicate title when section label matches page title
        header_html = f"<h2>{label}</h2>" if label != page["title"] else ""
        blocks.append(f"<article>{header_html}{body}</article>")

    nav_parts = []
    for path, p in spec["pages"].items():
        current = " aria-current='page'" if path == page_path else ""
        nav_parts.append(f"<li><a href='{path}'{current}>{p['title']}</a></li>")
    nav_items = "".join(nav_parts)

    return f"""<!doctype html>
<html lang='en' data-theme='light'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>{spec['heading']} &middot; {page['title']}</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Public+Sans:wght@300;400;500;600;700&display=swap" rel="stylesheet">
<link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/@picocss/pico@2/css/pico.min.css'>
<style>
:root {{
    --pico-font-family: 'Public Sans', -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
{spec['theme_css']}
}}

body {{
    display: flex;
    min-height: 100vh;
    margin: 0;
}}

.app-sidebar {{
    width: 16rem;
    flex-shrink: 0;
    padding: 2rem 1.5rem;
    border-right: 1px solid var(--pico-muted-border-color);
}}

.app-brand {{
    display: flex;
    align-items: center;
    gap: .875rem;
    margin-bottom: 2rem;
}}

.app-brand-text {{
    font-weight: 700;
    line-height: 1.2;
}}

.app-brand-tagline {{
    font-size: .8125rem;
    color: var(--pico-muted-color);
}}

.app-sidebar nav ul {{
    margin-bottom: 0;
}}

.app-main {{
    flex: 1;
    min-width: 0;
    overflow: auto;
}}

.app-main > .container {{
    padding-block: 2.5rem;
}}

.app-footnote {{
    font-size: .75rem;
    color: var(--pico-muted-color);
    margin-top: 2rem;
}}
</style>
</head><body>
<aside class='app-sidebar'>
    <div class='app-brand'>
        {spec['logo_svg']}
        <div>
            <div class='app-brand-text'>{spec['heading']}</div>
            <div class='app-brand-tagline'>{spec['tagline']}</div>
        </div>
    </div>
    <nav><ul>{nav_items}</ul></nav>
    <p class='app-footnote'>{hostname}<br>Mock site for embed testing</p>
</aside>
<main class='app-main'>
<div class='container'>
<hgroup>
    <h1>{page['title']}</h1>
    <p>{spec['heading']}</p>
</hgroup>
{''.join(blocks)}
</div>
</main>
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