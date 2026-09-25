#!/usr/bin/env node

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
