# Phase 7F: Account Probe Baseline

Status: `phase-7f-provider-account-probe-baseline`

## Completed

- `QqAuthorizationProbe` validates the filtered QQ session through the
  official profile endpoint and maps rejected sessions to `Expired` without
  exposing provider response text.
- `NetEaseAuthorizationProbe` validates the filtered `MUSIC_U` session through
  the official account endpoint and requires a positive profile user ID.
- Both probes use the shared `IPlatformHttpClient` boundary, bounded timeout,
  cancellation, safe parsing, account-scoped request identity hashes, and
  Cookie headers that never enter request keys or logs.
- Probe results are only returned to the provider auth flow. They do not write
  credentials and cannot mark a session valid without the flow's own cookie
  filtering.
- `ProviderAuthorizationBrowserHost` now supplies both provider flows with an
  in-private, provider-specific WebView2 profile. Top-level navigation and new
  windows are restricted to exact HTTPS hosts; only allowlisted session cookie
  names leave the browser boundary, and browser cookies are cleared afterwards.
- The account page exposes login, live state refresh, and logout. Successful
  probe-validated sessions are persisted only by the shared coordinator through
  Windows Credential Locker.

## Remaining validation boundary

- A real user must finish each provider's official login page and select
  `验证登录`. Automated password, QR, or CAPTCHA interaction is intentionally
  excluded.
- Computer Use could not enumerate the Windows desktop, so the live provider
  dialog, 125%/150% DPI, and screenshot acceptance remain unexecuted.

## Verification

- Focused probe tests: 4 passed.
- Full Debug tests: 295 passed, 0 failed.
- Full Release tests: 295 passed, 0 failed.
- Debug/Release builds: 0 warnings, 0 errors.
