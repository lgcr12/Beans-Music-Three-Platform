# Phase 6F: Page Batch And Shared Boundary Status

Status: `phase-6f-page-shell-and-boundary-baseline`

This increment completes the first low-coupling page batch after Phase 6E and
freezes the next authorization/playback boundaries. The work remains within the
Windows rebuild and does not claim real platform login or online playback.

## Completed pages

The following routes now have page implementations instead of the generic
placeholder:

```text
library
playlists
playlist
artist
album
ranking-detail
settings
accounts
downloads
queue
lyrics
universe
```

Page-local preview data is explicit and retains platform context. Local rows keep
the existing local playback boundary. QQ Music and NetEase Cloud Music rows show
the safe online playback restriction rather than fabricating a URL. Queue view
uses the existing singleton `IPlaybackService` and unsubscribes when unloaded.

## Frozen authorization boundary

The following contracts are now available for later platform adapters:

```text
IPlatformAuthService
PlatformAccountState
AuthorizationResult
```

`PreviewPlatformAuthService` deliberately reports `NotAuthorized` and a safe
future-stage message. Phase 7A additionally provides
`CredentialBackedPlatformAuthService` for real adapters, but no QQ or NetEase
adapter is registered yet. The existing `ISecureCredentialStore` remains the
only credential storage boundary.

## Frozen playback-source boundary

The following contracts are available for later provider-specific resolvers:

```text
IPlaybackSourceResolver
PlaybackSourceRequest
PlaybackSourceResult
PlaybackRestriction
```

`OnlinePlaybackBoundaryResolver` returns no URI and requires authorization for QQ
Music and NetEase Cloud Music. It never touches `MediaPlayer` or changes
`PlaybackService` lifecycle. A later QQ or NetEase resolver must implement this
contract before the single PlaybackService integration step.

## Scope protection

No QQ or NetEase endpoint, search contract, local scan core, global style, Shell
geometry, BottomPlayer, or existing playback lifecycle was redesigned. No login,
download, online URL, or provider credential was added. KuGou remains disabled.

## Verification

- Debug build: 0 warnings, 0 errors.
- Release build: verified after the page and contract changes.
- Debug and Release tests: 243 passed, 0 failed.
- Visual/DPI automation remains unexecuted because the configured Computer Use
  RPC is unavailable; no screenshot acceptance claim is made.
