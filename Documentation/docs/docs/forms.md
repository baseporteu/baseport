---
title: Forms and embeds
description: "Public pages and script-tag embeds over a table"
---

# Forms and embeds

A form is a public surface over a table. Forms are managed under **Forms**, separately from tables, and served from `/api/forms/{formId}`.

The console generates an embed tag for each form:

```html
<script src="https://baseport.example.com/embed.js?id=Kf3nQ8xR2vLm"></script>
```

The form renders at the tag's position.

## Kinds

A form has one of two kinds, fixed at creation:

- **form**: submits or looks up one record. Actions: `submit`, `lookup`, or both.
- **list**: a paged, searchable table over the configured columns.

**Read only** renders values instead of inputs and refuses writes. An unpublished form returns `404`.

## The hosted page

Every form also has a server-rendered page at `/f/{formId}`, for links and search engines:

```
https://baseport.example.com/f/Kf3nQ8xR2vLm
```

For a list, the HTML contains the first page of rows and works without JavaScript. The embed replaces it with the interactive version once loaded.

The server-rendered page uses the same column projection as the JSON route. Custom renderers and row actions are JavaScript expressions and are omitted.

For a submit form, the page contains the heading and description only.

## Styling

The embed exposes CSS variables on `.baserow-embed`. Its styles are added once per page; overrides need higher specificity.

## Allowed sites

**Settings > Sites** lists the origins allowed to embed forms, one per line:

```
https://shop.example.com
https://portal.example.org
```

An empty list allows every origin. The list drives the `embed` CORS policy and `frame-ancestors`, so browsers block embeds from other origins. It is a browser policy, not access control.

## Rate limits

Public form routes are rate limited per client and per form.

| Route | Per minute |
| --- | --- |
| Submit | 20 |
| Lookup | 10 |
| List and `/f/{formId}` | 60 |
| Schema | 60 |

Behind a reverse proxy, `Baseport__TrustForwardedHeaders=true` is required; without it every visitor shares one budget. See [Going to production](/docs/going-to-production).
