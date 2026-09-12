# Baseport TypeScript Types

Since every Baseport instance has custom tables, there is no generic TypeScript client package. Instead, you use standard `fetch` and generate types specific to your schema.

## Generate Types

1. Enable **Settings > API > Publish OpenAPI document** on your running instance.
2. Run the generator script:

```bash
node Scripts/generate-ts-types.js --baseport-url http://localhost:5000
```

This creates a local `types.d.ts` file describing your tables. Re-run this command whenever your schema changes.

## Usage

API responses wrap your table fields in a `data` object alongside record metadata (`id`, `createdAt`, etc.). The generated types strictly define this `data` object, guaranteeing TypeScript catches any mismatched fields if your schema changes.

```ts
import type { paths, components } from './Source/Baseport.Client.TS/types';

type Order = components['schemas']['Orders'];

async function listOrders(token: string) {
  const res = await fetch('/api/v1/orders/records', {
    headers: { Authorization: `Bearer ${token}` },
  });
  
  const body: paths['/api/v1/orders/records']['get']['responses']['200']['content']['application/json'] = await res.json();
  
  return (body.rows ?? []).map((row) => row.data as Order);
}
```