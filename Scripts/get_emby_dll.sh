#!/usr/bin/env bash
set -euo pipefail

NAS_HOST="root@192.168.33.100"
CONTAINER="emby"
LOCAL_DIR="$HOME/Downloads/emby-dlls"

mkdir -p "$LOCAL_DIR"

ssh "$NAS_HOST" "set -euo pipefail; \
tmp=\$(mktemp -d /tmp/emby-dlls.XXXXXX); \
docker cp ${CONTAINER}:/system/Emby.Providers.dll \$tmp/; \
docker cp ${CONTAINER}:/system/Emby.Server.Implementations.dll \$tmp/; \
docker cp ${CONTAINER}:/system/Emby.Sqlite.dll \$tmp/; \
docker cp ${CONTAINER}:/system/Emby.Server.MediaEncoding.dll \$tmp/; \
docker cp ${CONTAINER}:/system/Emby.ProcessRun.dll \$tmp/; \
docker cp ${CONTAINER}:/system/plugins/MovieDb.dll \$tmp/; \
docker cp ${CONTAINER}:/system/plugins/Tvdb.dll \$tmp/; \
docker cp ${CONTAINER}:/system/SQLitePCLRawEx.core.dll \$tmp/; \
tar -C \$tmp -cf - .; \
rm -rf \$tmp" | tar -C "$LOCAL_DIR" -xf -

echo "Done: $LOCAL_DIR"
ls -lh "$LOCAL_DIR"
