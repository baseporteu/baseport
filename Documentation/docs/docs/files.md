---
title: Files and uploads
description: "File fields, the storage API and upload serving"
---

# Files and uploads

A `file` field renders a file input in forms and the console. Files are saved to `uploads/` next to the database; the field stores the file's URL.

## Buckets

A bucket is declared in **Settings > Host > Buckets** before the API accepts it. An undeclared or disabled bucket returns `404`.

| Setting | Effect |
| --- | --- |
| Name | URL segment: 1 to 32 characters of lower-case letters, digits and hyphens |
| Enabled | Off returns `404` and removes the bucket from the OpenAPI document |
| Methods | `GET`, `POST`, `DELETE`; checked together with the API token's methods, `405` with `Allow` otherwise |
| Accept end-user tokens | Off refuses end-user JWTs with `403`; operator API tokens are unaffected |
| File size limit | 1 to 25 MB per file, `413` above it |
| Accepted types | Comma separated MIME types, wildcards such as `image/*` allowed; empty accepts every allowed extension. `415` otherwise |

The type is derived from the file extension. Each enabled bucket is documented in the OpenAPI document under **Storage**.

## Uploading

```bash
curl -X POST http://localhost:5000/api/v1/files/invoices \
  -H "Authorization: Bearer $TOKEN" \
  -F file=@invoice.pdf
```

```json
{
  "id": "invoices/Kf3nQ8xR2vLmA9dTbW.pdf",
  "bucket": "invoices",
  "name": "Kf3nQ8xR2vLmA9dTbW.pdf",
  "url": "http://localhost:5000/uploads/invoices/Kf3nQ8xR2vLmA9dTbW.pdf",
  "size": 184320,
  "content_type": "application/pdf"
}
```

The returned `url` is the value for a `file` field.

| Route | Action |
| --- | --- |
| `POST /api/v1/files/{bucket}` | Upload one file as `multipart/form-data` |
| `GET /api/v1/files/{bucket}/{name}` | Read it back, with range requests |
| `DELETE /api/v1/files/{bucket}/{name}` | Delete it |

All three require a bearer token and a declared bucket. Files are stored in a folder per bucket under `uploads/`. Deleting a bucket leaves its files on disk and reachable at their URLs.

## Limits

| Limit | Value |
| --- | --- |
| File size | 25 MB, or lower per bucket |
| Extensions | `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.svg`, `.pdf`, `.txt`, `.csv`, `.json`, `.zip` |
| Instance total | **Upload storage (MB)** in **Settings > Host**, default 10,240 MB |
| Free disk | Uploads are refused below 1 GB free, `507` |
| `POST /api/v1/files/{bucket}` | 30 per minute per client |

Stored names are 22 random characters plus the original extension; the uploaded filename is not used. The instance total is approximate under concurrent uploads; the free-disk floor is the hard limit.

## Serving

`/uploads` is served as static files without authentication, so a stored URL works in any client.

:::warning
An upload is protected only by its unguessable name (22 characters, 132 bits). Anyone with the URL can share it. Confidential files do not belong here.
:::

## Unused files

The `file-deletions` job removes files in the `uploads/` root that no record refers to. Bucket folders are not swept. The job is off by default: a file not yet attached to a record is indistinguishable from an abandoned one.

References are matched on the `/uploads/` path segment in each record's JSON; a filename in a text field is not a reference. The job is scheduled under **Settings** with the other [jobs](/docs/going-to-production).
