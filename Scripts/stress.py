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
    {"Name": "reference", "DataType": "text", "IsUnique": True, "IsIdentifier": True, "IsRequired": True, "Position": 0},
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
        # a keep-alive connection idle past the server's timeout (e.g. across fill()'s
        # multi-minute direct-sqlite phase) dies silently, one reconnect-and-retry covers it
        for attempt in range(2):
            try:
                self.conn.request(method, path, payload, headers)
                response = self.conn.getresponse()
                data = response.read()
                break
            except (http.client.HTTPException, OSError):
                self.conn.close()
                self.conn = http.client.HTTPConnection(self.host, self.port, timeout=30)
                if attempt == 1:
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


def sign_in(client, username, password):
    """a seeded admin is penned in until the one-time password is replaced, so
    the harness replaces it before touching anything else"""
    client.json("POST", "/api/auth/login", {"username": username, "password": password})
    me = client.json("GET", "/api/auth/me")
    if me.get("mustChangePassword"):
        client.json("POST", "/api/auth/password",
                    {"currentPassword": password, "newPassword": BENCH_PASSWORD})
        print(f"admin password was one-time; set to {BENCH_PASSWORD!r}", flush=True)


def seed(client, api_name):
    """table, fields and an API token, all through the admin API so this keeps
    working when storage shapes change underneath it"""
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


def seed_form(client, table_id, match_field="reference"):
    """a lookup-only form for the anonymous form-lookup scenario, no bearer token needed"""
    for form in client.json("GET", f"{ADMIN}/forms") or []:
        if form.get("tableId") == table_id:
            client.request("DELETE", f"{ADMIN}/forms/{form['id']}")
    form = client.json("POST", f"{ADMIN}/forms", {
        "tableId": table_id,
        "kind": "form",
        "title": "Stress lookup",
        "actions": ["lookup"],
        "isPublished": True,
        "configJson": json.dumps({"matchFields": [match_field], "resultFields": [match_field]}),
    })
    return form["id"]


def make_account(client, role, methods=None):
    """new account, issues its own API token, optionally scopes it"""
    username = f"stress-{role}-{short_id(8)}"
    account = client.json("POST", f"{ADMIN}/accounts", {"username": username, "email": "", "role": role})
    expiry = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 86400))
    issued = client.json("POST", f"{ADMIN}/accounts/{account['id']}/token", {"expiresAt": expiry})
    if methods is not None:
        client.json("PATCH", f"{ADMIN}/accounts/{account['id']}", {"apiTokenMethods": methods})
    token = issued.get("token") or issued.get("apiToken")
    if not token:
        sys.exit(f"No raw token in the issue response: {issued}")
    return account["id"], token


def fill(db_path, table_id, rows):
    """straight into SQLite. _records is five stable columns (Id, TableId,
    JsonData, CreatedAt, UpdatedAt) and the write path is not what this
    measures"""
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
        }), now, now))
        if len(batch) >= 5000:
            conn.executemany('INSERT INTO "_records" VALUES (?,?,?,?,?)', batch)
            batch.clear()
    if batch:
        conn.executemany('INSERT INTO "_records" VALUES (?,?,?,?,?)', batch)
    conn.commit()
    conn.close()
    return ids


def measure(base, token, cookie, scenario, requests, concurrency, anonymous=False):
    """each worker owns a connection and walks its share of the request list"""
    latencies, failures, rejected = [], 0, 0

    def worker(chunk):
        client = Client(base)
        if not anonymous:
            client.token, client.cookie = token, cookie
        local, bad, limited = [], 0, 0
        for method, path, body in chunk:
            start = time.perf_counter()
            try:
                status, _ = client.request(method, path, body)
                elapsed = (time.perf_counter() - start) * 1000
                if status == 429:
                    limited += 1
                elif status >= 400:
                    bad += 1
                else:
                    local.append(elapsed)
            except Exception:
                bad += 1
        client.conn.close()
        return local, bad, limited

    chunks = [requests[i::concurrency] for i in range(concurrency)]
    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        for local, bad, limited in pool.map(worker, chunks):
            latencies.extend(local)
            failures += bad
            rejected += limited

    if not latencies:
        return {"scenario": scenario, "n": 0, "failed": failures, "rejected": rejected}
    latencies.sort()
    return {
        "scenario": scenario,
        "n": len(latencies),
        "failed": failures,
        "rejected": rejected,
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
    parser.add_argument("--username", default="admin",
                        help="admin account username, the seeded one-time account or one already renamed to admin")
    parser.add_argument("--password", required=True,
                        help="one-time admin password from the startup log, or the one already set")
    parser.add_argument("--label", default="baseline")
    parser.add_argument("--out", default="")
    args = parser.parse_args()

    if not os.path.exists(args.db):
        sys.exit(f"No database at {args.db}. Start the server once so it creates one.")

    client = Client(args.base)
    sign_in(client, args.username, args.password)
    table_id, token = seed(client, args.api_name)
    print(f"seeding {args.rows} rows...", flush=True)
    started = time.perf_counter()
    ids = fill(args.db, table_id, args.rows)
    print(f"seeded in {time.perf_counter() - started:.1f}s", flush=True)

    n, api = args.requests, f"/api/v1/{args.api_name}/records"
    sample = random.sample(ids, min(n, len(ids)))

    def new_record():
        return {"reference": f"NEW-{short_id(10)}", "customer": "Load", "city": "Utrecht",
                "status": "new", "amount": 42.5, "note": "x"}

    # (requests, concurrency, anonymous), last two scenarios fix their own concurrency instead of args.concurrency
    scenarios = {
        "point read": ([("GET", f"{api}/{rid}", None) for rid in sample], args.concurrency, False),
        "list page 1": ([("GET", f"{api}?page=1&pageSize=25", None)] * n, args.concurrency, False),
        "list deep page": ([("GET", f"{api}?page={random.randint(1, max(1, args.rows // 25))}&pageSize=25", None) for _ in range(n)], args.concurrency, False),
        "search (json_each)": ([("GET", f"{api}?q={random.choice(CITIES)}", None) for _ in range(n)], args.concurrency, False),
        "sort unindexed": ([("GET", f"{api}?sort=amount&order=desc&pageSize=25", None)] * n, args.concurrency, False),
        "create (unique check)": ([("POST", api, new_record()) for _ in range(n)], args.concurrency, False),
    }

    if ids:
        fpid = seed_form(client, table_id)
        lookup_n = max(n, 50)
        lookup_refs = [f"REF-{random.randrange(args.rows):08d}" for _ in range(lookup_n)]
        scenarios["anon lookup, 50 conns"] = (
            [("GET", f"/api/forms/{fpid}/form?q={ref}", None) for ref in lookup_refs], 50, True)

        mixed_n = max(n, 100)
        mixed_ids = random.choices(ids, k=mixed_n)
        mixed = [
            ("POST", api, new_record()) if i % 10 == 9 else ("GET", f"{api}/{rid}", None)
            for i, rid in enumerate(mixed_ids)
        ]
        random.shuffle(mixed)
        scenarios["mixed 90/10, 100 conns"] = (mixed, 100, False)

    tx_api = "/api/transaction/v1/execute"

    def tx_body(count, transactional):
        return {"operations": [{"op": "create", "apiName": args.api_name, "value": new_record()} for _ in range(count)],
                "transaction": transactional}

    tx_small_n = max(n // 10, 20)
    scenarios["transaction 10 ops, sequential"] = (
        [("POST", tx_api, tx_body(10, False)) for _ in range(tx_small_n)], args.concurrency, False)
    scenarios["transaction 10 ops, atomic"] = (
        [("POST", tx_api, tx_body(10, True)) for _ in range(tx_small_n)], args.concurrency, False)

    tx_big_n = max(n // 50, 10)
    scenarios["transaction 128 ops, atomic"] = (
        [("POST", tx_api, tx_body(128, True)) for _ in range(tx_big_n)], args.concurrency, False)

    results = []
    for name, (requests, concurrency, anonymous) in scenarios.items():
        result = measure(args.base, token, client.cookie, name, requests, concurrency, anonymous)
        result["concurrency"] = concurrency
        results.append(result)
        if result["n"]:
            print(f"{name:<24} n={result['n']:<5} p50={result['p50']:7.2f}ms  "
                  f"p95={result['p95']:7.2f}ms  p99={result['p99']:7.2f}ms  "
                  f"max={result['max']:8.2f}ms  failed={result['failed']}  rate-limited={result['rejected']}")
        else:
            print(f"{name:<24} all requests refused: {result['failed']} failed, {result['rejected']} rate-limited")

    transaction_contention_check(args.base, token, client.cookie, args.api_name)
    api_token_methods_check(args.base, client, args.api_name)
    user_role_rule_check(args.base, client, args.api_name)
    audit_attribution_check(args.base, client, args.api_name)

    if args.out:
        with open(args.out, "w") as handle:
            json.dump({"label": args.label, "rows": args.rows, "results": results}, handle, indent=2)
        print(f"wrote {args.out}")


def transaction_contention_check(base, token, cookie, api_name, contenders=20, ops_per_batch=5):
    """Not a latency scenario: correctness under real concurrency. `contenders`
    threads each submit an atomic (transaction: true) batch whose *first* op
    creates a record with the same unique `reference`. SQLite's unique index
    lets exactly one of those inserts land; every other batch must come back
    409 with none of its other ops applied. Prints PASS/FAIL rather than
    timings, and exits non-zero on failure so it can gate CI later."""
    shared_reference = f"RACE-{short_id(8)}"
    tag = short_id(6)

    def batch(i):
        ops = [{"op": "create", "apiName": api_name,
                "value": {"reference": shared_reference, "customer": f"contender-{i}", "city": "Utrecht",
                          "status": "new", "amount": 1, "note": "race"}}]
        for j in range(ops_per_batch - 1):
            ops.append({"op": "create", "apiName": api_name,
                        "value": {"reference": f"RACE-{tag}-{i}-{j}", "customer": f"contender-{i}",
                                  "city": "Utrecht", "status": "new", "amount": 1, "note": "race"}})
        return {"operations": ops, "transaction": True}

    def send(i):
        client = Client(base)
        client.token, client.cookie = token, cookie
        status, data = client.request("POST", "/api/transaction/v1/execute", batch(i))
        client.conn.close()
        return status, data

    with ThreadPoolExecutor(max_workers=contenders) as pool:
        outcomes = list(pool.map(send, range(contenders)))

    api = f"/api/v1/{api_name}/records"
    client = Client(base)
    client.token, client.cookie = token, cookie
    winners = [s for s, _ in outcomes if s < 300]
    losers = [s for s, _ in outcomes if s >= 300]
    won_ref = client.json("GET", f"{api}?q={shared_reference}")
    lost_refs = client.json("GET", f"{api}?q=RACE-{tag}")

    ok = (
        len(winners) == 1
        and all(s == 409 for s in losers)
        and won_ref["total"] == 1
        and lost_refs["total"] == ops_per_batch - 1  # only the winner's other ops
    )
    print(f"\ntransaction contention ({contenders} concurrent atomic batches racing one unique field):")
    print(f"  winners={len(winners)} (want 1)  loser statuses={sorted(set(losers))} (want [409])  "
          f"winning-ref rows={won_ref['total']} (want 1)  winner's-other-rows={lost_refs['total']} (want {ops_per_batch - 1})")
    print("  PASS: every losing batch rolled back cleanly, no partial writes, no 500s"
          if ok else "  FAIL: see counts above")
    if not ok:
        sys.exit(1)


def api_token_methods_check(base, admin_client, api_name, concurrency=20):
    """A GET-only key must never create, concurrently, even though the table itself
    allows every verb. RecordTransactions.ApplyAsync runs the same check independently
    of MethodGate, so both call sites get exercised here."""
    _, read_only_token = make_account(admin_client, "consumer", methods=["GET"])
    _, full_token = make_account(admin_client, "consumer")
    api = f"/api/v1/{api_name}/records"

    def create_with(token):
        client = Client(base)
        client.token = token
        status, _ = client.request("POST", api, {"reference": f"ROK-{short_id(8)}", "customer": "x",
                                                   "city": "Utrecht", "status": "new", "amount": 1, "note": "x"})
        client.conn.close()
        return status

    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        blocked = list(pool.map(create_with, [read_only_token] * concurrency))

    allowed_status = create_with(full_token)

    tx_client = Client(base)
    tx_client.token = read_only_token
    tx_status, _ = tx_client.request("POST", "/api/transaction/v1/execute", {
        "operations": [{"op": "create", "apiName": api_name, "value": {"reference": f"ROKTX-{short_id(8)}"}}],
        "transaction": False,
    })
    tx_client.conn.close()

    read_client = Client(base)
    read_client.token = read_only_token
    read_status, _ = read_client.request("GET", api)
    read_client.conn.close()

    ok = all(s == 405 for s in blocked) and allowed_status < 300 and tx_status == 405 and read_status < 300
    print(f"\napi token methods ({concurrency} concurrent POSTs with a GET-only key):")
    print(f"  blocked statuses={sorted(set(blocked))} (want [405])  full-access-key create={allowed_status} (want 2xx)  "
          f"transaction endpoint, same key={tx_status} (want 405)  GET, same key={read_status} (want 2xx)")
    print("  PASS: per-key method scope holds under concurrency, in MethodGate and the transaction endpoint"
          if ok else "  FAIL: see statuses above")
    if not ok:
        sys.exit(1)


def user_role_rule_check(base, admin_client, api_name, concurrency=20):
    """_USER_.role in a CreateRule must hold under concurrent callers of both roles."""
    table = next(t for t in admin_client.json("GET", f"{ADMIN}/tables") if t.get("apiName") == api_name)
    original_rule = table.get("createRule", "")
    admin_client.json("PATCH", f"{ADMIN}/tables/{table['id']}", {"createRule": "_USER_.role = 'consumer'"})

    _, consumer_token = make_account(admin_client, "consumer")
    _, user_token = make_account(admin_client, "user")
    api = f"/api/v1/{api_name}/records"

    def create_with(token):
        client = Client(base)
        client.token = token
        status, _ = client.request("POST", api, {"reference": f"ROLE-{short_id(8)}", "customer": "x",
                                                   "city": "Utrecht", "status": "new", "amount": 1, "note": "x"})
        client.conn.close()
        return status

    tokens = [consumer_token] * concurrency + [user_token] * concurrency
    with ThreadPoolExecutor(max_workers=concurrency * 2) as pool:
        statuses = list(pool.map(create_with, tokens))

    admin_client.json("PATCH", f"{ADMIN}/tables/{table['id']}", {"createRule": original_rule})

    consumer_statuses, user_statuses = statuses[:concurrency], statuses[concurrency:]
    ok = all(s < 300 for s in consumer_statuses) and all(s == 403 for s in user_statuses)
    print(f"\n_USER_.role rule ({concurrency} consumer + {concurrency} user callers, CreateRule: _USER_.role = 'consumer'):")
    print(f"  consumer statuses={sorted(set(consumer_statuses))} (want 2xx)  user statuses={sorted(set(user_statuses))} (want [403])")
    print("  PASS: role-based rule holds under concurrent mixed-role load, no 500s"
          if ok else "  FAIL: see statuses above")
    if not ok:
        sys.exit(1)


def audit_attribution_check(base, admin_client, api_name, concurrency=20):
    """Two API keys fire concurrent, interleaved writes against the same table.
    ctx.Items is per-request, so there is nothing to lock, but that is exactly
    the claim under test: prove no cross-request bleed under real concurrency,
    not just read the code. Also checks admin console GETs now attribute, and
    that ClientIp landed."""
    audit_api = f"{api_name}-audit-{short_id(6).lower()}"
    table = admin_client.json("POST", f"{ADMIN}/tables", {"Name": f"Stress {audit_api}", "ApiName": audit_api})
    for field in FIELDS:
        admin_client.json("POST", f"{ADMIN}/tables/{table['id']}/fields", field)
    admin_client.json("PATCH", f"{ADMIN}/tables/{table['id']}", {"apiEnabled": True, "apiName": audit_api})

    acct_a, token_a = make_account(admin_client, "consumer")
    acct_b, token_b = make_account(admin_client, "consumer")
    api = f"/api/v1/{audit_api}/records"

    def create_with(token):
        client = Client(base)
        client.token = token
        status, _ = client.request("POST", api, {"reference": f"AUD-{short_id(8)}", "customer": "x",
                                                   "city": "Utrecht", "status": "new", "amount": 1, "note": "x"})
        client.conn.close()
        return status

    tokens = [token_a] * concurrency + [token_b] * concurrency
    with ThreadPoolExecutor(max_workers=concurrency * 2) as pool:
        statuses = list(pool.map(create_with, tokens))

    # a few admin console reads too, now that they are meant to attribute
    for _ in range(3):
        admin_client.request("GET", f"{ADMIN}/tables")

    time.sleep(0.3)  # writer batches on a channel; give it one drain cycle

    logs = admin_client.json("GET", f"{ADMIN}/logs?page=1&perPage=200&filter={audit_api}")["logs"]
    write_rows = [l for l in logs if l["method"] == "POST"]
    by_a = sum(1 for l in write_rows if l["userId"] == acct_a)
    by_b = sum(1 for l in write_rows if l["userId"] == acct_b)
    blank_or_other = [l["userId"] for l in write_rows if l["userId"] not in (acct_a, acct_b)]
    ips = {l["clientIp"] for l in write_rows}

    console_logs = admin_client.json("GET", f"{ADMIN}/logs?page=1&perPage=50&filter=_admin/tables")["logs"]
    console_gets = [l for l in console_logs if l["method"] == "GET"]

    ok = (
        all(s < 300 for s in statuses)
        and by_a == concurrency and by_b == concurrency
        and len(blank_or_other) == 0
        and ips == {"127.0.0.1"}
        and len(console_gets) > 0
        and all(l["userId"] for l in console_gets)
    )
    print(f"\naudit attribution ({concurrency} + {concurrency} concurrent interleaved writers, two API keys):")
    print(f"  writes by key A={by_a} (want {concurrency})  writes by key B={by_b} (want {concurrency})  "
          f"unattributed/wrong={len(blank_or_other)} (want 0)  client ips seen={ips}  "
          f"console GETs logged={len(console_gets)} (want >0), all attributed={all(l['userId'] for l in console_gets)}")
    print("  PASS: every write attributed to the key that made it, no cross-request bleed, console reads now audited"
          if ok else "  FAIL: see counts above")
    if not ok:
        sys.exit(1)


if __name__ == "__main__":
    main()
