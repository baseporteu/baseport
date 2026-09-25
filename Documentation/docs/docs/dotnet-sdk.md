---
title: .NET SDK
description: "Typed .NET client for records, storage, auth and live updates"
---

# .NET SDK

`Baseport.Client` targets `net8.0` (MAUI, WPF, Avalonia and ASP.NET Core hosts) and wraps the `/api/v1` routes.

```bash
dotnet add package Baseport.Client
```

## Registration

```csharp
services.AddBaseport(o =>
{
    o.BaseUrl = "http://localhost:5000";
    o.ApiToken = builder.Configuration["Baseport:ApiToken"];
});
```

Registers `IBaseportClient` as a singleton over `IHttpClientFactory`. For end-user sessions, `ApiToken` stays unset and `LoginAsync` signs in; the client refreshes the token one minute before expiry.

## Records

`Records()` takes the table's API name.

```csharp
public sealed record SalesOrder(string OrderNo, decimal Total);

var orders = client.Records("sales-orders");

var id = await orders.CreateAsync(new { OrderNo = "SO-100001", Total = 88.4m });

var page = await orders.ListAsync(query: "SO-1000", pageSize: 25);
foreach (var row in page.Rows)
    Console.WriteLine(row.As<SalesOrder>()?.OrderNo);

await orders.UpdateAsync(id, new { Total = 91.0m });
await orders.DeleteAsync(id);
```

`As<T>()` deserializes the record's `data`.

## Full-table walks

```csharp
await foreach (var row in orders.WalkAsync(pageSize: 200, cancellationToken: ct))
    Process(row.As<SalesOrder>());
```

`WalkAsync` uses keyset paging: constant cost per page, unaffected by concurrent inserts. Numbered pages use `OFFSET`, whose cost grows with the page number.

## Optimistic concurrency

Records carry an `ETag`. A write with the `ETag` is refused when the record changed since it was read:

```csharp
var order = await orders.ReadAsync(id);

try
{
    await orders.UpdateAsync(id, new { Total = 91.0m }, order.ETag);
}
catch (BaseportException e) when (e.IsPreconditionFailure)
{
    // re-read and merge
}
```

The `ETag` is optional. A write that loses a race fails with `e.IsConflict` regardless, through the server's concurrency token; the `ETag` reports the conflict before the write.

## Errors

Failures throw `BaseportException`, exposing the problem document through `Detail`, `InvalidFields` and predicates:

```csharp
catch (BaseportException e) when (e.IsValidationFailure)
{
    // e.InvalidFields lists rejected fields
}
catch (BaseportException e) when (e.IsConflict)
{
    // duplicate unique value, or a lost race
}
```

A conflict can succeed on retry with a different value; a validation failure cannot.

## Live updates

```csharp
await foreach (var change in orders.SubscribeAsync(ct))
    Console.WriteLine($"{change.Action} {change.Id}");
```

A record id limits the stream to one record. Read rules filter events as they filter reads.

## Files

```csharp
var stored = await client.Storage
    .Bucket("avatars")
    .UploadAsync("photo.png", "image/png", stream, ct);
```

`stored.Url` is the public file URL. See [Files and uploads](/docs/files).

## Token verification only

A service that only verifies Baseport JWTs does not need this package: `AddJwtBearer` with `/api/auth/v1/jwks.json` and the issuer and audience from **Settings > Authentication**.
