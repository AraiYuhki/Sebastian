#!/usr/bin/env bash
# sebastian-ci をビルドして起動する
# 使い方:
#   ./scripts/run.sh                          # カレントディレクトリを対象に実行
#   ./scripts/run.sh /path/to/repo            # 指定リポジトリを対象に実行
#   ./scripts/run.sh . --validate             # 構成の検証のみ
#   ./scripts/run.sh serve . --port 8080      # ダッシュボードを起動
# 引数はすべて sebastian-ci にそのまま渡されます。
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/SebastianCi/SebastianCi.csproj"
DLL="$REPO_ROOT/src/SebastianCi/bin/Release/net8.0/sebastian-ci.dll"

# ビルド成果物がない、またはソースの方が新しい場合はビルドする
needs_build=false
if [[ ! -f "$DLL" ]]; then
    needs_build=true
elif find "$REPO_ROOT/src/SebastianCi" \( -name bin -o -name obj \) -prune -o \
        \( -name '*.cs' -o -name '*.csproj' \) -newer "$DLL" -print | grep -q .; then
    needs_build=true
fi

if $needs_build; then
    echo "🔨 ビルドしています..."
    dotnet build "$PROJECT" --configuration Release
fi

exec dotnet "$DLL" "$@"
