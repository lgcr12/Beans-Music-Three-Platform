# 并发开发边界与文件所有权

本文档用于 Beans Music Windows Rebuild 的后续并行开发。目标是让低耦合页面可以并发推进，同时避免多个任务同时修改搜索、授权、播放器和全局资源。

## 依赖批次

```text
Phase 6E 综合搜索收口
        ↓
冻结统一搜索与播放器契约
        ↓
页面型任务并发
        ↓
统一授权基础设施
        ↓
QQ / 网易云授权适配并发
        ↓
统一播放源契约
        ↓
QQ / 网易云播放源适配并发
        ↓
PlaybackService 单点集成
        ↓
最终集成与视觉回归
```

当前基线：

```text
phase-6d1-local-ui-baseline
phase-6-concurrency-boundary-baseline
phase-6e-search-integration-baseline
```

Phase 6E 已完成。在后续任务中，搜索聚合契约和当前在线播放边界视为只读基础。

## 文件所有权

| 区域 | 当前所有者 | 其他任务 |
| --- | --- | --- |
| `App.xaml`、`MainWindow.*` | Shell 集成任务 | 只读 |
| `Shell/*`、`AppShell.*` | Shell 集成任务 | 只读 |
| `Styles/*`、公共 `Controls/*` | UI 基础任务 | 不修改 |
| `Pages/Search/*` | 搜索集成任务 | 不修改 |
| `Services/Search/*`、`Models/SearchModels.cs` | 搜索集成任务 | 只读 |
| `Pages/Library/*` | 音乐库任务 | 不修改 |
| `Pages/Playlist/*` | 歌单详情任务 | 不修改 |
| `Pages/Artist/*`、`Pages/Album/*` | 歌手/专辑详情任务 | 不修改 |
| `Pages/Settings/*` | 设置任务 | 不修改 |
| `Pages/Accounts/*` | 账号与平台 UI 任务 | 不修改 |
| `Pages/Downloads/*` | 下载管理任务 | 不修改 |
| `Pages/QueueLyrics/*` | 队列与歌词任务 | 不修改 |
| `Pages/MusicUniverse/*` | 音乐宇宙任务 | 不修改 |
| `Services/Accounts/*`、`Services/Security/*` | 授权基础设施任务 | 不修改 |
| `Services/Platforms/QQ/*` | QQ 平台任务 | 不修改网易云和共享授权契约 |
| `Services/Platforms/NetEase/*` | 网易云平台任务 | 不修改 QQ 和共享授权契约 |
| `Services/Playback/*` | 播放集成任务 | 适配任务只读 |
| `Controls/BottomPlayer/*` | 播放器 UI 任务 | 不修改 |

以下共享文件只能由单一集成任务修改：

```text
App.xaml
App.xaml.cs
MainWindow.xaml
MainWindow.xaml.cs
AppShell.xaml
AppShell.xaml.cs
Styles/*
PlaybackService
MusicSearchService
CredentialStore
全局 Models
DI 注册
```

## 页面并发组

音乐库、歌单详情、歌手/专辑详情、设置、账号、下载、队列歌词和音乐宇宙页面可以并发开发，但每个任务只修改自己的 `Pages/<Area>/*` 目录。页面可以使用明确标记的 PreviewData 或占位服务，不得直接改动真实平台服务。

页面任务不得自行调整全局颜色、字号、Slider、BottomPlayer、Sidebar、TopBar 或全局路由。页面需要公共能力时，先在自己的目录中使用临时适配，交付文档提出公共化建议，再由集成任务迁移。

## 授权并发组

授权基础设施必须先由一个任务建立统一契约，例如：

```text
IPlatformAuthService
ICredentialStore
PlatformCredentialState
AuthorizationResult
```

QQ 和网易云授权适配只能在契约冻结后并发。各适配器只拥有自己的平台目录，不得同时修改 `AuthService`、`CredentialStore`、`PlatformAccountState`、`PlaybackService` 或全局错误模型。凭证只能进入安全存储，不得写入代码、启动脚本或普通日志。

## 详情数据并发组

QQ 和网易云详情适配可以并发，但必须先冻结共享 UI 模型：

```text
PlaylistDetail
RankingDetail
ArtistDetail
AlbumDetail
```

页面任务负责统一呈现，平台任务负责自己的 DTO 映射和请求边界，不各自设计互不兼容的返回结构。

## 播放并发组

在线播放必须拆成四个依赖步骤：

```text
D1  IPlaybackSourceResolver 契约
D2  QQ 播放源适配
D3  网易云播放源适配
D4  PlaybackService 集成
```

D1 完成后，D2 和 D3 可以并发。D2、D3 不得直接操作 `MediaPlayer` 或修改 `PlaybackService` 生命周期；D4 由单一集成任务完成。在线播放适配必须遵守授权、区域、版权和服务端访问边界，不得从匿名搜索结果推断播放地址。

## 每个并行任务的前置要求

```text
1. 只修改任务明确授权的目录。
2. 不修改 AppShell、MainWindow、BottomPlayer 和全局 Styles。
3. 不修改其他页面、旧 Windows、Apple、服务端、版本号或发布流程。
4. 不创建第二套 Token、网络层、播放器或错误模型。
5. 复用已有统一模型和接口。
6. 不把 PreviewData 冒充真实数据。
7. 不留下空事件处理器；按钮必须有行为或明确禁用说明。
8. 新结果保留 PlatformId 和 NativeId。
9. 不改变 PlaybackService 生命周期。
10. 完成后运行对应构建和测试。
11. 报告实际修改文件、共享文件修改情况和未完成 Stub。
12. 不自动开始下一个阶段。
```

## 集成门槛

每个批次完成后，集成任务必须确认：

- Debug 和 Release 构建无警告、无错误；
- 现有测试和新增测试全部通过；
- 未产生敏感日志或硬编码凭证；
- 未出现第二套播放器、网络层或错误模型；
- 页面任务未修改共享文件；
- UI 自动化不可用时明确记录未执行，不伪造截图或 DPI 验收。

本文件只定义协作边界，不启动页面、授权或播放任务，也不代表这些后续阶段已经实现。
