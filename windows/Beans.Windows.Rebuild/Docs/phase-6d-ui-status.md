# Phase 6D.1: Local Music UI Status

Status: `phase-6d-ui-baseline`

This increment connects the existing Phase 6D catalog to a dedicated local
music page. QQ Music, NetEase Cloud Music, SearchPage main layout, AppShell
visual geometry, Sidebar, TopBar, BottomPlayer, and PlaybackService lifetime
remain unchanged.

## FolderPicker and routing

`LocalMusicPage` uses the Windows `FolderPicker`, initialized with the current
`MainWindow` HWND through `WindowNative.GetWindowHandle` and
`InitializeWithWindow.Initialize`. A cancelled picker returns quietly. A
selected folder is passed to `ILocalMusicCatalog.AddFolderAsync`; only that
folder is scanned. Duplicate folders show a lightweight status message, and
access failures are mapped to safe UI text. The page supports multiple folders,
removal, re-scan, and cancellation. Removing a folder clears its index records
but does not delete real files.

## LocalMusicPage

`Pages/LocalMusic/LocalMusicPage.xaml` and its code-behind provide:

- add-folder, re-scan, and cancel-scan actions;
- folder cards with display name, availability, track count, and last-scan time;
- background scan invocation with real catalog progress events;
- processed/total, added, updated, and failed counters;
- explicit empty, no-supported-audio, cancelled, unavailable, and error text;
- indexed local track rows with artwork fallback, local source label, LRC state,
  format, duration, tooltip path, and Missing state;
- double-click playback and queue actions through the existing playback service;
- recently played count backed by the local catalog;
- explicit placeholder states for album and artist page filters until a dedicated
  local grouped view is added.

The page keeps the existing shell and player instances. Scanning is dispatched
through `Task.Run`, while catalog progress is marshalled back through the page
dispatcher. Search still queries the catalog and never scans disk as a search
side effect.

## Persistence and privacy

The catalog continues to use the Phase 6D atomic JSON repository at
`%LOCALAPPDATA%\BeansMusic\Rebuild\local-index.json`; SQLite migration is
explicitly out of scope. Folder paths are displayed only in the page tooltip and
are not sent to either online adapter or ordinary network logs. The page does
not upload local audio, artwork, LRC, or metadata.

## Verification

- Debug build: 0 warnings, 0 errors.
- Debug tests: 211 passed, 0 failed.
- Release build: 0 warnings, 0 errors; Release tests: 211 passed, 0 failed.
- Automated Windows UI/manual smoke acceptance: not executed. The configured
  Computer Use RPC returned `Trusted RPC service is not configured`, so no
  screenshot or visual pass claim is made.
- No real user music directory was used. A temporary fixture directory remains
  the only automated local-media input.

## Known limits and Phase 6E boundary

The current page does not add a new SQLite repository, online lyrics, embedded
cover extraction, or grouped album/artist browsing. UI automation and a real
temporary-audio smoke pass remain pending until a working Windows UI automation
runtime is available. Phase 6E is not started; its scope remains combined QQ,
NetEase, and local ranking, cross-source paging/deduplication, and final SearchPage
and DPI acceptance.
