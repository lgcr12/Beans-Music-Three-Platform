param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $ShortcutName = 'Beans Music.lnk'
)

$projectRoot = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $projectRoot 'Start-BeansMusic.cmd'
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop $ShortcutName

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Launcher not found: $launcher"
}

$configurationPath = Join-Path $projectRoot "bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Beans.Windows.Rebuild.exe"
$iconPath = if (Test-Path -LiteralPath $configurationPath) {
    $configurationPath
} else {
    Join-Path $projectRoot 'Assets\Branding\beans-icon.ico'
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $launcher
$shortcut.Arguments = if ($Configuration -eq 'Debug') { '--debug' } else { '' }
$shortcut.WorkingDirectory = $projectRoot
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Description = '启动 Beans Music Windows Rebuild'
$shortcut.Save()

Write-Output "Created shortcut: $shortcutPath"
