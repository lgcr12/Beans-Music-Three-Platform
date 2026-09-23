# Beans Music Windows Rebuild

This directory is the isolated WinUI 3 rebuild. It does not reference the legacy
`windows/Beans.Windows` project or any of its XAML pages.

## Current scope

The current delivery contains phases 0 through 7F plus the first download-management baseline:

- reference repository business audit;
- clean application shell, navigation, dependency injection, design tokens, logging,
  and secure credential boundary;
- one window-level `MediaPlayer` service with queue, seek, volume, shuffle, repeat,
  favorites, and System Media Transport Controls metadata;
- the light Home page, right playback rail, and persistent bottom player;
- one shared Discover page whose current product surface contains only QQ Music and
  NetEase Cloud Music, with isolated platform state, PreviewData, cache keys,
  categories, scroll offsets, refresh state, cancellation, and safe authorization
  messaging;
- reusable platform selector and source badge controls, including source labels on Home;
- one DI-owned networking boundary with three isolated named platform clients, total
  timeouts, cancellation, one bounded retry, per-platform concurrency limits, in-flight
  request merging, safe error mapping, redacted logs, and size-limited JSON parsing;
- a common discovery-adapter contract plus production stubs and offline Fixture/Fake
  adapters for tests;
- one real anonymous QQ Music discovery adapter for public rankings and public
  recommended playlists, with a Hero derived from the real public response;
- section-level QQ cache, forced refresh, Fresh/Stale fallback, partial-success state,
  and explicit Unsupported results for categories, playlist square, and daily
  recommendations;
- one real anonymous NetEase Cloud Music discovery adapter for public rankings,
  editorial playlists, playlist-square categories, and the first public square page,
  with a Hero derived from public content and daily recommendations kept behind an
  explicit authorization boundary;
- NetEase section-level Fresh/Stale cache, forced refresh, category/page isolation,
  partial-success state, cancellation, and late-response protection through the shared
  discovery infrastructure;
- concrete empty-state pages for favorites and notifications, with the generic
  placeholder retained only as an unknown-route fallback.
- a unified SearchPage shell with TopBar search submission, debounced suggestions,
  aggregate/QQ/NetEase/local scopes, result-type filters, source status rail,
  cancellation and late-response protection;
- a real anonymous QQ Music search adapter for tracks, albums, artists, and search
  suggestions, with stable QQ native IDs, paging, request merging, isolated Fresh/Stale
  cache fallback, partial success, and an explicit Unsupported boundary for playlists;
- a real anonymous NetEase Cloud Music search adapter for tracks, albums, and artists,
  using the reference WEAPI public search contract with stable native IDs, paging,
  encrypted request forms, isolated Fresh/Stale cache fallback, partial success, and
  explicit Unsupported boundaries for playlists and typed suggestions;
- a DI-owned local music catalog with explicit folder registration, cancellable
  recursive indexing, stable path IDs, metadata fallback, local LRC association,
  missing-file state, local tracks/artists/albums search, validated local playback
  sources, and completion-only play history;
- a dedicated LocalMusicPage with Windows FolderPicker wiring, folder management,
  background scan progress, local track listing, missing/LRC/source states, and
  existing playback-service queue/play actions;
- a DiscoverPage UI/status tightening pass with responsive ranking card widths,
  explicit recommended-playlist empty/error/cache states, and a lighter Hero
  mask without changing platform request logic;
- a final three-source search integration pass that aggregates QQ Music, NetEase
  Cloud Music, and the local catalog with source-isolated status, stable
  platform/type/native-ID deduplication, deterministic matching order, explicit
  partial/all-empty/all-failure states, and local-only playback boundaries;
- a first low-coupling page batch for the music library, playlists, artist,
  album, ranking, settings, accounts, downloads, queue, lyrics, and music-universe
  routes, plus a shared authorization coordinator, isolated QQ/NetEase
  authorization adapter and browser/probe boundaries, official QQ/NetEase
  playback-source resolvers, and a tested single-player integration boundary;
- provider-isolated, in-private WebView2 login hosts for QQ Music and NetEase,
  exact top-level host allowlists, filtered cookie capture, live account probes,
  Credential Locker persistence, account status refresh, and logout;
- real anonymous QQ Music and NetEase online lyric adapters through the shared
  HTTP boundary, including LRC offset parsing, translated-line merge, safe
  missing/instrumental/error states, cancellation, and bounded caching;
- a persistent download manager and page with safe paths, bounded concurrency,
  storage checks, pause/range-resume, cancel, restart recovery, and explicit
  rejection when a provider does not expose an authorized offline source;

Real public discovery request families are enabled for QQ Music and NetEase Cloud
Music. KuGou Music is not part of the current product surface; its internal descriptor
and stub remain disabled and unavailable only to preserve the extension boundary. QQ
categories, QQ playlist square, account-only daily recommendations, real detail data,
online favorites, playlist mutations, and provider-authorized offline download sources
are not implemented. QQ and NetEase playlist search remain Unsupported because the reference
implementation does not provide a reliable anonymous search endpoint. NetEase typed
suggestions remain Unsupported because the reference only exposes hot-search discovery,
not a stable keyword suggestion endpoint.
Debug builds may use explicitly marked Preview fallback when live and cache data are unavailable;
Release builds disable Preview by default and do not silently substitute it for live
data. The
playable audio is an original, locally generated preview tone stored in
`Assets/Audio/preview-tone.wav`.

Phase 6B and Phase 6C replace the QQ and NetEase search Stubs with anonymous live
requests. Successful online search results are marked Live or CacheFresh/CacheStale as
appropriate; they enter the single playback resolver only when authorized and an
official source is available. No usable local
music index is created as a search side effect: local folders must be explicitly added
and scanned. The local source now reports `CatalogUnavailable` until an indexed track
exists, then returns local playable results through the shared playback service boundary.
The local repository is an atomic JSON file under the user's local application data;
this phase does not claim SQLite. Preview results remain explicit and are never marked
as live or playable online audio.

Phase 6E keeps QQ Music, NetEase Cloud Music, and local search independent at the
adapter boundary. Aggregate results deduplicate only the tuple
`PlatformId + ResultType + NativeId`; equal titles across platforms and local files
remain separate. Search suggestions isolate source failures. Online rows now resolve
through the single playback service boundary when credentials and an official source
are available; unauthorized or restricted rows remain safely unavailable. No KuGou
surface was added. Phase 7A through 7F provide secure-store-backed authorization
orchestration, provider-isolated browser/probe boundaries, official URL resolvers,
tested search-to-player integration, concrete WebView2 login hosts, live account
probes, and online lyrics. Users must complete authentication themselves on the
official provider page; automated credential entry is intentionally not implemented.

All networking tests use injected local handlers and sanitized JSON fixtures. They do
not contact QQ Music, NetEase Cloud Music, or any other external service.

## Build and run

```powershell
& 'D:\Apps\DotNetSDK\dotnet.exe' restore '.\Beans.Windows.Rebuild.slnx'
& 'D:\Apps\DotNetSDK\dotnet.exe' build '.\Beans.Windows.Rebuild.slnx' -c Debug --no-restore
& 'D:\Apps\DotNetSDK\dotnet.exe' test '.\Tests\Beans.Windows.Rebuild.Tests.csproj' -c Debug --no-restore
& '.\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Beans.Windows.Rebuild.exe'
```

For a one-click launch, use `Start-BeansMusic.cmd` in this directory. It starts
the Release executable when available and falls back to Debug. To create a
desktop shortcut without enabling startup-on-boot, run:

```powershell
& '.\scripts\New-BeansMusicShortcut.ps1'
```

See `Docs/windows-launch.md` for Debug shortcuts and the Windows-only push boundary.

NuGet packages are restored into this project's ignored `.packages` directory.
The SDK is installed outside the repository at `D:\Apps\DotNetSDK` in accordance
with the machine policy.

## Isolation contract

- `MainWindow` owns only window creation, sizing, caption buttons, and the root shell.
- `AppShell` owns the fixed Sidebar, TopBar, route host, and one BottomPlayer instance.
- `PlaybackService` is registered as a DI singleton and is not recreated by navigation.
- pages do not issue platform HTTP requests or read credentials directly.
- credentials must pass through `ISecureCredentialStore` and Windows Credential Locker.
- PreviewData is centralized under `PreviewData` and is never persisted as real user data.
- the legacy Windows app, Apple clients, server, version, and release workflow remain untouched.

See `Docs/reference-business-audit.md`, `Docs/phase-0-3-status.md`,
`Docs/phase-4-status.md`, `Docs/phase-5a-networking-status.md`,
`Docs/phase-5b-qq-discovery-status.md`,
`Docs/phase-5c-netease-discovery-status.md`, and
`Docs/phase-5e-dual-platform-discovery-baseline.md`, and
`Docs/phase-6a-search-shell-status.md`, and
`Docs/phase-6b-qq-search-status.md`,
`Docs/phase-6c-netease-search-status.md`, and
`Docs/phase-6d-local-music-status.md`, and
`Docs/phase-6d-ui-status.md`, and
`Docs/phase-5b-qq-discovery-ui-status.md`, and
`Docs/phase-6e-search-integration-status.md`, and
`Docs/concurrency-ownership.md`,
`Docs/phase-6f-page-and-contract-status.md`, and
`Docs/phase-7a-auth-infrastructure-status.md`,
`Docs/phase-7b-provider-auth-boundary-status.md` for the audit, delivery, and
`Docs/phase-7c-playback-resolver-status.md` for the audit, delivery, and
parallel-development records.
See `Docs/phase-7d-playback-and-auth-flow-status.md` for the current integration
and explicit remaining boundaries.

The account page requires the local Beans API for Beans registration and sync;
see `Docs/windows-launch.md` for the service startup command and the QQ public
playlist fallback behavior.
