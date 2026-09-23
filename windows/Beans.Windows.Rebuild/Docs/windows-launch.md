# Windows 快速启动

## 直接启动

先构建一次项目，然后双击：

```text
windows/Beans.Windows.Rebuild/Start-BeansMusic.cmd
```

启动脚本优先使用 Release 构建；Release 不存在时自动回退到 Debug 构建。需要明确启动 Debug 时，可以执行：

```powershell
& '.\Start-BeansMusic.cmd' --debug
```

## 创建桌面快捷方式

在项目目录执行：

```powershell
& '.\scripts\New-BeansMusicShortcut.ps1'
```

脚本会在当前 Windows 桌面创建 `Beans Music.lnk`，默认指向 Release。创建 Debug 快捷方式：

```powershell
& '.\scripts\New-BeansMusicShortcut.ps1' -Configuration Debug -ShortcutName 'Beans Music Debug.lnk'
```

快捷方式只调用仓库内的启动脚本，不会写入开机自启动，也不会修改 Windows 系统设置。

## 构建、测试和启动

```powershell
$dotnet = 'D:\Apps\DotNetSDK\dotnet.exe'
& $dotnet build '.\Beans.Windows.Rebuild.slnx' -c Debug --no-restore
& $dotnet test '.\Tests\Beans.Windows.Rebuild.Tests.csproj' -c Debug --no-restore
& $dotnet build '.\Beans.Windows.Rebuild.slnx' -c Release --no-restore
& '.\Start-BeansMusic.cmd'
```

以上命令需要从 `windows/Beans.Windows.Rebuild` 目录执行。应用数据和缓存仍由程序写入用户本地应用数据目录，不会写入仓库。

## Beans 账号注册前置条件

Beans 注册、邮箱验证、登录和同步需要账号服务实际运行。默认地址是
`http://localhost:8080/`；桌面客户端不会在服务不可达时伪造注册成功。

从仓库根目录启动本地服务（需要 Docker Desktop）：

```powershell
Set-Location '.\\server'
Copy-Item '.env.example' '.env' -Force
& docker compose up --build
```

服务启动后再回到账号页注册。若不运行服务，页面会显示“无法连接 Beans 服务”；
QQ 音乐和网易云音乐的官方登录仍可独立使用。

## QQ 推荐歌单网络边界

QQ 的匿名 `GetRecommendPlaylist` 接口在部分地区会返回业务码
`500003/860100001`。Windows 客户端会自动切换到 QQ 公开歌单接口，仍只展示真实
网络数据；两个接口都失败时保留明确的失败/缓存状态，不生成假歌单。

## 当前推送边界

本次更新只包含 `windows/Beans.Windows.Rebuild`。旧的 `windows/Beans.Windows`、`Beans.Core`、Apple 客户端和服务端文件不在本次提交范围内。
