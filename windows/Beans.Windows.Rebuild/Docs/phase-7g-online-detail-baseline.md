# Phase 7G：公开详情读取基线

## 已完成

- 新增页面可注入契约 `IOnlineMusicDetailService`。
- 统一详情类型：歌单、歌手、专辑、排行榜。
- 详情曲目统一转换为现有 `SearchResultItem`，不绕过统一播放边界。
- 详情缓存明确标记 `Live`、`CacheFresh`、`CacheStale`；网络失败时仅在存在旧缓存的情况下回退。
- 平台原生标识在发起请求前严格校验：
  - 网易云：仅正整数 ID；
  - QQ 歌单/排行榜：仅正整数 ID；
  - QQ 歌手/专辑：仅字母数字 MID；
  - 拒绝空白、控制字符、路径字符和查询字符。
- `PlaylistPage`、`ArtistPage`、`AlbumPage`、`RankingDetailPage` 已移除运行时 PreviewData 详情加载，支持异步加载真实详情和取消。

## 平台能力矩阵

| 平台 | 歌单 | 歌手 | 专辑 | 排行榜 |
|---|---|---|---|---|
| QQ 音乐 | 匿名公开接口 | 匿名公开热门曲目 | **Unsupported** | 匿名公开接口 |
| 网易云音乐 | 匿名公开接口 | 匿名公开热门曲目 | 匿名公开接口 | 复用公开歌单详情 |

QQ 专辑详情没有在本基线中猜测或伪造协议，明确返回 `Unsupported`。歌手相关专辑列表也没有可靠公开响应来源，因此 `RelatedCollections` 当前为空。

## 安全与播放边界

- 所有网络请求复用 `IPlatformHttpClient`，没有第二套网络层。
- 请求键只包含平台、详情类型、已验证 NativeId，不包含 Cookie、Token、查询串或账号标识。
- 详情层不读取凭证，不操作 `MediaPlayer`，也不生成播放 URL。
- 页面点击曲目后仍调用已有 `IPlaybackService.PlaySearchResultAsync`；是否可播放由统一播放源解析器决定。
- 平台响应缺少业务状态、主对象、曲目 ID 或曲目标题时按安全的 `InvalidResponse` 失败，不生成占位曲目。

## 最终接线要求

父级集成需要在唯一 DI 组合根注册：

```csharp
services.AddSingleton<IOnlineMusicDetailAdapter, QqMusicDetailAdapter>();
services.AddSingleton<IOnlineMusicDetailAdapter, NetEaseMusicDetailAdapter>();
services.AddSingleton<IOnlineMusicDetailService, OnlineMusicDetailService>();
```

并由 `AppShell` 将同一个 `IOnlineMusicDetailService` 注入四个详情页面。`PlaylistPage` 需要传入完整的 `PlatformRouteParameter`，不能只传 `NativeId`，否则无法安全判断平台。

## 验证

- Debug/Release 构建：0 warnings / 0 errors。
- Debug/Release 全量测试：304/304 通过（并发工作树统一快照）。
- `OnlineMusicDetailTests`：9/9 通过，覆盖 QQ 歌单映射、当前 Musicu 歌手歌曲响应、QQ 专辑明确 Unsupported、网易云歌手映射、非法 NativeId 请求前拒绝、Fresh 缓存来源改写。
- 2026-09-21 只读公网探测确认 QQ 排行榜详情、歌单详情与 Musicu 歌手歌曲接口当前返回预期结构；该探测不代替可重复的 fixture 测试。
- UI 自动化与真实公网人工验收：未执行；不得据此声称公网接口或截图验收已通过。
