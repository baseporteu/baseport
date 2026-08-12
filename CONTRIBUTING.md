# Contributing

Thanks for helping out. Keep pull requests small and focused, reuse patterns already in the codebase instead of inventing new ones, and avoid adding dependencies unless they're really needed.

## Setup

```bash
cd Source
dotnet run --project Baseport --urls http://localhost:5263
```

Your one-time admin password is printed to the console on first start. Use it to seed some demo data:

```bash
ADMIN_PASSWORD=abc ./POPULATE.sh
```

To try the embed forms on a couple of mock sites:

```bash
python3 Scripts/bootstrap-sites.py
```

## Before you open a pull request

```bash
dotnet build Baseport.slnx
dotnet test Baseport.slnx
node Scripts/test-frontend.js
```

Build must be warning-free, both test suites must be green.
