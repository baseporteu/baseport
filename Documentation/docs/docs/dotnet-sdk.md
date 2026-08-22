---
title: .NET SDK
description: "Reading and writing records from a .NET app without hand-rolling HTTP"
---

# .NET SDK

`Baseport.Client` targets `net8.0`, so MAUI, WPF, Avalonia and ASP.NET Core hosts can all take it. It wraps the same `/api/v1` routes you would otherwise call with `HttpClient`.

```bash
dotnet add package Baseport.Client
```

## Register it

```csharp
services.AddBaseport(o =>
{
    o.BaseUrl = "http://localhost:5000";
    o.ApiToken = builder.Configuration["Baseport:ApiToken"];
});
```

`IBaseportClient` is registered as a singleton over `IHttpClientFactory`. If your callers sign in as end users rather than sharing a static token, leave `ApiToken` unset and call `LoginAsync`. The client refreshes its own token a minute before expiry.

## Records

`Records()` takes the table's published API name, not its internal one.

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

`As<T>()` deserializes the record's `data` into your own type.

## Reading a whole table

```csharp
await foreach (var row in orders.WalkAsync(pageSize: 200, cancellationToken: ct))
    Process(row.As<SalesOrder>());
```

Use this instead of looping `page: 1, 2, 3`. Page numbers count rows to skip, so they get slower the further in you go, and a row written while you page shifts everything after it. `WalkAsync` resumes from where the last page stopped, so neither happens.

## Not overwriting someone else's change

Records come back with an `ETag`. Pass it to a write and the write is refused if the record moved on since you read it:

```csharp
var order = await orders.ReadAsync(id);

try
{
    await orders.UpdateAsync(id, new { Total = 91.0m }, order.ETag);
}
catch (BaseportException e) when (e.IsPreconditionFailure)
{
    // Someone wrote first. Re-read and decide what to do with their version.
}
```

You can leave the `ETag` off and write unconditionally. A write that loses a race is still refused (`e.IsConflict`), so you cannot silently drop another writer's change either way. The `ETag` is what lets you find out before the write rather than after.

## Errors

Failures throw `BaseportException`, which exposes the server's problem document through `Detail`, `InvalidFields` and a few predicates:

```csharp
catch (BaseportException e) when (e.IsValidationFailure)
{
    // e.InvalidFields names the fields that were rejected
}
catch (BaseportException e) when (e.IsConflict)
{
    // A value that must be unique is already used, or a write lost a race
}
```

Worth splitting the two: a conflict may be worth retrying with a different value, a validation failure is not.

## Live updates

```csharp
await foreach (var change in orders.SubscribeAsync(ct))
    Console.WriteLine($"{change.Action} {change.Id}");
```

Pass a record id to watch one row instead of the table. Access rules apply to a stream the same way they apply to a read.

## Files

```csharp
var stored = await client.Storage
    .Bucket("avatars")
    .UploadAsync("photo.png", "image/png", stream, ct);
```

`stored.Url` is the address to hand back to a browser. See [Files and uploads](/docs/files).

## If you only need to verify tokens

A service that just checks Baseport JWTs does not need this package. Point `AddJwtBearer` at `/api/auth/v1/jwks.json` with the issuer and audience from **Settings › Authentication**.
