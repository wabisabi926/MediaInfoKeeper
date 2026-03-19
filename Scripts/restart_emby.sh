#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(cd "$script_dir/.." && pwd)"

remote="${REMOTE_SSH:-root@192.168.33.100}"
local_dll="${LOCAL_DLL:-$project_dir/Build/bin/Debug/net8.0/MediaInfoKeeper.dll}"
remote_dir="${REMOTE_DIR:-/opt/emby/config/plugins}"
compose_dir="${COMPOSE_DIR:-/opt/emby}"

# 0) Build
dotnet build "$project_dir/MediaInfoKeeper.csproj" -c Debug

# 1) Copy DLL
scp "$local_dll" "${remote}:${remote_dir}/"

# 2) Restart Emby (Docker Compose)
ssh "$remote" "cd \"$compose_dir\" && docker compose restart"
