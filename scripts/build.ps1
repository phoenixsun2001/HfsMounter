#Requires -Version 5.1
<#
.SYNOPSIS
    编译并发布 HfsMounter（框架依赖，非单文件，保证 WinFsp 原生库放在 exe 同目录）。
.EXAMPLE
    .\build.ps1
    .\build.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$srcDir = Split-Path -Parent $scriptDir
$project = Join-Path $srcDir 'HfsMounter\HfsMounter.csproj'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $srcDir) 'publish'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet 命令，请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download'
}
if (-not (Test-Path -LiteralPath $project)) {
    throw "找不到工程文件: $project"
}

Write-Host "发布 $project" -ForegroundColor Cyan
Write-Host "输出目录: $OutputDirectory"
Write-Host "自包含: $SelfContained"

dotnet publish $project `
    -c Release `
    -r win-x64 `
    -p:SelfContained=$SelfContained `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $OutputDirectory

if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $OutputDirectory 'HfsMounter.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "发布完成但没有找到 $exe" }

$total = (Get-ChildItem -LiteralPath $OutputDirectory -File | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host ("发布成功: {0}（{1:N1} MB，共 {2} 个文件）" -f $exe, ($total / 1MB), (Get-ChildItem -LiteralPath $OutputDirectory -File).Count) -ForegroundColor Green
Write-Host '下一步（管理员）：.\install-service.ps1'
