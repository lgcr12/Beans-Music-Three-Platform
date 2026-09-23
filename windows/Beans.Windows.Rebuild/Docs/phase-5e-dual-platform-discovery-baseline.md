# Phase 5E：QQ + 网易云双平台发现基线

## 1. 正式平台范围

当前正式在线平台只有：

- `qq`：QQ 音乐；
- `netease`：网易云音乐。

本地与内部来源继续保留 `local`（本地音乐）和 `beans`（Beans 歌单），本阶段没有扩展其业务。没有实现搜索、登录、播放地址、详情、下载或酷狗。

## 2. 酷狗边界与历史偏好迁移

`PlatformId.KuGouMusic`、平台接口、命名网络客户端和安全 Stub 继续保留，避免删除平台扩展边界；但应用描述对象固定为 `IsAvailable = false`、`IsEnabled = false`，`SafeStatusText = 当前版本暂不支持酷狗音乐`。发现页只从可用且已启用的平台生成 QQ/网易云选择项，因此不会显示酷狗入口、状态、Preview、登录入口或错误。

Registry 在恢复偏好时不会允许不可用平台被旧的 `platform.enabled.kugou=True` 重新启用，并会把它写回 `False`。若 `platform.current=kugou`，当前平台回退到第一个已启用的 QQ/网易云平台并持久化新的稳定 ID；不会抛异常或弹窗，也不会回退到酷狗。运行时对酷狗执行启用或选择同样不会生效。

## 3. 发现状态文案

| DataOrigin | QQ 音乐 | 网易云音乐 |
| --- | --- | --- |
| Live | `QQ 音乐 · 公开内容 · 更新于 HH:mm` | `网易云音乐 · 公开内容 · 更新于 HH:mm` |
| CacheFresh | `QQ 音乐 · 来自缓存 · 更新于 HH:mm` | `网易云音乐 · 来自缓存 · 更新于 HH:mm` |
| CacheStale | `QQ 音乐 · 网络不可用，正在显示上次内容` | `网易云音乐 · 网络不可用，正在显示上次内容` |
| Preview | `QQ 音乐 · 预览内容 · 在线服务尚未连接` | `网易云音乐 · 预览内容 · 在线服务尚未连接` |

状态栏不再混入授权标签、刷新标签或技术错误。刷新进度继续由独立 ProgressBar 表达。每日推荐提示独立计算：网易云匿名边界显示 `登录网易云音乐后查看每日推荐`；QQ Adapter 的真实能力为 Unsupported，因此显示 `当前版本暂不支持 QQ 音乐每日推荐`。如果 QQ 将来明确返回 Unauthorized，已有分支会显示 `登录 QQ 音乐后查看每日推荐`。

## 4. 首页来源规则

所有来源统一使用：

- `QQ 音乐`
- `网易云音乐`
- `本地音乐`
- `Beans 歌单`
- `未知来源`

首页 PreviewData 不再包含酷狗实体。播放 Preview 明确追加 `· 预览内容`，不会使用 `QQ`、`网易云`、`Beans` 或 `本地预览` 作为最终来源名称。无法解析的排行榜平台不再回退为本地音乐，而是显示未知来源。

## 5. 双平台隔离

发现聚合继续按 `PlatformId + operation + category + page + account hash` 构造缓存键。QQ 与网易云的 Hero、排行榜、推荐歌单、分类和歌单广场从各自 Adapter 映射，平台实体保留各自稳定 ID。Fresh/Stale 回退只读取同键缓存，不跨平台替代。

`DiscoverViewModel` 为每个平台保留独立 `PlatformDiscoveryState`，其中包含 Content、SelectedCategory、ScrollOffset、IsLoading、IsRefreshing、ErrorState、LastLoadedAt 和 IsStale。切换时取消当前请求，并以递增 request generation 和当前平台 ID 双重校验响应；晚到响应不能覆盖新平台。强制刷新不提前清空旧内容，失败时仍保留可用内容并只标记当前平台错误。

## 6. Live / Fresh / Stale / Preview 行为

- Live：公开请求成功并写入当前平台、当前板块缓存。
- CacheFresh：非强制加载命中未过期的同平台缓存，不重复公开请求。
- CacheStale：网络失败后仅回退到同平台过期成功值，保留内容并标记 Stale。
- Preview：只允许 Debug 的显式 Preview 策略在无 Live/缓存时使用；Release 默认禁用，不会静默伪装成 Live 或缓存。
- 网易云每日推荐在匿名状态不发网络请求并返回 Unauthorized。
- QQ 每日推荐不发网络请求并返回 Unsupported。

## 7. DPI 与响应式验收记录

2026-09-20 使用项目已有的 `BEANS_PREVIEW_WIDTH` / `BEANS_PREVIEW_HEIGHT` 分别启动 Debug 应用：

| 有效尺寸 | 启动与窗口响应 | 截图级视觉复核 |
| --- | --- | --- |
| 1440×900 | 通过，`Beans Music` 窗口创建且 Responding=True | 待人工复核 |
| 1152×720 | 通过，`Beans Music` 窗口创建且 Responding=True | 待人工复核 |
| 960×600 | 通过，`Beans Music` 窗口创建且 Responding=True | 待人工复核 |

当前 Codex 会话的原生 Computer Use API 被禁用：能力文档可读取，但原生 `listApps/listWindows` 不可调用，因此无法取得窗口截图或执行 QQ/网易云点击与滚动检查。本记录没有把无重叠、无截断、无默认 WinUI 蓝色、无横向页面滚动或 XAML Binding Error 伪报为目视通过。代码侧确认三个目标宽度分别落入 Wide、Compact、Narrow 路径，页面级横向滚动被禁用，列表与分类保留其局部横向滚动；截图级项目仍需在可用的原生 UI 工具或人工窗口中复核。

## 8. 测试与构建

基线为 118 项。本阶段新增 22 个 Phase 5E 执行实例，总计 140 项，覆盖：正式平台列表、酷狗隐藏与禁用、历史偏好迁移和持久化、QQ/网易云精确来源状态、每日推荐能力提示、五类来源名称、未知来源保护、首页不含酷狗，以及内容、Hero、排行榜、歌单、滚动、刷新和错误状态隔离。既有 Phase 5A/5B/5C 测试继续保留。

- Debug build：0 warnings，0 errors。
- Release build：0 warnings，0 errors。
- Debug tests：140 passed，0 failed，0 skipped。
- Release tests：140 passed，0 failed，0 skipped。
- 测试不依赖真实外网。

最终构建结果以完成本文档后的最后一次验证命令为准。

## 9. 安全与生命周期

没有新增凭证、Cookie、Token、完整 URL、请求体或响应体日志。现有安全日志仍只记录平台、操作、CorrelationId、Host、Path、错误码、状态码、耗时、重试和 DataOrigin。源码与 Fixture 扫描未发现硬编码凭证。

`IPlaybackService` 仍由 DI 注册为 Singleton；`AppShell` 仍只创建一个 BottomPlayer 并在页面导航和平台切换间复用。本阶段没有修改 MainWindow、AppShell、Sidebar、TopBar、BottomPlayer 或 Home/Discover 的 XAML 几何、断点、颜色 Token、字体 Token、Slider/按钮模板。

## 10. 已知限制

- QQ 分类与歌单广场仍为 Unsupported。
- QQ 每日推荐仍为 Unsupported。
- 网易云每日推荐仍需要未来授权流程。
- 公开 Web Endpoint 可能变化。
- Debug Preview 仍是显式演示数据，Release 不以其替代真实数据。
- 三档尺寸的截图级视觉复核因本次会话原生 Computer Use 不可用而待人工完成。

## 11. Phase 6 接入点

Phase 6 只应实现 QQ 音乐、网易云音乐与本地音乐的统一搜索，界面范围为综合、QQ 音乐、网易云音乐和本地音乐。继续复用 `IMusicPlatformRegistry`、`IMusicPlatformService.SearchAsync`、共享网络层、平台稳定 ID、安全错误与取消边界，并保持不同来源结果可识别。不要把酷狗加入搜索，不要同时实现登录、播放地址、下载或写操作。

Phase 5E 到此停止，不开始 Phase 6。
