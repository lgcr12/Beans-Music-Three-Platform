# Phase 6B：QQ 音乐真实匿名搜索适配

## 1. 范围

本阶段只把 Phase 6A 的 QQ 搜索 Stub 替换为真实匿名 Adapter。没有接入网易云真实搜索、本地磁盘扫描、QQ 登录、Cookie、播放地址、收藏、下载、详情页或酷狗，也没有开始 Phase 6C。SearchPage、TopBar 几何、AppShell、HomePage、DiscoverPage、BottomPlayer、PlaybackService 和 Phase 5A 网络层均未重新设计。

QQ 当前搜索能力：

| 类型 | 状态 | 数据来源 |
| --- | --- | --- |
| 歌曲 | 已接入 | `search_for_qq_cp`，`t=0` |
| 专辑 | 已接入 | `search_for_qq_cp`，`t=8` |
| 歌手 | 已接入 | `smartbox_new.fcg` 的歌手结果 |
| 输入建议 | 已接入 | `smartbox_new.fcg` 的歌手、歌曲、专辑结果 |
| 歌单 | `Unsupported` | 参考实现没有可靠匿名歌单搜索 Endpoint，不发请求 |

## 2. 新增和修改文件

新增：

- `Services/Platforms/QQ/Dto/Search/QqSearchDtos.cs`
- `Services/Platforms/QQ/QqSearchRequestKeys.cs`
- `Services/Platforms/QQ/QqSearchMapper.cs`
- `Services/Platforms/QQ/QqMusicSearchAdapter.cs`
- `Tests/Infrastructure/QqSearchTestInfrastructure.cs`
- `Tests/QqSearchAdapterTests.cs`
- `Tests/Fixtures/QQ/qq-search-*.json`
- `Docs/phase-6b-qq-search-status.md`

修改：

- `App.xaml.cs`
- `Models/SearchModels.cs`
- `Services/Search/SearchAdapters.cs`
- `Services/Search/MusicSearchService.cs`
- `Tests/Phase6ASearchTests.cs`
- `README.md`

原 `QqMusicSearchStubAdapter` 已移除，DI 中同一个 `IPlatformSearchAdapter` 注册点改为 `QqMusicSearchAdapter`，没有创建第二套 SearchPage、搜索服务、HTTP 客户端、播放器或缓存实现。

## 3. Endpoint 与请求边界

歌曲和专辑使用匿名 GET：

```text
https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp
```

参数固定为 `format=json`、规范化关键词 `w`、每页数量 `n`、一基页码 `p` 和结果类型 `t`。`t=0` 为歌曲，`t=8` 为专辑；`Offset / PageSize + 1` 稳定映射到页码，每页限制在 1 至 50。请求只设置公开 Referer，不设置 Cookie、Token 或 Authorization。

歌手和输入建议使用匿名 GET：

```text
https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg
```

参数为 `format=json`、`s_from=pc_header`、`type=1` 和关键词 `key`。Smartbox 没有可靠的任意页分页契约，因此歌手结果只支持首批公开结果；后续页安全返回空结果，不伪造分页。

关键词会 Trim、移除控制字符并限制为 80 个字符。空关键词不发请求。请求通过 `IPlatformHttpClientFactory.Get("qq")`、`PlatformRequestContext` 和 `PlatformResponse<T>`，继续使用平台并发上限、一次有界重试、同键请求合并、总超时、响应大小限制和取消传播。安全日志只记录 Host 与 Path，不记录 QueryString、请求体或关键词。

## 4. DTO 与 Mapper

`QqSearchDtos.cs` 建模：

- `QqClientSearchEnvelope` 及歌曲、专辑分页节点；
- 歌曲的 `songmid`、歌手、专辑 MID、时长、付费标记和文件质量大小；
- 专辑的 `albummid`、名称、歌手、发行时间和歌曲数；
- `QqSmartboxEnvelope` 及歌手、歌曲、专辑建议组；
- Mapper 输入 Payload，不向上层暴露 QQ 原始 DTO。

解析器要求根业务码存在且为 0、目标数据节点存在、总数存在，并校验每个结果的真实 MID 与标题。非法 JSON 为 `ParseFailure`，节点或必需字段缺失为 `InvalidResponse`，非零业务码为 `ServiceUnavailable`。

`QqSearchMapper` 统一生成：

```text
Platform = QqMusic
NativeId = QQ MID
StableId = qq:{track|album|artist}:{MID}
SourceDisplayName = QQ 音乐
SourceBadgeText = QQ 音乐
```

歌曲映射标题、歌手、专辑、T002 专辑封面、秒级时长和接口文件大小提供的最高质量（FLAC、APE、320K、128K）。专辑映射 T002 封面、歌手和发行时间；歌手使用返回图片或 T001 歌手封面模板。缺失封面回退 Beans 图标，歌手/专辑缺失使用“未知歌手/未知专辑”，未提供质量显示“未知”。

所有结果 `IsPlayable=false`，限制文案固定为 `QQ 音乐播放适配将在后续阶段接入`。搜索成功不代表播放地址可用，不会修改当前播放或让 BottomPlayer 进入错误状态。

## 5. 查询、综合搜索与总数

单类型筛选只请求相应能力。`All` 最多并行歌曲、专辑、歌手三类请求，受 QQ 平台并发上限约束；不会请求歌单。单个子请求失败时保留其他成功结果，Adapter 设置 `IsPartialSuccess=true` 和安全文案，不产生无界请求风暴。

平台返回的 `totalnum/count` 进入 `SearchAdapterResponse.TotalCount`。统一 `MusicSearchService` 现在汇总平台报告总数，而不是把当前页条数误作总数；同平台同 StableId 仍去重，跨平台同名内容仍保留。

## 6. 缓存与数据来源

搜索复用现有单例 `DiscoveryCache` 的泛型值存储，没有新建第二套缓存。对外请求键格式为：

```text
qq:search:{filter}:{normalizedKeyword}:{page}:{pageSize}
```

建议键为 `qq:search:suggestions:{normalizedKeyword}:1:6`。包含保留分隔符或安全敏感词片段的关键词会替换为确定性 SHA-256 短指纹，既保持隔离又不把潜在敏感内容写入键。缓存仍按 QQ 平台、类型、关键词、页码和页大小隔离，不与网易云共享。

- 正式搜索：10 分钟；
- 输入建议：2 分钟；
- 成功网络响应：`Live`；
- 未过期缓存：`CacheFresh`；
- 网络失败且只有过期成功缓存：`CacheStale`，保留缓存原始 `LoadedAt`；
- 失败和取消：不写缓存、不覆盖既有成功值；
- 强制刷新：绕过 Fresh 读取，失败后仍可安全回退既有 Fresh/Stale；
- 综合部分成功：返回当前 Live 结果，但不写入完整查询缓存。

QQ 不再返回 Preview。Release 与 Debug 都优先真实 QQ 搜索；无缓存失败时显示 `QQ 音乐搜索暂时不可用`。网易云仍保持 Phase 6A 的明确 Stub/Preview 行为，本地仍保持 `CatalogUnavailable`。

## 7. 错误和安全

HTTP 与运行时错误继续由 Phase 5A `IPlatformErrorMapper` 处理：取消、超时、无网络、401、403、404、429 和 5xx 分别映射到既有安全错误码。搜索响应保留安全 `ErrorCode`，但 SearchPage 只显示安全文案，不显示原始异常、StackTrace、完整 URL 或响应体。401/凭证失效会进入 `Unauthorized/RequiresAuthorization`；当前三个真实匿名搜索请求不需要授权。

代码与 Fixture 均不包含 Cookie、Token、Authorization、用户标识、播放地址或硬编码凭证。网络层 `UseCookies=false`，Adapter 不读写凭证或用户数据。

## 8. 测试与构建

Phase 6A 基线为 153 项。本阶段新增 23 个 QQ 搜索测试实例，总计 176 项，覆盖：DI 注册、能力声明、歌曲/专辑/歌手/建议请求与映射、真实 MID 和 StableId、总数、分页、歌单不发网、空关键词、综合成功与部分成功、请求键隔离与安全指纹、Fresh/Stale、强刷失败回退、请求合并、取消不入缓存、空结果、非法 JSON、必需字段、业务错误、安全日志和 Fixture 脱敏。

最终验证：

```text
Debug build: 0 warnings, 0 errors
Release build: 0 warnings, 0 errors
Debug tests: 176 passed, 0 failed, 0 skipped
Release tests: 176 passed, 0 failed, 0 skipped
```

默认测试全部使用本地 Handler 和脱敏 Fixture，不依赖真实外网。`QQ_SEARCH_LIVE_TEST` 未启用，因此本轮没有执行真实联网烟雾测试，也没有把离线 Fixture 结果描述为在线验证。

## 9. UI 验收与已知限制

本轮没有修改任何 SearchPage XAML 或响应式断点。Debug/Release 编译覆盖 XAML 编译，但本轮没有使用可用的原生 UI 自动化取得窗口截图，因此未声称完成 125%/150% DPI、滚动、文字截断、默认蓝色、运行时 Binding Error 或点击播放的目视验收。这些项目仍需人工窗口复核。

已知限制：

- QQ 歌单搜索保持 `Unsupported`；
- Smartbox 歌手结果只提供首批匿名结果；
- QQ 搜索结果没有播放 URL，始终不可播放；
- 没有真实联网烟雾测试结论；
- 网易云真实搜索和本地索引尚未接入。

## 10. Phase 6C 接入点

Phase 6C 只应把 `NetEaseMusicSearchStubAdapter` 替换为网易云真实匿名搜索 Adapter，复用当前 `IPlatformSearchAdapter`、`MusicSearchService`、SearchPage、Phase 5A 网络层、稳定身份、同平台缓存和错误边界。应按参考实现分别确认歌曲、专辑、歌手与热搜/建议能力；不应在 Phase 6C 同时实现本地扫描、登录、播放地址、歌单详情或 UI 重设计。

Phase 6B 到此停止，不自动开始 Phase 6C。
