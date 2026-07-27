# sebastian-ci を Release 構成でビルドする
# 使い方: .\scripts\build.ps1 [-Configuration Debug|Release]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\SebastianCi\SebastianCi.csproj"

dotnet build $project --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Error "ビルドに失敗しました (exit code: $LASTEXITCODE)"
    exit $LASTEXITCODE
}

$exe = Join-Path $repoRoot "src\SebastianCi\bin\$Configuration\net8.0\sebastian-ci.exe"
Write-Host ""
Write-Host "✅ ビルド完了: $exe"
