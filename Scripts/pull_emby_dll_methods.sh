#!/bin/sh
set -eu

# Usage:
#   ./pull_emby_dll_methods.sh
NAS_HOST="root@192.168.33.100"
REMOTE_DIR="/strm/mediainfo"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
DOCS_DIR="${PROJECT_DIR}/Docs"

mkdir -p "${DOCS_DIR}"

tmp_file="$(mktemp)"
trap 'rm -f "$tmp_file"' EXIT INT TERM
ssh "${NAS_HOST}" "find '${REMOTE_DIR}' -mindepth 2 -maxdepth 2 -type f -path '${REMOTE_DIR}/emby_*/*_methods.txt' | sort" > "$tmp_file"

if [ ! -s "$tmp_file" ]; then
  echo "No files matched: ${REMOTE_DIR}/emby_*/*_methods.txt"
  exit 0
fi

count=0
skipped=0
while IFS= read -r remote_path; do
  [ -n "$remote_path" ] || continue
  rel_path="${remote_path#${REMOTE_DIR}/}"
  local_path="${DOCS_DIR}/${rel_path}"
  local_dir="$(dirname "${local_path}")"
  mkdir -p "${local_dir}"

  if [ -f "${local_path}" ]; then
    echo "Skip existing: ${local_path}"
    skipped=$((skipped + 1))
    continue
  fi

  echo "Copying ${NAS_HOST}:${remote_path} -> ${local_path}"
  scp "${NAS_HOST}:${remote_path}" "${local_path}"
  count=$((count + 1))
done < "$tmp_file"

echo "Done. Downloaded ${count} file(s), skipped ${skipped} existing file(s)."
find "${DOCS_DIR}" -maxdepth 2 -type f -path "${DOCS_DIR}/emby_*/*_methods.txt" -print
