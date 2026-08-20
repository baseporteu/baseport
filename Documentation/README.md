# Baseport Documentation

This directory holds the Baseport documentation, served by [Bark](https://github.com/melosso/bark). Pages are plain markdown under `docs/`, with site configuration in `docs/config.json`. There is nothing to install to write a page.

## Running locally

```bash
docker compose up -d
```

The site becomes available at `http://localhost:5991`.

## Layout

| Path | Purpose |
|---|---|
| `docs/config.json` | Site configuration: navigation, sidebar, branding, edit links |
| `docs/index.md` | Landing page |
| `docs/docs/` | The documentation itself, published under `/docs/` |

## Publishing

`.github/workflows/documentation-pages.yml` builds this directory with Bark and deploys it to GitHub Pages on every push that touches `Documentation/docs/**`. It publishes to `https://baseporteu.github.io/baseport`.

Prerequisite: Pages has to be enabled on the repository first, under Settings > Pages, with Source set to "GitHub Actions". The workflow fails until it is.

## Writing style

Write the way you would explain something to a colleague over coffee. Plain, direct, no buzzwords, no filler transitions like "Furthermore" or "It is important to note", and no enthusiasm the reader did not ask for.

A few habits worth avoiding, because they read as clever rather than useful:

- Oblique headings. "Files on disk" beats "What lands on disk".
- Figurative verbs where an ordinary one works. Something is saved, sent or returned; it does not land, flow or stream.
- Closing aphorisms. If a sentence is there for rhythm rather than information, cut it.
- Design rationale in a page somebody is reading to get something working. Say what it does. The reasoning belongs in AGENTS.md.

Copy also follows the Bark tone guidelines: suggestive and explanatory rather than commanding, no em or en dashes, and loud callouts reserved for genuine edge cases. A quick pre-publish check for aggressive phrasing:

```bash
grep -niE '\b(must|mandatory|strictly|forbidden)\b' docs/docs/page.md
```

Hits in prose are worth rewording. Factual constraints in tables, error messages and code samples can stay as they are.

Everything on these pages has to match the code as it is now. Check a route or a setting name against the source before you write it down.
