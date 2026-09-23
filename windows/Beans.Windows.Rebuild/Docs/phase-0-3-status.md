# Phases 0-3 Delivery Record

## Added files and boundaries

All source and assets are under `windows/Beans.Windows.Rebuild`. The project has its own
`.slnx`, `.csproj`, ignored `.packages` directory, App/MainWindow, Shell, pages, controls,
models, services, infrastructure, styles, PreviewData, assets, and documentation.

No file under these existing areas was changed by this rebuild:

- `windows/Beans.Windows`;
- `windows/Beans.Core` and its tests;
- `Beans` or `BeansMac`;
- `server`;
- root versioning or release configuration.

## Runnable behavior

- starts as a native WinUI 3/.NET 10 unpackaged desktop preview;
- displays the fixed Sidebar, TopBar, Home page, right playback rail, and BottomPlayer;
- navigates to explicit incomplete placeholders and returns home;
- keeps one `PlaybackService` and BottomPlayer across route changes;
- plays, pauses, seeks, changes volume, moves previous/next, toggles shuffle/repeat and
  favorite state, and publishes System Media Transport Controls metadata;
- renders local Banner, playlist, track, and fallback images without network dependence;
- adapts Sidebar, right rail, playlist count, and BottomPlayer columns at the specified
  window breakpoints.

## Real interfaces versus PreviewData

Real in this round:

- WinUI 3 shell and control interaction;
- local `MediaPlayer` playback and local original WAV source;
- transport state, timeline, queue, volume, seek, repeat, and shuffle mechanics;
- Windows Credential Locker boundary;
- dependency injection, navigation, logging, and typed common platform contracts.

PreviewData in this round:

- Home hero text;
- four recommended playlists;
- five recent/hot tracks;
- current track metadata, five similar recommendations, and three personal playlist rows;
- local Pexels photographs and the generated local preview tone.

No QQ Music, NetEase, KuGou, Beans account, search, download, or local scan request is
presented as complete.

## Not connected yet

- concrete `IMusicPlatformService` and `IMusicPlatformRegistry` adapters;
- online search, discovery, playlist, artist, album, ranking, lyrics, and comments;
- credential login and probe pages;
- remote image cache;
- SQLite persistence, local scanning, durable downloads, and playback history;
- Queue/Lyrics, Music Universe, Accounts, Settings, and Immersive Player pages;
- paper theme settings beyond the current first-round resource dictionary.

## Safety and performance notes

- no credential is embedded in code, XAML, scripts, PreviewData, or logs;
- the credential service uses Windows Credential Locker and logs only a platform ID;
- no provider Cookie, token, playback key, or authorization URL is handled in this round;
- the progress clock updates only observable player fields every 250 ms;
- player and timer lifetime is window-scoped and does not follow page construction;
- local images set decode widths in reusable row/card controls where images are created
  programmatically; remote cache/decode policy remains a later service task;
- Home's track ListView retains virtualization and is not placed inside its own outer
  vertical ListView scroll workaround.

## Verification snapshot

- .NET SDK: `10.0.401` from `D:\Apps\DotNetSDK`;
- build: Debug x64, zero warnings, zero errors;
- XAML compiler: passed; no compile-time resource or binding errors;
- runtime: Home image resources loaded, local audio opened, play/pause glyph changed, and
  both player timelines advanced;
- navigation: incomplete routes visibly state "尚未完成" and keep the BottomPlayer alive;
- the verification monitor used 150% DPI; startup sizing converts effective view dimensions
  to physical pixels with `GetDpiForWindow`, while responsive states use effective width;
- effective 1440 x 900: full Sidebar and right rail, four playlist columns, and the complete
  BottomPlayer were visually inspected with no horizontal scrollbar or overlap;
- effective 1152 x 720: compact Sidebar, hidden right rail, three playlist columns, and the
  compact BottomPlayer were visually inspected with no overlap;
- effective 960 x 600: icon-only Sidebar, hidden right rail, two playlist columns, compact
  top actions, and a full-width BottomPlayer were visually inspected with no overlap.

## Known issues and accepted first-round limits

- the bundled preview audio is 14 seconds, so its runtime duration intentionally differs
  from the fictional 04:29 catalog row used for visual PreviewData;
- online playback address expiry and quality fallback cannot be exercised before concrete
  platform adapters exist;
- credential, download, local scan, and lyrics behavior is defined by contracts/audit only;
- no release package or installer is produced, and this project is not a formal release
  target.

The first round stops here. The next permitted stage is the separately approved search
and platform-adapter round; it must reuse this Shell, BottomPlayer, tokens, and playback
lifecycle.
