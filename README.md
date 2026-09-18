# Beans Music 🎵

> 面向 **iOS 26、macOS 15（Mac Catalyst）和 Windows 11** 的跨端音乐播放器，聚合网易云音乐与 QQ 音乐，并提供端到端加密的 Beans 账号、授权保险库和歌单同步。
> 本软件完全开源，仅供学习研究使用。

> [!IMPORTANT]
> 本仓库是对 [XIaodou0416/Beans-Music](https://github.com/XIaodou0416/Beans-Music) 的二次开发，不是原项目的官方发行版。原作者版权声明和 MIT License 已保留。详细改动请阅读 [二次开发说明](docs/二次开发说明.md)。

## 本二次开发的主要改动

- 支持 iOS 26、macOS 15 Mac Catalyst 与 Windows 11 WinUI 3。
- 新增可自托管的 Beans 账号服务、邮箱注册、设备管理和扫码登录。
- 新增端到端加密保险库、跨端歌单与偏好同步、离线 outbox。
- QQ 音乐/网易云授权不再强制要求先登录 Beans 账号。
- 增加 QQ 音乐会员状态、播放密钥与多音质回落处理。
- 新增青碧玻璃、清透纸感、午夜霓虹三套界面风格，全局字号调节和“音乐宇宙”个人页。
- 重制应用图标，增加三端构建、测试与打包流程。

[![原项目预览](https://img.shields.io/badge/原项目-GitHub%20Pages-blue)](https://xiaodou0416.github.io/Beans-Music/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-iOS%2026%20%7C%20macOS%2015%20%7C%20Windows%2011-orange)]()
[![Swift](https://img.shields.io/badge/Swift-5-orange)]()

**原项目资料：** [源码仓库](https://github.com/XIaodou0416/Beans-Music) · [HTML 介绍页](https://xiaodou0416.github.io/Beans-Music/)

## 项目说明

- 本仓库在原项目基础上进行独立二次开发和维护。
- 部分需求分析、UI 方案、代码实现与文档由 OpenAI Codex 辅助完成。
- 项目依照 MIT License 开源，但 QQ 音乐、网易云音乐的商标、内容和服务仍归各自权利人所有。
- 使用者应遵守所在地法律、平台服务条款和音乐版权限制。

## 📱 界面预览

<p align="center">
  <img src="docs/screenshots/home.png" width="320" alt="首页">
  <img src="docs/screenshots/home-light.png" width="320" alt="首页（浅色）">
</p>
<p align="center">
  <img src="docs/screenshots/lyrics.png" width="320" alt="歌词页">
  <img src="docs/screenshots/player.png" width="320" alt="播放封面页">
</p>
<p align="center">
  <img src="docs/screenshots/search.png" width="320" alt="搜索页面">
  <img src="docs/screenshots/library.png" width="320" alt="音乐库">
</p>
<p align="center">
  <img src="docs/screenshots/settings.png" width="320" alt="全局设置">
  <img src="docs/screenshots/theme-light.png" width="320" alt="主题设置（浅色）">
</p>

---


## 🎨 核心卖点：DIY 美化，你的播放器你做主

这不是一个"长什么样就什么样"的播放器——**从全局主题到播放器每一个组件，全部都可以自定义**。

**全局主题，一键换肤**
- 内置 9 套主题色：琥珀暖金 / 青碧湖绿 / 樱粉 / 星蓝 / 罗兰紫 / 赛博青 / 蜜桃粉 / 鎏金黑 / 翡翠绿
- 不满意？打开**色盘**，任意颜色随便选，整个 App 的强调色、按钮、进度条、文字高亮全部跟随
- 主题模式跟随系统 / 浅色 / 深色，深浅色两套观感分别适配

**壁纸库：你的背景你做主**
- 上传**多张**自定义壁纸，自动存入壁纸库，随时在库中切换替换，不用反复去相册
- 支持浅色 / 深色两套背景，暗色壁纸不再发黑
- 全局背景同步：主页、搜索、音乐库、「我的」一键同步同一套背景

**全局 UI 样式**
- 内置默认柔和、清透简洁、紧凑淡雅、描边高亮等样式
- 底栏、卡片、播放器、弹窗全部跟随，质感统一

**播放器底部布局：自由拖动**
- 进度条、控制按钮、指示线支持 **XY 自由拖动 + 缩放**，摆到任何你喜欢的位置
- 调整时有**悬浮窗实时预览**，边调边看效果，一键恢复默认
- 指示线位置、显示开关均可单独控制

**歌词：专业级 DIY**
- 颜色：当前行 / 未播放行 / 发光颜色，色盘任选；渐变起止色自由搭配
- 效果：发光强度、模糊起始距离与模糊强度、辉光全可调
- 排版：字号、行距、显示行数、对齐（居中 / 居左）、位置重心全可调
- 3D 立体倾斜：上下后仰 + 左右倾斜，营造空间感
- 长按多选复制歌词、歌词翻译、逐行跟随、点击跳转

**进度条与氛围**
- 4 种进度条样式：流光 / 辉光 / 极光 / 波浪，带渐变辉光效果
- DJ 节奏脉冲光效，随旋律律动
- 圆形封面模式 + 自动旋转，封面点击切换歌词视图

**一键备份你的全部设置**
- 主题、配色、歌词效果、播放器布局、音质等所有自定义配置
- 一键导出为 JSON 分享 / 一键导入恢复，换机不再重新调

---

## 🚀 双平台聚合，一个 App 听遍网易云 + QQ 音乐

- 网易云 / QQ 音乐一键切换：每日推荐、排行榜、推荐歌单、搜索、热门搜索全部跟随平台
- 网易云：扫码登录 + 应用内网页登录，同步歌单、收藏、听歌排行与 VIP / SVIP 标识
- QQ 音乐：扫码授权 + 网页登录 / Cookie 导入，同步歌单与 VIP 标识
- 双平台歌曲评论区、歌词、歌手主页互通

## 🎧 播放器：从封面里长出来的界面

- 动态封面取色：背景渐变、进度条、播放按钮、功能按钮、歌词高亮全部实时跟随专辑封面主色，切歌平滑过渡
- 抖音式上下滑动切歌，过渡动画丝滑；左右滑动也可切换
- 封面点击切换歌词视图（封面飞入左上角动画）、圆形封面模式 + 自动旋转
- 底部指示线上滑呼出评论区（网易云 + QQ 评论）

## ⚡ 播放能力拉满

- 5 档音质（标准 / 较高 / 极高 / 无损 / Hi-Res），无损与 Hi-Res 自动回落
- 免费听歌开关（灰色 / VIP / 版权歌曲自动匹配第三方音源）
- 第三方音源：内置聆澜 LX / CR / QT 三种预设，作为官方地址不可用时的备用来源
- 歌曲下载（低质量 128k / 高质量 320k，存到文件 App）
- 锁屏控制、控制中心、后台播放、播放队列与插队、倍速（0.5x~2.0x）、循环模式、±15 秒、睡眠定时
- 播放历史、网易云听歌排行（本周 / 所有时间）

## 📱 系统体验

- iOS 26 原生液态玻璃（Liquid Glass）TabBar，与系统 App 一致
- 首次进入 4 页引导（欢迎 / DIY 美化 / 双平台 / 免责确认）+ 版本更新说明弹窗 + 更新日志
- 软件使用说明、深浅色自适应

---

## 📂 工程结构

```
Beans/
├── BeansApp.swift                应用入口（首次使用引导页 + 免责确认）
├── RootView.swift                底部 Tab 液态玻璃导航 + 迷你播放器
├── DiscoverView.swift            发现页（每日推荐 / 排行榜 / 推荐歌单）
├── SearchView.swift              搜索（网易云 / QQ 切换 / 热搜）
├── LibraryView.swift             音乐库（歌单 / 本地音乐库 / 最近播放）
├── ProfileView.swift             我的（功能 / 听歌排行 / 使用说明 / 设置）
├── PlayerView.swift              播放器（动态取色 / 歌词 / 底部控制 / 设置）
├── PlayerManager.swift           播放核心（队列 / 倍速 / 定时 / 历史）
├── LoginView.swift               网易云登录（扫码 + 网页登录）
├── NetEaseWebLoginSheet.swift    网易云网页登录（WKWebView 读取 Cookie）
├── QQMusicAPI.swift / QQMusicAuth.swift / QQWebLoginSheet.swift  QQ 音乐接入
├── NetEaseAPI.swift / NetEaseCrypto.swift   网易云接口与加密（weapi / eapi）
├── AuthStore.swift / Models.swift   登录态与数据模型
├── Theme.swift / CoverPalette.swift   全局主题与封面取色
├── PlayerLayout.swift / PlayerAmbience.swift / DJVisualView.swift   布局与氛围
├── DownloadManager.swift / UnblockService.swift / UnblockSourceStore.swift   下载与音源
├── CommentsSheet.swift / ArtistHomeSheet.swift   评论与歌手主页
├── Changelog.swift / Components.swift / SongCell.swift   日志与组件库
└── Assets.xcassets                图标与资源
```

---

## 🔨 构建

### Beans 账号服务

需要 Docker Compose。复制 `server/.env.example` 为 `server/.env` 并设置 PostgreSQL 密码与 JWT 签名密钥，然后运行：

```bash
docker compose --env-file server/.env -f server/compose.yaml up --build
```

- API 默认由 Caddy 暴露在 `http://localhost`。
- 开发邮件可在 `http://localhost:8025` 的 Mailpit 查看。
- 生产环境应关闭 `BEANS_EXPOSE_CODES` 并填写 SMTP 配置。
- 服务端只存储加密保险库、加密同步正文和必要的账号元数据，不代理音乐 API 或音频流。

### Apple

GitHub Actions 自动构建未签名 IPA、Mac Catalyst 应用和 Windows MSIX，构建产物应从当前仓库的 Actions 或 Releases 获取。

本地构建需要 macOS、Xcode 26 和 XcodeGen。正式部署目标为 iOS 26 与 macOS 15；旧版 Xcode 只能通过临时覆盖部署目标做结构验证。

```bash
brew install xcodegen
xcodegen generate
xcodebuild -project Beans.xcodeproj -scheme Beans -configuration Release \
  -sdk iphoneos -derivedDataPath build \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" build
mkdir -p Payload
cp -R build/Build/Products/Release-iphoneos/Beans.app Payload/
ditto -c -k --sequesterRsrc --keepParent Payload Beans-unsigned.ipa
```

Mac Catalyst：

```bash
xcodebuild -project Beans.xcodeproj -scheme BeansCatalyst -configuration Release \
  -destination 'generic/platform=macOS,variant=Mac Catalyst' -derivedDataPath build-catalyst \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" build
```

### Windows 11

安装 .NET 10 SDK 和 Visual Studio 的 WinUI/Windows App SDK 工作负载，在 Windows 11 上运行：

```powershell
dotnet test windows/Beans.Core.Tests/Beans.Core.Tests.csproj -c Release
dotnet build windows/Beans.Windows/Beans.Windows.csproj -c Release -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
```

MSIX 输出位于 `windows/Beans.Windows/AppPackages`。macOS 可以构建和测试 `Beans.Core`，但不能执行 Windows 的 XAML 编译器。

Windows 客户端已包含：Beans 注册/登录/邮箱验证、二维码登录与设备撤销、三套中文主题、QQ 音乐/网易云 WebView2 授权恢复、客户端资料校验、只读歌单镜像加密同步、本地音乐扫描与 MediaPlayer 播放、同名 LRC 歌词高亮、队列，以及仅保存在本机的合法音频断点下载。Beans 服务端不接收平台 Cookie，也不代理第三方音频。

### 分平台发布与更新

三个客户端使用独立的 GitHub Release 通道，只检测并下载本端资产：

- iOS：`ios-v*`，Release 只包含 `.ipa`。
- macOS：`mac-v*`，Release 只包含 Catalyst `.app.zip`。
- Windows：`windows-v*`，Release 只包含与当前 CPU 架构匹配的 `.msix`。

可以在 GitHub Actions 的 `Publish Platform Release` 中选择平台和版本，也可以推送对应标签触发发布：

```bash
git tag ios-v1.6.0 && git push origin ios-v1.6.0
git tag mac-v1.6.0 && git push origin mac-v1.6.0
git tag windows-v1.6.0 && git push origin windows-v1.6.0
```

客户端在启动、回到前台和持续运行期间按 24 小时周期检查，并支持手动检查。Swift/WinUI 可执行代码不能绕过系统签名做热更新；iOS IPA、Catalyst 应用和 Windows MSIX 仍需重新安装。仅非可执行的主题或远端配置适合后续增加资源热更新。

### 验收边界

正式目标是 iOS 26、macOS 15 和 Windows 11。当前开发机只有 Xcode 16.2/iOS 18.2 SDK 且未安装 .NET 10/Windows SDK，因此本机只能完成 Apple 结构构建、XAML/XML/契约静态检查；正式 Apple 26 构建、Windows XAML/MSIX 构建和真实 QQ/网易云账号互通需由 `.github/workflows/ecosystem-ci.yml` 在 macOS 26 与 Windows 11 runner 执行。

### 安全与同步

- 密码通过 Argon2id 和 HKDF 在本地派生，密码及保险库主密钥不上传。
- QQ 音乐和网易云 Cookie 在 Apple Keychain 或 Windows DPAPI 中保存；跨端同步前使用 AES-256-GCM 加密。
- SQLite outbox 支持离线写入、增量游标、删除墓碑和版本冲突重试。
- 忘记密码会撤销所有 Beans 会话并销毁旧跨端保险库；当前设备的第三方平台授权继续保存在 Keychain/DPAPI 中，其他设备需重新建立同步。
- 第三方歌单是只读镜像；Beans 自建歌单可跨端编辑。

## 📲 安装

未签名 IPA 需自行签名安装：Sideloadly / AltStore / 爱思助手，使用 Apple ID 签名（免费自签 7 天有效）。
- 支持 iOS 26；Mac 正式客户端使用 macOS 15 Mac Catalyst
- Bundle ID：`com.beans.app`

---

## ⚠️ 免责声明

- 本应用仅供个人学习研究使用，禁止用于商业及非法用途，如产生法律纠纷与作者无关
- 音乐 API 来源于 GitHub 开源项目（非官方版 API），本软件不提供任何音频存储服务
- 音乐版权归各平台所有，请支持正版；如需下载音频请在各平台官方渠道购买
- “QQ”、“QQ音乐”及企鹅形象等文字、图形和商业标识，其著作权或商标权归腾讯公司所有
- “网易云”、“网易云音乐”等文字、图形和商业标识，其著作权或商标权归网易公司所有
- 具体内容请参考各平台用户协议

## 📄 License

[MIT](LICENSE) © 2026 XIaodou0416

**开源说明：** 本项目为开源软件，代码公开透明，接受 Issue 反馈与 Fork 学习。非官方 API 属逆向学习范畴，请尊重各平台服务条款，合理使用。
