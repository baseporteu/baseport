"""A demo "legacy ERP" to point a Baseport proxy table at: run it standalone and
give Baseport its /openapi.json as the spec URL. Two resources speak plain REST
(one with no query capability at all, one that also accepts $filter/$top), two
speak OData v4. Baseport's proxy importer reads the generated spec generically --
nothing here is Baseport-specific, and nothing in Baseport needed to change to
onboard it.

Run:
    pip install -r requirements.txt
    cp .env.example .env      # optional, every value has a default
    uvicorn app.main:app --reload --port 8100 --app-dir Scripts/demo-api
"""

from uuid import uuid4

from fastapi import Depends, FastAPI, Header, Query, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from . import db
from .config import settings
from .odata import apply_filter, apply_orderby, apply_select, apply_top_skip, odata_error

app = FastAPI(
    title="Legacy ERP (demo)",
    description="A mixed REST/OData API standing in for a real production system, for exercising Baseport proxy tables.",
    version="1.0.0",
)


@app.on_event("startup")
def _startup() -> None:
    db.init_and_seed()


class ApiError(Exception):
    def __init__(self, status_code: int, message: str, code: str = "Error"):
        self.status_code = status_code
        self.message = message
        self.code = code


@app.exception_handler(ApiError)
async def _api_error(request: Request, exc: ApiError) -> JSONResponse:
    # Same auth check, two conventions: OData wraps errors, plain REST uses FastAPI's own {"detail": ...} shape.
    body = odata_error(exc.code, exc.message) if request.url.path.startswith("/odata/") else {"detail": exc.message}
    return JSONResponse(status_code=exc.status_code, content=body)


def require_auth(authorization: str | None = Header(None)) -> None:
    expected = f"Bearer {settings.api_token}"
    if authorization != expected:
        raise ApiError(401, "A valid bearer token is required.", "Unauthorized")


def next_id(prefix: str) -> str:
    return f"{prefix}-{uuid4().hex[:10]}"


# ---------- REST models ----------

class VendorOut(BaseModel):
    id: str
    name: str
    country: str
    email: str
    active: bool
    created_at: str


class VendorCreate(BaseModel):
    name: str
    country: str
    email: str
    active: bool = True


class PartOut(BaseModel):
    id: str
    sku: str
    description: str
    category: str
    unit_cost: float
    in_stock: int
    created_at: str


class PartCreate(BaseModel):
    sku: str
    description: str
    category: str
    unit_cost: float
    in_stock: int = 0


# ---------- OData models ----------

class PurchaseOrderOut(BaseModel):
    id: str
    vendor_id: str
    order_date: str
    status: str
    total: float
    created_at: str


class PurchaseOrderCreate(BaseModel):
    vendor_id: str
    order_date: str
    status: str = "draft"
    total: float = 0


class PurchaseOrderLineOut(BaseModel):
    id: str
    purchase_order_id: str
    part_id: str
    quantity: int
    unit_cost: float
    line_total: float
    created_at: str


class PurchaseOrderLineCreate(BaseModel):
    purchase_order_id: str
    part_id: str
    quantity: int
    unit_cost: float


# ---------- REST: vendors (deliberately no query capability -- some real APIs just return everything) ----------

VENDOR_LIST_CAP = 500


@app.get("/api/v1/vendors", response_model=dict, tags=["vendors"])
def list_vendors(_: None = Depends(require_auth)):
    rows = db.fetch_all("vendors")[:VENDOR_LIST_CAP]
    return {"items": rows, "total": len(rows)}


@app.get("/api/v1/vendors/{vendor_id}", response_model=VendorOut, tags=["vendors"])
def get_vendor(vendor_id: str, _: None = Depends(require_auth)):
    row = db.fetch_one("vendors", vendor_id)
    if row is None:
        raise ApiError(404, f"No vendor '{vendor_id}'.", "NotFound")
    return row


@app.post("/api/v1/vendors", response_model=VendorOut, status_code=201, tags=["vendors"])
def create_vendor(body: VendorCreate, _: None = Depends(require_auth)):
    row = {"id": next_id("VEN"), "created_at": db._stamp(db.START), **body.model_dump()}
    row["active"] = int(row["active"])
    db.insert("vendors", row)
    row["active"] = bool(row["active"])
    return row


# ---------- REST: parts (also accepts $filter/$top -- a REST API that borrowed OData's query dialect) ----------

@app.get("/api/v1/parts", response_model=dict, tags=["parts"])
def list_parts(
    _: None = Depends(require_auth),
    filter_: str | None = Query(None, alias="$filter"),
    top: int | None = Query(None, alias="$top", ge=1, le=1000),
):
    rows = apply_filter(db.fetch_all("parts"), filter_)
    total = len(rows)
    if top is not None:
        rows = rows[:top]
    return {"items": rows, "total": total}


@app.get("/api/v1/parts/{part_id}", response_model=PartOut, tags=["parts"])
def get_part(part_id: str, _: None = Depends(require_auth)):
    row = db.fetch_one("parts", part_id)
    if row is None:
        raise ApiError(404, f"No part '{part_id}'.", "NotFound")
    return row


@app.post("/api/v1/parts", response_model=PartOut, status_code=201, tags=["parts"])
def create_part(body: PartCreate, _: None = Depends(require_auth)):
    row = {"id": next_id("PRT"), "created_at": db._stamp(db.START), **body.model_dump()}
    db.insert("parts", row)
    return row


# ---------- OData: purchase orders and their lines ----------

def _odata_list(table: str, filter_, top, skip, select, orderby, count) -> dict:
    rows = apply_filter(db.fetch_all(table), filter_)
    rows = apply_orderby(rows, orderby)
    total = len(rows)
    rows = apply_top_skip(rows, top, skip)
    rows = apply_select(rows, select)
    body: dict = {"value": rows}
    if count:
        body["@odata.count"] = total
    return body


@app.get("/odata/PurchaseOrders", response_model=dict, tags=["purchase-orders"])
def list_purchase_orders(
    _: None = Depends(require_auth),
    filter_: str | None = Query(None, alias="$filter"),
    top: int | None = Query(None, alias="$top", ge=1, le=1000),
    skip: int | None = Query(None, alias="$skip", ge=0),
    select: str | None = Query(None, alias="$select"),
    orderby: str | None = Query(None, alias="$orderby"),
    count: bool = Query(False, alias="$count"),
):
    return _odata_list("purchase_orders", filter_, top, skip, select, orderby, count)


@app.get("/odata/PurchaseOrders/{order_id}", response_model=PurchaseOrderOut, tags=["purchase-orders"])
def get_purchase_order(order_id: str, _: None = Depends(require_auth)):
    row = db.fetch_one("purchase_orders", order_id)
    if row is None:
        raise ApiError(404, f"No purchase order '{order_id}'.", "NotFound")
    return row


@app.post("/odata/PurchaseOrders", response_model=PurchaseOrderOut, status_code=201, tags=["purchase-orders"])
def create_purchase_order(body: PurchaseOrderCreate, _: None = Depends(require_auth)):
    row = {"id": next_id("PO"), "created_at": db._stamp(db.START), **body.model_dump()}
    db.insert("purchase_orders", row)
    return row


@app.get("/odata/PurchaseOrderLines", response_model=dict, tags=["purchase-order-lines"])
def list_purchase_order_lines(
    _: None = Depends(require_auth),
    filter_: str | None = Query(None, alias="$filter"),
    top: int | None = Query(None, alias="$top", ge=1, le=1000),
    skip: int | None = Query(None, alias="$skip", ge=0),
    select: str | None = Query(None, alias="$select"),
    orderby: str | None = Query(None, alias="$orderby"),
    count: bool = Query(False, alias="$count"),
):
    return _odata_list("purchase_order_lines", filter_, top, skip, select, orderby, count)


@app.get("/odata/PurchaseOrderLines/{line_id}", response_model=PurchaseOrderLineOut, tags=["purchase-order-lines"])
def get_purchase_order_line(line_id: str, _: None = Depends(require_auth)):
    row = db.fetch_one("purchase_order_lines", line_id)
    if row is None:
        raise ApiError(404, f"No purchase order line '{line_id}'.", "NotFound")
    return row


@app.post("/odata/PurchaseOrderLines", response_model=PurchaseOrderLineOut, status_code=201, tags=["purchase-order-lines"])
def create_purchase_order_line(body: PurchaseOrderLineCreate, _: None = Depends(require_auth)):
    row = {
        "id": next_id("POL"), "created_at": db._stamp(db.START),
        "line_total": round(body.quantity * body.unit_cost, 2), **body.model_dump(),
    }
    db.insert("purchase_order_lines", row)
    return row


if __name__ == "__main__":
    import uvicorn

    uvicorn.run("app.main:app", host=settings.host, port=settings.port, reload=False)
