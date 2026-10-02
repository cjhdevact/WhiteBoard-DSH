#requires -Version 7.0
<#
.SYNOPSIS
    构建并验证「互动白板」示例工程。

.DESCRIPTION
    依次执行：还原 → 编译主程序 → 编译冒烟测试 → 运行两套测试。
    脚本直接针对 .csproj 构建（而不是 .sln），因为在部分机器上
    `dotnet build xxx.sln` 会受到 .NET 工作负载清单缺失的影响而失败，
    逐项目构建不受该问题影响。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER SkipTests
    只编译，不运行测试。

.PARAMETER CaptureDir
    指定后额外运行界面截图模式，把主界面 / 批注层截图输出到该目录。

.EXAMPLE
    pwsh ./build.ps1
    pwsh ./build.ps1 -Configuration Debug -CaptureDir ./captures
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipTests,

    [string]$CaptureDir
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'WhiteBoardApp\WhiteBoard.csproj'
$tests = Join-Path $root 'WhiteBoard.Tests\WhiteBoard.SmokeTests.csproj'
$inputTests = Join-Path $root 'WhiteBoard.InputTests\WhiteBoard.InputTests.csproj'

# 在受限环境（例如沙箱）中，dotnet CLI 需要一个可写的主目录
$dotnetHome = Join-Path $root 'WhiteBoardApp\.dotnet-home'
if (-not (Test-Path $dotnetHome)) { New-Item -ItemType Directory -Path $dotnetHome -Force | Out-Null }
$env:DOTNET_CLI_HOME = $dotnetHome
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'

function Invoke-Step {
    param([string]$Title, [scriptblock]$Action)

    Write-Host ''
    Write-Host "── $Title ──" -ForegroundColor Cyan
    & $Action

    if ($LASTEXITCODE -ne 0) {
        throw "$Title 失败（退出码 $LASTEXITCODE）"
    }
}

Write-Host "互动白板 构建脚本  配置=$Configuration" -ForegroundColor Green
Write-Host "SDK: $(dotnet --version)"

Invoke-Step '编译主程序' {
    dotnet build $app -c $Configuration --nologo
}

if (Test-Path $tests) {
    Invoke-Step '编译冒烟测试' {
        dotnet build $tests -c $Configuration --nologo
    }
}

if (Test-Path $inputTests) {
    Invoke-Step '编译交互回归测试' {
        dotnet build $inputTests -c $Configuration --nologo
    }
}

if (-not $SkipTests) {
    Invoke-Step '应用内自检（XAML / 界面 / 存盘）' {
        $exe = Join-Path $root "WhiteBoardApp\bin\$Configuration\net8.0-windows\WhiteBoard.exe"
        & $exe --smoke-test

        if ($LASTEXITCODE -ne 0) {
            throw "应用内自检失败（退出码 $LASTEXITCODE）"
        }
    }

    if (Test-Path $inputTests) {
        Invoke-Step '交互回归测试（拖动 / 橡皮 / 底纹性能）' {
            dotnet run --project $inputTests -c $Configuration --no-build
        }
    }

    if (Test-Path $tests) {
        Invoke-Step '外部冒烟测试（算法 / 渲染 / 序列化）' {
            $artifacts = Join-Path $root 'WhiteBoard.Tests\artifacts'
            dotnet run --project $tests -c $Configuration --no-build -- $artifacts
            Write-Host "渲染样张已输出到 $artifacts" -ForegroundColor DarkGray
        }
    }
}

if ($CaptureDir) {
    Invoke-Step '界面截图' {
        $exe = Join-Path $root "WhiteBoardApp\bin\$Configuration\net8.0-windows\WhiteBoard.exe"
        & $exe --capture (Join-Path $root $CaptureDir)
    }
}

Write-Host ''
Write-Host '全部完成 ✔' -ForegroundColor Green
