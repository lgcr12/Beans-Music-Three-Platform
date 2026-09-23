# Phase 7E: Lyrics Baseline

Status: `phase-7e-dual-platform-lyrics-baseline`

## Completed

- One DI-owned `ILyricsService` selects source adapters by `PlatformId`, caches
  normalized results, supports invalidation, propagates cancellation, and maps
  failures to safe states.
- `LrcParser` supports offset metadata, multiple timestamps per line,
  duplicate-timestamp translation merging, malformed-line filtering, and
  instrumental detection.
- Local lyrics use an indexed `LrcPath` or a same-name `.lrc` file beside the
  audio file. File reads and parsing are cancellable.
- `LyricsPage` follows the current singleton playback item, shows loading,
  empty, unsupported, and error states, and selects the current lyric line as
  playback position changes.
- QQ online lyrics use the repository reference endpoint on the exact
  `c.y.qq.com` provider host. Song MID values are allow-list validated before
  request construction, response HTML entities are decoded, and provider
  business failures become safe lyric states.
- NetEase online lyrics use the repository reference endpoint on the exact
  `music.163.com` provider host. Numeric song IDs are validated before request
  construction, and `lrc` plus `tlyric` are merged by effective timestamp while
  preserving each document's declared offset.
- Both online adapters use the shared `IPlatformHttpClient`; request keys carry
  only a deterministic native-ID hash, query values and response bodies never
  enter safe logs, and no account cookie or credential is required or read.
- Provider-declared missing, uncollected, and pure-music responses are rendered
  as explicit safe states rather than invented lyrics.
- Loaded online lyrics expire after six hours; negative results expire after
  ten minutes and Unsupported results after five minutes. Local lyrics keep
  the existing explicit-invalidation cache behavior.

## Remaining boundary

- Word-level karaoke timing is preserved by the common model but is not
  produced by the current LRC parser.
- The provider endpoints are public/anonymous reads. Authenticated lyric
  variants are intentionally not added, and the adapters never imply account
  authorization or membership rights.

## Verification

- Live endpoint contract check on 2026-09-21: QQ song MID
  `0039MnYb0qxYhV` and NetEase song ID `186016` both returned HTTP 200 with
  real timed lyric payloads.
- Automated coverage includes request host/path/referrer, native-ID rejection,
  absence of account cookies, QQ entity decoding, NetEase translation/offset
  merging, missing/pure-music states, and online cache expiry.
- Debug and Release isolated builds: 0 warnings, 0 errors.
- Debug and Release aggregate tests at this baseline: 295 passed, 0 failed,
  0 skipped. No UI acceptance is inferred from the endpoint or unit checks.
- UI automation remains unexecuted because Computer Use could not enumerate
  the running application window.
