# Phase 5A 统一联网基础设施交付记录

## 1. 范围与基线

本阶段只修改 `windows/Beans.Windows.Rebuild`，以 Phase 4 的三平台发现页为逻辑基线。未创建 Git 提交或标签，因此 `phase-4-platform-discovery-baseline` 仅作为文档中的基线名称，不代表仓库中存在同名 tag。

本阶段没有实现 QQ 音乐、网易云音乐或酷狗音乐的真实 Endpoint，也没有进入搜索、登录、播放地址、下载或 UI 重设计。旧 Windows 项目、Apple 端、服务端、版本号和发布流程均未修改。

## 2. 新增和修改文件

新增的生产代码：

- `Models/NetworkingModels.cs`
- `Services/Networking/IPlatformHttpClient.cs`
- `Services/Networking/PlatformHttpClient.cs`
- `Services/Networking/PlatformHttpClientFactory.cs`
- `Services/Networking/PlatformErrorMapper.cs`
- `Services/Networking/PlatformJsonSerializer.cs`
- `Services/Networking/SafePlatformLogger.cs`
- `Services/Platforms/IPlatformDiscoveryAdapter.cs`
- `Services/Platforms/StubPlatformDiscoveryAdapter.cs`
- `Services/Platforms/PreviewModePolicy.cs`

修改的接入代码：

- `App.xaml.cs`
- `Models/PlatformModels.cs`
- `Services/Platforms/DiscoveryCache.cs`
- `Services/Platforms/PreviewDiscoveryService.cs`
- `ViewModels/DiscoverViewModel.cs`
- `README.md`

新增或修改的离线测试：

- `Tests/NetworkingInfrastructureTests.cs`
- `Tests/Infrastructure/NetworkingHandlers.cs`
- `Tests/Infrastructure/TestDiscoveryAdapters.cs`
- `Tests/Fixtures/Networking/success-response.json`
- `Tests/Fixtures/Networking/missing-required.json`
- `Tests/Fixtures/Networking/invalid-response.json`
- `Tests/Beans.Windows.Rebuild.Tests.csproj`
- `Tests/PlatformInfrastructureTests.cs`

## 3. 网络架构

```text
DiscoverPage
    |
    v
DiscoverViewModel -- request generation / current platform / page lifetime
    |
    v
IMusicDiscoveryService -- Preview remains the current UI data source
    |
    +-------------------- future Phase 5B/5C/5D --------------------+
    |                                                              |
    v                                                              v
IPlatformDiscoveryAdapter                                  DiscoveryCache
    |
    v
IPlatformHttpClientFactory
    |
    +--> Beans.Platform.Qq ------+
    +--> Beans.Platform.NetEase -+--> timeout / cancellation / retry
    +--> Beans.Platform.KuGou ---+    concurrency / merge / safe log
                                      error map / bounded JSON parse
```

适配器只负责平台 DTO、平台字段校验和业务模型映射。页面不直接访问 HTTP、请求上下文、响应体或凭证。

## 4. HttpClient 生命周期与命名客户端

`IPlatformHttpClientFactory` 由依赖注入注册为单例。工厂只创建并复用三个长生命周期客户端：

- `Beans.Platform.Qq`
- `Beans.Platform.NetEase`
- `Beans.Platform.KuGou`

每个平台使用独立 `SocketsHttpHandler`、连接池和并发信号量。启用 gzip、deflate 和 Brotli 解压，连接池生命周期为 10 分钟，连接超时为 6 秒；逻辑请求总超时由 `PlatformRequestContext.Timeout` 控制。适配器不得直接 `new HttpClient()`。

## 5. 请求上下文与响应

`PlatformRequestContext` 包含平台、操作名、唯一 CorrelationId、RequestKey、总超时、是否授权请求、缓存策略、不可逆账号摘要、重试上限和调用者取消令牌。公共请求使用固定 `anonymous` 标记；账号标识采用带应用域前缀的 SHA-256 摘要。

`PlatformResponse<T>` 明确区分成功值、错误、HTTP 状态、耗时、重试次数、DataOrigin、加载时间、过期状态、缓存键和安全消息，不使用 `null` 同时表达多种状态。

## 6. 错误模型

`PlatformErrorMapper` 将调用者取消、总超时、网络错误、401、403、404、429、5xx、非法请求、无效响应、解析失败、安全失败和未知失败映射为稳定的 `PlatformErrorCode`。UI 只需要读取安全错误码、消息、是否需要登录和是否可重试；异常文本、堆栈、Header、Cookie、Token 和响应体不会进入 UI 模型。

## 7. 超时与重试策略

- 每个逻辑操作使用一个覆盖信号量等待、发送、重试延迟和解析的总超时。
- 调用者主动取消映射为 `Cancelled`，不重试，也不转为页面错误。
- 公开请求最多重试一次；授权请求自动关闭重试。
- 仅网络瞬断、429 和 5xx 可重试；401、403、404、解析失败和无效响应不重试。
- 429 尊重合法 `Retry-After`，等待上限为 2 秒；其他重试使用短延迟和轻微随机抖动。
- 所有重试仍受同一总超时限制，不会形成无限请求。

## 8. 平台限流

QQ、网易云、酷狗各自持有一个上限为 3 的 `SemaphoreSlim`。同平台请求最多三个并发，不同平台互不阻塞。等待期间取消不会进入 HTTP Handler；异常和取消路径都在 `finally` 中释放许可。

## 9. 相同请求合并

合并键由 `RequestKey + AccountIdentityHash + 结果类型` 组成，且 RequestKey 必须以平台稳定 ID 开头。相同平台、相同账号上下文、相同结果类型的并发读取共享一个底层任务。一个等待者取消只结束自己的等待；仅当所有等待者都离开时才取消共享操作。

完成和失败任务都会从 in-flight 字典原子移除。未采用的竞争候选和完成后的共享取消源会被释放，失败请求可以再次发送。RequestKey 拒绝查询串和 cookie、token、authorization、password、secret、signature、session、csrf、uin 等敏感片段。

## 10. 晚到响应保护

Phase 4 的 `DiscoverViewModel` 继续使用平台 ID、递增 request generation、页面取消源和当前平台检查。网络层的 CorrelationId 与平台隔离不会绕过该检查。旧响应可以由后续真实适配器写入其自身平台缓存，但不能更新另一个平台或已经离开的页面；取消响应不会显示错误。

## 11. 安全日志与脱敏

`SafePlatformLogger` 只接收平台、操作名、CorrelationId、Host、绝对 Path、错误码、HTTP 状态、耗时、重试次数和 DataOrigin。完整 URL、QueryString、Header、请求体、响应体、异常、Cookie、Token 和凭证不传给日志器。

`SensitiveDataRedactor` 处理 cookie、set-cookie、token、authorization、Bearer、password、passwd、secret、api key、signature、sign、uin、session 和 csrf。URL 日志只保留 Host 与 `AbsolutePath`。

## 12. JSON 解析边界

`PlatformJsonSerializer` 集中维护大小写兼容和数字字符串兼容配置。响应以流读取，默认上限 1 MiB；超限返回 `SecurityFailure`。反序列化在后台任务执行，非法 JSON 返回 `ParseFailure`，缺失必需字段由 Adapter 映射阶段返回 `InvalidResponse`。平台 DTO 不暴露给页面，也不长期保存 `JsonDocument`。

## 13. DataOrigin 与缓存规则

- `Live`：本次网络读取成功。
- `CacheFresh`：仍在有效期内的成功缓存。
- `CacheStale`：后续真实适配器在网络失败时返回的同平台历史成功内容，并必须设置 `IsStale = true`。
- `Preview`：明确的开发预览内容，并必须设置 `IsPreview = true`。

`PlatformDiscoveryContent` 现在携带 DataOrigin、LoadedAt、IsStale、IsPreview 和 SafeStatusText。Preview 不会被标记成 Live；跨平台缓存键包含 PlatformId 和账号摘要。由于本阶段没有真实 Adapter，`CacheStale` 的回退触发点保留给 Phase 5B 的在线聚合服务，本阶段只建立模型与测试边界。

## 14. Preview 与 Release 行为

Debug 构建默认允许 Preview，状态明确显示 `预览内容 · 在线服务尚未连接`。Release 构建默认关闭 Preview；没有真实数据或缓存时返回安全的“在线服务尚未连接”状态，不会静默展示 Preview。可注入的 `IPreviewModePolicy` 让两种行为可离线测试。

## 15. Adapter 边界

`IPlatformDiscoveryAdapter` 定义 Hero、公开榜单、公开推荐歌单、歌单广场、每日推荐和分类六个入口，并公开能力模型。生产环境当前注册的三个实现均为 `StubPlatformDiscoveryAdapter`，只返回 `Unsupported`。Fixture Adapter 和 Fake Adapter 只存在于测试项目，不会打入正式应用。

## 16. 测试基础设施与结果

测试 Handler 支持固定响应、响应序列、延迟、异常、取消、并发计数和 Retry-After。JSON Fixture 为人工构造且脱敏的通用数据，只复制到测试输出目录，不进入正式应用包。

本阶段新增网络、安全和 Adapter 测试 45 项；连同 Phase 4 的 14 项，共 59 项。Debug 与 Release 均为 59 通过、0 失败、0 跳过。覆盖客户端复用、平台隔离、超时/取消、错误映射、有限重试、限流、请求合并、账号隔离、资源清理、安全日志、脱敏、响应大小、四种 DataOrigin、Preview 策略、Fixture/Fake/Stub Adapter，以及 Phase 4 的晚到响应与页面取消保护。所有测试完全离线。

## 17. 构建结果

- Debug：0 个警告，0 个错误。
- Release：0 个警告，0 个错误。
- Debug tests：59 passed，0 failed，0 skipped。
- Release tests：59 passed，0 failed，0 skipped。

## 18. 尚未实现的真实 Adapter

QQ、网易云和酷狗均未配置真实 BaseAddress、Endpoint、签名、Cookie 或 Token。没有真实发现请求、登录、搜索、详情、播放地址、收藏写入、下载或用户数据同步。现有应用仍由 Preview 服务提供 Debug 数据，三个生产 Adapter 都是安全 Stub。

## 19. Phase 5B 的明确接入点

Phase 5B 只实现 QQ 音乐公开发现 Adapter，并复用本阶段的 `IPlatformHttpClientFactory.Get("qq")`、`PlatformRequestContext.PublicDiscovery`、解析器、错误模型和安全日志。应实现的公开接口清单：

- `GetHeroAsync`
- `GetPublicRankingsAsync`
- `GetPublicRecommendedPlaylistsAsync`
- `GetPublicPlaylistSquareAsync`
- `GetCategoriesAsync`

`GetDailyRecommendationsAsync` 继续保持 `Unsupported`，直到单独的授权阶段。Phase 5B 还应增加 QQ 私有 DTO、字段映射、脱敏 Fixture、解析测试和网络失败后的同平台 stale-cache 聚合，但不得改写共享网络层或同时接入网易云、酷狗。

## 20. 停止点

Phase 5A 到此结束。UI 几何、Discover XAML、Home、Shell、Sidebar、BottomPlayer 和旧项目均未因本阶段重构。当前没有任何真实音乐平台已接通。
