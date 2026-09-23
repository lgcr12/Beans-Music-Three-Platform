# Phase 7B: QQ Authorization Flow Boundary

Status: `phase-7b-qq-auth-flow-boundary`

This round adds a concrete, provider-isolated QQ authorization flow without
adding a WebView2 package or inventing a login result.

## Implemented

- `QqProviderDomainPolicy` accepts only HTTPS navigation on the exact QQ hosts
  required by the login shell. HTTP, custom ports, user-info URLs, and lookalike
  hosts are rejected.
- `QqAuthorizationCallbackExtractor` validates the allowlisted callback origin,
  requires a cryptographically compared state value, and keeps callback codes
  transient. A code is never treated as a stored credential.
- `QqAuthorizationCookieExtractor` keeps only QQ identity/key cookies (`uin`,
  `wxuin`, `qm_keyst`, `qqmusic_key`, `p_skey`, `skey`) and rejects control
  characters, oversized values, missing identity, or missing login key.
- `QqAuthorizationFlow` requires both an injected browser capture and an
  injected account probe. It returns `Valid`/`Expiring` only after the probe
  confirms the filtered session. Rejected, expired, cancelled, or incomplete
  captures return no credentials.

## Deliberate limitations

- No WebView2 runtime/host is included in this round. A host must implement
  `IQqAuthorizationBrowser`, use an isolated provider profile, enforce the
  supplied allowlist on every navigation, and return an in-memory capture.
- No QQ account-probe HTTP implementation is included. A host must implement
  `IQqAuthorizationProbe` using the approved shared networking boundary before
  registering the flow as live.
- The callback code is not exchanged by this class; the browser/probe boundary
  must complete the provider-specific exchange and expose only the resulting
  cookies. No callback URL, code, cookie jar, or exception text is logged.

## Verification

- `QqAuthorizationFlowTests`: 5 passed.
- Full Debug test suite after this change: 278 passed, 0 failed, 0 skipped.
