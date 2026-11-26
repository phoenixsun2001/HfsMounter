#Requires -Version 5.1
<#
.SYNOPSIS
    安装 HfsMounter 常驻服务：拷贝程序到 Program Files、注册开机自启服务、启动并验证。
.DESCRIPTION
    需要管理员权限。会自动检查 WinFsp 运行时；缺少时尝试用 winget 安装（可加 -NoWinget 跳过）。
.PARAMETER LinkerLetter
    可选。安装完成后，为当前用户额外创建一个登录自启任务，把挂载目录映射成该盘符（等效 subst）。
.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -LinkerLetter X:
#>
[CmdletBinding()]
param(
    [string]$LinkerLetter,
    [string]$DataDirectory,
    [switch]$NoWinget,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '需要管理员权限。请右键“以管理员身份运行”，或用管理员 PowerShell 执行本脚本。'
    }
}

function Get-WinFspStatus {
    $system32 = Join-Path $env:SystemRoot 'System32'
    $loader = Join-Path $system32 'winfsp-x64.dll'
    $driver = Join-Path $system32 'drivers\winfsp.sys'
    [pscustomobject]@{
        Loader = Test-Path -LiteralPath $loader
        Driver = Test-Path -LiteralPath $driver
        Installed = (Test-Path -LiteralPath $loader) -and (Test-Path -LiteralPath $driver)
    }
}

Assert-Administrator

$scriptDir = Split-Path -Parent $PSCommandPath
$srcDir = Split-Path -Parent $scriptDir
$publishDir = Join-Path (Split-Path -Parent $srcDir) 'publish'
$installDir = Join-Path $env:ProgramFiles 'HfsMounter'
$exeInPublish = Join-Path $publishDir 'HfsMounter.exe'

Write-Host '=== HfsMounter 服务安装 ===' -ForegroundColor Cyan

# ---------------------------------------------------------------- 1. WinFsp
$winfsp = Get-WinFspStatus
if (-not $winfsp.Installed) {
    Write-Host '未检测到 WinFsp 运行时（winfsp-x64.dll / winfsp.sys）。' -ForegroundColor Yellow
    if (-not $NoWinget -and (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Host '尝试通过 winget 安装 WinFsp（会出现 UAC 或安装向导）...' -ForegroundColor Yellow
        winget install --id WinFsp.WinFsp -e --accept-package-agreements --accept-source-agreements
        $winfsp = Get-WinFspStatus
    }
    if (-not $winfsp.Installed) {
        Write-Host ''
        Write-Host '请手动安装 WinFsp 2.1 或更高版本，然后重新运行本脚本：' -ForegroundColor Red
        Write-Host '    winget install WinFsp.WinFsp'
        Write-Host '    https://winfsp.dev/rel/'
        throw 'WinFsp 未安装，无法挂载。'
    }
    Write-Host 'WinFsp 安装完成。' -ForegroundColor Green
} else {
    Write-Host 'WinFsp 运行时已就绪。' -ForegroundColor Green
}

# ---------------------------------------------------------------- 2. 编译
if (-not (Test-Path -LiteralPath $exeInPublish)) {
    if ($SkipBuild) { throw "找不到 $exeInPublish，且指定了 -SkipBuild。" }
    Write-Host '尚未发布过，先执行 build.ps1 ...' -ForegroundColor Cyan
    & (Join-Path $scriptDir 'build.ps1')
}
if (-not (Test-Path -LiteralPath $exeInPublish)) { throw "找不到 $exeInPublish" }

# ---------------------------------------------------------------- 3. 拷贝
Write-Host "拷贝程序到 $installDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

# 正在运行的服务会占用 exe，先停掉再覆盖
$existing = Get-Service -Name 'HfsMounter' -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host '停止正在运行的旧服务 ...'
    Stop-Service -Name 'HfsMounter' -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

Get-ChildItem -LiteralPath $publishDir -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $installDir -Force
}

$exe = Join-Path $installDir 'HfsMounter.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "拷贝后找不到 $exe" }
Write-Host "程序位置: $exe" -ForegroundColor Green

# ---------------------------------------------------------------- 4. 注册服务
$installArgs = @('--install-service')
if ($DataDirectory) { $installArgs = @('--data-dir', $DataDirectory) + $installArgs }

Write-Host '注册 Windows 服务（自动启动、延迟启动、失败自动重启）...' -ForegroundColor Cyan
& $exe @installArgs
if ($LASTEXITCODE -ne 0) { throw "注册服务失败，退出码 $LASTEXITCODE" }

Write-Host '启动服务 ...' -ForegroundColor Cyan
& $exe --start
if ($LASTEXITCODE -ne 0) { Write-Host '首次启动返回非 0，继续检查状态。' -ForegroundColor Yellow }

Start-Sleep -Seconds 2

# ---------------------------------------------------------------- 5. 盘符联动（可选）
if ($LinkerLetter) {
    if (-not $DataDirectory) { $DataDirectory = Join-Path $env:ProgramData 'HfsMounter' }
    $configPath = Join-Path $DataDirectory 'config.json'
    $mountRoot = 'C:\MacDisk'
    if (Test-Path -LiteralPath $configPath) {
        try { $mountRoot = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).mountRoot } catch { }
    }
    if (-not $mountRoot) { $mountRoot = 'C:\MacDisk' }
    Write-Host "创建登录自启的盘符映射任务: $LinkerLetter -> $mountRoot" -ForegroundColor Cyan
    & $exe --linker-install $LinkerLetter $mountRoot
    if ($LASTEXITCODE -ne 0) { Write-Host '盘符映射任务创建失败（不影响目录挂载）。' -ForegroundColor Yellow }
}

# ---------------------------------------------------------------- 6. 报告
Write-Host ''
& $exe --status
Write-Host ''
Write-Host '安装完成。插上 Mac 磁盘后会自动挂载到配置里的挂载根目录，无需再手动运行任何脚本。' -ForegroundColor Green
Write-Host '卸载：.\uninstall-service.ps1' -ForegroundColor DarkGray
