# sebastian-ci をビルドして起動する
# 使い方:
#   .\scripts\run.ps1                          # カレントディレクトリを対象に実行
#   .\scripts\run.ps1 C:\path\to\repo          # 指定リポジトリを対象に実行
#   .\scripts\run.ps1 . --validate             # 構成の検証のみ
#   .\scripts\run.ps1 serve . --port 8080      # ダッシュボードを起動
# 引数はすべて sebastian-ci にそのまま渡されます。

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\SebastianCi\SebastianCi.csproj"
$exe = Join-Path $repoRoot "src\SebastianCi\bin\Release\net8.0\sebastian-ci.exe"

# ビルド成果物がない、またはソースの方が新しい場合はビルドする
$needsBuild = -not (Test-Path $exe)
if (-not $needsBuild) {
    $exeTime = (Get-Item $exe).LastWriteTimeUtc
    $newer = Get-ChildItem (Join-Path $repoRoot "src\SebastianCi") -Recurse -Include *.cs, *.csproj |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $exeTime }
    $needsBuild = $null -ne $newer
}

if ($needsBuild) {
    Write-Host "🔨 ビルドしています..."
    dotnet build $project --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ビルドに失敗しました (exit code: $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

& $exe @args
exit $LASTEXITCODE
