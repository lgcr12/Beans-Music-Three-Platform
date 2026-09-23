# Phase 7H：本机收藏与播放历史基线

## 已完成

- `IUserLibraryService` 统一管理本机收藏与最近播放。
- 收藏以 `PlatformId + LibraryItemKind + NativeId` 作为身份键，重复收藏只更新安全元数据。
- JSON 采用临时文件、写穿透和同目录原子替换；保留上一个可解析版本为 `.bak`。
- 主文件损坏时回退备份；主文件和备份都损坏时返回安全空状态，不向 UI 暴露解析异常。
- 播放历史按平台歌曲身份去重、按最近播放倒序并限制条目上限（默认 200）。
- `LibraryMediaSnapshot` 不包含播放 URL、Cookie、token、provider payload 或凭证字段。
- Preview 数据会在契约入口被拒绝，不能写入真实收藏或播放历史。
- `FavoritesPage` 已改用真实持久化收藏，支持本机移除。
- `LibraryPage` 已改用真实收藏统计与播放历史；删除了该页面的假统计、假歌单和假最近添加。
- 在线歌单创建仍明确为未接入，不伪造平台写入成功。

## 安全边界

- 在线收藏当前是 Beans 本机收藏，不代表 QQ 音乐或网易云音乐账号侧收藏。
- 在线历史重播只重建平台与原生歌曲标识，再交给统一播放源解析器；持久层永不保存解析后的媒体地址。
- 本地歌曲历史可保存身份与展示元数据，但因为持久层不保存播放路径，需从“本地音乐”页再次播放。

## 共享接线要求

当前 `App.xaml.cs`、`AppShell` 和 `PlaybackService` 属于共享冻结文件，本阶段没有修改。页面暂时通过 `UserLibraryRuntime.Current` 共享同一个持久化服务实例。

共享所有者最终应完成以下一次性接线：

1. 在 DI 中将 `IUserLibraryService` 注册为单例。
2. 将该实例传入 `FavoritesPage` 与 `LibraryPage`，随后可移除临时的 `UserLibraryRuntime` 组合根。
3. 仅在 `PlaySearchResultAsync` / 本地 `PlayAsync` **确认成功开始播放后**，通过 `LibraryMediaSnapshot.TryCreate` 调用 `RecordPlaybackAsync`；解析失败、授权失败和 Preview 条目不得写历史。
4. `ToggleFavorite` 或页面收藏按钮通过同一个服务调用 `SetFavoriteAsync`；不得把本地收藏冒充成平台账号写操作。
5. 播放进度结束时可补写真实 `playedDuration`，但不能因此重复增加播放次数；若需要结束回写，应另加不递增次数的进度契约。

## 验证范围

`UserLibraryServiceTests` 共 7 项，覆盖：身份保留、敏感字段不落盘、收藏去重与移除、Preview 拒绝、历史去重与上限、备份恢复、双损坏安全重置，以及结构合法但条目无效时的安全清理。

UI 自动化和 DPI 截图验收不在本阶段伪造为通过。
