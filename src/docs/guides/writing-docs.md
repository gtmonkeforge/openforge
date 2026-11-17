---
order: 90
description: How documentation pages are stored, edited and organised.
---
# Writing documentation

Every page is a Markdown file in the server's `docs` folder. The page at `/docs/guides/writing-docs` is the file `docs/guides/writing-docs.md`, and `docs/index.md` is the docs home page.

## Editing pages

Administrators see an **Edit** button on every page, and a **New page** box in the sidebar. Saving writes the file straight to the `docs` folder, and the change is live immediately.

You can also edit the files directly on the server, or commit them to the repository. The server notices changed files on the next request, so there's no need to restart it.

If two people edit the same page at once, the second save is refused instead of overwriting the first.

## Organising pages

Each folder becomes a section in the sidebar, named after the folder. Pages in the top level of `docs` appear above the sections.

Front matter at the top of a file can change how a page is listed:

```markdown
---
title: Getting paid
section: Creators
order: 1
description: The summary shown in search results and link previews.
---
```

| Key | What it does |
| --- | --- |
| `title` | The page title. Defaults to the first `# Heading`, then the file name. |
| `section` | The sidebar section. Defaults to the folder name. |
| `order` | Sorts pages within a section, lowest first. Defaults to 100, then pages sort by title. |
| `description` | Shown under search results and in link previews. Defaults to the start of the page. |

## Links and images

Link to other pages by file name, the same way you would on GitHub: `[Writing docs](../guides/writing-docs.md)`. Links to a heading work too: `[Front matter](writing-docs.md#organising-pages)`.

PNG, JPEG, GIF and WebP images placed in the `docs` folder are served alongside the pages, so `![Screenshot](images/launchpad.png)` works from any page in the same folder.
