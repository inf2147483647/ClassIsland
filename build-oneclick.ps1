<#
.SYNOPSIS
    一键编译 ClassIsland（Windows / Release），并产出可直接运行的 dist 目录。

.DESCRIPTION
    做的事只有四步：
      1. 检查编译前置条件（.NET SDK、vendors/EdgeTtsSharp 子模块）；
      2. dotnet build 编译 ClassIsland.Desktop（Release，会连带 Core / Platforms / Shared）；
      3. 按官方发行包的目录约定组装 dist：
             dist\PackageType          内容为 folder
             dist\app-<版本>-0\        主程序（ClassIsland.Desktop.exe 等）
             dist\data\                数据目录（首次运行会自行初始化）
             dist\ClassIsland.cmd      双击即启动
      4. 打印耗时与产物路径（加 -Run 则直接启动）。

.PARAMETER Clean
    先删掉 dist 并执行 dotnet clean，再完整重建（默认只做增量编译 + 覆盖拷贝）。

.PARAMETER Run
    编译完成后立即启动刚生成的实例。

.PARAMETER NoPause
    结束时不等待按键（供命令行 / 计划任务调用）。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER IncludeSymbols
    连 .pdb 一起拷进 dist（默认不拷，能省 300 MB 左右；需要看崩溃堆栈时再加）。

.EXAMPLE
    .\build-oneclick.ps1
    增量编译并组装 dist。

.EXAMPLE
    .\build-oneclick.ps1 -Clean -Run
    清空重建，完成后启动。
#>
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Run,
    [switch]$NoPause,
    [switch]$IncludeSymbols,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$startedAt = Get-Date
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $repo 'ClassIsland.Desktop\ClassIsland.Desktop.csproj'
$logPath = Join-Path $repo 'build-oneclick.log'
$dist = Join-Path $repo 'dist'

function Write-Step([string]$message)
{
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Write-Detail([string]$message)
{
    Write-Host "    $message" -ForegroundColor DarkGray
}

function Stop-WithError([string]$message, [string]$hint)
{
    Write-Host ''
    Write-Host "构建失败：$message" -ForegroundColor Red
    if ($hint) { Write-Host "提示：$hint" -ForegroundColor Yellow }
    if (Test-Path -LiteralPath $logPath)
    {
        Write-Host ''
        Write-Host "---- 构建日志最后 30 行（$logPath）----" -ForegroundColor Yellow
        Get-Content -LiteralPath $logPath -Tail 30 | ForEach-Object { Write-Host "    $_" }
    }

    if (-not $NoPause) { Write-Host ''; Read-Host '按 Enter 退出' | Out-Null }
    exit 1
}

try
{
    Write-Host '==============================================' -ForegroundColor White
    Write-Host ' ClassIsland 一键编译' -ForegroundColor White
    Write-Host " 仓库：$repo" -ForegroundColor DarkGray
    Write-Host " 配置：$Configuration    开始：$($startedAt.ToString('HH:mm:ss'))" -ForegroundColor DarkGray
    Write-Host '==============================================' -ForegroundColor White

    # ---------- 1. 前置条件 ----------
    Write-Step '检查编译环境'

    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
    if (-not $dotnet)
    {
        $fallback = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $fallback) { $dotnet = $fallback }
    }

    if (-not $dotnet)
    {
        Stop-WithError '找不到 .NET SDK（dotnet 命令）' '请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download'
    }

    $sdks = & $dotnet --list-sdks 2>&1
    Write-Detail "dotnet：$dotnet"
    Write-Detail "SDK：$(($sdks | Select-Object -Last 1))"

    if (-not (Test-Path -LiteralPath $project))
    {
        Stop-WithError "找不到工程文件：$project" '请把本脚本放在仓库根目录（与 ClassIsland.sln 同级）。'
    }

    # vendors/EdgeTtsSharp 是 git 子模块，未初始化时编译会在 Edge_tts_sharp / eVoice 处报类型找不到。
    $edgeTtsProject = Join-Path $repo 'vendors\EdgeTtsSharp\Edge_tts_sharp\Edge_tts_sharp.csproj'
    if (-not (Test-Path -LiteralPath $edgeTtsProject))
    {
        Write-Detail '子模块 vendors/EdgeTtsSharp 未初始化，尝试拉取…'
        & git -C $repo submodule update --init --recursive 2>&1 | ForEach-Object { Write-Detail $_ }
        if (-not (Test-Path -LiteralPath $edgeTtsProject))
        {
            Stop-WithError '子模块 vendors/EdgeTtsSharp 仍然是空的' '请在仓库根目录执行：git submodule update --init --recursive（需要网络 / 仓库权限）'
        }
    }

    Write-Detail '子模块 vendors/EdgeTtsSharp 就绪'

    # ---------- 2. 编译 ----------
    Write-Step "编译 ClassIsland.Desktop（$Configuration）"

    if ($Clean)
    {
        Write-Detail '清空 dist 与 obj/bin 输出…'
        if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
        & $dotnet clean $project -c $Configuration --nologo -v q 2>&1 | Out-Null
    }

    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }
    & $dotnet build $project -c $Configuration --nologo 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0)
    {
        Stop-WithError "dotnet build 返回 $LASTEXITCODE" '常见原因：有文件被正在运行的 ClassIsland 占用（先关掉），或还原失败（检查网络 / NuGet 源）。'
    }

    # ---------- 3. 组装 dist ----------
    Write-Step '组装 dist'

    $exe = Get-ChildItem -LiteralPath (Join-Path $repo "ClassIsland.Desktop\bin\$Configuration") -Recurse -Filter 'ClassIsland.Desktop.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe)
    {
        Stop-WithError "编译成功但找不到 ClassIsland.Desktop.exe" "预期路径：ClassIsland.Desktop\bin\$Configuration\<目标框架>\"
    }

    $outputDir = $exe.Directory.FullName
    $version = $exe.VersionInfo.FileVersion
    if (-not $version -or $version -notmatch '^\d') { $version = $exe.VersionInfo.ProductVersion }
    if (-not $version -or $version -notmatch '^\d') { $version = '0.0.0.0' }

    $appDirName = "app-$version-0"
    $appDir = Join-Path $dist $appDirName
    Write-Detail "版本：$version"
    Write-Detail "输出：$outputDir"
    Write-Detail "目标：$appDir"

    # 目标目录里的旧实例可能正占用文件；先提示，不强行结束用户进程。
    $running = Get-Process -Name 'ClassIsland*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) }
    if ($running)
    {
        Write-Host ''
        Write-Host "    注意：$($running.Count) 个正在运行的 ClassIsland 实例来自 dist，文件可能被占用。" -ForegroundColor Yellow
        Write-Host '          若下面拷贝失败，请先关闭这些实例再重跑本脚本。' -ForegroundColor Yellow
    }

    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $dist 'data') -Force | Out-Null

    # /E 连同空目录；/R:1 /W:1 避免文件被占用时长时间重试；不删目标端多余文件（避免误删运行时产物）。
    $roboArgs = @($outputDir, $appDir, '/E', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/XD', 'ref', 'refint')
    if (-not $IncludeSymbols)
    {
        # 调试符号对运行没有影响，默认不拷（能省 300 MB 左右）。
        $roboArgs += @('/XF', '*.pdb')
    }
    & robocopy @roboArgs | Out-Null
    $roboExit = $LASTEXITCODE
    # robocopy：0/1/2/3 都是成功，>=8 才是失败
    if ($roboExit -ge 8)
    {
        Stop-WithError "拷贝产物失败（robocopy 退出码 $roboExit）" '请关闭正在运行的 ClassIsland 实例后重试。'
    }

    # PackageType：让 ClassIsland 认得出「folder 发行包」，从而把数据目录定位到 dist\data
    Set-Content -LiteralPath (Join-Path $dist 'PackageType') -Value 'folder' -NoNewline -Encoding ascii

    # 双击即启动（等价于直接运行 app-<版本>-0\ClassIsland.Desktop.exe）
    $launchCmd = Join-Path $dist 'ClassIsland.cmd'
    @"
@echo off
start "" "%~dp0$appDirName\ClassIsland.Desktop.exe"
"@ | Set-Content -LiteralPath $launchCmd -Encoding ascii

    # ---------- 4. 汇总 ----------
    $elapsed = (Get-Date) - $startedAt
    $files = Get-ChildItem -LiteralPath $appDir -Recurse -File -ErrorAction SilentlyContinue
    $sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)

    Write-Step '完成'
    Write-Host "    版本         : $version" -ForegroundColor Green
    Write-Host "    主程序       : $appDir\ClassIsland.Desktop.exe" -ForegroundColor Green
    Write-Host "    启动脚本     : $launchCmd" -ForegroundColor Green
    Write-Host "    数据目录     : $(Join-Path $dist 'data')" -ForegroundColor Green
    Write-Host "    产物         : $($files.Count) 个文件，$sizeMb MB" -ForegroundColor Green
    Write-Host "    耗时         : $([int]$elapsed.TotalMinutes) 分 $($elapsed.Seconds) 秒" -ForegroundColor Green
    Write-Host "    构建日志     : $logPath" -ForegroundColor DarkGray
    if (-not $IncludeSymbols) { Write-Host '    符号文件     : 未拷贝（加 -IncludeSymbols 可保留 .pdb）' -ForegroundColor DarkGray }

    if ($Run)
    {
        Write-Step '启动'
        Start-Process -FilePath $exe.FullName -WorkingDirectory $appDir
        Write-Detail "已启动：$($exe.FullName)"
    }
    else
    {
        Write-Host ''
        Write-Host '    直接运行：' -ForegroundColor DarkGray
        Write-Host "      $launchCmd" -ForegroundColor DarkGray
    }
}
catch
{
    Stop-WithError $_.Exception.Message $_.ScriptStackTrace
}

if (-not $NoPause)
{
    Write-Host ''
    Read-Host '按 Enter 退出' | Out-Null
}
