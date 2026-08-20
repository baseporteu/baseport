---
title: Forms and embeds
description: "Publish a table as a public page or a script tag, without building a front end"
---

# Forms and embeds

A form is a public page built on top of a table. Forms are managed separately from tables, under **Forms** in the console, and served from `/api/forms/{formId}`.

Point a form at a table and the console gives you a line to paste:

```html
<script src="https://baseport.example.com/embed.js?id=Kf3nQ8xR2vLm"></script>
```

The form appears wherever you put the tag.

## Form or list

A form is either a **form** or a **list**.

- A **form** collects or looks up one record. Set its action to `submit` or `lookup`, or turn both on.
- A **list** shows a paged, searchable table of records using the columns you pick.

**Read only** shows values instead of inputs and rejects writes. An unpublished form returns `404`.

## The hosted page

A script tag does not put anything in the HTML a search engine sees, so every form also has its own page at `/f/{formId}`:

```
https://baseport.example.com/f/Kf3nQ8xR2vLm
```

For a list, the first page of rows is included in the HTML, so you can share the link, search engines can index it, and it still works with JavaScript turned off. Once the embed loads it replaces that with the interactive version.

The server-rendered version is intentionally basic. It shows the same columns the list is configured with, using the same code the JSON route uses, so it cannot expose a field the embed would have hidden. Custom renderers and row actions are JavaScript expressions and Baseport has no JavaScript engine, so they are left out rather than approximated.

For a submit form the page shows only the heading and description. Rendering a second set of inputs with nowhere to send them would just be a form that does nothing.

## Styling

Override the CSS variables on `.baserow-embed` from your own stylesheet. The embed adds its styles once per page, so your rules only need to be more specific.

## Which sites may embed

Under **Settings**, list the origins that are allowed to embed your forms, one per line:

```
https://shop.example.com
https://portal.example.org
```

Leave it empty and any site can embed them, which is usually what you want while developing. Once you fill it in, remember to keep it current, because a site that is not on the list will not render the form.

## Rate limits

The public form routes are rate limited per client, per form, so one busy visitor does not use up everyone else's budget.

| Route | Per minute |
| --- | --- |
| Submit | 20 |
| Lookup | 10 |
| List and `/f/{formId}` | 60 |
| Schema | 60 |

Behind a reverse proxy, set `Baseport__TrustForwardedHeaders` to `true`, otherwise every visitor counts against the same limit. See [Going to production](/docs/going-to-production).
