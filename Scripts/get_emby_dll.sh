#!/usr/bin/env bash
set -euo pipefail

NAS_HOST="${NAS_HOST:-root@192.168.33.100}"
CONTAINER="${CONTAINER:-emby}"
LOCAL_DIR="${LOCAL_DIR:-/Users/honue/Documents/Emby/dlls}"
OVERWRITE=0

DEFAULT_DLLS=(
  "/system/Emby.Providers.dll"
  "/system/Emby.Server.Implementations.dll"
  "/system/Emby.Sqlite.dll"
  "/system/Emby.Server.MediaEncoding.dll"
  "/system/Emby.ProcessRun.dll"
  "/system/plugins/MovieDb.dll"
  "/system/plugins/Tvdb.dll"
  "/system/SQLitePCLRawEx.core.dll"
)

print_usage() {
  cat <<EOF
Usage:
  ./get_emby_dll.sh
  ./get_emby_dll.sh Emby.Providers.dll MovieDb.dll
  ./get_emby_dll.sh /system/Emby.Providers.dll /system/plugins/MovieDb.dll
  ./get_emby_dll.sh --host root@192.168.33.100 --container emby --local-dir "/Users/honue/Documents/Emby/dlls" Emby.Sqlite.dll
  ./get_emby_dll.sh --overwrite Emby.Providers.dll
  ./get_emby_dll.sh --list

Options:
  --host <ssh-target>       SSH target, default: ${NAS_HOST}
  --container <name>        Docker container name, default: ${CONTAINER}
  --local-dir <path>        Local output directory, default: ${LOCAL_DIR}
  --overwrite               Overwrite existing local files
  --list                    Print built-in DLL paths and exit
  -h, --help                Show this help

Notes:
  - Without DLL arguments, the script downloads the built-in default DLL list.
  - Existing local files are skipped by default. Use --overwrite to replace them.
  - This directory should contain DLL files only. Decompiled source goes under:
    /Users/honue/Documents/Emby/dlls/source
  - DLL arguments can be a full container path, a bare file name, or a plugin file name.
  - Bare names ending in .dll default to /system/<name>, except MovieDb.dll and Tvdb.dll which default to /system/plugins/.
EOF
}

normalize_remote_path() {
  local value="$1"

  case "$value" in
    /*)
      printf '%s\n' "$value"
      return
      ;;
    MovieDb.dll|Tvdb.dll)
      printf '/system/plugins/%s\n' "$value"
      return
      ;;
    *.dll)
      printf '/system/%s\n' "$value"
      return
      ;;
    *)
      printf '/system/%s.dll\n' "$value"
      return
      ;;
  esac
}

print_default_dlls() {
  local dll
  for dll in "${DEFAULT_DLLS[@]}"; do
    printf '%s\n' "$dll"
  done
}

DLLS=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --host)
      NAS_HOST="${2:?missing value for --host}"
      shift 2
      ;;
    --container)
      CONTAINER="${2:?missing value for --container}"
      shift 2
      ;;
    --local-dir)
      LOCAL_DIR="${2:?missing value for --local-dir}"
      shift 2
      ;;
    --overwrite)
      OVERWRITE=1
      shift
      ;;
    --list)
      print_default_dlls
      exit 0
      ;;
    -h|--help)
      print_usage
      exit 0
      ;;
    --)
      shift
      while [ "$#" -gt 0 ]; do
        DLLS+=("$(normalize_remote_path "$1")")
        shift
      done
      break
      ;;
    -*)
      echo "Unknown option: $1" >&2
      print_usage >&2
      exit 1
      ;;
    *)
      DLLS+=("$(normalize_remote_path "$1")")
      shift
      ;;
  esac
done

if [ "${#DLLS[@]}" -eq 0 ]; then
  DLLS=("${DEFAULT_DLLS[@]}")
fi

mkdir -p "$LOCAL_DIR"

DLLS_TO_DOWNLOAD=()
SKIPPED_LOCAL=()

for dll_path in "${DLLS[@]}"; do
  local_path="${LOCAL_DIR}/$(basename "$dll_path")"
  if [ "$OVERWRITE" -eq 0 ] && [ -e "$local_path" ]; then
    SKIPPED_LOCAL+=("$local_path")
    continue
  fi

  DLLS_TO_DOWNLOAD+=("$dll_path")
done

if [ "${#DLLS_TO_DOWNLOAD[@]}" -eq 0 ]; then
  echo "No files to download. ${#SKIPPED_LOCAL[@]} file(s) already exist locally."
  printf '  skip %s\n' "${SKIPPED_LOCAL[@]}"
  exit 0
fi

REMOTE_SCRIPT_LINES=(
  "set -euo pipefail"
  "tmp=\$(mktemp -d /tmp/emby-dlls.XXXXXX)"
  "trap 'rm -rf \"\$tmp\"' EXIT"
)

for dll_path in "${DLLS_TO_DOWNLOAD[@]}"; do
  REMOTE_SCRIPT_LINES+=("docker cp '${CONTAINER}:${dll_path}' \"\$tmp/\"")
done

REMOTE_SCRIPT_LINES+=("tar -C \"\$tmp\" -cf - .")
REMOTE_SCRIPT="$(printf '%s\n' "${REMOTE_SCRIPT_LINES[@]}")"

echo "Downloading ${#DLLS_TO_DOWNLOAD[@]} file(s) from ${NAS_HOST}:${CONTAINER} -> ${LOCAL_DIR}"
printf '  %s\n' "${DLLS_TO_DOWNLOAD[@]}"

if [ "${#SKIPPED_LOCAL[@]}" -gt 0 ]; then
  echo "Skipping ${#SKIPPED_LOCAL[@]} existing local file(s):"
  printf '  %s\n' "${SKIPPED_LOCAL[@]}"
fi

ssh "$NAS_HOST" "$REMOTE_SCRIPT" | tar -C "$LOCAL_DIR" -xf -

echo "Done: $LOCAL_DIR"
ls -lh "$LOCAL_DIR"
