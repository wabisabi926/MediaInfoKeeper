#!/usr/bin/env bash
set -euo pipefail

remote="${REMOTE_SSH:-root@192.168.33.100}"
local_dll="${LOCAL_DLL:-/Users/honue/Documents/WorkSpace/MediaInfoKeeper/Build/bin/Debug/net8.0/MediaInfoKeeper.dll}"
remote_dir="${REMOTE_DIR:-/opt/emby/config/plugins}"
compose_dir="${COMPOSE_DIR:-/opt/emby}"

# 1) Copy DLL
scp "$local_dll" "${remote}:${remote_dir}/"

# 2) Restart Emby (Docker Compose)
ssh "$remote" "cd \"$compose_dir\" && docker compose restart"
