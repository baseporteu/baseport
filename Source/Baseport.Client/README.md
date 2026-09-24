# Baseport.Client

Typed .NET client for the Baseport REST, auth, storage and realtime APIs.

## Install

```
dotnet add package Baseport.Client
```

## Usage

```csharp
using Baseport.Client;

var client = new BaseportClient("https://your-baseport-site.example");
await client.LoginAsync("user@example.com", "password");

var records = client.Records("orders");
var open = await records.ListAsync(filter: new Dictionary<string, string> { ["status"] = "open" });
var storage = client.Storage;

var ids = await client.ExecuteAsync(
[
    RecordOperation.Create("orders", new { status = "open" }),
    RecordOperation.Delete("orders", "abc123def456")
], transaction: true);
```

### With dependency injection

```csharp
services.AddBaseport(options =>
{
    options.BaseUrl = "https://your-baseport-site.example";
    options.ApiToken = "..."; // optional, for server-to-server calls
});
```

## Links

- Source: https://github.com/baseporteu/baseport
- License: EUPL-1.2
