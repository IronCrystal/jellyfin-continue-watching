# Continue Watching Plus for Jellyfin

One **Continue Watching** row, like Plex: items you're partway through plus the next episode of every show you're
watching, merged and sorted by recent activity.

It changes the server's answer to the Continue Watching request (`/UserItems/Resume` and the older
`/Users/{id}/Items/Resume`), so it works in the web app, the desktop app, and most other clients, including Infuse.

A fork of [Continue Watching Tweaks](https://github.com/dyancat/jellyfin-continue-watching-tweaks) by dyancat
(itself built on [italohdc/Jellyfin-NextUpMerge-Plugin](https://github.com/italohdc/Jellyfin-NextUpMerge-Plugin)).
The merge rules are theirs; this fork adds login checks, an in-memory cache, and per-user Next Up settings.

## Compatibility

Jellyfin **12.3** or newer (built against 12.3.0). Uninstall Continue Watching Tweaks first; both change the same
request.

## Install

### From a plugin repository (recommended)

1. Dashboard → Plugins → Repositories → **+**
2. Name: `Continue Watching Plus`, URL:
   `https://raw.githubusercontent.com/IronCrystal/jellyfin-continue-watching/main/manifest.json`
3. Install **Continue Watching Plus** from the catalog, then restart Jellyfin.

### Manually

Copy `Jellyfin.Plugin.ContinueWatchingPlus.dll` (from a release zip, or `./build.sh`) into
`<jellyfin data>/plugins/ContinueWatchingPlus_1.0.0.0/` and restart Jellyfin.

## What's in the row

| Setting | Default | Effect |
|---|---|---|
| One entry per series | on | No Next Up episode for a show you're partway through another episode of. |
| Only include series with a finished episode | on | A show you only opened briefly doesn't appear. |
| Promote recently added episodes | on | A newly added next episode moves its show to the front. |
| Hide the separate Next Up list | off | Empties the separate Next Up row. Series pages still find the next episode. |
| Only change responses for Infuse | off | Other clients get Jellyfin's normal lists. |

Each user's own **Max days in Next Up** and **Enable rewatching in Next Up** are respected. The web app stores these in
the browser rather than on the server, so the plugin picks them up from the web app's own Next Up request the next
time that user opens the home page, and remembers them. Users who never use the web app get the defaults on the
plugin's settings page (365 days, rewatching off, the same as the web app).

Requests the plugin leaves to Jellyfin: Continue Listening/Reading (`mediaTypes` without Video), library-scoped
(`parentId`) and search (`searchTerm`) queries.

## Security

The plugin runs inside Jellyfin's own request pipeline, on Jellyfin's own endpoints, after Jellyfin's login check:

- No valid login → **401**, from Jellyfin itself.
- A user asking for another user's list → **403**, the same check and response as Jellyfin's built-in endpoint.
  Administrators may read any user's list, as with Jellyfin.

## Speed

Each user's merged list (item IDs and order only) is kept in memory. Images, fields, and progress bars are filled in
fresh on every request.

- **Cleared when something changes:** that user's playback starts or stops, an item is marked played or unplayed,
  videos are added to or removed from the library, the user's account or this plugin's settings change, or the web
  app reports different Next Up settings.
- **Rebuilt in the background** right after a change (after 10 s for library scans, which add items in bursts), so the
  home page usually gets an answer from memory. A request that arrives before the rebuild finishes computes the list
  itself; it never gets the old one.
- **Safety net:** lists are rebuilt at least every 5 minutes (configurable) and at midnight UTC, when the Next Up
  date cutoff moves. Users who loaded the row in the last 24 hours are refreshed ahead of time, so they keep getting
  answers from memory.

Trade-off: the first load after a Jellyfin restart computes the list (about as fast as Jellyfin's own Next Up), and
changes Jellyfin doesn't announce, such as which libraries a user may see, show up within the safety-net interval.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
./build.sh
```

Runs the tests and writes `dist/Jellyfin.Plugin.ContinueWatchingPlus.dll`.

## Publishing a release

1. Push this repository to GitHub.
2. Tag and push: `git tag v1.0.0 && git push origin v1.0.0`.

The release workflow runs the tests, builds the plugin, attaches the zip to a GitHub release, and adds the version
(with its checksum) to `manifest.json` on `main`, which is what Jellyfin's plugin catalog reads.
