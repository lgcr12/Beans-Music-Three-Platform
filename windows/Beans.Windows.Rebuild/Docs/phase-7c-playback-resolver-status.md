# Phase 7C: Provider Playback Source Resolvers

Status: `phase-7c-playback-resolver-baseline`

QQ Music and NetEase Cloud Music now have provider-specific source resolvers
behind `IPlaybackSourceResolver`.

## Completed

- `QqPlaybackSourceResolver` requests the official vkey response, handles
  quality fallback, and converts provider CDN prefixes into an official HTTPS
  `PlaybackSource`.
- `NetEasePlaybackSourceResolver` requests the official player URL response and
  falls back through the requested quality order.
- Both resolvers read the session only from `ISecureCredentialStore`.
- Authenticated request keys contain only a stable hash of the native ID and
  quality; cookies and session values never enter request keys or logs.
- Missing or malformed credentials return `RequiresAuthorization`.
- Provider 301/401/403 and missing URLs return safe restrictions.
- Resolvers never touch `MediaPlayer` or change `PlaybackService` lifetime.
- Both resolvers are registered in DI for the later single-point playback
  integration.

## Not yet completed

- Real WebView2/QR login hosts that populate the secure session values;
- download and online lyric integration.

## Verification

- QQ resolver tests: 3 passed.
- NetEase resolver tests: 3 passed.
- Playback integration tests cover routing, source conversion, and safe
  authorization failure.
- `PlaybackService` retries one failed online source through the same resolver
  boundary and guards against replacing a newer current item.
- Full Debug/Release suite: 272 passed, 0 failed, 0 skipped.
