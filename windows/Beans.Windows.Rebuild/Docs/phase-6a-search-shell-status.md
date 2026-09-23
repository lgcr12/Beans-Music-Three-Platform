# Phase 6A：统一搜索架构与 SearchPage 基线

## 1. 范围与平台

本阶段只建立搜索骨架和本地搜索边界，不接入 QQ 音乐或网易云音乐真实搜索 Endpoint，不实现登录、播放地址、收藏、详情、下载或酷狗。

用户可见来源范围固定为：`综合`、`QQ 音乐`、`网易云音乐`、`本地音乐`。酷狗没有进入搜索模型、筛选器或来源栏。

当前状态：

- QQ 音乐：Debug 使用明确标记的 Stub Preview；Release 返回 `QQ 音乐搜索将在后续阶段接入`。
- 网易云音乐：Debug 使用明确标记的 Stub Preview；Release 返回 `网易云音乐搜索将在后续阶段接入`。
- 本地音乐：当前没有真实目录索引，返回 `CatalogUnavailable` 和 `添加本地音乐目录后，可在这里搜索歌曲`；搜索不会扫描磁盘。

## 2. 文件与组件树

核心新增文件：

- `Models/SearchModels.cs`
- `Services/Search/IPlatformSearchAdapter.cs`
- `Services/Search/SearchAdapters.cs`
- `Services/Search/MusicSearchService.cs`
- `ViewModels/SearchViewModel.cs`
- `Pages/Search/SearchPage.xaml`
- `Pages/Search/SearchPage.xaml.cs`
- `Tests/Phase6ASearchTests.cs`

修改文件：

- `App.xaml.cs`：注册 Search Service、三个边界 Adapter 和 SearchViewModel；
- `Shell/TopBar.xaml(.cs)`：复用现有搜索框，增加 TextChanged、防抖建议 Popup 和 Enter 提交；
- `Shell/AppShell.xaml(.cs)`：接入搜索事件、SearchPage 路由和既有 BottomPlayer/PlaybackService；
- `README.md`：更新当前阶段与搜索边界。

页面结构：

```text
AppShell
├── TopBar
│   ├── 既有全局搜索框
│   └── SearchSuggestion Popup
├── SearchPage
│   ├── Header / QueryTitle / ResultSummary
│   ├── 来源范围：综合 / QQ 音乐 / 网易云音乐 / 本地音乐
│   ├── 结果类型：全部 / 歌曲 / 专辑 / 歌手 / 歌单
│   ├── ResultsPanel
│   │   ├── ResultsList
│   │   ├── Loading / Empty / Partial / Notice
│   │   └── 播放与更多菜单
│   └── SourceRail
└── 既有 BottomPlayer
```

没有创建第二个搜索框、第二个播放器或第二个 PlaybackService。

## 3. 统一模型

`SearchQuery` 包含 `Keyword`、`Filter`、`Scope`、`Offset`、`PageSize`、`EnabledPlatformIds`、`IncludeLocalMusic`、`IsLoadMore`、`RequestGeneration` 和 `ForceRefresh`。`RequestKey` 由规范化关键词、来源范围、筛选、分页和本地开关组成，不包含凭证或完整路径。

`SearchResultItem` 统一承载 Track、Album、Artist、Playlist，并保留 `Platform`、`NativeId`、`StableId`、标题、艺术家、专辑、来源、音质、可播放性、限制状态、`DataOrigin` 和 PayloadReference。稳定身份格式为：

```text
PlatformId + ResultType + NativeId
```

本地条目的 NativeId 来自注入 Catalog 的稳定标识；完整文件路径不进入 UI 文本、日志或稳定 ID。

`SearchSourceStatus` 承载来源、状态、数量、耗时、安全消息、授权要求、启用状态和 DataOrigin。状态包括 Idle、Searching、Succeeded、Empty、Error、Unauthorized、Disabled、Unsupported、CatalogUnavailable 和 Preview。

`AggregatedSearchResponse` 承载 Query、Items、Sources、TotalCount、Elapsed、HasMore、IsPartialSuccess、SafeMessage、DataOrigin 和 LoadedAt。

## 4. Adapter 与聚合

`IPlatformSearchAdapter` 提供 `Platform`、`IsEnabled`、`SearchAsync` 和 `GetSuggestionsAsync`。本阶段实现：

- `QqMusicSearchStubAdapter`；
- `NetEaseMusicSearchStubAdapter`；
- `LocalMusicSearchAdapter`；
- `SearchPreviewAdapter`（供明确的 Preview 场景和离线测试使用）。

`MusicSearchService` 按来源范围决定调用集合。综合只调用启用的 QQ/网易云和本地 Adapter；QQ 范围不调用网易云；网易云范围不调用 QQ；本地范围不调用在线 Adapter。

聚合保持来源独立：跨平台同名结果不去重；同平台、同类型、同 NativeId 按 StableId 去重。排序固定为 Track、Album、Artist、Playlist，再按本地、QQ、网易云和 NativeId 稳定排序。任一来源错误只进入 SourceRail，其他成功来源继续显示。

## 5. 防抖、取消与晚到保护

TopBar 输入至少两个字符后，`SearchViewModel.UpdateDraftAsync` 等待 280ms，再请求建议；新输入会取消旧建议。建议最多显示六项，可在 TopBar Popup 或 SearchPage 标题下选择，选择后提交正式搜索。

正式搜索由 `SearchViewModel` 持有独立 CancellationTokenSource 和递增 `RequestGeneration`。新关键词、来源范围、结果类型或离开页面都会取消旧请求。结果提交前同时检查 generation 和取消状态，因此旧关键词、旧来源或离开页面后的晚到响应不能覆盖当前页面。相同关键词、来源和筛选条件在非强制刷新时不会重复请求。

## 6. 搜索页面交互

Enter 从 TopBar 导航到 SearchPage；再次提交会更新当前查询。结果行支持点击播放和更多菜单。真实可播放本地条目将复用现有 `IPlaybackService`；当前在线 Stub/Preview 条目标记不可播放，并显示 `在线播放将在后续播放适配阶段开放`，不会伪造在线播放地址。

更多菜单保留播放、下一首播放、加入队列、打开歌手、打开专辑、打开歌单、添加到歌单和收藏入口。详情操作进入既有占位路由并携带 PlatformId、NativeId 和标题；歌单写入与收藏项在本阶段明确禁用。

AppShell 始终复用同一个 PlaybackService 和 BottomPlayer。搜索页不会重建它们。当前本地 Catalog 不可用，因此没有真实本地播放条目；本地索引接入后只需实现 `ILocalMusicSearchCatalog`，无需改写 SearchPage。

## 7. Preview 与 Release

Debug 的 QQ/网易云 Stub 可以返回 `SearchDataOrigin.Preview`，来源栏显示“预览结果”，每个在线 Preview 结果 `IsPlayable = false`。Release 的 Preview 策略关闭，Stub 只返回 Unsupported 和“后续阶段接入”安全文案，不静默展示 Preview。

本地 CatalogUnavailable 是明确的真实能力边界，不使用 QQ/网易云数据冒充本地音乐，不写入索引、不写搜索历史、不发真实外网请求。

## 8. 测试与构建

Phase 5E 基线为 140 项；Phase 6A 新增 13 个测试执行实例，总计 153 项。新增覆盖：StableId、跨平台同名隔离、同平台去重、来源范围调用隔离、聚合局部失败、本地 CatalogUnavailable、Debug/Release Stub 边界、建议、重复请求抑制、取消/晚到保护和结果筛选。

最终验证：

- Debug build：0 warnings，0 errors；
- Release build：0 warnings，0 errors；
- Debug tests：153 passed，0 failed，0 skipped；
- Release tests：153 passed，0 failed，0 skipped；
- 测试不依赖真实外网。

## 9. 已知限制与后续接入点

- QQ 和网易云真实搜索尚未接入；
- 本地音乐目录索引尚未实现；
- 在线 Preview 不可播放；
- SearchPage 的视觉结构已接入既有 Token 和断点，但仍需在可用的原生 UI 工具中完成截图级 DPI、滚动、重叠和 XAML Binding Error 验收；
- 真实 QQ 搜索应在 Phase 6B 实现 `IPlatformSearchAdapter` 的公开搜索能力、稳定 NativeId 映射、真实 Live/Cache 状态、平台请求键和同平台缓存，不修改 SearchPage、聚合规则或 PlaybackService 生命周期。

Phase 6A 到此停止，不开始 Phase 6B。
