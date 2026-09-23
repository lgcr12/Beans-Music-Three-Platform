# QQ discovery test fixtures

- Purpose: offline parser, mapper, adapter, cache, and aggregation tests for Phase 5B.
- Structure source: `Beans/QQMusicAPI.swift`, specifically `topLists()` and `recommendPlaylists()`.
- Constructed: 2026-09-20.
- Sanitization: all IDs, titles, creators, counts, and image paths are synthetic.
- Excluded data: credentials, account identifiers, personal profiles, membership data, playback URLs, playback keys, signatures, and complete request headers.
- Packaging: copied only by `Beans.Windows.Rebuild.Tests.csproj`; excluded from the application project.

No fixture is evidence of a successful live QQ Music request.
