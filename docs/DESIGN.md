# Shape-Shifter — Jellyfin plugin that impersonates a Plex Media Server

## Goal

Let unmodified Plex clients (Plex for TV/iOS/Android, **Plex Web in browsers**,
Plexamp, Infuse, PlexKodiConnect, …) keep working against a Jellyfin server
while clients are migrated to Jellyfin one at a time. Once the last client has
moved, the plugin is uninstalled and PMS is gone for good.

In scope: server registration and discovery, libraries, browsing, playback
(direct play, HLS **and DASH** transcoding), watch history, auth for owner and
shared users, **metadata editing from Plex clients**.

Non-goals: Plex Pass extras (downloads/sync, DVR, Live TV), managing shares
from the server side, being a general-purpose public PMS replacement.

## Two identity modes

A Plex client never "adds a server by URL". It signs in to plex.tv, asks
plex.tv which servers the account can see (`/api/v2/resources`), gets a list
of connection URIs plus a per-server access token, and then talks to the
server directly. So what a client sees is decided by **which server identity
plex.tv knows about and which connections that identity publishes**.

| | Side-by-side (testing) | Takeover (cut-over) |
|---|---|---|
| Machine identifier | new, generated | imported from PMS (`ProcessedMachineIdentifier`) |
| Registered with plex.tv by | claim token from plex.tv/claim | already registered (imported `PlexOnlineToken`) |
| Shows up in clients as | a **second** server next to the real one | the existing server |
| Real PMS | keeps running, untouched | stopped |
| Certificates | custom cert only (no plex.direct cert) | plex.direct cert + custom cert |
| Shares | none yet (owner + configured local tokens) | inherited |
| Watch history / ids | optional data-only import from a **copy** of the PMS database | full import |

**Side-by-side is the default.** It lets the owner test with real clients
(including Plex Web) against real media while the production PMS keeps
serving everyone else. Switching to takeover is a deliberate, separate step,
and rollback is "stop Jellyfin's listener, start PMS".

Side-by-side can also run with plex.tv disabled entirely (`EnablePlexTvAuth`
off, a local token per test user), for clients that can connect by address
(PlexKodiConnect, Infuse, a browser pointed at `/identity`).

**Known unknown:** sharing the side-by-side server with friends needs plex.tv
to know its libraries. How PMS reports sections to plex.tv hasn't been
captured yet. Test with the owner account first; if the share dialog shows no
libraries, capture PMS's plex.tv traffic and add section sync.

### Cut-over procedure (takeover)

1. Test side-by-side until the client matrix (`docs/CLIENTS.md`) is green.
2. Stop PMS.
3. Run the **Import** task in takeover mode against the PMS data directory:
   - `Preferences.xml`: `ProcessedMachineIdentifier`, `PlexOnlineToken`,
     `PlexOnlineUsername`, `FriendlyName`, `secureConnections`,
     `ManualPortMappingMode`/`ManualPortMappingPort`, `customConnections`,
     `customCertificatePath`/`customCertificateKey`/`customCertificateDomain`.
   - `Cache/cert-v2.p12`: the plex.direct certificate, password
     `sha512("plex" + ProcessedMachineIdentifier)`.
   - The library database (copied first, opened read-only): sections, items
     and parts matched by file path, per-account watch state.
4. Review the connection preview on the status page (what will be published
   vs. what plex.tv lists now), then enable publishing.
5. Clients reconnect on their own.

## Components

### 1. Listener (`Server/PlexListenerService`)

An `IHostedService` hosting its own Kestrel instance. It's separate from
Jellyfin's ASP.NET app because Plex paths collide with Jellyfin's (`/`,
`/library/...`) and clients expect Plex's port and certificates.

- **Configurable port**, defaulting to 32400. Non-default ports work as long as
  they're what gets published (component 5).
- **HTTP and HTTPS on the same port**, as PMS does. A connection middleware
  peeks at the first byte (a TLS ClientHello starts with `0x16`) and either
  continues into TLS or goes straight to the HTTP handler. An extra TLS-only
  port is optional.
- **Several certificates, chosen by SNI**: the plex.direct cert for
  `*.<hash>.plex.direct`, the custom cert for the custom domain(s), and the
  first loaded cert for SNI-less clients. Both certificates can be loaded at
  once, so plex.direct and custom-URL connections work side by side.
- Middleware: CORS (Plex Web is cross-origin from app.plex.tv), `X-Plex-*`
  header/query normalisation, auth, response negotiation (XML default, JSON
  when `Accept: application/json`).

### 2. Plex response model (`Plex/`)

A loosely-typed `PlexNode` tree (element name, ordered attributes, children)
serialized to PMS-style XML or JSON (`{"MediaContainer": {…, "Metadata": […]}}`).

### 3. Identity mapping (`Data/IdMap`)

sqlite table mapping Plex integer ids ↔ Jellyfin ids for sections, items,
parts and streams. Allocated on first sight, starting at 50,000,000 so ids
seeded from PMS (by file path) never collide. **Import before first use** so
seeded ids win over allocated ones.

### 4. Auth (`Auth/`)

| Token | Resolution |
|---|---|
| configured local token | the mapped user (tests, offline) |
| server's own `PlexOnlineToken` | owner |
| owner's account token | plex.tv `GET /api/v2/user` → id == owner id |
| shared user's server access token | `GET plex.tv/api/servers/{mid}/shared_servers` |
| managed/home user token | plex.tv `GET /api/v2/user` |

Cached (1h positive, 1min negative), and kept serving cached principals when
plex.tv is unreachable. Plex accounts map to Jellyfin users explicitly or by
username. The owner falls back to the first Jellyfin admin.

### 5. plex.tv registration and publishing (`PlexTv/`)

Required for side-by-side (new identity) and for non-default ports or custom
URLs in either mode.

- **Claim** (side-by-side): `POST https://plex.tv/api/claim/exchange?token=claim-…`
  with `X-Plex-Client-Identifier: <machineId>`, `X-Plex-Provides: server`.
  The returned token becomes the server token. The claim token is pasted into
  the settings page and is only valid for ~4 minutes.
- **Publish connections**: `PUT https://plex.tv/devices/{machineId}` with
  repeated `Connection[][uri]=…`, `httpsEnabled`, `httpsRequired`, using the
  server token. On start, every 15 minutes, and on config change. Never
  publish an empty list (plex.tv answers 422). The connection set is:
  - LAN addresses on the listener port: `https://a-b-c-d.<hash>.plex.direct:<port>`
    when a plex.direct cert is loaded, plus `http://a.b.c.d:<port>` unless
    secure connections are *required*.
  - The public address (configured or from `https://plex.tv/:/ip`) on the
    public port (`ManualPortMappingPort`), as plex.direct.
  - Every custom access URL, verbatim.
- **Status page** (`GET /ShapeShifter/Status`, Jellyfin admin only): listener
  state, loaded certs and expiry, the connection set we'd publish, and what
  plex.tv's `/api/v2/resources` currently lists for our machine id. This lets
  takeover be checked before it changes anything. Publishing is **off by
  default in takeover mode** until switched on.

Unverified details (exact flags plex.tv expects, how custom URLs are
recorded) get checked with the status page against a live account.

### 6. GDM (`Discovery/GdmResponder`)

udp/32414 multicast. Replies to `M-SEARCH` with `Content-Type: plex/media-server`,
`Resource-Identifier`, `Name`, `Port`, `Version`. In side-by-side mode on the
same host as PMS the port is taken; the responder logs and steps aside.

### 7. Library API (`Api/LibraryEndpoints`)

Sections, section browse (filters, sorts, paging), metadata, children,
allLeaves, on deck, continue watching, recently added, hubs, search,
collections. Everything is queried as the mapped Jellyfin user, so Jellyfin
permissions and parental controls apply.

### 8. Playback

- **Direct play:** `/library/parts/{id}/{ts}/file.ext` from disk, with range support.
- **Decision:** `/video/:/transcode/universal/decision` → direct play if allowed
  and within bitrate. Otherwise transcode with the client's requested protocol.
- **HLS** (`start.m3u8`): delegated to Jellyfin's HLS endpoint over loopback.
  Playlists are rewritten onto a per-session secret path with Jellyfin's API
  key stripped.
- **DASH** (`start.mpd`, Plex Web in Chrome/Firefox/Edge — half the users):
  Jellyfin has no DASH output, so the plugin runs **Jellyfin's ffmpeg** itself
  with Jellyfin's hardware-acceleration setting:
  - The plugin serves its own **static MPD**: duration = item duration −
    offset, fixed 4s `SegmentTemplate`, separate video (`avc1.640029`) and
    audio (`mp4a.40.2`) adaptation sets, URLs
    `session/{id}/{secret}/{rep}/header` and `…/{rep}/{n}.m4s`.
  - ffmpeg's `dash` muxer writes fMP4 init/media segments into a session dir
    in Jellyfin's transcode path, with keyframes forced every 4s so segments
    line up with the template.
  - A segment request waits until the segment is complete (the next one
    exists, or ffmpeg finished). A request far outside the produced range
    restarts ffmpeg at that segment (`-ss` + `-output_ts_offset`), so
    seeking works.
  - Sessions die on `/stop`, when replaced, or after 2 minutes without
    requests. Their directory is deleted.
  - v1 always re-encodes video for DASH, because copied video doesn't have
    aligned keyframes. A later version can use Jellyfin's keyframe extraction
    to stream-copy with a `SegmentTimeline`.
- **Audio** (`/music/:/transcode/universal/…`) — later.
- **Subtitles:** `/library/streams/{id}` via Jellyfin's subtitle endpoint;
  burn-in for transcodes.

### 9. Watch state & sessions

`/:/timeline` → `ISessionManager` playback start/progress/stop (resume,
played, dashboard all work). `/:/scrobble`, `/:/unscrobble`, `/:/progress`,
`/:/rate` → user data. `/status/sessions` from Jellyfin sessions.

### 10. Metadata editing (`Api/MetadataEditEndpoints`)

Plex clients edit with `PUT /library/sections/{id}/all?type=&id=1,2&…` (also
accepted on `PUT /library/metadata/{ids}`):

| Plex param | Jellyfin |
|---|---|
| `title.value` | `Name` (lock: `Name`) |
| `titleSort.value` | `ForcedSortName` |
| `originalTitle.value` | `OriginalTitle` |
| `summary.value` | `Overview` (lock: `Overview`) |
| `tagline.value` | `Tagline` |
| `studio.value` | `Studios` (lock: `Studios`) |
| `contentRating.value` | `OfficialRating` (lock: `OfficialRating`) |
| `originallyAvailableAt.value` | `PremiereDate` (+ `ProductionYear`) |
| `year.value` | `ProductionYear` |
| `audienceRating.value` / `rating.value` | `CommunityRating` / `CriticRating` |
| `index.value`, `parentIndex.value` | `IndexNumber`, `ParentIndexNumber` |
| `genre[n].tag.tag`, `genre[].tag.tag-` | add/remove `Genres` (lock: `Genres`) |
| `label[…]` | `Tags` (lock: `Tags`) |
| `country[…]` | `ProductionLocations` |
| `collection[…]` | box set membership |
| `director[…]`, `writer[…]`, `actor[…]` | people (lock: `Cast`) |
| `{field}.locked=0/1` | `LockedFields` where Jellyfin has a lock for it |

Also:
- **Artwork:** `GET /library/metadata/{rk}/posters|arts` (Jellyfin remote
  images), `PUT …/poster|art?url=` (select), `POST …/posters|arts` (upload body).
- **Refresh:** `PUT …/refresh`.
- **Matching:** `GET …/matches?title=&year=` (Jellyfin remote search) →
  `SearchResult` list; `PUT …/match?guid=` sets provider ids and refreshes;
  `PUT …/unmatch`.
- **Delete:** `DELETE /library/metadata/{rk}`, only when deletion is enabled
  in config.

Who may edit: Jellyfin administrators, plus users listed as metadata editors
in config. Everyone else gets 403. Caveat: Plex apps only *show* editing UI
when plex.tv says the account owns (or administers) the server. The server
can't change that for shared users.

In this deployment the people who edit do so signed in as the **owner
account**. Their tokens resolve to the owner, who maps to a Jellyfin admin, so
editing works without any editor list. The side effect, the same as on PMS
today, is that their watch history is the owner's. The editor list only
matters for clients that connect with a local token mapped to a non-admin
Jellyfin user.

## Configuration

Listener (bind, port, extra TLS port), certificates (plex.direct, custom +
domain), identity mode, claim token, server token, owner, advertised version,
secure-connections mode, public address/port, custom access URLs, publishing
on/off, user mappings, metadata editors, allow deletion, Jellyfin loopback URL,
PMS data dir + path rewrites + import mode (data-only / takeover).

## Testing

- Unit tests: serializer, router, dual-mode TLS, playlist rewriting, MPD
  generation, metadata-edit parameter parsing.
- Integration: the DASH transcoder against a real ffmpeg with generated test
  media (segments exist, durations align with the template).
- Side-by-side with real clients; record PMS traffic per client (mitmproxy)
  and diff response shapes. Track results in `docs/CLIENTS.md`.

## Risks

- **Client strictness:** apps silently show empty screens on missing
  attributes. Mitigation: golden responses captured from the real PMS.
- **plex.tv behaviour:** claim/publish endpoints are reverse-engineered. The
  status page shows what plex.tv actually has.
- **DASH:** Plex Web's player tolerances (codec strings, segment timing,
  seek behaviour) are untested. The first thing to test side-by-side.
- **Cert expiry:** the imported plex.direct cert expires (~1 year), and
  renewing it needs PMS's CSR flow (not implemented). The custom cert is
  renewed by you as today.
- **Plex ToS:** personal use with your own account only.
