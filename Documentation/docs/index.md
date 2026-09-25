---
title: Baseport
description: A single-binary backend with typed tables, a REST API, live updates and embeddable forms over one SQLite file
layout: home
hero:
  name: Baseport
  text: A backend in one binary.
  tagline: Typed tables, a REST API, live updates and embeddable forms over one SQLite file. No database server to run.
  actions:
    - theme: brand
      text: Get started
      link: docs/how-to-use
    - theme: alt
      text: Documentation
      link: docs/
    - theme: alt
      text: View on GitHub
      link: https://github.com/baseporteu/baseport
features:
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 10h18M9 10v10"/></svg>'
    title: Typed tables
    details: Text, numbers, currency, dates, choices, files, references and computed fields, validated on every write path.
    link: docs/tables-and-fields
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M8 6 3 12l5 6M16 6l5 6-5 6"/></svg>'
    title: REST API and OpenAPI 3.2
    details: Paged, filterable CRUD per published table, cursor paging, ETags and a generated OpenAPI document.
    link: docs/api
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M4 12h3l2-5 4 10 2-5h5"/></svg>'
    title: Live updates
    details: Server-Sent Events per table or per record, filtered by the same read rules as the API.
    link: docs/api
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 3 4 6v6c0 4.5 3.4 8 8 9 4.6-1 8-4.5 8-9V6z"/><path d="m9 12 2 2 4-4"/></svg>'
    title: Access rules
    details: Per-record create, read, update and delete rules written as SQLite expressions over the caller, the row and the request.
    link: docs/access-rules
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="4" y="3" width="16" height="18" rx="2"/><path d="M8 8h8M8 12h8M8 16h5"/></svg>'
    title: Forms and embeds
    details: Submit, lookup and list forms as a script tag or a server-rendered page, rate limited per client.
    link: docs/forms
  - icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><ellipse cx="12" cy="6" rx="7" ry="3"/><path d="M5 6v12c0 1.7 3.1 3 7 3s7-1.3 7-3V6M5 12c0 1.7 3.1 3 7 3s7-1.3 7-3"/></svg>'
    title: One SQLite file
    details: Schema, records, accounts and settings in one database. Optional Postgres and TDS listeners for SQL clients.
    link: docs/postgres-and-tds
---
