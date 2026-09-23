# Phase 7B: Provider Authorization Adapter Boundaries

Status: `phase-7b-provider-auth-boundary-baseline`

QQ Music and NetEase Cloud Music now have isolated adapter contracts that can
be implemented independently of the shared authorization coordinator.

## Adapter boundaries

- `QqMusicAuthAdapter` owns only QQ credential names and QQ flow validation.
- `NetEaseMusicAuthAdapter` owns only NetEase credential names and NetEase flow
  validation.
- Both adapters accept only `session` and `refresh` entries, reject unknown or
  unsafe values, propagate cancellation, and never write to secure storage.
- The default adapters do not make network requests and return an explicit
  not-connected result. They do not claim `Valid` for opaque persisted values.
- `QqAuthorizationFlow` and `NetEaseAuthorizationFlow` now provide the
  provider-domain constrained browser-capture and account-probe boundary.
  They remain host-injected: no WebView2 instance or live provider probe is
  created by the default application composition root.

## Verification

- QQ adapter tests: 7 passed.
- NetEase adapter tests: 3 passed.
- Full Debug/Release suite: 272 passed, 0 failed, 0 skipped.

Online playback remains behind the separate `IPlaybackSourceResolver` contract;
these adapters do not produce playback URLs.
