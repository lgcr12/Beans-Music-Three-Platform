# Download management baseline

## Implemented boundary

The rebuild now has a persistence-backed `IDownloadManager` independent from
`PlaybackService` and the shared discovery/search HTTP contracts. It supports:

- durable task and selected-directory state with atomic JSON replacement;
- enqueue, pause, range-aware resume, cancel, and record removal;
- a bounded worker pool (two concurrent transfers by default);
- HTTPS-only transfer sources, an audio extension/media-type allowlist, redirect
  rejection, response-length validation, and safe request-header handling;
- sanitized, task-unique file names and a containment check before every move,
  truncate, or partial-file deletion;
- a pre-transfer disk-space check with reserved free space;
- explicit unsupported, authorization, storage, file, network, invalid-response,
  cancelled, and failed states with safe UI messages;
- restart recovery: a task that was queued, resolving, or downloading is restored
  as paused rather than silently restarting;
- partial-file cleanup on cancel/removal while removal of a completed record does
  not delete the user's completed audio file.

`DownloadsPage` now reads the durable task list, lets the user select the target
folder, renders progress and safe state text, and exposes the supported controls.

## Authorization boundary

Production registration deliberately uses `UnsupportedDownloadSourceResolver`.
Neither playback sources nor PreviewData are accepted as offline download sources.
QQ Music or NetEase Music must later provide a dedicated resolver that proves the
account and content are authorized for offline saving before `HttpDownloadTransport`
can run. No provider URL is fabricated and no unresolved source is treated as live.

## Deliberate limits

- Provider-specific authorized offline-source resolvers are not implemented.
- Search/detail pages do not yet enqueue downloads; the service contract is ready
  for that integration after a provider resolver exists.
- Active transfers do not continue while the application process is stopped. Their
  partial files and durable records are recovered as paused on the next launch.
- Persistence follows the rebuild's current atomic JSON repository approach rather
  than introducing a SQLite dependency in this isolated phase.
- UI automation, screenshots, and DPI acceptance are not claimed by this baseline.
