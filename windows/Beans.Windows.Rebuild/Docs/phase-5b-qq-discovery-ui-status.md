# Phase 5B.1: Discovery UI Status

Status: `phase-5b-qq-discovery-ui-baseline`

This is a UI/status tightening pass over the existing DiscoverPage. The QQ
anonymous discovery requests and adapters are unchanged. The page remains the
full Discover route with its title, platform selector, platform status, refresh
action, Hero, ranking section, playlist section, and existing shell/player.

## Changes

- Ranking cards no longer rely on one fixed width. Their container widths are
  recalculated from the rendered list width: four columns when wide, three at
  medium width, and two at compact width, with a visible internal horizontal
  scroll area for additional rankings.
- The ranking card keeps its own image and information composition rather than
  reusing the playlist card template. Images remain uniformly filled within a
  dedicated ranking ratio.
- Hero masking is now left-weighted: the right side keeps more artwork detail,
  while the title remains readable with a wider left inset.
- Recommended playlists now have explicit loading/empty/unsupported/error and
  stale-cache text. An empty list collapses the fixed card rail, so the page no
  longer leaves a large unexplained blank block.

## Scope and verification

No QQ/NetEase request logic, SearchPage, AppShell, Sidebar, TopBar,
BottomPlayer, or PlaybackService lifetime was changed. No Preview playlist is
used to hide a real empty/error state.

- Debug build: 0 warnings, 0 errors.
- Release build: 0 warnings, 0 errors.
- Debug tests: 211 passed.
- Release tests: 211 passed.
- Automated visual/UI acceptance: not executed. Windows Computer Use returned
  `Trusted RPC service is not configured`, so no screenshot or DPI pass is
  claimed.

The 1440x900, 1152x720, and 960x600 layouts are covered by the responsive
width calculation, but require a working UI automation or manual desktop pass
for visual confirmation. Phase 6E is not started.
