#Requires -Version 5.1
<#
.SYNOPSIS
    查看 HfsMounter 服务状态、检测到的 HFS+ 分区、挂载点和最近日志。
.EXAMPLE
    .\status.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$srcDir = Split-Path -Parent $scriptDir
$candidates = @(
    (Join-Path $env:ProgramFiles 'HfsMounter\HfsMounter.exe'),
    (Join-Path (Split-Path -Parent $srcDir) 'publish\HfsMounter.exe'),
    (Join-Path $srcDir 'HfsMounter\bin\Debug\net8.0\HfsMounter.exe'),
    (Join-Path $srcDir 'HfsMounter\bin\Release\net8.0\win-x64\HfsMounter.exe')
)

$exe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) {
    throw '找不到 HfsMounter.exe：请先运行 .\build.ps1 或 .\install-service.ps1。'
}

& $exe --status
exit $LASTEXITCODE
