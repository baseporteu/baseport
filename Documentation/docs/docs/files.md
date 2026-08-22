---
title: Files and uploads
description: "The file field, the storage API and how uploads are served"
---

# Files and uploads

Add a `file` field to a table and you get a file input in forms and in the console. The file is saved to `uploads/` next to the database, and the field stores its URL.

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

Save that `url` into a `file` field on a record.

| Route | What it does |
| --- | --- |
| `POST /api/v1/files/{bucket}` | Upload one file as `multipart/form-data` |
| `GET /api/v1/files/{bucket}/{name}` | Read it back, with range requests |
| `DELETE /api/v1/files/{bucket}/{name}` | Delete it |

All three need a bearer token. A bucket is a folder under `uploads/`, and its name is 1 to 32 characters of lower-case letters, digits and hyphens.

## Limits

- 25 MB per file
- allowed extensions: `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.svg`, `.pdf`, `.txt`, `.csv`, `.json`, `.zip`
- files are stored under 22 random characters plus the original extension, never the filename that was uploaded

## How uploads are served

`/uploads` is served as static files with no authentication. A `file` field stores an absolute URL, the file has to be fetchable without a session or a token, the same as any other URL you would put in that field.

:::warning
The only thing protecting an upload is that nobody can guess its name. Twenty-two characters is 132 bits, which is enough, but whoever has the URL can pass it on. Do not put files here that would be a problem in the wrong hands.
:::

## Deleting unused files

The `file-deletions` job removes uploads that no record refers to any more. It is **off by default**, because a file you have uploaded but not yet attached to a record looks the same as an abandoned one. Turn it on under **Settings** once you are confident your code attaches files promptly.
