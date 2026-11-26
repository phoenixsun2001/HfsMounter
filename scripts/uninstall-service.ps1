#Requires -Version 5.1
<#
.SYNOPSIS
    卸载 HfsMounter 常驻服务（停止、删除服务，可选删除盘符联动任务与安装目录）。
.EXAMPLE
    .\uninstall-service.ps1
    .\uninstall-service.ps1 -RemoveFiles
#>
[CmdletBinding()]
param(
    [switch]$RemoveFiles,
    [switch]$KeepLinker
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '需要管理员权限。请右键“以管理员身份运行”。'
    }
}

Assert-Administrator

$scriptDir = Split-Path -Parent $PSCommandPath
$srcDir = Split-Path -Parent $scriptDir
$installDir = Join-Path $env:ProgramFiles 'HfsMounter'
$exe = Join-Path $installDir 'HfsMounter.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    $exe = Join-Path (Split-Path -Parent $srcDir) 'publish\HfsMounter.exe'
}

Write-Host '=== HfsMounter 服务卸载 ===' -ForegroundColor Cyan

if (Test-Path -LiteralPath $exe) {
    if (-not $KeepLinker) {
        Write-Host '删除盘符联动任务 ...'
        & $exe --linker-uninstall | Out-Null
    }
    Write-Host '停止并删除服务 ...'
    & $exe --uninstall-service
    if ($LASTEXITCODE -ne 0) { Write-Host '删除服务返回非 0，可能本就没安装。' -ForegroundColor Yellow }
} else {
    Write-Host "找不到 $exe，改用 sc.exe 直接删除服务。" -ForegroundColor Yellow
    Stop-Service -Name 'HfsMounter' -Force -ErrorAction SilentlyContinue
    & sc.exe delete HfsMounter | Out-Null
    & schtasks.exe /Delete /F /TN 'HfsMounter-DriveLetter' 2>$null | Out-Null
}

if ($RemoveFiles -and (Test-Path -LiteralPath $installDir)) {
    Write-Host "删除安装目录 $installDir ..."
    Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host '卸载完成。配置与日志保留在 %ProgramData%\HfsMounter（如需彻底清理请手动删除）。' -ForegroundColor Green
