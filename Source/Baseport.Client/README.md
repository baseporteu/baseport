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
var storage = client.Storage;
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
