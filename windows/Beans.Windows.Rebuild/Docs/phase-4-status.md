# Phase 4 Delivery Record

## Scope and boundaries

Phase 4 extends only `windows/Beans.Windows.Rebuild`. It adds the three-platform
infrastructure, one shared Discover page, source badges on Home, and focused tests. It
does not implement Search, Library, account sign-in, detail pages, downloads, local
scanning, settings redesign, or a second player/shell.

The legacy Windows project, Apple clients, server, versioning, and release files remain
outside this phase.

## Platform registration

`MusicPlatformRegistry` owns the three online descriptors and preserves the existing
`IMusicPlatformService` / `IMusicPlatformRegistry` boundary. Stable IDs are:

- `qq` for QQ Music;
- `netease` for NetEase Cloud Music;
- `kugou` for KuGou Music.

Descriptors centralize display name, compact name, logo glyph, brand color, enabled and
available state, authorization state, anonymous discovery support, capability flags, and
safe status text. Current platform and enabled flags are persisted as non-sensitive JSON
preferences under the user's local application data. A disabled current platform falls back to the first enabled platform, and the
registry refuses to disable the final enabled online platform.

`PreviewMusicPlatformService` intentionally throws a safe "not supported" result for
real network operations. It does not call provider endpoints or claim authorization.

## Discovery architecture

`IMusicDiscoveryService` exposes the common discovery, rankings, recommended playlists,
daily recommendations, and playlist-square operations. `PreviewDiscoveryService` is the
only Phase 4 implementation. It reads centralized platform-specific PreviewData:

- `PreviewData/QqDiscoveryPreviewData.cs`;
- `PreviewData/NetEaseDiscoveryPreviewData.cs`;
- `PreviewData/KuGouDiscoveryPreviewData.cs`.

The three files use distinct titles, rankings, playlists, category sets, and safe login
messaging. Login-only daily recommendations remain empty; the UI never fabricates an
authorized response.

## State, cache, and cancellation

`DiscoverViewModel` maintains one `PlatformDiscoveryState` per platform. Each state owns
its content, initial/loading/refresh flags, safe error, category, scroll offset, last load
time, and stale flag. Switching platforms saves the current offset, restores the target
offset, uses cached target content immediately, and only loads when needed.

Every foreground request has a cancellation token and monotonically increasing request
version. A platform switch cancels the previous request; a late response cannot update a
different platform. Cancellation is not translated into an error. A refresh preserves
existing content while the lightweight progress indicator is shown.

`DiscoveryCacheKey` contains `PlatformId`, content type, category, page, and account
identity hash. The in-memory cache has a 24-entry capacity and 20-minute PreviewData
lifetime. Removing one platform removes only that platform's entries.

## Discover page and routing

One `DiscoverPage` is hosted by the existing `AppShell`. It contains:

- reusable `PlatformSelector`;
- compact platform and authorization status row;
- platform-specific Hero/login entry;
- rankings, recommended playlists, and categorized playlist square;
- initial loading, retained-content refresh, empty, and safe error states.

Ranking and playlist clicks route to explicit incomplete pages with a
`PlatformRouteParameter` containing platform ID, native ID, and title. Account actions
route to the existing account placeholder with the same platform identity. No click
handler is intentionally empty.

Discover uses the established 1024 / 1180 / 1360 effective-width breakpoints and keeps
horizontal scrolling inside content rails rather than on the page.

## Home source identification

`PlatformBadge` renders a small, restrained source mark for QQ Music, NetEase Cloud
Music, KuGou Music, Beans, local content, and unknown sources. Home playlist covers and
track rows now show it without changing the page grid. BottomPlayer and the Home playing
rail expose the current `SourceLabel`. The bundled playable item remains labeled local
preview rather than being misrepresented as a provider stream.

## Reference behavior adapted

The existing reference audit maps provider discovery capabilities into common rankings,
recommendations, and playlist models. Phase 4 adapts those capability boundaries, stable
native identities, per-provider authorization state, failure isolation, request
cancellation, and safe status messages. It does not copy Swift UI or networking code and
does not include unblock, paywall, membership, or region-bypass behavior.

## Tests

`Tests/PlatformInfrastructureTests.cs` covers 14 focused cases:

- stable platform ID uniqueness;
- current-platform persistence;
- disabled-current fallback and one-enabled minimum;
- cross-platform cache-key isolation;
- independent scroll offsets and categories;
- late-request and cancellation safety;
- distinct, explicitly partial PreviewData;
- no fabricated unauthorized daily recommendations;
- unknown-source labeling;
- platform-state notifications;
- platform/native route identity.

Latest Debug test result: 14 passed, 0 failed, 0 skipped.

## Verification and remaining stubs

- Debug build: zero warnings and zero errors;
- Release build: zero warnings and zero errors;
- default Home startup: main window created and remained responsive;
- direct Discover startup smoke test: the page, platform registry, preference store,
  selector, and initial PreviewData load constructed successfully and the window remained
  responsive before the default Home route was restored;
- real QQ, NetEase, and KuGou HTTP adapters: still stubbed;
- full sign-in: still an explicit account placeholder;
- ranking, playlist, search, and library detail flows: still explicit placeholders.

Native UI automation was unavailable during the final Phase 4 pass, so new screenshot
claims are intentionally not recorded here. The previously verified Shell DPI sizing and
Home breakpoints are unchanged; Discover retains the same breakpoints and received a
separate page-construction smoke test. Full 1440 x 900, 150%, and 960 x 600 screenshot
verification remains required when the native UI inspection surface is available.

Phase 4 stops here. The next stage should replace one Preview discovery adapter at a time
with cancellable, schema-guarded provider integration before starting the full Search or
Library pages.
