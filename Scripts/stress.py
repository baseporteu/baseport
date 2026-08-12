#!/usr/bin/env python3
"""Load baseline for the public REST API. Standard library only.

Seeds a table over HTTP, bulk-inserts records straight into SQLite (100k rows
through the API would take longer than the measurement), then reports
p50/p95/p99 per scenario.

    python3 Scripts/stress.py --rows 100000
    python3 Scripts/stress.py --rows 1000 --label before-generated-columns
"""

import argparse, http.client, json, os, random, sqlite3, statistics, string, sys, time
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import urlparse

ALPHABET = string.ascii_letters + string.digits
CITIES = ["Amsterdam", "Rotterdam", "Utrecht", "Eindhoven", "Groningen", "Breda"]
STATUSES = ["new", "open", "pending", "closed"]

FIELDS = [
    {"Name": "reference", "DataType": "text", "IsUnique": True, "IsIdentifier": True, "Position": 0},
    {"Name": "customer", "DataType": "text", "Position": 1},
    {"Name": "city", "DataType": "text", "Position": 2},
    {"Name": "status", "DataType": "text", "Position": 3},
    {"Name": "amount", "DataType": "number", "Position": 4},
    {"Name": "note", "DataType": "longtext", "Position": 5},
]


class Client:
    """One keep-alive connection. Reused across requests so the numbers measure
    the server, not TCP and TLS setup."""

    def __init__(self, base):
        url = urlparse(base)
        self.host, self.port = url.hostname, url.port or 80
        self.conn = http.client.HTTPConnection(self.host, self.port, timeout=30)
        self.cookie = None
        self.token = None

    def request(self, method, path, body=None):
        headers = {"Content-Type": "application/json"}
        if self.cookie:
            headers["Cookie"] = self.cookie
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        payload = json.dumps(body) if body is not None else None
        try:
            self.conn.request(method, path, payload, headers)
            response = self.conn.getresponse()
            data = response.read()
        except (http.client.HTTPException, OSError):
            self.conn.close()
            self.conn = http.client.HTTPConnection(self.host, self.port, timeout=30)
            raise
        if set_cookie := response.getheader("Set-Cookie"):
            self.cookie = set_cookie.split(";")[0]
        return response.status, data

    def json(self, method, path, body=None):
        status, data = self.request(method, path, body)
        if status >= 400:
            sys.exit(f"{method} {path} -> {status}: {data[:300].decode('utf-8', 'replace')}")
        return json.loads(data) if data else None


def short_id(length=12):
    return "".join(random.choices(ALPHABET, k=length))


ADMIN = "/api/_admin"
BENCH_PASSWORD = "stress-harness-password-1"


def sign_in(client, password):
    """A seeded admin is penned in until the one-time password is replaced, so
    the harness replaces it before touching anything else."""
    client.json("POST", "/api/auth/login", {"username": "admin", "password": password})
    me = client.json("GET", "/api/auth/me")
    if me.get("mustChangePassword"):
        client.json("POST", "/api/auth/password",
                    {"currentPassword": password, "newPassword": BENCH_PASSWORD})
        print(f"admin password was one-time; set to {BENCH_PASSWORD!r}", flush=True)


def seed(client, api_name):
    """Table, fields and an API token, all through the admin API so this keeps
    working when storage shapes change underneath it."""
    for table in client.json("GET", f"{ADMIN}/tables"):
        if table.get("apiName") == api_name:
            client.request("DELETE", f"{ADMIN}/tables/{table['id']}")

    table = client.json("POST", f"{ADMIN}/tables", {"Name": f"Stress {api_name}", "ApiName": api_name})
    table_id = table["id"]
    for field in FIELDS:
        client.json("POST", f"{ADMIN}/tables/{table_id}/fields", field)
    client.json("PATCH", f"{ADMIN}/tables/{table_id}", {"apiEnabled": True, "apiName": api_name})

    accounts = client.json("GET", f"{ADMIN}/accounts")
    admin = next(a for a in accounts if a.get("role") == "admin")
    expiry = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 86400))
    issued = client.json("POST", f"{ADMIN}/accounts/{admin['id']}/token", {"expiresAt": expiry})
    client.json("PATCH", f"{ADMIN}/accounts/{admin['id']}", {"apiEnabled": True})

    token = issued.get("token") or issued.get("apiToken")
    if not token:
        sys.exit(f"No raw token in the issue response: {issued}")
    return table_id, token


def fill(db_path, table_id, rows):
    """Straight into SQLite. _records is four stable columns and the write
    path is not what this measures."""
    conn = sqlite3.connect(db_path)
    conn.execute("PRAGMA journal_mode=WAL")
    now = time.strftime("%Y-%m-%d %H:%M:%S")
    batch, ids = [], []
    for i in range(rows):
        rid = short_id()
        ids.append(rid)
        batch.append((rid, table_id, json.dumps({
            "reference": f"REF-{i:08d}",
            "customer": f"Customer {random.randint(1, 5000)}",
            "city": random.choice(CITIES),
            "status": random.choice(STATUSES),
            "amount": round(random.uniform(10, 5000), 2),
            "note": "lorem ipsum " * random.randint(1, 8),
        }), now))
        if len(batch) >= 5000:
            conn.executemany('INSERT INTO "_records" VALUES (?,?,?,?)', batch)
            batch.clear()
    if batch:
        conn.executemany('INSERT INTO "_records" VALUES (?,?,?,?)', batch)
    conn.commit()
    conn.close()
    return ids


def measure(base, token, cookie, scenario, requests, concurrency):
    """Each worker owns a connection and walks its share of the request list."""
    latencies, failures = [], 0

    def worker(chunk):
        client = Client(base)
        client.token, client.cookie = token, cookie
        local, bad = [], 0
        for method, path, body in chunk:
            start = time.perf_counter()
            try:
                status, _ = client.request(method, path, body)
                elapsed = (time.perf_counter() - start) * 1000
                if status >= 400:
                    bad += 1
                else:
                    local.append(elapsed)
            except Exception:
                bad += 1
        client.conn.close()
        return local, bad

    chunks = [requests[i::concurrency] for i in range(concurrency)]
    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        for local, bad in pool.map(worker, chunks):
            latencies.extend(local)
            failures += bad

    if not latencies:
        return {"scenario": scenario, "n": 0, "failed": failures}
    latencies.sort()
    return {
        "scenario": scenario,
        "n": len(latencies),
        "failed": failures,
        "p50": statistics.median(latencies),
        "p95": latencies[int(len(latencies) * 0.95) - 1],
        "p99": latencies[int(len(latencies) * 0.99) - 1],
        "max": latencies[-1],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:5000")
    parser.add_argument("--db", default="Source/Baseport/baseport.db")
    parser.add_argument("--rows", type=int, default=100_000)
    parser.add_argument("--requests", type=int, default=500)
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--api-name", default="stress")
    parser.add_argument("--password", required=True,
                        help="one-time admin password from the startup log, or the one already set")
    parser.add_argument("--label", default="baseline")
    parser.add_argument("--out", default="")
    args = parser.parse_args()

    if not os.path.exists(args.db):
        sys.exit(f"No database at {args.db}. Start the server once so it creates one.")

    client = Client(args.base)
    sign_in(client, args.password)
    table_id, token = seed(client, args.api_name)
    print(f"seeding {args.rows} rows...", flush=True)
    started = time.perf_counter()
    ids = fill(args.db, table_id, args.rows)
    print(f"seeded in {time.perf_counter() - started:.1f}s", flush=True)

    n, api = args.requests, f"/api/v1/{args.api_name}/records"
    sample = random.sample(ids, min(n, len(ids)))
    scenarios = {
        "point read": [("GET", f"{api}/{rid}", None) for rid in sample],
        "list page 1": [("GET", f"{api}?page=1&pageSize=25", None)] * n,
        "list deep page": [("GET", f"{api}?page={random.randint(1, max(1, args.rows // 25))}&pageSize=25", None) for _ in range(n)],
        "search (json_each)": [("GET", f"{api}?q={random.choice(CITIES)}", None) for _ in range(n)],
        "sort unindexed": [("GET", f"{api}?sort=amount&order=desc&pageSize=25", None)] * n,
        "create (unique check)": [("POST", api, {
            "reference": f"NEW-{short_id(10)}", "customer": "Load", "city": "Utrecht",
            "status": "new", "amount": 42.5, "note": "x",
        }) for _ in range(n)],
    }

    results = []
    for name, requests in scenarios.items():
        result = measure(args.base, token, client.cookie, name, requests, args.concurrency)
        results.append(result)
        if result["n"]:
            print(f"{name:<24} n={result['n']:<5} p50={result['p50']:7.2f}ms  "
                  f"p95={result['p95']:7.2f}ms  p99={result['p99']:7.2f}ms  "
                  f"max={result['max']:8.2f}ms  failed={result['failed']}")
        else:
            print(f"{name:<24} all {result['failed']} request(s) failed")

    if args.out:
        with open(args.out, "w") as handle:
            json.dump({"label": args.label, "rows": args.rows,
                       "concurrency": args.concurrency, "results": results}, handle, indent=2)
        print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
