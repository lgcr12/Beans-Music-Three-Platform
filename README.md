# Beans Music · Three Platform Edition

<p align="center">
  <img src="design/icon/beans-icon-master.png" width="104" alt="Beans Music icon">
</p>

Beans Music 是一个面向 iPhone、Mac 和 Windows 11 的开源音乐客户端工程。仓库同时维护 Apple 客户端、Windows 原生重构版，以及可选的自托管 Beans 账号服务。

当前版本重点不是把同一套界面机械复制到三端，而是在共享账号、安全和音乐数据边界的前提下，为每个平台保留合适的原生交互。

## 项目来源与二次开发声明

> 本仓库是对 **[XIaodou0416/Beans-Music](https://github.com/XIaodou0416/Beans-Music)** 的独立二次开发，不是原项目的官方发行版。
>
> 二次开发起点为原仓库提交 **3617004**。原作者版权信息、MIT License 和原仓库地址均予以保留。本仓库新增功能、构建产物、问题反馈和维护决定不代表原作者立场。

- 原始仓库：https://github.com/XIaodou0416/Beans-Music
- 当前二改仓库：https://github.com/lgcr12/Beans-Music-Three-Platform
- 详细差异：[docs/二次开发说明.md](docs/二次开发说明.md)

## 界面预览

### Windows 11 · 二次元动态播放器

Windows 重构版提供夏日与春樱两套动态场景。人物局部、云层、水面、树冠、风痕和花瓣由 Composition 动画驱动，并支持暂停渐停、窗口最小化停止和系统减少动效。

<p align="center">
  <img src="docs/screenshots/windows-anime-summer.png" width="49%" alt="Windows 夏日动态播放器">
  <img src="docs/screenshots/windows-anime-spring.png" width="49%" alt="Windows 春樱动态播放器">
</p>

### iOS · SwiftUI 客户端

<p align="center">
  <img src="docs/screenshots/home.png" width="23%" alt="iOS 首页">
  <img src="docs/screenshots/player.png" width="23%" alt="iOS 播放器">
  <img src="docs/screenshots/lyrics.png" width="23%" alt="iOS 歌词">
  <img src="docs/screenshots/settings.png" width="23%" alt="iOS 设置">
</p>

Apple 截图来自 iOS 目标。Mac Catalyst 目标复用完整 Apple 客户端代码；仓库中的原生 BeansMac 目标则采用独立的桌面三栏布局。

## 平台与技术栈

| 目标 | 工程入口 | 技术栈 | 当前定位 |
| --- | --- | --- | --- |
| iOS 26 | Beans | Swift 5、SwiftUI、AVFoundation、MediaPlayer、GRDB、Argon2Swift、Keychain | 完整移动客户端 |
| macOS 15 原生 | BeansMac | Swift、SwiftUI、AVFoundation | 本地音乐库与桌面三栏播放器 |
| macOS 15 Catalyst | BeansCatalyst | SwiftUI、Mac Catalyst、GRDB、Argon2Swift | 复用完整 Apple 客户端能力 |
| Windows 11 | windows/Beans.Windows.Rebuild | C#、.NET 10、WinUI 3、Windows App SDK 2.5、MediaPlayer、WebView2、Credential Locker | 当前 Windows 主线客户端 |
| Beans 服务 | server/Beans.Api | ASP.NET Core 10、EF Core、PostgreSQL 17、Redis 7.4、JWT、Caddy、Docker Compose | 可选的账号与加密同步服务 |

Windows 当前主线是 **Beans.Windows.Rebuild**。仓库中的 **windows/Beans.Windows** 属于较早的 Windows 实现，保留用于历史兼容和迁移对照。

## 主要功能

### 音乐发现与资料库

- 聚合 QQ 音乐与网易云音乐的公开发现、榜单、搜索和歌单信息。
- 支持本地音乐目录扫描、元数据回退、同名 LRC 关联和缺失文件状态。
- 平台数据、缓存、授权状态和错误提示彼此隔离，不把预览数据伪装成在线数据。
- Beans 自建歌单支持本地持久化；第三方歌单按平台能力保持只读或受限操作。

### 播放体验

- 单播放器实例管理队列、播放进度、音量、随机、循环和系统媒体控制。
- 在线歌曲通过平台授权边界解析官方可播放地址；无权限或受限制内容不会伪装成成功。
- 在线与本地歌词、翻译行合并、逐行高亮和播放跳转。
- Windows 提供玻璃歌单、动态播放器、封面氛围、二次元入口和页面转场。
- iOS 提供播放器布局、主题、歌词样式、壁纸和氛围效果等深度自定义能力。

### 账号、安全与同步

- Beans 邮箱注册、验证、登录、令牌轮换、设备管理和扫码登录。
- Apple 平台凭证保存于 Keychain；Windows 平台凭证保存于 Credential Locker / DPAPI 边界。
- Argon2id / HKDF 密钥派生、AES-256-GCM 加密保险库，以及离线 outbox 和增量同步。
- 服务端不保存第三方平台明文 Cookie，不代理音乐接口或音频流。
- 不登录 Beans 账号时，QQ 音乐、网易云音乐和本地播放仍可按各端实现独立使用。

### 平台能力概览

| 能力 | iOS | macOS Catalyst | macOS 原生 | Windows |
| --- | :---: | :---: | :---: | :---: |
| QQ / 网易云发现与搜索 | ✓ | ✓ | — | ✓ |
| 本地音乐播放 | ✓ | ✓ | ✓ | ✓ |
| Beans 账号与加密同步 | ✓ | ✓ | — | ✓ |
| 深度主题与播放器自定义 | ✓ | ✓ | 基础 | ✓ |
| 动态二次元播放器 | — | — | — | ✓ |
| 桌面三栏布局 | — | 适配 | ✓ | ✓ |

## 快速开始

### 1. 获取源码

~~~bash
git clone https://github.com/lgcr12/Beans-Music-Three-Platform.git
cd Beans-Music-Three-Platform
~~~

### 2. iOS

要求：

- macOS
- Xcode 26 或包含目标 iOS SDK 的兼容版本
- XcodeGen

~~~bash
brew install xcodegen
xcodegen generate
open Beans.xcodeproj
~~~

命令行结构构建：

~~~bash
xcodebuild -project Beans.xcodeproj -scheme Beans -configuration Debug \
  -sdk iphonesimulator CODE_SIGNING_ALLOWED=NO build
~~~

iOS 目标 Bundle ID 为 **com.beans.app**。真机安装需要使用自己的开发者签名。

### 3. macOS

生成工程后，可以选择两个 Mac 目标。

原生 SwiftUI 桌面客户端：

~~~bash
xcodebuild -project Beans.xcodeproj -scheme BeansMac -configuration Debug build
~~~

完整 Apple 功能的 Mac Catalyst 客户端：

~~~bash
xcodebuild -project Beans.xcodeproj -scheme BeansCatalyst -configuration Debug \
  -destination 'generic/platform=macOS,variant=Mac Catalyst' build
~~~

### 4. Windows 11

要求：

- Windows 11 22H2 或更高版本
- .NET SDK 10.0.401 或兼容的 10.0 SDK
- Visual Studio 的 Windows 应用开发 / WinUI 工作负载；仅命令行构建时也可使用仓库已配置的 SDK

~~~powershell
Set-Location .\windows\Beans.Windows.Rebuild

dotnet restore '.\Beans.Windows.Rebuild.slnx'
dotnet build '.\Beans.Windows.Rebuild.slnx' -c Debug --no-restore
dotnet test '.\Tests\Beans.Windows.Rebuild.Tests.csproj' -c Debug --no-restore
& '.\Start-BeansMusic.cmd' --debug
~~~

Windows 客户端为非打包 WinUI 3 应用，首次构建会在项目内独立恢复 NuGet 依赖。

### 5. 可选的 Beans 账号服务

要求 Docker Desktop 或兼容的 Docker Compose 环境。

~~~bash
cp server/.env.example server/.env
docker compose --env-file server/.env -f server/compose.yaml up --build
~~~

开发环境默认包含：

- Beans API：容器内 8080，由 Caddy 暴露
- PostgreSQL：账号、设备和加密同步元数据
- Redis：缓存与一次性流程状态
- Mailpit：http://localhost:8025

请在 **server/.env** 中设置 PostgreSQL 密码和 JWT 签名密钥。不要把真实密钥、Cookie、证书或签名文件提交到仓库。

## 目录结构

~~~text
Beans/                              iOS 与 Mac Catalyst 共享客户端
BeansMac/                           独立原生 macOS 客户端
windows/Beans.Windows.Rebuild/      当前 Windows 11 WinUI 3 主线
windows/Beans.Core/                 Windows 共享业务与同步核心
server/Beans.Api/                   ASP.NET Core 账号服务
contracts/                          OpenAPI 与跨端契约
design/                             品牌与设计源文件
docs/                               架构、二改说明和截图
.github/workflows/                  三端构建与分平台发布流程
~~~

## 构建与发布约定

- iOS Release 标签：**ios-vX.Y.Z**
- macOS Release 标签：**mac-vX.Y.Z**
- Windows Release 标签：**windows-vX.Y.Z**
- 三个平台分别生成自己的安装产物，不进行可执行代码热更新。
- 主题、图片等非可执行资源可以独立演进，但必须保留来源与许可信息。

现有 Windows MSIX 发布工作流仍指向历史工程 **windows/Beans.Windows**；当前 **Beans.Windows.Rebuild** 是非打包开发主线，完成打包迁移前请按上面的命令直接构建和运行。

截至 2026-09-28，Windows 重构版的本地验证结果为 **467 项测试全部通过**，Release 构建为 **0 警告、0 错误**。

## 使用边界

- 本项目仅用于个人学习、技术研究和非商业交流。
- QQ 音乐、网易云音乐及其内容、商标和服务归对应权利人所有。
- 播放能力取决于用户账号权益、有效授权、地区限制和平台风控。
- 项目不提供音频存储服务，不应被用于绕过会员、版权或数字内容保护。
- 使用者应自行遵守所在地法律以及第三方平台的服务条款。

## License

本项目沿用 [MIT License](LICENSE)。原作者与二次开发者的版权声明以仓库历史、License 和第三方声明文件为准。
