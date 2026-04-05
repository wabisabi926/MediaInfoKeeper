#!/usr/bin/env bash
set -euo pipefail

EMBY_DOCS_DIR="${EMBY_DOCS_DIR:-emby_4.9.3.0}"
DLL_DIR="${DLL_DIR:-/Users/honue/Documents/Emby/dlls}"
VERSION_LABEL="${VERSION_LABEL:-${EMBY_DOCS_DIR#*_}}"
SOURCE_ROOT="${SOURCE_ROOT:-${DLL_DIR}/source}"

OVERWRITE=0
CLEAN=0
DLL_FILTERS=()

print_usage() {
  cat <<EOF
Usage:
  ./decompile_emby_dlls.sh
  ./decompile_emby_dlls.sh Emby.Providers.dll MediaBrowser.Controller.dll
  ./decompile_emby_dlls.sh --overwrite
  ./decompile_emby_dlls.sh --dll-dir /Users/honue/Documents/Emby/dlls --source-root /Users/honue/Documents/Emby/dlls/source
  EMBY_DOCS_DIR=emby_4.9.3.0 ./decompile_emby_dlls.sh

Options:
  --dll-dir <path>         Directory containing downloaded Emby DLL files.
                           Default: ${DLL_DIR}
  --source-root <path>     Root output directory for decompiled source folders.
                           Default: ${SOURCE_ROOT}
  --version-label <text>   Folder suffix for each decompiled project.
                           Default: ${VERSION_LABEL}
  --overwrite              Re-run decompilation even if output already exists.
  --clean                  Remove the source root before exporting.
  --help, -h               Show this help.

Arguments:
  <dll-name>               Optional DLL file names to decompile.
                           Without arguments, all top-level *.dll files in --dll-dir are exported.

Environment:
  EMBY_DOCS_DIR            Version tag used to derive the default folder suffix.
                           Default: emby_4.9.3.0
  VERSION_LABEL            Same as --version-label. Default strips prefix from EMBY_DOCS_DIR.
  ILSPYCMD                 Override the ilspycmd executable path.
  DLL_DIR                  Same as --dll-dir.
  SOURCE_ROOT              Same as --source-root.

Notes:
  - DLL files stay under ${DLL_DIR}
  - Decompiled sources go to ${SOURCE_ROOT}/<AssemblyName>_<VersionLabel>
  - If a same-name source folder already exists next to the DLL, it is moved to:
    ${SOURCE_ROOT}/<AssemblyName>_<VersionLabel>
EOF
}

fail() {
  echo "Error: $*" >&2
  exit 1
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --dll-dir)
      DLL_DIR="${2:?missing value for --dll-dir}"
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

[ -d "${DLL_DIR}" ] || fail "DLL directory not found: ${DLL_DIR}"

if [ "${CLEAN}" -eq 1 ] && [ -d "${SOURCE_ROOT}" ]; then
  rm -rf "${SOURCE_ROOT}"
fi

mkdir -p "${SOURCE_ROOT}"

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
  fail "ilspycmd exists but is not runnable. Run '${ILSPY_CMD} --version' to inspect the missing runtime/framework."
fi

discover_dlls() {
  if [ "${#DLL_FILTERS[@]}" -gt 0 ]; then
    local item
    for item in "${DLL_FILTERS[@]}"; do
      if [ -f "${item}" ]; then
        printf '%s\n' "${item}"
        continue
      fi

      if [ -f "${DLL_DIR}/${item}" ]; then
        printf '%s\n' "${DLL_DIR}/${item}"
        continue
      fi

      fail "DLL not found: ${item}"
    done
    return
  fi

  find "${DLL_DIR}" -maxdepth 1 -type f -name '*.dll' | sort
}

DLLS=()
while IFS= read -r dll_path; do
  [ -n "${dll_path}" ] || continue
  DLLS+=("${dll_path}")
done < <(discover_dlls)
[ "${#DLLS[@]}" -gt 0 ] || fail "no DLL files found under ${DLL_DIR}"

SUCCESS_NAMES=()
SKIPPED_NAMES=()
FAILED_NAMES=()
MOVED_SOURCE_NAMES=()
SKIPPED_SOURCE_NAMES=()

move_existing_source_dir() {
  local base_name="$1"
  local source_dir="${DLL_DIR}/${base_name}"
  local target_source_dir="${SOURCE_ROOT}/${base_name}_${VERSION_LABEL}"

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
  target_dir="${SOURCE_ROOT}/${base_name}_${VERSION_LABEL}"

  if [ -d "${DLL_DIR}/${base_name}" ] && [ ! -d "${target_dir}" ]; then
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

for dll_path in "${DLLS[@]}"; do
  decompile_one "${dll_path}"
done

echo
echo "Done."
echo "  success: ${#SUCCESS_NAMES[@]}"
echo "  skipped: ${#SKIPPED_NAMES[@]}"
echo "  failed : ${#FAILED_NAMES[@]}"
echo "  source : ${SOURCE_ROOT}"

if [ "${#FAILED_NAMES[@]}" -gt 0 ]; then
  exit 1
fi
