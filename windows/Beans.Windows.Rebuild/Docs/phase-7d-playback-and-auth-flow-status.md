# Phase 7D: Playback Integration and Provider Auth Flow Boundaries

Status: `phase-7d-playback-integration-baseline`

This phase connects the existing provider source resolvers to the single
window playback service and completes provider-specific, testable
authorization-flow boundaries.

## Completed

- `PlaybackSourceResolverRouter` routes one request to exactly one QQ or
  NetEase resolver by `PlatformId`.
- `PlaybackService.PlaySearchResultAsync` and
  `QueueSearchResultAsync` resolve online search tracks before creating the
  existing `PlaybackItem`. Local playback continues through the same
  `MediaPlayer` singleton.
- Search actions can play, queue, or insert an online track after successful
  source resolution. Missing credentials, provider restrictions, invalid
  sources, and unsupported result types remain safe failures.
- A failed online source gets at most one fresh official-source resolution;
  the retry is cancelled when the user changes the current item and never
  loops on a provider failure. Local files do not enter this retry path.
- Library, playlist, artist, album, and ranking pages share the same playback
  entry point. Their bundled PreviewData is explicitly rejected; only future
  Live/Cache items with valid provider-native IDs can enter source resolution.
- QQ and NetEase each have an isolated browser-capture/probe flow boundary.
  The flows enforce exact HTTPS provider hosts, filter cookies, validate
  callback state where applicable, and require a provider account probe before
  returning `Valid` or `Expiring`.
- The shared DI container uses `CredentialBackedPlatformAuthService` with the
  two provider adapters and keeps the secure credential store as the only
  persistence boundary.

## Explicitly not claimed

- No fake login, callback token, cookie, or playback URL is generated.
- The concrete WebView2 host, QR UI, live provider account probe, refresh-token
  rotation, and provider-specific callback exchange are still external host
  work. Until those are supplied, the default adapters safely report that the
  login window is not connected.
- Downloads, online lyrics, favorites, playlist mutations, and playback URL
  refresh after an active `MediaPlayer` failure remain later phases.

## Verification

- Debug build: 0 warnings, 0 errors.
- Debug tests: 272 passed, 0 failed, 0 skipped.
- Release build: 0 warnings, 0 errors.
- Release tests: 272 passed, 0 failed, 0 skipped.
- UI automation, live account login, and DPI screenshot acceptance remain
  unexecuted in this environment.
