#!/usr/bin/env bash
# sebastian-ci を Release 構成でビルドする
# 使い方: ./scripts/build.sh [Debug|Release]
set -euo pipefail

CONFIGURATION="${1:-Release}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/SebastianCi/SebastianCi.csproj"

dotnet build "$PROJECT" --configuration "$CONFIGURATION"

echo ""
echo "✅ ビルド完了: $REPO_ROOT/src/SebastianCi/bin/$CONFIGURATION/net8.0/sebastian-ci.dll"
