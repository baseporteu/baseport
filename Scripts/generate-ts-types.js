#!/usr/bin/env node
/* Typed access to a Baseport instance's own tables, from TypeScript.

   There is no fixed Baseport API surface to publish a client for: every
   instance's /api/v1 describes whatever tables that instance's author
   defined, which is why the .NET SDK (Source/Baseport.Client) is hand-written
   but this side is generated instead. Run this against your own running
   instance whenever its schema changes; it is developer tooling, not
   something the binary ships or runs itself.

   Requires network access to fetch openapi-typescript through npx (nothing
   is installed into node_modules or committed as a dependency), and a
   running Baseport instance with Settings > API > "Publish OpenAPI document"
   turned on.

   Usage:
       node Scripts/generate-ts-types.js
       node Scripts/generate-ts-types.js --baseport-url http://localhost:5000 --out Source/Baseport.Client.TS/types.d.ts
*/

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

function parseArgs(argv) {
    const args = { baseportUrl: 'http://localhost:5000', out: 'Source/Baseport.Client.TS/types.d.ts' };
    for (let i = 0; i < argv.length; i++) {
        if (argv[i] === '--baseport-url') args.baseportUrl = argv[++i];
        else if (argv[i] === '--out') args.out = argv[++i];
    }
    return args;
}

async function main() {
    const args = parseArgs(process.argv.slice(2));
    const root = path.join(__dirname, '..');
    const outPath = path.isAbsolute(args.out) ? args.out : path.join(root, args.out);
    const url = `${args.baseportUrl.replace(/\/$/, '')}/api/openapi.json`;

    console.log(`[generate-ts-types] fetching ${url}`);
    let response;
    try {
        response = await fetch(url);
    } catch (e) {
        console.error(`Could not reach ${args.baseportUrl}. Is the instance running?`);
        console.error(e.message);
        process.exitCode = 1;
        return;
    }
    if (response.status === 404) {
        console.error('Got 404. Settings > API > "Publish OpenAPI document" is off on this instance.');
        process.exitCode = 1;
        return;
    }
    if (!response.ok) {
        console.error(`Baseport returned ${response.status} for ${url}`);
        process.exitCode = 1;
        return;
    }

    const document = await response.text();
    const tmpFile = path.join(os.tmpdir(), `baseport-openapi-${process.pid}.json`);
    fs.writeFileSync(tmpFile, document);

    fs.mkdirSync(path.dirname(outPath), { recursive: true });
    console.log(`[generate-ts-types] generating ${path.relative(root, outPath)}`);
    const result = spawnSync('npx', ['--yes', 'openapi-typescript', tmpFile, '-o', outPath], {
        stdio: 'inherit',
    });
    fs.rmSync(tmpFile, { force: true });

    if (result.status !== 0) {
        process.exitCode = result.status ?? 1;
        return;
    }
    console.log('[generate-ts-types] done');
}

main();
