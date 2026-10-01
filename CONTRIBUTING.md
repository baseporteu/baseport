# Contributing

Thanks for helping out. Keep pull requests small and focused, reuse patterns already in the codebase instead of inventing new ones, and avoid adding dependencies unless they're really needed.

## Setup

The SDK is pinned in `global.json` and it's a preview, so distro packages won't have it. This installs the exact version side by side with whatever you already have:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --jsonfile global.json
```

```bash
cd Source
dotnet run --project Baseport --urls http://localhost:5000
```

Your one-time admin password is printed to the console on first start. Use it to seed some demo data:

```bash
./POPULATE.sh
```

That is 294,000 rows and takes about twenty seconds. Put `SCALE=0.05` in front for 15,000 rows in about a second.

To try the embed forms on a couple of mock sites:

```bash
python3 Scripts/bootstrap-sites.py
```

## Building

The published release is one self-contained file per platform: `wwwroot`, `appsettings.json` and the .NET runtime all travel inside it. To build the same thing the release workflow does:

```bash
cd Source
dotnet publish Baseport/Baseport.csproj -c Release -r linux-x64 -o out
```

Swap `-r linux-x64` for `-r win-x64` for the Windows build. Running it from a checkout does not need the publish step at all, see Setup above.

## Dependencies

Package versions live in `Source/Directory.Packages.props`, and every project commits a `packages.lock.json`. CI restores with `--locked-mode`, so a changed version without an updated lock file fails the build. After changing a version:

```bash
cd Source
dotnet restore Baseport.slnx --force-evaluate
```

Commit the lock files with the version change.

## Cutting a release

1. Cut an upgrade fixture from the previous tag, so the new release is tested against a database that release created:

   ```bash
   Scripts/make-fixture.sh v0.0.1-alpha.24
   ```

   It writes `<tag>.db` and `<tag>.expected.json` to `Source/Baseport.Tests/Fixtures/upgrades/`; `UpgradeFixtureTests` runs every fixture there.
2. Run the release workflow with the new tag.

## Documentation

The docs site lives in `Documentation/` and is built by Bark. See [Documentation/README.md](Documentation/README.md) for how to run it locally and where each page goes.

## Before you open a pull request

```bash
cd Source
dotnet build Baseport.slnx
dotnet test Baseport.slnx
node ../Scripts/test-frontend.js
cd .. && npx prettier@3 --check "Source/Baseport/wwwroot/**/*.js" "Scripts/*.js"
```

JavaScript is formatted with Prettier using `.prettierrc.json`; `--write` instead of `--check` applies it. Template literals are left as written (`embeddedLanguageFormatting: off`), because the embed's `html` templates are markup.

Build must be warning-free, both test suites must be green.
