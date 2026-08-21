#!/usr/bin/env python3
"""Two minimal mock sites for testing Baseport embeds the way they actually get
used: pasted onto someone else's domain, loaded cross-origin, spread across more
than one page, behind the sidebar-and-breadcrumb chrome a real customer or ops
portal actually has. Standard library only, reads form ids straight out of
baseport.db (no admin login needed).

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
            :root {
                --bs-primary: #696cff;
                --bs-primary-hover: #5f61e6;
                --sidebar-bg: #ffffff;
                --sidebar-text: #697a8d;
                --sidebar-border: #eceef1;
                --sidebar-link: #697a8d;
                --sidebar-link-hover-bg: rgba(67, 89, 113, 0.04);
                --sidebar-link-active: #696cff;
                --sidebar-link-active-bg: rgba(105, 108, 255, 0.16);
                --body-bg: #f5f5f9;
                --btn-shadow: rgba(105, 108, 255, 0.35);
            }
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
            :root {
                --bs-primary: #ffab00;
                --bs-primary-hover: #e69a00;
                --sidebar-bg: #ffffff;
                --sidebar-text: #697a8d;
                --sidebar-border: #eceef1;
                --sidebar-link: #697a8d;
                --sidebar-link-hover-bg: rgba(67, 89, 113, 0.04);
                --sidebar-link-active: #ffab00;
                --sidebar-link-active-bg: rgba(255, 171, 0, 0.16);
                --body-bg: #f5f5f9;
                --btn-shadow: rgba(255, 171, 0, 0.35);
            }
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
            "<p class='text-muted mb-0'><em>Not found -- run POPULATE.sh, or check the title in SITES matches your seed.</em></p>"
        )
        # Avoid duplicate title when section label matches page title
        header_html = f"<h2 class='h5 mb-3 fw-bold' style='color: #566a7f;'>{label}</h2>" if label != page["title"] else ""
        blocks.append(f"<div class='mb-5 baseport-overrides'>{header_html}{body}</div>")

    nav_items = "".join(
        f"<li class='nav-item'><a class='nav-link{' active' if path == page_path else ''}' href='{path}'>{p['title']}</a></li>"
        for path, p in spec["pages"].items()
    )

    return f"""<!doctype html>
<html lang='en'><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>{spec['heading']} &middot; {page['title']}</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Public+Sans:wght@300;400;500;600;700&display=swap" rel="stylesheet">
<link href='https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css' rel='stylesheet'>
<style>
{spec['theme_css']}

body {{
    background-color: var(--body-bg);
    color: #566a7f;
    font-family: 'Public Sans', -apple-system, BlinkMacSystemFont, "Segoe UI", "Oxygen", "Ubuntu", "Cantarell", "Fira Sans", "Droid Sans", "Helvetica Neue", sans-serif;
}}

.app-sidebar {{
    background-color: var(--sidebar-bg);
    box-shadow: 0 0.125rem 0.375rem 0 rgba(161, 172, 184, 0.12);
    z-index: 10;
}}

.app-sidebar .nav-link {{
    color: var(--sidebar-link);
    border-radius: 0.375rem;
    padding: 0.625rem 1rem;
    font-weight: 400;
    margin-bottom: 0.25rem;
    transition: all 0.2s ease-in-out;
}}

.app-sidebar .nav-link:hover {{
    color: var(--sidebar-link-active);
    background-color: var(--sidebar-link-hover-bg);
}}

.app-sidebar .nav-link.active {{
    color: var(--sidebar-link-active);
    background-color: var(--sidebar-link-active-bg);
    font-weight: 600;
}}

.app-brand {{
    display: flex;
    align-items: center;
    gap: 0.875rem;
    padding-bottom: 0.5rem;
}}

.app-brand-text {{
    color: #566a7f;
    font-weight: 700;
    letter-spacing: -0.5px;
}}

.breadcrumb-item a {{
    color: var(--bs-primary);
    text-decoration: none;
}}

.breadcrumb-item.active {{
    color: #697a8d;
}}

/* BASEPORT EMBED OVERRIDES */

.baseport-overrides iframe {{
    border: none !important;
    background: transparent !important;
    width: 100%;
    min-height: 400px;
}}

.baseport-overrides input,
.baseport-overrides select,
.baseport-overrides textarea {{
    border: 1px solid #d9dee3;
    border-radius: 0.375rem;
    padding: 0.4375rem 0.875rem;
    font-size: 0.9375rem;
    color: #697a8d;
    background-color: #fff;
    transition: border-color 0.15s ease-in-out, box-shadow 0.15s ease-in-out;
}}

.baseport-overrides input:focus,
.baseport-overrides select:focus,
.baseport-overrides textarea:focus {{
    border-color: var(--bs-primary);
    box-shadow: 0 0 0 0.25rem var(--sidebar-link-active-bg);
    outline: 0;
}}

/* General Button Overrides */
.baseport-overrides button,
.baseport-overrides .btn,
.baseport-overrides a.btn {{
    border-radius: 0.375rem;
    font-weight: 500;
    font-size: 0.875rem;
    padding: 0.375rem 0.875rem;
    transition: all 0.15s ease-in-out;
}}

/* Primary Buttons */
.baseport-overrides .btn-primary,
.baseport-overrides button[type="submit"] {{
    background-color: var(--bs-primary) !important;
    border-color: var(--bs-primary) !important;
    color: #ffffff !important;
    box-shadow: 0 0.125rem 0.25rem 0 var(--btn-shadow);
}}

.baseport-overrides .btn-primary:hover,
.baseport-overrides button[type="submit"]:hover {{
    background-color: var(--bs-primary-hover) !important;
    border-color: var(--bs-primary-hover) !important;
    color: #ffffff !important;
}}

/* Table Row Action Buttons ("View order", etc.) */
.baseport-overrides table .btn,
.baseport-overrides table a.btn,
.baseport-overrides .btn-outline-primary,
.baseport-overrides .btn-secondary,
.baseport-overrides .btn-light {{
    background-color: rgba(105, 108, 255, 0.08) !important;
    border: 1px solid transparent !important;
    color: var(--bs-primary) !important;
    box-shadow: none !important;
}}

.baseport-overrides table .btn:hover,
.baseport-overrides table a.btn:hover,
.baseport-overrides .btn-outline-primary:hover,
.baseport-overrides .btn-secondary:hover,
.baseport-overrides .btn-light:hover {{
    background-color: var(--bs-primary) !important;
    border-color: var(--bs-primary) !important;
    color: #ffffff !important;
    box-shadow: 0 0.125rem 0.25rem 0 var(--btn-shadow) !important;
}}

.baseport-overrides table {{
    border-collapse: collapse;
    width: 100%;
}}

.baseport-overrides th {{
    text-transform: uppercase;
    font-size: 0.75rem;
    letter-spacing: 0.5px;
    color: #a1acb8;
    border-bottom: 1px solid #d9dee3;
    padding-bottom: 0.75rem;
}}
</style>
</head><body class='d-flex flex-column flex-md-row vh-100'>
<div class='d-flex flex-column flex-shrink-0 p-4 app-sidebar' style='width:100%;max-width:16rem'>
<div class='app-brand mb-2'>
    {spec['logo_svg']}
    <div>
        <div class='fs-5 app-brand-text leading-tight'>{spec['heading']}</div>
        <div class='small text-muted' style='font-size: 0.75rem;'>{spec['tagline']}</div>
    </div>
</div>
<ul class='nav nav-pills flex-column mb-auto mt-4'>{nav_items}</ul>
<div class='small text-muted mt-4' style='font-size: 0.75rem;'>
    <div class='fw-semibold' style='color: #a1acb8;'>{hostname}</div>
    Mock site for embed testing
</div>
</div>
<div class='flex-grow-1 overflow-auto'>
<div class='container py-5' style='max-width:1000px'>
<nav aria-label='breadcrumb'><ol class='breadcrumb' style='font-size: 0.875rem;'>
<li class='breadcrumb-item text-muted'>{spec['heading']}</li>
<li class='breadcrumb-item active' aria-current='page'>{page['title']}</li>
</ol></nav>
<h1 class='h4 mb-4' style='color: #566a7f; font-weight: 500;'>{page['title']}</h1>
{''.join(blocks)}
</div>
</div>
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