# Phase 6C：网易云音乐真实公开搜索适配

## 1. 范围与支持矩阵

本阶段只替换网易云 Search Stub，复用 Phase 6A/6B 的 `IPlatformSearchAdapter`、统一聚合、共享网络层和 `DiscoveryCache`。没有修改 SearchPage 几何、AppShell、TopBar、HomePage、DiscoverPage、BottomPlayer、PlaybackService、QQ Search Adapter、登录、Cookie、用户接口、播放地址、评论、详情、下载、本地扫描或酷狗。

| 类型 | 状态 | 协议 |
| --- | --- | --- |
| 歌曲 | 已接入 | 匿名 WEAPI `POST /weapi/cloudsearch/pc`，`type=1` |
| 专辑 | 已接入 | 匿名 WEAPI `POST /weapi/cloudsearch/pc`，`type=10` |
| 歌手 | 已接入 | 匿名 WEAPI `POST /weapi/cloudsearch/pc`，`type=100` |
| 歌单 | `Unsupported` | 参考实现未提供稳定匿名歌单搜索实现，不发请求 |
| 输入建议 | `Unsupported` | 参考实现只有热搜词接口，没有稳定按关键词联想接口，不把热搜冒充建议 |
| 每日推荐 | `RequiresAuthorization` | 继续由发现 Adapter 的授权边界处理，不进入搜索 Adapter |

## 2. 新增和修改文件

新增：

- `Services/Platforms/NetEase/Dto/Search/NetEaseSearchDtos.cs`
- `Services/Platforms/NetEase/NetEaseSearchRequestKeys.cs`
- `Services/Platforms/NetEase/NetEaseSearchMapper.cs`
- `Services/Platforms/NetEase/NetEaseMusicSearchAdapter.cs`
- `Tests/Infrastructure/NetEaseSearchTestInfrastructure.cs`
- `Tests/NetEaseSearchAdapterTests.cs`
- `Tests/Fixtures/NetEase/netease-search-*.json`
- `Docs/phase-6c-netease-search-status.md`

修改：

- `App.xaml.cs`：同一个 `IPlatformSearchAdapter` 注册点替换为 `NetEaseMusicSearchAdapter`；
- `Services/Search/SearchAdapters.cs`：移除网易云生产 Stub，保留通用显式 `SearchPreviewAdapter` 供离线 Preview 测试；
- `Tests/Phase6ASearchTests.cs`：将旧 Stub 边界测试改为显式 Preview Adapter 测试；
- `README.md`：更新当前阶段状态。

QQ 生产代码和 Phase 5A 网络实现没有修改。

## 3. 参考协议与匿名请求

参考仓库 `Beans/NetEaseAPI.swift` 的公开搜索实现使用：

```text
https://music.163.com/weapi/cloudsearch/pc
```

请求方法为 `POST`，Content-Type 为 `application/x-www-form-urlencoded`，表单只包含加密后的 `params` 和 `encSecKey`。加密公共负载由 `NetEaseSearchProtocol.CreatePayload` 明确构造：

```text
s       = 规范化关键词
type    = 1 歌曲 / 10 专辑 / 100 歌手
limit   = 1..50
offset  = SearchQuery.Offset
total   = true
```

`NetEaseWeapi` 只复用公开搜索所需的匿名 WEAPI 包装。它不读取或生成用户 Cookie，不发送 `MUSIC_U`、账号 ID、Authorization 或 Token；随机会话密钥只用于单次表单加密，不记录、不持久化。请求继续经过 `IPlatformHttpClientFactory.Get("netease")`、`PlatformRequestContext`、共享超时、限流、一次重试、请求合并、大小限制、取消和安全日志。

日志只保留 `music.163.com` 与 `/weapi/cloudsearch/pc`，不会记录 QueryString、加密表单、关键词、Cookie 或响应体。

## 4. DTO 与业务码

`Dto/Search/NetEaseSearchDtos.cs` 建模真实搜索响应：

- `NetEaseTrackSearchResponse`：`code`、`result.songCount`、`result.songs`；
- `NetEaseAlbumSearchResponse`：`code`、`result.albumCount`、`result.albums`；
- `NetEaseArtistSearchResponse`：`code`、`result.artistCount`、`result.artists`；
- 歌曲同时覆盖参考模型使用的 `artists/album/duration` 与公开响应常见的 `ar/al/dt` 字段；
- 音质读取 `sq/h/m/l` 提供的 bitrate；
- 专辑读取 `artist`、`picUrl`、`publishTime`、`size`；
- 歌手读取 `picUrl`、`img1v1Url`、`briefDesc` 和 alias。

HTTP 200 不代表业务成功。`code=200` 才是成功；301、302、401 映射 `Unauthorized`，其他非 200 业务码映射 `ServiceUnavailable`，缺少 code、result、计数或计数大于零时缺少结果列表映射 `InvalidResponse`，非法 JSON 映射 `ParseFailure`。计数为 0 且省略结果数组被视为合法空结果。

## 5. Mapper 与统一结果

`NetEaseSearchMapper` 是 DTO 到 `SearchResultItem` 的唯一出口：

```text
Platform = NetEaseMusic
SourceDisplayName = 网易云音乐
SourceBadgeText = 网易云音乐
StableId = netease:{track|album|artist}:{NativeId}
```

歌曲映射真实歌曲 ID、标题、歌手、专辑、专辑封面、毫秒时长和质量；专辑映射真实专辑 ID、名称、歌手、封面和 UTC 发行日期；歌手映射真实歌手 ID、名称、头像和简介。缺失封面使用 Beans 占位图，缺失歌手/专辑使用“未知歌手/未知专辑”，缺失质量显示“未知”。标题、数组下标和封面 URL 从不作为 ID。

本轮没有网易云播放地址，因此所有搜索结果 `IsPlayable=false`，限制文案固定为 `网易云音乐播放适配将在后续阶段接入`。选择和加入队列仍保留真实 `PlatformId=netease`，不会重建 BottomPlayer，也不会停止当前播放或把网络结果伪装成本地文件。

## 6. 分页、综合搜索与状态

`SearchQuery.Offset / PageSize` 映射为 WEAPI 的 `offset / limit`；请求键使用一基页码：

```text
netease:search:{filter}:{normalizedKeyword}:{page}:{pageSize}
```

保留分隔符或安全敏感片段的关键词使用确定性 SHA-256 短指纹，避免请求键触发敏感片段校验或泄露潜在敏感词。不同平台、类型、关键词和分页完全隔离。

`All` 最多并行歌曲、专辑、歌手三类实际支持的请求，不发送歌单请求。一个类型失败时保留其他成功结果并设置 `IsPartialSuccess=true`；全部失败时由网易云来源独立返回安全错误。平台报告的 `songCount / albumCount / artistCount` 进入统一总数，跨平台同名结果不去重，同平台同 StableId 仍去重。

## 7. 缓存与 DataOrigin

搜索复用现有单例 `DiscoveryCache`，正式搜索缓存 10 分钟。缓存值按网易云搜索键隔离，不与 QQ 共享：

- 真实成功：`Live`；
- 未过期成功缓存：`CacheFresh`；
- 网络失败且存在过期成功值：`CacheStale`，保留原始 `LoadedAt`；
- 强制刷新绕过 Fresh，失败后保留已有 Fresh/Stale；
- 取消、解析失败和业务失败不写入或覆盖成功缓存；
- 综合部分成功不写入完整聚合缓存；
- 网易云搜索不使用 Preview 回退。

Release 与 Debug 在真实 Adapter 中都只展示 Live 或缓存结果；无网络且无缓存时显示 `网易云音乐搜索暂时不可用`。显式 `SearchPreviewAdapter` 仍可用于离线 Preview 测试，但不会通过网易云生产 DI 注册。

## 8. 测试与构建

Phase 6B 基线为 176 项。本阶段新增 25 个网易云 Search 测试实例，总计 201 项，覆盖：DI 注册、匿名 WEAPI Endpoint 与加密表单、参数构造、能力声明、歌曲/专辑/歌手映射、质量/时长/封面、分页键、歌单 Unsupported、建议不发网、空结果、业务错误、Unauthorized、综合成功与部分成功、Fresh/Stale、强刷失败回退、请求合并、取消不入缓存、错误码、安全日志和脱敏 Fixture。

最终验证：

```text
Debug build: 0 warnings, 0 errors
Release build: 0 warnings, 0 errors
Debug tests: 201 passed, 0 failed, 0 skipped
Release tests: 201 passed, 0 failed, 0 skipped
```

默认测试完全离线，不依赖真实网易云网络。`NETEASE_SEARCH_LIVE_TEST` 未启用，因此本轮没有执行真实联网烟雾测试，也没有把 Fixture 结果描述为在线成功。

## 9. UI 验收与已知限制

本轮没有修改 SearchPage、TopBar、断点或任何 UI 几何。原生 UI 自动化工具当前不可用，因此人工进入 SearchPage、DPI 125%/150%、滚动、Binding Error、默认蓝色、点击播放和队列行为均未执行截图级验收；没有将构建通过描述为视觉验收通过。

已知限制：

- 网易云歌单搜索保持 `Unsupported`；
- 网易云输入建议保持 `Unsupported`，不会用热搜词冒充联想；
- 搜索结果没有播放地址，始终不可播放；
- WEAPI 和公开 Web Endpoint 属于平台实现细节，可能变化；
- 本地目录索引尚未实现。

## 10. Phase 6D 接入点

Phase 6D 应在不改变当前搜索聚合边界的前提下实现本地音乐索引：用户文件夹选择、可取消的后台扫描、音频元数据解析、封面与同名 LRC 查找、SQLite 索引、文件变化检测、`ILocalMusicSearchCatalog` 映射以及本地 `IPlaybackService` 播放。不得把本阶段 QQ/网易云网络结果写入本地索引，也不得在 Phase 6D 同时接入酷狗、登录或网络播放地址。

Phase 6C 到此停止，不自动进入 Phase 6D。
