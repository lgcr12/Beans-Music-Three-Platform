# Phase 6D: Local Music Status

Status: `phase-6d-local-music-baseline`

Phase 6D adds the local music catalog boundary used by the existing search and
playback surfaces. The work is limited to `windows/Beans.Windows.Rebuild`; QQ
Music and NetEase Cloud Music adapters, the SearchPage geometry, the legacy
Windows application, and release metadata are unchanged.

## Delivered

- `Services/LocalMusic/LocalMusicModels.cs` defines folders, tracks, scan
  progress, metadata state, lyric source, play history, and catalog contracts.
- `Services/LocalMusic/LocalMusicCatalog.cs` provides folder add/remove,
  recursive scanning, progress events, cancellation, incremental replacement,
  missing-file marking, local tracks/artists/albums search, paging, and local
  playback history.
- `Services/LocalMusic/AudioMetadataReader.cs` reads the basic WAV duration and
  uses safe filename/fallback metadata for formats that do not have a bundled
  parser. Metadata failures are represented as `Fallback` or `Failed` state.
- `Services/LocalMusic/LrcFileResolver.cs` associates same-folder `.lrc` files
  using exact and normalized filename matching. Remote lyrics are not fetched.
- `Services/LocalMusic/LocalPlaybackSourceFactory.cs` validates file existence
  and access before returning a local `file:` playback URI.
- `Services/LocalMusic/LocalMusicPersistence.cs` writes the catalog through an
  atomic temporary-file replacement. The default path is
  `%LOCALAPPDATA%\BeansMusic\Rebuild\local-index.json`.
- `App.xaml.cs` registers one catalog instance for both the local catalog and
  the existing `ILocalMusicSearchCatalog` adapter boundary.
- `Tests/LocalMusicCatalogTests.cs` covers folder normalization, scanning,
  extension filtering, LRC association, stable IDs, update and missing-file
  behavior, search paging, cancellation, playback-source validation, and
  completed-play history.

## Scan and incremental rules

The scanner accepts `.mp3`, `.flac`, `.m4a`, `.aac`, `.wav`, `.ogg`, `.opus`,
and `.wma`, skips hidden files and files above the configured size limit, and
recurses only through folders explicitly added by the user. A track ID is
stable for a normalized path:

```text
local:track:{lowercase first 12 bytes of SHA-256(normalized path)}
```

Rescanning replaces the record at that ID instead of duplicating it. A file
that disappears from an indexed folder is retained with `IsMissing = true` and
is excluded from normal search and recently played results. Removing a folder
does not delete real files; by default it removes that folder's index records.
Cancellation returns a cancelled summary and does not persist partial scan
writes.

## Metadata, artwork, and lyrics boundaries

The reader keeps metadata local. WAV duration is parsed from the RIFF header;
other formats use the filename and safe fallback values until a vetted parser
is introduced. Embedded artwork is not uploaded; the current fallback artwork is
the packaged Beans icon. Only same-folder local LRC files are associated, and
no network lyric or cover lookup is performed.

## Search and playback

The existing local search adapter now receives the DI-owned catalog. It returns
`CatalogUnavailable` before a usable indexed track exists, then serves local
track, artist, and album results with stable IDs and local playback URIs.
Playlist search remains empty for the local source. The playback source factory
checks `IsMissing`, file existence, and access permissions before producing a
source. A playback start records `LastPlayedAt`; play count and completion
history are updated only when completion is reported.

## Storage and privacy boundary

The current implementation intentionally uses a local atomic JSON repository,
not SQLite. No SQLite package was added to this rebuild, so this delivery must
not be described as a SQLite implementation. The index and history remain on
the local machine; full local paths are not sent to QQ Music or NetEase Cloud
Music adapters and are not written to network logs. Tests use temporary fixture
folders only and never enumerate a real user music directory.

## Verification

- Debug build: 0 warnings, 0 errors.
- Release build: verified separately with the same solution and no warnings or
  errors.
- Debug tests: 211 passed, 0 failed.
- Release tests: verified separately, 211 passed, 0 failed.
- Local catalog focused tests: 10 passed, 0 failed.
- Local smoke test against a real user music directory: not executed.
- UI manual acceptance of folder selection, progress display, and local playback:
  not executed. No screenshots or visual claims are made.

## Known limitations and Phase 6E boundary

The catalog API is now consumed by the Phase 6D.1 native Windows folder-picker
page without changing the existing shell geometry. Only a user-selected folder
is passed to `AddFolderAsync`; no automatic user-directory scan is performed.
Artwork extraction is currently a
packaged fallback, and LRC association stores the local file boundary without
adding an online lyric parser. Playback history is exposed through explicit
catalog methods while the existing `PlaybackService` lifecycle remains unchanged.

Phase 6E is the later integration point for combined QQ, NetEase, and local
ranking, cross-source paging and deduplication policy, native local-folder UI,
and final DPI/UI acceptance. It is not started by this delivery.
