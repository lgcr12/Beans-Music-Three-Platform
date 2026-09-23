# Phase 6E: Search Integration Status

Status: `phase-6e-search-integration-baseline`

The S1 search-integration task is complete. The following contracts are frozen
for later page, authorization, and playback work:

```text
SearchQuery
SearchResultItem
AggregatedSearchResponse
SearchSourceStatus
IMusicSearchService
IPlatformSearchAdapter
```

Later tasks may consume these contracts but must not redesign the shared search
surface while implementing page-local features.

This increment closes the shared search surface for QQ Music, NetEase Cloud
Music, and the local catalog. It does not start online authorization or Phase 7.
SearchPage geometry, AppShell, TopBar, BottomPlayer, PlaybackService lifetime,
the two online adapters, and LocalMusicCatalog scanning remain unchanged.

## Source architecture

`MusicSearchService` resolves the requested scope and runs the selected platform
adapters concurrently. Aggregate scope resolves QQ Music, NetEase Cloud Music,
and local music when the local adapter is registered. QQ, NetEase, and Local
scopes call only their own adapter. Missing adapters are represented as a disabled
source boundary; the local adapter itself reports `CatalogUnavailable` until a
real indexed catalog is available. Search never scans a disk.

Each adapter response remains source-isolated in `SearchSourceStatus`. A thrown
adapter error becomes a safe source error and does not remove results returned by
other sources. Cancellation is propagated rather than converted into an error.

## Aggregation, deduplication, and ordering

The aggregate keeps the adapter-reported `TotalCount` for paging and provider
metadata, while `Items` contains unique rows. Deduplication uses only:

```text
PlatformId + ResultType + NativeId
```

Therefore QQ and NetEase rows with the same title remain separate, local files do
not merge with online rows, and different result types or native IDs remain
distinct. `StableId` is retained as the row identity and final deterministic tie
breaker; it is not used as a substitute for the explicit dedupe tuple.

Ordering is deterministic: exact title matches, title prefixes, title contains,
artist matches, album matches, then other matches; within a match tier, tracks,
albums, artists, and playlists are ordered consistently. The resolved source order
and each adapter's original item order are retained before `StableId` is used as a
last tie-breaker. Repeating the same request therefore does not randomly reorder
rows.

`HasMore` is the OR of source paging flags. The service preserves each source's
status and count, but SearchPage does not yet expose a cross-source load-more
control. Full per-source continuation remains a later search-surface increment.

## Status and data origins

The service distinguishes `Live`, `CacheFresh`, `CacheStale`, and `Preview` on
both source status and result rows. Preview and stale data are never silently
relabeled as live. SearchViewModel presents:

- `部分来源不可用，已显示可用结果` when usable rows coexist with a source failure;
- `网络不可用，正在显示上次搜索结果` for stale-only result visibility;
- `当前显示预览结果` for preview-only result visibility;
- `没有匹配结果` when all selected sources complete empty;
- `当前来源暂时无法搜索` when all selected sources fail;
- the local adapter's `尚未添加本地音乐目录` when a local-only catalog is unavailable.

The source rail keeps independent QQ, NetEase, and local messages. A local
`CatalogUnavailable` response is never converted into an ordinary empty result.

## Suggestions and request lifecycle

Suggestion calls use a scope-aware request key, cancel the previous input, dedupe
text case-insensitively, and isolate one source's suggestion failure from the
others. Identical completed or in-flight search requests are suppressed. New
queries and scope/filter changes increment the generation, cancel the prior
request, and reject late responses. Leaving SearchPage cancels both search and
suggestion work.

## Playback boundary

Local rows are playable only when the indexed file still exists and carry their
real local path through the existing `PlaybackService`. Missing files show a safe
local-file message. QQ Music and NetEase Cloud Music rows remain non-playable;
clicking or queue actions show `在线播放适配将在后续阶段接入`. No login, online
playback URL, download, or second player was introduced.

## Files changed

- `Services/Search/MusicSearchService.cs`
- `Models/SearchModels.cs`
- `ViewModels/SearchViewModel.cs`
- `Pages/Search/SearchPage.xaml.cs`
- `Tests/Phase6ESearchIntegrationTests.cs`
- `README.md`

No QQ/NetEase endpoint or mapper, local scanning core, SearchPage geometry,
AppShell, BottomPlayer, PlaybackService lifecycle, legacy Windows project, Apple,
server, version, or release workflow was modified.

## Verification

- Debug build: 0 warnings, 0 errors.
- Debug tests: 218 passed, 0 failed.
- Release build: 0 warnings, 0 errors.
- Release tests: 218 passed, 0 failed.
- Windows UI automation/manual smoke acceptance: not executed. The configured
  Computer Use RPC previously returned `Trusted RPC service is not configured`;
  no screenshot or visual/DPI pass claim is made.
- No real user music directory was used by tests.
- No sensitive values or credentials were added to source or logs.

## Phase 7 boundary

The next phase may add explicit authorization and provider-approved online
playback resolution at the existing `SearchResultItem`/`PlaybackService` boundary.
It must not infer URLs from anonymous search responses, add credentials to code or
startup scripts, or bypass provider access and licensing controls.
