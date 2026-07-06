param(
    [string]$Version = "dev",
    [string[]]$Runtime = @("win-x64", "linux-x64", "osx-x64", "osx-arm64"),
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts/release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectFile = Join-Path $projectRoot "ai-proxy-hub.csproj"

if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot $OutputRoot
}

$versionRoot = Join-Path $OutputRoot $Version
New-Item -ItemType Directory -Force -Path $versionRoot | Out-Null

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

if (-not $SkipTests) {
    Invoke-Checked "dotnet" @("test", (Join-Path $projectRoot "tests/ProxyTests/ProxyTests.csproj"), "-c", $Configuration)
}

foreach ($rid in $Runtime) {
    $publishDir = Join-Path $versionRoot "publish-$rid"
    $packageDir = Join-Path $versionRoot "copilot-ai-proxy-$Version-$rid"
    $archivePath = Join-Path $versionRoot "copilot-ai-proxy-$Version-$rid.zip"

    if (Test-Path $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
    if (Test-Path $packageDir) {
        Remove-Item -LiteralPath $packageDir -Recurse -Force
    }
    if (Test-Path $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Invoke-Checked "dotnet" @(
        "publish", $projectFile,
        "-c", $Configuration,
        "-r", $rid,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-o", $publishDir
    )

    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

    Copy-Item -Path (Join-Path $publishDir "*") -Destination $packageDir -Recurse -Force
    foreach ($publishedExtra in @("docs", "tests")) {
        $extraPath = Join-Path $packageDir $publishedExtra
        if (Test-Path $extraPath) {
            Remove-Item -LiteralPath $extraPath -Recurse -Force
        }
    }

    Copy-Item -LiteralPath (Join-Path $projectRoot ".env.example") -Destination $packageDir -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $packageDir -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot "README.zh-CN.md") -Destination $packageDir -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot "config") -Destination $packageDir -Recurse -Force

    if ($rid.StartsWith("win")) {
        $startScript = @"
@echo off
setlocal
cd /d "%~dp0"
if not exist ".env" (
  copy ".env.example" ".env" >nul
  echo Created .env from .env.example.
  echo Edit .env with your provider API keys, then run start-windows.cmd again.
  pause
  exit /b 1
)
ai-proxy-hub.exe
"@
        Set-Content -LiteralPath (Join-Path $packageDir "start-windows.cmd") -Value $startScript -Encoding ASCII
    }
    else {
        $startScript = @'
#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"
if [ ! -f ".env" ]; then
  cp ".env.example" ".env"
  echo "Created .env from .env.example."
  echo "Edit .env with your provider API keys, then run ./start-unix.sh again."
  exit 1
fi
chmod +x ./ai-proxy-hub 2>/dev/null || true
exec ./ai-proxy-hub
'@
        Set-Content -LiteralPath (Join-Path $packageDir "start-unix.sh") -Value $startScript -Encoding UTF8
    }

    Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $archivePath -Force

    Remove-Item -LiteralPath $publishDir -Recurse -Force
    Remove-Item -LiteralPath $packageDir -Recurse -Force

    Write-Host "Created $archivePath"
}
