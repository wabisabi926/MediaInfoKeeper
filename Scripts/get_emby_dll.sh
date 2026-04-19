#!/usr/bin/env bash
set -euo pipefail

NAS_HOST="${NAS_HOST:-root@192.168.33.100}"
CONTAINER="${CONTAINER:-emby}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION_READER_SCRIPT="${VERSION_READER_SCRIPT:-${SCRIPT_DIR}/read_assembly_version.py}"
LOCAL_DIR="${LOCAL_DIR:-/Users/honue/Documents/Emby/dlls}"
EMBY_VERSION="${EMBY_VERSION:-}"
OVERWRITE=0

DEFAULT_DLLS=(
  "/system/Emby.Api.dll"
  "/system/Emby.Naming.dll"
  "/system/Emby.Notifications.dll"
  "/system/Emby.ProcessRun.dll"
  "/system/Emby.Providers.dll"
  "/system/Emby.Server.Implementations.dll"
  "/system/Emby.Server.MediaEncoding.dll"
  "/system/Emby.Sqlite.dll"
  "/system/Emby.Web.GenericUI.dll"
  "/system/Emby.Web.dll"
  "/system/MediaBrowser.Controller.dll"
  "/system/MediaBrowser.Model.dll"
  "/system/plugins/MovieDb.dll"
  "/system/SQLitePCLRawEx.core.dll"
  "/system/plugins/Tvdb.dll"
)

print_usage() {
  cat <<EOF
Usage:
  ./get_emby_dll.sh
  ./get_emby_dll.sh Emby.Providers.dll MovieDb.dll
  ./get_emby_dll.sh /system/Emby.Providers.dll /system/plugins/MovieDb.dll
  ./get_emby_dll.sh --host root@192.168.33.100 --container emby --local-dir "/Users/honue/Documents/Emby/dlls" Emby.Sqlite.dll
  ./get_emby_dll.sh --emby-version 4.9.3.0 Emby.Sqlite.dll
  ./get_emby_dll.sh --overwrite Emby.Providers.dll
  ./get_emby_dll.sh --list

Options:
  --host <ssh-target>       SSH target, default: ${NAS_HOST}
  --container <name>        Docker container name, default: ${CONTAINER}
  --local-dir <path>        Local DLL root directory. Files are stored under:
                            <local-dir>/<emby-version>/
                            Default: ${LOCAL_DIR}
  --emby-version <version>  Override detected Emby version for the target folder name
  --overwrite               Overwrite existing local files
  --list                    Print built-in DLL paths and exit
  -h, --help                Show this help

Notes:
  - Without DLL arguments, the script downloads the built-in default DLL list.
  - Existing local files are skipped by default. Use --overwrite to replace them.
  - The script detects Emby version from /system/EmbyServer.dll by default.
  - Decompiled source should go under:
    <local-dir>/<emby-version>/source
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

run_version_reader() {
  local dll_path="$1"

  python3 "${VERSION_READER_SCRIPT}" "${dll_path}"
}

detect_remote_emby_version() {
  local tmp_dir
  local tmp_dll

  tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/emby-version.XXXXXX")"
  tmp_dll="${tmp_dir}/EmbyServer.dll"
  trap 'rm -rf "${tmp_dir}"' RETURN

  ssh "${NAS_HOST}" "docker exec '${CONTAINER}' cat /system/EmbyServer.dll" > "${tmp_dll}"
  run_version_reader "${tmp_dll}"
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
    --emby-version)
      EMBY_VERSION="${2:?missing value for --emby-version}"
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

if [ -z "${EMBY_VERSION}" ]; then
  EMBY_VERSION="$(detect_remote_emby_version)"
fi

TARGET_DIR="${LOCAL_DIR}/${EMBY_VERSION}"
mkdir -p "${TARGET_DIR}"

DLLS_TO_DOWNLOAD=()
SKIPPED_LOCAL=()

for dll_path in "${DLLS[@]}"; do
  local_path="${TARGET_DIR}/$(basename "$dll_path")"
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

echo "Detected Emby version: ${EMBY_VERSION}"
echo "Downloading ${#DLLS_TO_DOWNLOAD[@]} file(s) from ${NAS_HOST}:${CONTAINER} -> ${TARGET_DIR}"
printf '  %s\n' "${DLLS_TO_DOWNLOAD[@]}"

if [ "${#SKIPPED_LOCAL[@]}" -gt 0 ]; then
  echo "Skipping ${#SKIPPED_LOCAL[@]} existing local file(s):"
  printf '  %s\n' "${SKIPPED_LOCAL[@]}"
fi

ssh "$NAS_HOST" "$REMOTE_SCRIPT" | tar -C "$TARGET_DIR" -xf -

mkdir -p "${TARGET_DIR}/source"

echo "Done: ${TARGET_DIR}"
ls -lh "${TARGET_DIR}"
