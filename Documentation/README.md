# Baseport Documentation

Baseport documentation, served by [Bark](https://github.com/melosso/bark). Pages are plain markdown under `docs/`; site configuration is `docs/config.json`. Writing a page needs no installation. This file is also the writing instruction for agents that edit these pages.

## Running locally

```bash
docker compose up -d
```

The site is served at `http://localhost:5991`.

## Layout

| Path | Purpose |
|---|---|
| `docs/config.json` | Site configuration: navigation, sidebar, branding, edit links |
| `docs/index.md` | Landing page |
| `docs/docs/` | The documentation itself, published under `/docs/` |

## Publishing

`.github/workflows/documentation-pages.yml` builds this directory with Bark and deploys it to GitHub Pages on every push that touches `Documentation/docs/**`. It publishes to `https://baseporteu.github.io/baseport`.

Prerequisite: Pages enabled on the repository (Settings > Pages, Source "GitHub Actions"). The workflow fails without it.

## Writing style

Concise, direct and structural. State what a component does and where its values come from.

- No UI narration. Not "On screen you will see..."; describe the component and its behavior.
- Accessibility is documented as attributes and their sources (ARIA roles, labels, locale keys), never as a narrated screen reader experience.
- No filler or transitions ("sit in", "shows only the", "It is important to note"). Connect a behavior to its setting, route or source file.
- Plain headings. "Files on disk", not "What lands on disk".
- Ordinary verbs. Data is saved, sent or returned.
- No design rationale on task pages. The reasoning belongs in `AGENTS.md`.
- No em or en dashes. Callouts only for genuine edge cases.

Anti-pattern:

> On screen, each link shows only the title, with an arrow that points in its direction. Screen readers also hear the direction, and the links sit in a navigation region named "Pagination".

Preferred:

> Pagination links display the title and a directional arrow. The navigation region and ARIA labels are populated from the locale files (`pagerPrevious`, `pagerNext`, `pagerAria`).

Every statement matches the code as it is now. Check a route or a setting name against the source before writing it down.
