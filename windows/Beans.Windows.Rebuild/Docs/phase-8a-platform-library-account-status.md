# Phase 8A：平台资料库、可见探针与 Beans 账号基线

本阶段完成了以下真实边界：

- QQ 音乐搜索与详情保留 `media_mid`，官方 vkey 播放按请求音质逐级回退；
- QQ vkey 返回多个 CDN 时，以 `Range: bytes=0-1`、`ResponseHeadersRead` 逐个探测官方 HTTPS 节点，首个节点失败后继续回退，探测不读取音频正文；
- QQ 播放区分登录失效、会员权益不足、区域限制与资源不可用；
- QQ 音乐只读加载账号资料、我喜欢、创建目录及歌单曲目；
- 网易云音乐只读加载账号资料、会员状态、创建/收藏歌单及完整曲目；
- “创建的歌单”页面显示双平台真实快照，并路由到授权歌单详情；
- “账号与平台”页面提供显式刷新探针，显示账号、会员与检查时间；
- Beans 账号具备注册、邮箱验证、登录、Token 刷新、退出、保险库及加密同步基础设施；
- Beans Token 与保险库密钥进入 Credential Locker；同步载荷使用 AES-GCM 加密后传输。

## 冻结契约

```text
IPlatformLibraryService
IPlatformLibraryAdapter
PlatformLibrarySnapshot
PlatformProbeSnapshot
PlatformUserPlaylist
PlatformPlaylistTrack
IBeansAccountService
IBeansSyncService
IBeansVaultService
```

## 安全边界

- 不提供会员绕过或第三方解锁源；
- 会员歌曲只在登录账号拥有有效权益且官方接口返回播放源时播放；
- CDN 探测仅允许 QQ 官方域名和 443/默认 HTTPS 端口，HTTP 自动重定向关闭，非官方地址不会收到平台 Cookie；
- 平台 Cookie 不上传 Beans 服务，不进入普通文件、请求键或日志；
- UI 只显示安全状态，不显示 Cookie、Token 或平台原始异常；
- Beans 服务地址由用户显式配置；项目不虚构生产服务地址。

## 验证边界

- 离线 Fixture/Handler 测试覆盖请求结构、状态映射、加密篡改拒绝、部分成功、QQ CDN 回退、全部节点失败、非官方域名拒绝和音频正文零读取；
- Debug 与 Release 测试均为 `377/377` 通过，Release 构建为 0 警告、0 错误；
- 最新 Debug 进程启动后保持响应，本次启动后未发现新的 `Application Error` 或 `.NET Runtime` 崩溃事件；
- Windows UI 自动化助手无法枚举应用窗口，因此本轮未执行设置页、授权页、歌单页和实际点击播放的视觉验收；
- 真实 QQ/网易云账号、真实会员歌曲与真实 Beans 服务仍需用户完成登录后人工验收；
- 未经真实账号验证，不声称周杰伦或会员曲目已经在线验收通过。
