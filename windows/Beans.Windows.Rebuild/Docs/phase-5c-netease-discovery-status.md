# Phase 5C 网易云音乐公开发现适配交付记录

## 1. 范围与基线

本阶段只修改 `windows/Beans.Windows.Rebuild`，以 Phase 5B 的 90 项测试为逻辑基线。未创建 Git 提交或标签。

本阶段只实现网易云音乐匿名公开发现。没有实现登录、Cookie 导入或持久化、用户歌单、每日推荐内容、私人 FM、搜索、详情、评论、播放地址、音质密钥、收藏、下载或写操作，也没有开始酷狗 Phase 5D。

## 2. 新增和修改文件

新增生产代码：

- `Services/Platforms/NetEase/Dto/NetEaseDiscoveryDtos.cs`
- `Services/Platforms/NetEase/NetEaseDiscoveryRequestKeys.cs`
- `Services/Platforms/NetEase/NetEaseDiscoveryMapper.cs`
- `Services/Platforms/NetEase/NetEaseWeapi.cs`
- `Services/Platforms/NetEase/NetEaseMusicDiscoveryAdapter.cs`

修改接线和共享聚合：

- `App.xaml.cs`
- `Services/Networking/PlatformHttpClientFactory.cs`
- `Services/Platforms/QQ/QqPlatformDiscoveryService.cs`
- `ViewModels/DiscoverViewModel.cs`
- `README.md`

新增测试与 Fixtures：

- `Tests/NetEaseDiscoveryAdapterTests.cs`
- `Tests/NetEaseDiscoveryServiceTests.cs`
- `Tests/Infrastructure/NetEaseDiscoveryTestInfrastructure.cs`
- `Tests/Fixtures/NetEase/*.json`
- `Tests/Fixtures/NetEase/README.md`

保留 `QqPlatformDiscoveryService` 的现有类型名以避免破坏 Phase 5B 接线和测试，但其内部已按平台注册的 Adapter 聚合 QQ 与网易云；没有创建第二套发现服务。

共享网络层只做了一项安全兼容修复：默认 `SocketsHttpHandler.UseCookies = false`，防止匿名请求因服务端 `Set-Cookie` 自动形成隐式 CookieContainer。该修复不读取、保存或回送 Cookie，也不改变 QQ 的请求协议。

## 3. 已接入的公开能力

- `GetPublicRankingsAsync`：公开榜单摘要，包含真实榜单 NativeId、名称、封面、更新频率和响应提供时的前三首歌曲名。
- `GetPublicRecommendedPlaylistsAsync`：使用公开精品歌单作为编辑精选入口。
- `GetPublicPlaylistSquareAsync`：公开歌单广场，支持分类、页码和固定每页 18 项。
- `GetCategoriesAsync`：公开歌单分类，去除空值和重复项。
- `GetHeroAsync`：由公开精品歌单首项派生；聚合服务正常加载时复用推荐结果，不额外请求。
- `GetDailyRecommendationsAsync`：不进入网络层，明确返回 `Unauthorized` 和“登录网易云音乐后查看每日推荐”。

适配器能力声明为公开 Hero、榜单、推荐歌单、歌单广场、分类可用；每日推荐存在授权能力但本阶段不实现授权请求；用户歌单、搜索和播放均为 false。

## 4. 参考实现与 Endpoint

实现依据同仓库 `Beans/NetEaseAPI.swift` 和 `Beans/NetEaseCrypto.swift`：

- 请求包装：`NetEaseAPI.swift` 第 46-91 行；`weapi` 为 POST、`application/x-www-form-urlencoded`、`params` 与 `encSecKey`。
- 榜单：`topLists()`，`/api/toplist/detail`，转换为 `https://music.163.com/weapi/toplist/detail`。
- 歌单广场：`playlistSquare()`，`/api/playlist/list`，转换为 `/weapi/playlist/list`。
- 精品歌单：`highQualityPlaylists()`，`/api/playlist/highquality/list`，转换为 `/weapi/playlist/highquality/list`。
- 分类：`playlistCatlist()`，`/api/playlist/catlist`，转换为 `/weapi/playlist/catlist`。
- 每日推荐：`dailyRecommend()` 指向登录相关接口，因此当前匿名适配器不发送该请求。
- DTO 字段依据 `Models.swift` 的 `Playlist` 和 `TopList`，并补充实际公开响应中的 `playCount`、`tags` 和榜单 `tracks` 可选字段。

公开请求只发送公共 Referer、非账号 User-Agent 和加密表单。`csrf_token` 以空字符串放在加密公共负载中，与参考实现的匿名分支一致；不发送 Cookie、Authorization、MUSIC_U、账号 ID 或个人设备标识。

`NetEaseWeapi` 只实现公开发现所需的 AES-128-CBC 双层加密和 RSA 公钥封装。随机 16 字节会话密钥仅用于单次请求，不是用户凭证，不记录日志，也不持久化。没有实现登录加密或授权流程。

## 5. DTO、Mapper 与业务码

DTO 仅位于 `Services/Platforms/NetEase/Dto`，不会暴露到 ViewModel 或页面。外层 DTO 处理 `code`、`msg`、`list`、`playlists` 和 `sub`；空数组是成功，缺少必需数据节点或条目缺少 NativeId/名称是 `InvalidResponse`，非法 JSON 是 `ParseFailure`。

业务码 200 为成功；301、302、401 映射为 `Unauthorized`；其他非 200 业务码映射为 `ServiceUnavailable`。HTTP 状态、超时、取消、重试和网络错误继续由 Phase 5A 统一设施处理。

Mapper 统一输出 `PlatformId = netease`。歌单保存真实 NativeId、标题、创建者、封面、可选歌曲数、可选播放量、首个分类标签、来源徽标和 `netease:playlist:<NativeId>` 最小安全引用。缺失封面使用统一本地占位；缺失歌曲数或播放量保持 null，不伪造数值。

## 6. 缓存、并发与局部失败

继续复用 `DiscoveryCache`，没有网易云专属缓存服务。缓存键格式包含 PlatformId、Operation、Category、Page 和 `AccountIdentityHash = anonymous`：

- Hero：20 分钟。
- 排行榜：30 分钟。
- 推荐歌单：20 分钟。
- 分类：18 小时。
- 歌单广场：20 分钟，分类和页码独立。

非强制加载优先 Fresh Cache；强制刷新绕过 Fresh 读取但不提前删除旧值；失败后依次回退 Fresh、Stale；无缓存时保留安全错误。请求合并、每平台并发限制、取消和超时继续由共享 `IPlatformHttpClient` 处理。ViewModel 的请求版本和平台 ID 检查继续阻止分类或平台切换后的晚到响应覆盖当前页面。

Hero、排行榜、推荐歌单、分类、歌单广场和每日推荐均有独立 SectionState。任一公开板块成功即保留成功内容；失败板块只携带安全错误。每日推荐的 `Unauthorized` 不清空公开内容，并在现有状态行显示“每日推荐需登录”。

## 7. DataOrigin 与 Preview

- 真实适配器响应：`Live`。
- 未过期缓存：`CacheFresh`。
- 网络失败后的过期缓存：`CacheStale`。
- 只有显式启用开发预览且所有真实/缓存板块均不可用时：`Preview`。

Release 默认禁用 Preview，不会把预览内容标为 Live。页面布局和控件几何未改变；只修正网易云副标题并绑定每日推荐授权状态。

## 8. 测试与构建

新增 27 项网易云专项测试和 1 项共享匿名 Cookie 回归测试，共新增 28 项，总测试数 118。覆盖 Adapter 能力、公开 Endpoint、`weapi` 表单、无 Cookie/Authorization、NativeId 与可选字段映射、Hero、分类、分页歌单、空列表、缺失字段、非法 JSON、业务错误、未授权、请求合并、安全日志、Fixture 安全、Fresh/Stale、强制刷新、分类/页隔离、取消、局部成功和 Preview 边界。

最终验证结果：

- Debug build：0 warnings，0 errors。
- Release build：0 warnings，0 errors。
- Debug tests：118 passed，0 failed，0 skipped。
- Release tests：118 passed，0 failed，0 skipped。

默认测试完全离线，不访问网易云或其他外部服务。

## 9. 验收状态与已知限制

真实联网烟雾测试已通过 Debug 应用的页面加载执行。当前机器匿名访问成功返回网易云公开榜单、精品歌单、分类和歌单广场；核心字段可解析，响应未落盘，未使用用户凭证。该结果是 2026-09-20 的单次真实联网检查，不作为默认测试，也不保证公开 Endpoint 永久稳定。

通过 Windows Computer Use 在约 1440x900 窗口完成页面级验收：进入发现页后网易云 Hero、排行榜、推荐歌单和歌单广场均显示真实内容与网易云来源徽标；状态为公开内容并明确显示“每日推荐需登录”；从网易云切到 QQ 后没有网易云内容残留，再切回网易云恢复正确内容；占位详情路由携带平台和 NativeId；BottomPlayer 在导航和平台切换后保持存在；可见远程图片正常渲染；未观察到错误面板、横向页面滚动条或 XAML Binding Error。

本轮没有进行断网故障注入，也没有重新执行 125% 和 150% DPI 的页面级截图验收；这些行为由离线缓存/取消测试与既有 Phase 4 布局基线覆盖，但不冒充本轮人工验证。

已知限制：

- 公开 Web Endpoint 和 `weapi` 协议属于平台实现细节，可能变化。
- 本阶段只取歌单广场第一页的 UI 工作流，Adapter 和缓存键保留页码能力。
- 排行榜详情、歌单详情和播放均为现有“尚未完成”占位路由。
- 远程封面运行时失效仍依赖现有 Image 控件行为。
- 每日推荐需要授权，本阶段没有登录实现，也不使用公开歌单冒充个性化内容。

## 10. Phase 5D 接入点

Phase 5D 只应核实并实现酷狗匿名公开 Hero、排行榜、推荐歌单、歌单广场和分类；继续复用 `IPlatformHttpClient`、`DiscoveryCache`、统一错误、日志、SectionState 和平台聚合服务。未由参考实现确认的能力必须保持 Unsupported。不得把本阶段的网易云 `weapi` 包装复制给酷狗，也不得同时实现搜索、登录或播放地址。

Phase 5C 到此停止，不自动进入 Phase 5D。
