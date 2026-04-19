#!/usr/bin/env bash

if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION_READER_SCRIPT="${VERSION_READER_SCRIPT:-${SCRIPT_DIR}/read_assembly_version.py}"
EMBY_DOCS_DIR="${EMBY_DOCS_DIR:-emby_4.10.0.10}"
DLL_ROOT="${DLL_ROOT:-/Users/honue/Documents/Emby/dlls}"
DLL_DIR="${DLL_DIR:-}"
EMBY_VERSION="${EMBY_VERSION:-}"
VERSION_LABEL="${VERSION_LABEL:-}"
SOURCE_ROOT="${SOURCE_ROOT:-}"

OVERWRITE=0
CLEAN=0
DLL_FILTERS=()

print_usage() {
  cat <<EOF
Usage:
  ./decompile_emby_dlls.sh
  ./decompile_emby_dlls.sh Emby.Providers.dll MediaBrowser.Controller.dll
  ./decompile_emby_dlls.sh --overwrite
  ./decompile_emby_dlls.sh --dll-root /Users/honue/Documents/Emby/dlls
  ./decompile_emby_dlls.sh --emby-version 4.9.3.0
  ./decompile_emby_dlls.sh --dll-dir /Users/honue/Documents/Emby/dlls/4.9.3.0 --source-root /Users/honue/Documents/Emby/dlls/4.9.3.0/source
  EMBY_DOCS_DIR=emby_4.9.3.0 ./decompile_emby_dlls.sh

Options:
  --dll-root <path>        Root directory that contains versioned Emby DLL folders.
                           Default: ${DLL_ROOT}
  --dll-dir <path>         Directory containing downloaded Emby DLL files.
                           Default: <dll-root>/<emby-version>
  --emby-version <text>    Use this Emby version folder name directly.
  --source-root <path>     Root output directory for decompiled source folders.
                           Default: <dll-dir>/source
  --version-label <text>   Folder suffix for each decompiled project.
                           Default: <emby-version>
  --overwrite              Re-run decompilation even if output already exists.
  --clean                  Remove the source root before exporting.
  --help, -h               Show this help.

Arguments:
  <dll-name>               Optional DLL file names to decompile.
                           Without arguments, all top-level *.dll files in each target DLL directory are exported.

Environment:
  EMBY_DOCS_DIR            Version tag used to derive the default folder suffix.
                           Default: emby_4.9.3.0
  EMBY_VERSION             Same as --emby-version.
  VERSION_LABEL            Same as --version-label. Default strips prefix from EMBY_DOCS_DIR.
  ILSPYCMD                 Override the ilspycmd executable path.
  DLL_ROOT                 Same as --dll-root.
  DLL_DIR                  Same as --dll-dir.
  SOURCE_ROOT              Same as --source-root.

Notes:
  - Without --emby-version or --dll-dir, the script scans all versioned directories under <dll-root>.
  - DLL files stay under <dll-root>/<emby-version>
  - Decompiled sources go to <dll-root>/<emby-version>/source/<AssemblyName>_<VersionLabel>
  - If a same-name source folder already exists next to the DLL, it is moved to:
    <dll-root>/<emby-version>/source/<AssemblyName>_<VersionLabel>
EOF
}

fail() {
  echo "Error: $*" >&2
  exit 1
}

configure_dotnet_runtime() {
  if [ -n "${DOTNET_ROOT:-}" ] && [ -x "${DOTNET_ROOT}/dotnet" ]; then
    case ":${PATH}:" in
      *":${DOTNET_ROOT}:"*) ;;
      *) export PATH="${DOTNET_ROOT}:${PATH}" ;;
    esac
  fi

  case ":${PATH}:" in
    *":${HOME}/.dotnet/tools:"*) ;;
    *) export PATH="${HOME}/.dotnet/tools:${PATH}" ;;
  esac

  if [ -n "${DOTNET_ROOT:-}" ] && [ -x "${DOTNET_ROOT}/dotnet" ]; then
    return
  fi

  local brew_dotnet8="/opt/homebrew/opt/dotnet@8/libexec"
  if [ -x "${brew_dotnet8}/dotnet" ]; then
    export DOTNET_ROOT="${brew_dotnet8}"
    case ":${PATH}:" in
      *":${DOTNET_ROOT}:"*) ;;
      *) export PATH="${DOTNET_ROOT}:${PATH}" ;;
    esac
  fi
}

configure_dotnet_runtime

run_version_reader() {
  local dll_path="$1"

  python3 "${VERSION_READER_SCRIPT}" "${dll_path}"
}

is_version_string() {
  local value="$1"
  [[ "${value}" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]
}

resolve_emby_version() {
  if [ -n "${EMBY_VERSION}" ]; then
    printf '%s\n' "${EMBY_VERSION}"
    return
  fi

  if [ -n "${DLL_DIR}" ]; then
    local dir_name
    dir_name="$(basename "${DLL_DIR}")"
    if is_version_string "${dir_name}"; then
      printf '%s\n' "${dir_name}"
      return
    fi

    if [ -f "${DLL_DIR}/EmbyServer.dll" ]; then
      run_version_reader "${DLL_DIR}/EmbyServer.dll"
      return
    fi
  fi

  if [ -f "${DLL_ROOT}/EmbyServer.dll" ]; then
    run_version_reader "${DLL_ROOT}/EmbyServer.dll"
    return
  fi

  printf '%s\n' "${EMBY_DOCS_DIR#*_}"
}

discover_target_dirs() {
  if [ -n "${DLL_DIR}" ]; then
    printf '%s\n' "${DLL_DIR}"
    return
  fi

  if [ -n "${EMBY_VERSION}" ]; then
    printf '%s\n' "${DLL_ROOT}/${EMBY_VERSION}"
    return
  fi

  local found=0
  local path
  while IFS= read -r path; do
    [ -n "${path}" ] || continue
    if ! is_version_string "$(basename "${path}")"; then
      continue
    fi
    found=1
    printf '%s\n' "${path}"
  done < <(find "${DLL_ROOT}" -mindepth 1 -maxdepth 1 -type d | sort)

  if [ "${found}" -eq 1 ]; then
    return
  fi

  if [ -f "${DLL_ROOT}/EmbyServer.dll" ]; then
    printf '%s\n' "${DLL_ROOT}"
  fi
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --dll-root)
      DLL_ROOT="${2:?missing value for --dll-root}"
      shift 2
      ;;
    --dll-dir)
      DLL_DIR="${2:?missing value for --dll-dir}"
      shift 2
      ;;
    --emby-version)
      EMBY_VERSION="${2:?missing value for --emby-version}"
      shift 2
      ;;
    --source-root)
      SOURCE_ROOT="${2:?missing value for --source-root}"
      shift 2
      ;;
    --version-label)
      VERSION_LABEL="${2:?missing value for --version-label}"
      shift 2
      ;;
    --overwrite)
      OVERWRITE=1
      shift
      ;;
    --clean)
      CLEAN=1
      shift
      ;;
    -h|--help)
      print_usage
      exit 0
      ;;
    --)
      shift
      while [ "$#" -gt 0 ]; do
        DLL_FILTERS+=("$1")
        shift
      done
      break
      ;;
    -*)
      fail "unknown option: $1"
      ;;
    *)
      DLL_FILTERS+=("$1")
      shift
      ;;
  esac
done

resolve_ilspycmd() {
  if [ -n "${ILSPYCMD:-}" ]; then
    printf '%s\n' "${ILSPYCMD}"
    return
  fi

  if command -v ilspycmd >/dev/null 2>&1; then
    command -v ilspycmd
    return
  fi

  if [ -x "${HOME}/.dotnet/tools/ilspycmd" ]; then
    printf '%s\n' "${HOME}/.dotnet/tools/ilspycmd"
    return
  fi

  return 1
}

ILSPY_CMD="$(resolve_ilspycmd || true)"
[ -n "${ILSPY_CMD}" ] || fail "ilspycmd not found. Install it first with: dotnet tool install --global ilspycmd"

if ! "${ILSPY_CMD}" --version >/dev/null 2>&1; then
  fail "ilspycmd exists but is not runnable. Install .NET 8 runtime or set DOTNET_ROOT, then run '${ILSPY_CMD} --version' to inspect the missing framework."
fi

discover_dlls() {
  local current_dll_dir="$1"

  if [ "${#DLL_FILTERS[@]}" -gt 0 ]; then
    local item
    for item in "${DLL_FILTERS[@]}"; do
      if [ -f "${item}" ]; then
        printf '%s\n' "${item}"
        continue
      fi

      if [ -f "${current_dll_dir}/${item}" ]; then
        printf '%s\n' "${current_dll_dir}/${item}"
        continue
      fi

      echo "  skip missing DLL in ${current_dll_dir}: ${item}" >&2
    done
    return
  fi

  find "${current_dll_dir}" -maxdepth 1 -type f -name '*.dll' | sort
}

SUCCESS_NAMES=()
SKIPPED_NAMES=()
FAILED_NAMES=()
MOVED_SOURCE_NAMES=()
SKIPPED_SOURCE_NAMES=()
TARGET_DIRS=()
PROCESSED_DIRS=()

CURRENT_DLL_DIR=""
CURRENT_SOURCE_ROOT=""
CURRENT_VERSION_LABEL=""

move_existing_source_dir() {
  local base_name="$1"
  local source_dir="${CURRENT_DLL_DIR}/${base_name}"
  local target_source_dir="${CURRENT_SOURCE_ROOT}/${base_name}_${CURRENT_VERSION_LABEL}"

  if [ ! -d "${source_dir}" ]; then
    return
  fi

  if [ -d "${target_source_dir}" ]; then
    if [ "${OVERWRITE}" -eq 1 ]; then
      rm -rf "${target_source_dir}"
    else
      echo "  skip existing source dir: ${target_source_dir}"
      SKIPPED_SOURCE_NAMES+=("${base_name}")
      return
    fi
  fi

  echo "  moving existing source dir -> ${target_source_dir}"
  mv "${source_dir}" "${target_source_dir}"
  MOVED_SOURCE_NAMES+=("${base_name}")
}

decompile_one() {
  local dll_path="$1"
  local dll_name
  local base_name
  local target_dir

  dll_name="$(basename "${dll_path}")"
  base_name="${dll_name%.dll}"
  target_dir="${CURRENT_SOURCE_ROOT}/${base_name}_${CURRENT_VERSION_LABEL}"

  if [ -d "${CURRENT_DLL_DIR}/${base_name}" ] && [ ! -d "${target_dir}" ]; then
    move_existing_source_dir "${base_name}"
    SUCCESS_NAMES+=("${dll_name}")
    return
  fi

  if [ -d "${target_dir}" ] && [ "${OVERWRITE}" -ne 1 ]; then
    echo "Skip existing: ${target_dir}"
    SKIPPED_NAMES+=("${dll_name}")
    return
  fi

  rm -rf "${target_dir}"
  mkdir -p "${target_dir}"

  echo "Decompiling ${dll_name} -> ${target_dir}"
  if "${ILSPY_CMD}" -p -o "${target_dir}" "${dll_path}" >/dev/null 2>&1; then
    SUCCESS_NAMES+=("${dll_name}")
    return
  fi

  FAILED_NAMES+=("${dll_name}")
  rm -rf "${target_dir}"
  echo "  failed: ${dll_name}" >&2
}

resolve_version_for_dir() {
  local current_dll_dir="$1"

  if [ -n "${VERSION_LABEL}" ]; then
    printf '%s\n' "${VERSION_LABEL}"
    return
  fi

  local dir_name
  dir_name="$(basename "${current_dll_dir}")"
  if is_version_string "${dir_name}"; then
    printf '%s\n' "${dir_name}"
    return
  fi

  if [ -f "${current_dll_dir}/EmbyServer.dll" ]; then
    run_version_reader "${current_dll_dir}/EmbyServer.dll"
    return
  fi

  printf '%s\n' "$(resolve_emby_version)"
}

TARGET_DIR_COUNT=0
while IFS= read -r target_dir; do
  [ -n "${target_dir}" ] || continue
  TARGET_DIRS+=("${target_dir}")
  TARGET_DIR_COUNT=$((TARGET_DIR_COUNT + 1))
done < <(discover_target_dirs)

[ "${TARGET_DIR_COUNT}" -gt 0 ] || fail "no DLL directories found under ${DLL_ROOT}"

if [ -n "${SOURCE_ROOT}" ] && [ "${TARGET_DIR_COUNT}" -gt 1 ]; then
  fail "--source-root can only be used with a single target DLL directory"
fi

if [ -n "${VERSION_LABEL}" ] && [ "${TARGET_DIR_COUNT}" -gt 1 ]; then
  fail "--version-label can only be used with a single target DLL directory"
fi

TOTAL_DLLS=0

for target_dir in "${TARGET_DIRS[@]}"; do
  [ -d "${target_dir}" ] || fail "DLL directory not found: ${target_dir}"

  CURRENT_DLL_DIR="${target_dir}"
  CURRENT_VERSION_LABEL="$(resolve_version_for_dir "${CURRENT_DLL_DIR}")"
  CURRENT_SOURCE_ROOT="${SOURCE_ROOT:-${CURRENT_DLL_DIR}/source}"

  if [ "${CLEAN}" -eq 1 ] && [ -d "${CURRENT_SOURCE_ROOT}" ]; then
    rm -rf "${CURRENT_SOURCE_ROOT}"
  fi

  mkdir -p "${CURRENT_SOURCE_ROOT}"

  DLLS=()
  while IFS= read -r dll_path; do
    [ -n "${dll_path}" ] || continue
    DLLS+=("${dll_path}")
  done < <(discover_dlls "${CURRENT_DLL_DIR}")

  if [ "${#DLLS[@]}" -eq 0 ]; then
    echo "Skip empty DLL dir: ${CURRENT_DLL_DIR}"
    continue
  fi

  PROCESSED_DIRS+=("${CURRENT_DLL_DIR}")
  echo "Processing DLL dir: ${CURRENT_DLL_DIR}"

  for dll_path in "${DLLS[@]}"; do
    TOTAL_DLLS=$((TOTAL_DLLS + 1))
    decompile_one "${dll_path}"
  done
done

[ "${TOTAL_DLLS}" -gt 0 ] || fail "no matching DLL files found"

echo
echo "Done."
echo "  dirs   : ${#PROCESSED_DIRS[@]}"
echo "  dlls   : ${TOTAL_DLLS}"
echo "  success: ${#SUCCESS_NAMES[@]}"
echo "  skipped: ${#SKIPPED_NAMES[@]}"
echo "  failed : ${#FAILED_NAMES[@]}"
if [ "${#PROCESSED_DIRS[@]}" -eq 1 ]; then
  echo "  source : ${CURRENT_SOURCE_ROOT}"
else
  echo "  source : <each-version-dir>/source"
fi

if [ "${#FAILED_NAMES[@]}" -gt 0 ]; then
  exit 1
fi
