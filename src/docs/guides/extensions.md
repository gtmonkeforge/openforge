---
order: 20
description: Accept community extensions for your mod, host documentation, and build an in-game marketplace with the Extensions API.
---
# Extensions and docs

Every mod page can have two optional tabs: **Extensions** and **Docs**. Both are off by default. To turn them on, open your mod, choose **Manage**, then go to **Page tabs**. Turning a tab off hides it, but nothing in it is deleted.

## Extensions

An extension is a free mod built on top of another mod, such as a cosmetic pack, a map or a plugin. Extensions work like ordinary mods. Each one has its own page, releases, comments and MonkeShield report, and staff review it like any other public mod. Unlike other mods, extensions can't be sold:

- they can't have a price or subscription tiers,
- they can't be included in creator membership tiers,
- they can't use injector delivery, so marketplaces can always download them directly.

### Publishing an extension

Open the mod you want to extend, go to its **Extensions** tab and choose **Publish an extension**. The publish form works as usual, and the new mod is linked to its parent for good. You can't turn an existing mod into an extension, or move an extension to a different parent.

You can also publish an extension from a script. When you create the mod, send an `extends` field set to the parent's GUID along with the usual fields:

```
curl -H "Authorization: Bearer YOUR_API_KEY" \
  -F name="Neon Hats" -F guid=com.you.neonhats -F version=1.0.0 \
  -F description="Glowing hats." -F extends=com.example.hatloader \
  -F downloads_bepinex5=@NeonHats.dll \
  https://monkeforge.org/api/v1/mods
```

After staff approve the extension, it waits for the parent mod's developers to approve it too. Until they do, people can still open and download it from its own page, but it isn't listed on the parent's Extensions tab or in its marketplace.

### Moderating extensions for your mod

Once staff have approved an extension, it shows up in your Extensions tab under **Pending**. You'll also get a notification when someone submits one. From the tab you can:

- **Approve** an extension to list it on your Extensions tab and in the Extensions API.
- **Unapprove** an extension to take it off both. Its own page stays up.
- **Feature** an approved extension so it's listed first and marked as featured. You can feature up to 12 at a time.

The extension's developer gets a notification whenever you approve or unapprove their extension. Extensions you publish for your own mod are approved automatically.

## The Extensions API

Your mod can show the same list in-game and install extensions from it. The endpoint is public and doesn't need a key:

```
GET /api/v1/mods/{guid}/extensions
```

| Parameter | Values | Default |
|---|---|---|
| `q` | Search text for names, descriptions, GUIDs and authors | none |
| `category` | A mod category, such as `Cosmetic` | all |
| `featured` | `true` to return only featured extensions | `false` |
| `sort` | `featured`, `popular`, `newest` or `name` | `featured` |
| `page`, `limit` | Paging. `limit` can be up to 100 | `1`, `24` |

The response has `items`, `total`, `page` and `limit`. Each item is a mod, in the same format as `/api/v1/mods`, with two extra fields:

- `extends` names the parent mod and includes `featured`.
- `latest_release` is the newest stable release. Its `downloads` list has each file's `url`, `sha256`, `kind` (`dll` or `zip`) and `loader`.

Check each file's SHA-256 checksum before loading it. The list only ever contains extensions that staff and your developers have both approved. If you turn the Extensions tab off, the endpoint returns 404.

Your mod's developers can also pass `state=pending`, `state=unapproved` or `state=all` while signed in. The response then includes `counts` for each state. Moderation uses `PATCH /api/v1/mods/{guid}/extensions/{extension}` with `{"state": "approved"}` or `{"featured": true}`.

## Docs

The Docs tab hosts Markdown pages for your mod: install steps, configuration, an FAQ or an API reference. Anyone who can publish the mod can add, edit, reorder and delete pages. You can write pages before turning the tab on. Until you do, only your mod's developers can see them.

- Each page has a title and an address, such as `getting-started`. Pages are at `/view/{your-mod}/docs/{address}`, and the first page is also the tab's landing page.
- To link to another page, use its address: `[Configuration](configuration)`.
- A mod can have up to 50 pages of up to 100,000 characters each. Pages use the same Markdown as mod descriptions, so images and raw HTML aren't supported.

Pages are also available as JSON at `GET /api/v1/mods/{guid}/docs` and `GET /api/v1/mods/{guid}/docs/{address}`.
