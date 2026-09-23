# Phase 7A: Authorization Infrastructure

Status: `phase-7a-auth-infrastructure-baseline`

This phase establishes the shared authorization orchestration boundary. It does
not claim that QQ Music or NetEase Cloud Music login is live yet.

## Completed

- `IPlatformAuthAdapter` isolates provider login and credential validation.
- `CredentialBackedPlatformAuthService` is the single coordinator for state,
  authorization, and logout.
- Provider adapters cannot access the secure store directly.
- Multiple credential entries are supported through `CredentialNames`.
- Empty, whitespace-only, mismatched-platform, expired, or invalid sessions are
  never persisted as authorized credentials.
- `ISecureCredentialStore` remains the only persistence boundary and logout
  removes only the selected platform's entries.
- Cancellation is propagated through state checks, authorization, and logout.
- Missing adapters return `CanAuthorize = false` and a safe unsupported message.

## Follow-up boundary

Provider-specific browser-capture/probe flows now exist in the QQ and NetEase
adapter directories. They are injectable and do not depend on WebView2 types,
so the shared service remains unit-testable. A real WebView2 host, QR UI, live
account probe, and refresh-token rotation are still required before live login
can be enabled.

## Verification

- Debug build: 0 warnings, 0 errors.
- Release build: 0 warnings, 0 errors.
- Debug and Release tests: 272 passed, 0 failed, 0 skipped.
- UI automation/DPI verification remains unexecuted because the configured
  Computer Use RPC is unavailable.
