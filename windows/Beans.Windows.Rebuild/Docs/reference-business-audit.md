# Reference Repository Business Audit

Audit target: `https://github.com/lgcr12/Beans-Music-Three-Platform`

Audited checkout: repository `main` branch at the local `HEAD`. Existing uncommitted
Windows changes were not used as the UI base and were not modified. Protocol and behavior
findings below come primarily from the checked-in Swift client and the checked-in Windows
core contracts.

## License and product boundary

The repository uses the MIT License, copyright 2026 XIaodou0416. The root README states
that it is an independent derivative of XIaodou0416/Beans-Music and not an official
release. Any redistribution of substantial source must retain the MIT notice and the
upstream attribution already present in the repository.

Music-provider brands, content, membership rights, and service terms are not covered by
the source license. The rebuild must not bypass subscriptions, copyright, regional limits,
or platform access controls. The reference repository's `UnblockService` and "免费听歌"
fallback behavior therefore cannot be migrated into this Windows client. Only official
or user-authorized playback sources belong behind `IMusicPlatformService`.

## Platform interface map

| Capability | QQ Music reference | NetEase reference | KuGou reference | Windows rebuild decision |
|---|---|---|---|---|
| Authentication | QR OAuth, webpage cookie import, manual cookie import, profile/VIP probe | QR login, webpage cookie import, account probe | QR login, device registration, token/VIP state | Reimplement per platform behind isolated auth adapters; WebView2 only on real provider domains |
| Search | songs, artists, albums, hot keys | songs, artists, albums, hot search | songs, artists, albums, hot words | Normalize into `SearchResult`; isolate provider failures |
| Discovery | top lists, recommendations, playlists | daily recommendations, rankings, playlist square, personalized, new songs, similar songs, FM | rankings, recommended playlists | Map to `PlatformHomeContent`, `RankingList`, and platform-specific capability flags |
| User library | playlists, favorites, create/delete, like | playlists, favorites, create/delete, add/remove tracks, like | user playlists | Preserve capability differences; unsupported mutations return an explicit capability error |
| Details | playlist tracks, artist hot songs/albums | playlist tracks, artist songs/albums, album songs | playlist tracks and completed playback metadata | Preserve native IDs and opaque provider payloads |
| Playback | vkey resolution with bitrate/quality attempts and URL probing | eapi/weapi URL info with requested quality and official standard fallback | v5/web URL resolution with standard/exhigh/lossless/hi-res hashes | Resolve through `PlaybackSource`; retry expired URLs and only fall back to an actually available official quality |
| Lyrics | LRC | LRC plus translated LRC | lyric search/download | Normalize into `LyricDocument`; parse away from UI thread |
| Comments | song comments | song comments | gateway and legacy comment endpoints | Normalize page results and isolate errors |

### QQ Music

Primary evidence: `Beans/QQMusicAPI.swift` and `Beans/QQMusicAuth.swift`.

- Search supports songs, artists, and albums, with hot-key discovery.
- Library behavior includes user playlists, favorite songs, create/delete playlist, and
  like/unlike.
- Playback resolution uses song MID plus media MID and tests a requested bitrate through
  official vkey responses. It records attempts and distinguishes missing credentials,
  authorization, membership, region, and unavailable-source failures.
- Lyrics are returned as LRC; comments, rankings, recommended songs/playlists, playlist
  tracks, artist songs, and artist albums are present.
- Login can come from QR authorization or provider web cookies. The reference's broad
  fallback that collects all `qq.com` cookies is too permissive for Windows and should be
  replaced by a minimal allowlist plus an isolated WebView2 profile.

### NetEase Cloud Music

Primary evidence: `Beans/NetEaseAPI.swift`, `Beans/NetEaseCrypto.swift`, and
`Beans/NetEaseWebLoginSheet.swift`.

- Requests use provider-specific WEAPI/EAPI formatting and preserve cookies locally.
- QR login and real-domain web login are supported, followed by an account probe.
- Search, user playlists, playlist tracks, quality-specific song URL information,
  comments, artist and album details, play records, favorites, daily recommendations,
  rankings, playlist square, personalized playlists, similar songs, and personal FM are
  represented.
- Lyrics include translated LRC and are merged by timestamp in `Models.swift`.
- Playlist creation and add/remove/delete mutations exist and must be gated by a valid
  user session and explicit user action.

### KuGou Music

Primary evidence: `Beans/KugouMusicAPI.swift` and `Beans/KugouMusicAuth.swift`.

- QR login, device preparation, user playlists, search, rankings, recommendations,
  playlist details, lyrics, comments, and playback URL resolution are implemented.
- Playback metadata holds per-quality hashes for standard, exhigh, lossless, and hi-res.
- The implementation tries v5 and web endpoints and refreshes membership status. A
  Windows adapter needs a dedicated request signer, device state store, cancellation, and
  safe error translation; Swift persistence must not be copied mechanically.

## Unified data model map

The reference Swift `Song`, `Artist`, `Album`, `Playlist`, `LyricLine`, account, queue,
and download types are provider-shaped. The rebuild introduces UI-safe common models:

- stable identity: `MusicIdentity(PlatformId, NativeId)`;
- catalog: `MusicTrack`, `MusicArtist`, `MusicAlbum`, `MusicPlaylist`,
  `PlaylistDetail`, `RankingList`, and `RankingDetail`;
- operations: `SearchResult`, `PlaybackSource`, `AudioQuality`, and `PlatformComment`;
- text: `LyricDocument`, `LyricLine`, and optional timed `LyricWord` entries;
- local state: `LocalTrack`, `QueueItem`, `PlayHistoryItem`, `DownloadTask`, and
  `UserMusicStatistics`;
- account boundary: `PlatformAccount` and `CredentialState`.

Titles and artist names are display fields only and are never treated as unique keys.
Opaque provider payloads may be retained for round trips but must not be exposed directly
to pages.

## Playback flow

Reference flow in `PlayerManager.swift`:

1. choose the current queue item and requested quality;
2. resolve an official platform URL;
3. attempt an official lower quality when the requested quality is unavailable;
4. create one AVPlayer item and attach end/failure observers;
5. update progress, queue state, now-playing metadata, artwork, history, sleep timer, and
   playback rate;
6. retry when a QQ vkey/CDN URL fails during playback.

Windows translation:

- one DI singleton owns one `MediaPlayer` for the window lifetime;
- pages and controls consume observable playback state and never create players;
- seek input suspends timer overwrite until commit;
- System Media Transport Controls receive title, artist, album, artwork, and transport
  capabilities;
- expired URL refresh and quality fallback will be added with the platform adapters;
- third-party unblock sources are intentionally excluded.

The first-round implementation exercises this lifecycle with an original local WAV. It
does not claim online playback support.

## Lyrics flow

The reference obtains provider text, parses LRC timestamps, reads a declared offset, and
merges translated lines by matching timestamps. Player progress selects the active line.
The Windows implementation should keep parsing in `LyricsService`, cache normalized
documents, update only the previous/current visible rows, and cancel work when the page is
left. Local music additionally checks a same-name `.lrc` file. No lyric UI is implemented
in the first round.

## Download flow

`DownloadManager.swift` currently resolves a quality-dependent URL, downloads one file,
validates the response, chooses a safe extension, and moves the temporary file. It has a
quality fallback chain but is not a durable multi-task manager with pause/resume,
concurrency, disk accounting, or restart recovery.

The Windows download system therefore requires a rewrite: durable SQLite task state,
range-request capability detection, safe file naming, explicit pause/resume/cancel,
bounded concurrency, storage checks, and a user-selected directory. It must reject
content the user is not authorized to save.

## Local music and persistence

`LocalLibraryStore.swift` covers user-created local playlists and track membership.
Existing Windows core code also contains local scan, lyric, history, and SQLite-oriented
models, but the legacy UI couples them to `MainWindow`. Only validated non-UI behavior
should later be adapted. Scanning, metadata parsing, cover extraction, and database work
must remain off the UI thread and support cancellation and incremental updates.

## Account, credentials, and sync

- Apple uses Keychain through `BeansSecureStore`; the checked-in Windows code uses
  Windows Vault concepts. The rebuild exposes only `ISecureCredentialStore` and stores
  secrets with Windows Credential Locker.
- Provider credentials stay on the device. Beans server synchronization may carry only
  the repository's encrypted envelopes, never raw provider cookies.
- UI models must expose state and masked descriptions, not cookie/token dictionaries.
- Login WebView2 instances need separate provider profiles, exact provider-domain
  allowlists, session cleanup on logout, and no fake login HTML.
- logs may contain platform ID, operation, safe error code, status, elapsed time, retry,
  and cache hit. They must not contain cookies, tokens, passwords, full playback keys, or
  authorization query strings.

## Reuse and rewrite decisions

Can be behaviorally reused after tests:

- provider capability boundaries and stable native identifiers;
- quality ordering and official fallback semantics;
- LRC parsing, translation merge, and declared/user offset behavior;
- queue, repeat, shuffle, history, sleep timer, and system media metadata semantics;
- safe credential-state probing and encrypted Beans sync contracts;
- local library and listening-statistics data shapes.

Must be rewritten for Windows:

- all Swift networking, crypto wrappers, lifecycle, Keychain, WebKit, AVPlayer, and UI;
- the legacy Windows `MainWindow` page routing and code-behind state ownership;
- image caching and decode sizing as a shared service;
- durable downloads and background transfer behavior;
- provider error translation and cancellable request orchestration;
- any broad cookie collection and every third-party unblock/paywall-bypass path.

## Phase risks to carry forward

1. Provider endpoints are unofficial implementation details and may change; adapters need
   cancellation, timeouts, schema guards, and safe error codes.
2. Account cookies and playback keys are high-value secrets. They must never enter binding
   diagnostics, telemetry, crash payloads, or PreviewData.
3. Membership and region restrictions are provider decisions. UI must explain them and
   must not synthesize an unlocked state.
4. Remote artwork requires bounded disk caching, decode dimensions, and a stable local
   fallback to prevent layout shifts and repeated decode cost.
5. High-frequency progress and lyrics updates must be scoped to their controls rather
   than causing page-wide collection replacement.
