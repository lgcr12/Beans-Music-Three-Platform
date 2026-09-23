# Phase 5B QQ 音乐公开发现适配交付记录

## 1. 范围与基线

本阶段只修改 `windows/Beans.Windows.Rebuild`，以 Phase 5A 为逻辑稳定基线。未创建 Git 提交或标签，因此 `phase-5a-networking-baseline` 仅为文档中的基线名称。

本阶段只接入 QQ 音乐匿名公开排行榜和公开推荐歌单。没有实现登录、Cookie、用户资料、用户歌单、每日推荐、个性电台、搜索、详情、播放地址、收藏、下载或写操作，也没有开始网易云或酷狗适配。

## 2. 新增和修改文件

新增生产代码：

- `Services/Platforms/QQ/Dto/QqDiscoveryDtos.cs`
- `Services/Platforms/QQ/QqDiscoveryRequestKeys.cs`
- `Services/Platforms/QQ/QqDiscoveryMapper.cs`
- `Services/Platforms/QQ/QqMusicDiscoveryAdapter.cs`
- `Services/Platforms/QQ/QqPlatformDiscoveryService.cs`

修改的接线与兼容模型：

- `App.xaml.cs`
- `Models/MusicModels.cs`
- `Models/PlatformModels.cs`
- `Services/Platforms/IPlatformDiscoveryAdapter.cs`
- `Services/Platforms/IMusicDiscoveryService.cs`
- `Services/Platforms/DiscoveryCache.cs`
- `ViewModels/DiscoverViewModel.cs`
- `Pages/Discover/DiscoverPage.xaml`
- `Pages/Discover/DiscoverPage.xaml.cs`
- `README.md`

新增测试与 Fixtures：

- `Tests/QqDiscoveryAdapterTests.cs`
- `Tests/QqDiscoveryServiceTests.cs`
- `Tests/Infrastructure/QqDiscoveryTestInfrastructure.cs`
- `Tests/Fixtures/QQ/*.json`
- `Tests/Fixtures/QQ/README.md`

## 3. 已接入的 QQ 公开能力

- `GetPublicRankingsAsync`：真实匿名公开排行榜列表和前三首歌曲摘要。
- `GetPublicRecommendedPlaylistsAsync`：真实匿名公开推荐歌单。
- `GetHeroAsync`：由真实推荐歌单首项生成；聚合服务在正常加载时复用推荐结果，不额外请求。
- 真实 NativeId、封面、标题、创建者、歌曲数和播放量映射到统一模型；缺失字段保持为空，不伪造数值。
- 分板块 Fresh Cache、强制刷新、网络失败后的 Fresh/Stale 回退和局部失败状态。

## 4. 明确未实现的 QQ 能力

以下接口返回 `Unsupported` 且不进入网络层：

- `GetPublicPlaylistSquareAsync`
- `GetCategoriesAsync`
- `GetDailyRecommendationsAsync`

参考仓库没有独立的 QQ 匿名分类或歌单广场实现，因此未发明 Endpoint。每日推荐属于授权边界，本阶段没有用公开榜单冒充每日推荐。搜索、播放、登录和用户歌单能力标记均为 false。

## 5. 参考实现位置

业务和请求结构来自同仓库 `Beans/QQMusicAPI.swift`：

- 基础匿名 GET 和公共 Header：第 30-45 行。
- `musicu.fcg` JSON POST：第 47-72 行。
- `topLists()`：第 921-940 行。
- `recommendPlaylists()`：第 1041-1061 行。

没有复制 Swift 网络生命周期、Cookie 处理或登录代码。Windows Adapter 只复用 Phase 5A 的客户端、错误、日志、限流、请求合并和 JSON 边界。

## 6. Endpoint、方法与公共 Header

排行榜：

- Method：GET
- Host：`c.y.qq.com`
- Path：`/v8/fcg-bin/fcg_myqq_toplist.fcg`
- 非敏感参数：`format=json`

推荐歌单：

- Method：POST
- Host：`u.y.qq.com`
- Path：`/cgi-bin/musicu.fcg`
- Module：`music.srfDissInfo.RecommendPlaylist`
- Method name：`GetRecommendPlaylist`

两类请求使用公共 `Accept`、应用 User-Agent 和 `Referer: https://y.qq.com/`。POST 使用 JSON Content-Type。未发送 Cookie、Authorization、Token 或个人 UIN。

请求体中的 `uin: 0`、`ct: 24` 和 `cv: 0` 来自参考实现。`uin: 0` 是固定匿名参数，不是账号、个人标识或凭证。日志不记录请求体或完整查询串。

## 7. DTO 与业务状态校验

QQ DTO 位于 `Services/Platforms/QQ/Dto`，只包含发现页需要的响应节点：

- 排行榜：根业务码、`data.topList`、榜单 ID、标题、更新时间、封面和歌曲摘要。
- 推荐歌单：根业务码、`req_1.code`、`req_1.data.v_playlist`、`tid`、标题、封面、歌曲数、播放量和公开创建者名称。

HTTP 200 后仍检查 QQ 根业务码、模块业务码、必需数据节点和稳定 ID。非法 JSON 映射为 `ParseFailure`，缺失节点或必需字段映射为 `InvalidResponse`，非零业务码映射为安全的 `ServiceUnavailable`。

## 8. Mapper 与统一模型

`QqDiscoveryMapper` 是 QQ DTO 唯一的业务模型出口：

- 排行榜 ID 使用真实 `topList[].id`。
- 歌单 ID 使用真实 `v_playlist[].tid`，仅在结构提供时兼容 `id`。
- 所有实体平台均为 `qq` / `PlatformId.QqMusic`。
- 标题、图片 URL 和数组位置从不作为 ID。
- `TrackCount` 和 `PlayCount` 均可空；字段缺失时 UI 显示空白，不伪造 0。
- 空封面映射到统一本地品牌占位图。
- `PayloadReference` 只保存 `qq:playlist:{NativeId}`，不保留原始 DTO 或敏感参数。
- `IsPlayable=false`，因为播放地址尚未实现。

## 9. 能力声明

QQ 当前只声明：匿名 Hero、匿名排行榜、匿名推荐歌单。匿名歌单广场、匿名分类、授权每日推荐、授权用户歌单、搜索和播放均为 false。网易云与酷狗仍注册安全 Stub，未提前声明尚未实现的能力。

## 10. 缓存键与时长

复用同一个 `DiscoveryCache`，没有 QQ 专用第二套缓存：

- Hero：`qq / hero / page 1 / anonymous`，20 分钟。
- 排行榜：`qq / rankings / page 1 / anonymous`，30 分钟。
- 推荐歌单：`qq / recommended-playlists / page 1 / anonymous`，20 分钟。
- 预留分类键：`qq:discovery:categories:anonymous`。
- 预留歌单广场请求键包含 category 和 page，但当前接口返回 Unsupported。

缓存容量仍为 24，过期成功值保留为有界 stale 候选，直到同键替换、平台失效或容量淘汰。

## 11. Phase 5A 最小兼容扩展

Phase 5A 的 `DiscoveryCache` 原先只能保存完整页面，并在过期时立即删除，无法满足 Phase 5B 的分板块时长和 stale 回退。本阶段只做以下向后兼容扩展：

- 在同一缓存中增加泛型 `SetValue` / `TryGetValue`；原完整页面 API 保留。
- 过期项可由显式 `includeExpired` 读取，容量上限不变。
- 强制刷新不再先删除缓存，而是绕过 Fresh；失败时仍可回退原成功值。
- `PlatformDiscoveryContent` 增加只读 Section 状态。
- `MusicPlaylist` 增加可选播放量、可空歌曲数、能力标记和安全引用。

没有修改 Phase 5A HTTP、错误、日志、请求合并或限流实现。新增回归测试覆盖这些扩展。

## 12. 局部失败

排行榜和推荐歌单并发且独立。任一板块失败时，成功板块继续返回，Hero 优先从推荐歌单生成，推荐不可用时可从真实排行榜生成。`SectionStates` 保存每个板块的成功、错误码、来源、时间和 stale 状态；页面保持原布局，只显示简洁的部分成功状态。

分类和歌单广场因为明确 Unsupported 会使当前 QQ 页面标记为部分成功。歌单广场标题右侧显示“当前阶段未接入”，不会在 Live 页面错误显示 `PreviewData`。

## 13. DataOrigin 行为

- `Live`：本次 QQ 匿名请求成功。
- `CacheFresh`：未强制刷新时直接使用有效缓存，或强制刷新失败后保留仍有效的成功缓存。
- `CacheStale`：网络失败且只有已过期成功缓存时展示旧内容，并设置 `IsStale=true`。
- `Preview`：仅在 Preview 策略明确启用且 QQ 无任何真实/缓存板块可用时使用，始终显示 `预览内容 · 在线服务尚未连接`。

混合结果中只要有 stale 板块，页面整体标记 `CacheStale`，避免把旧内容伪装为全新 Live。

## 14. Preview 与 Release

Debug 延续 Phase 5A 的明确 Preview 策略：真实请求与缓存均不可用时可回退 QQ Preview，并保留 Preview 标识。Release 编译分支默认关闭 Preview；无缓存失败时显示安全错误，不会静默使用 Preview。Fixtures 仅由测试项目复制，不进入应用包。

## 15. 请求取消与晚到响应

QQ 请求上下文使用页面传入的 CancellationToken。Phase 5A 网络层继续负责总超时、调用者取消、共享等待者和平台限流。聚合服务在并发板块返回后再次检查取消；取消不会写入成功缓存。

DiscoverViewModel 继续使用平台 ID 和 request generation。QQ 晚到响应不能覆盖网易云或酷狗状态，旧分类响应不能覆盖新 generation，页面离开后的取消不显示错误。

## 16. 安全与错误映射

Adapter 不读取 Credential Locker，不设置 Cookie，不发送 Authorization，不保存 Header 或响应体。RequestKey 固定以 `qq:` 开头且只包含操作、匿名标记及预留的 category/page。Phase 5A 日志仍只接收 Host 与 Path，不接收排行榜查询串或推荐歌单请求体。

HTTP、取消、超时和网络错误沿用 `PlatformErrorMapper`。QQ 业务错误、解析错误和字段错误在 DTO 边界转换为统一安全错误，不向 UI 暴露原始响应或异常消息。

## 17. Fixtures

`Tests/Fixtures/QQ` 中的响应结构来自参考 Swift 方法，但所有 ID、标题、创建者、计数和图片路径均为人工构造。构造日期为 2026-09-20。Fixtures 不含凭证、账号标识、个人资料、会员信息、播放地址、播放密钥、签名或完整请求 Header。

## 18. 测试结果

Phase 5B 新增 31 项离线测试，总数从 59 增至 90。Debug 和 Release 均为 90 通过、0 失败、0 跳过。

新增覆盖包括：能力声明、真实 Endpoint 方法、Referer、匿名请求体、无 Cookie/Authorization、业务码、非法 JSON、缺失字段、空数组、可选字段、NativeId、平台标记、封面占位、播放量、可空歌曲数、Unsupported 不入网、请求合并、安全日志、Fixture 脱敏、正确 OperationName/RequestKey、Live/Fresh/Stale/Preview、强制刷新、无缓存错误、取消不写缓存、平台/category/page 隔离、局部失败和其他平台 Preview 边界。

Phase 5A 的 59 项测试全部继续通过。

## 19. 构建结果

- Debug：0 个警告，0 个错误。
- Release：0 个警告，0 个错误。
- Debug tests：90 passed，0 failed，0 skipped。
- Release tests：90 passed，0 failed，0 skipped。

## 20. 真实联网烟雾测试与 UI 验收

真实联网烟雾测试未执行。默认测试完全离线，未把 Fixtures 称为真实联网成功，也未保存真实响应。

最终 Debug 应用已启动，系统进程检查显示窗口标题为 `Beans Music` 且持续响应。桌面检查工具返回 `apps=[]`，同时 `sky` RPC 未配置，无法绑定 WinUI 原生窗口，因此没有执行“发现 → QQ 音乐”的页面级点击、截图、真实内容或 XAML Binding 验收。人工 UI 验收状态为：仅完成进程级启动，页面级未执行。

## 21. 已知限制

- QQ Endpoint 属于平台公开 Web 实现细节，可能变化。
- 本阶段未执行真实联网烟雾测试，因此不声明当前网络环境已成功返回 QQ 数据。
- QQ 分类和歌单广场因参考实现缺失而明确 Unsupported。
- 推荐歌单的创建者、歌曲数和播放量只有响应提供时才展示。
- Section 错误已进入模型；当前冻结布局只显示整体部分成功状态和歌单广场 Unsupported 文案。
- 远程图片空值有本地占位；远程资源运行时失效仍依赖现有 Image 控件行为，尚无独立图片缓存服务。

## 22. Phase 5C 接入点

Phase 5C 只实现网易云匿名公开发现 Adapter，并复用现有共享网络与缓存边界：

- `GetHeroAsync`
- `GetPublicRankingsAsync`
- `GetPublicRecommendedPlaylistsAsync`
- `GetPublicPlaylistSquareAsync`
- `GetCategoriesAsync`

必须先从 `Beans/NetEaseAPI.swift` 核实每个公开 Endpoint、加密要求、业务码、匿名能力和稳定 ID。`GetDailyRecommendationsAsync` 继续保持授权边界；不得同时开始酷狗、搜索、登录或播放地址。

Phase 5B 到此停止。当前只有 QQ 排行榜和推荐歌单进入真实匿名 Adapter；没有声明网易云、酷狗或任何登录专属能力已接通。
