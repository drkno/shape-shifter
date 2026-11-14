# Shape-Shifter

A Jellyfin plugin that makes Jellyfin pretend to be a Plex Media Server, so
existing Plex clients keep working (unmodified) while you move them to Jellyfin
one at a time.

This is probably not something that complies with Plex's Terms of Service. It should not
be used with a public server. Use as a migration aid, nothing else.

See [docs/DESIGN.md](docs/DESIGN.md) for the architecture and the reasoning behind it.

## AI Warning

This code was entirely written by AI using Vibes. It has not been extensively reviewed.

## Status

| Area | State |
|---|---|
| Listener on a configurable port, HTTP + HTTPS on one port, XML/JSON | done, tested |
| Certificates: plex.direct and custom, chosen by SNI | done, SNI matching unit-tested |
| Side-by-side mode: own identity, claim via plex.tv/claim | done, untested against plex.tv |
| Takeover mode: import PMS identity, both certs, custom URLs, public port | done, untested against a real PMS |
| Publishing connections to plex.tv (LAN, public, custom URLs) | done, untested against plex.tv |
| Admin status page (what we'd publish vs. what plex.tv lists) | done |
| GDM LAN discovery | done |
| Auth: server token, owner, shared users, local test tokens | done, untested against plex.tv |
| Library browsing, hubs, on deck, search, artwork | done |
| Direct play, subtitles, stream selection, play queues | done |
| HLS transcoding via Jellyfin | first pass |
| DASH transcoding (Plex Web) via Jellyfin's ffmpeg | done, tested with real ffmpeg; untested in Plex Web |
| Watch state → Jellyfin sessions, scrobble, ratings | done |
| Metadata editing: fields, tags, locks, artwork, refresh, match, delete | done, untested with a Plex client |
| Data import from PMS (ids + watch history), safe while PMS runs | done, untested against a real PMS |
| Music transcoding, sharing the side-by-side server with friends | not yet |

## Build

```bash
dotnet build -c Release
```

```bash
dotnet test
```

Targets Jellyfin 12.4 (`net10.0`). Copy
`src/Jellyfin.Plugin.ShapeShifter/bin/Release/net10.0/Jellyfin.Plugin.ShapeShifter.dll`
into `<jellyfin data>/plugins/ShapeShifter/` and restart Jellyfin.

## Testing side by side (production PMS keeps running)

1. Point Jellyfin libraries at the same media and let them scan.
2. In the plugin settings, leave the mode on **Side-by-side**. Give it a name
   you'll recognise ("Shape-Shifter test") and a port that's free on that host.
   If Jellyfin runs on the PMS host, 32400 is taken.
3. Optional: set the PMS data path (and path rewrites), leave "take over
   identity" **unticked**, and run **Scheduled Tasks → Shape-Shifter → Import
   from Plex Media Server**. This copies ids and watch history from a snapshot
   of the PMS database and never writes to PMS.
4. Get a claim token from <https://plex.tv/claim> as the owner, paste it, and
   press **Claim**.
5. Add a custom access URL and/or custom certificate if you want TLS. A
   side-by-side server has no plex.direct certificate. Tick **Publish
   connections**, then check the status panel: "would publish" vs.
   `plexTvResource`.
6. The test server now shows up in Plex apps signed in as the owner, next to
   the real one. Clients that connect by address can use a local token from
   the user mappings instead.

## Cut-over (takeover)

1. Stop PMS. Keep its data directory: the plugin only reads it.
2. Set the PMS data path, tick **take over identity**, and run the import
   before any client connects to this identity. It imports:
   `ProcessedMachineIdentifier`, the server token, the plex.direct
   certificate, the custom certificate and domain, custom access URLs, the
   secure-connections setting, and the manually mapped public port.
3. Set the listener port to PMS's. Check the status panel's "would publish"
   list against what plex.tv lists now, then tick **Publish connections**.
4. Clients reconnect to "the same server". Rollback: stop Jellyfin, start PMS.

## Metadata editing

Jellyfin administrators, plus anyone listed under **Metadata editors**, can
edit from Plex clients. Plex apps decide for themselves whether to show the
edit UI: they show it to the server owner (and Plex Home admins), not to
users the server is shared with. Anyone signed in as the owner account maps to
the owner's Jellyfin user (an admin), so they can edit with no extra setup.

## Security notes

- Jellyfin's loopback API key is stripped from every playlist before it
  reaches a Plex client. Transcode segments are authorised by a per-session
  random secret.
- `/photo/:/transcode` only serves this server's own artwork paths. Poster
  selection by URL (editors only) accepts http/https only.
- Media deletion from Plex clients is off unless enabled.
- For personal use with your own Plex account only.
