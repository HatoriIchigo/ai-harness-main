#!/usr/bin/env bash
# tree-sitter の native（libtree-sitter 本体と grammar）を linux-x64 / win-x64 向けにビルドし、
# native/<rid>/ へ配置して versions.json を更新する。
#
# 使用者側の環境には dotnet しか無い前提のため、生成物はリポジトリへコミットして配布する
# （publish が runtimes/<rid>/native/ へ配る）。このスクリプトは開発側でのみ実行する。
#
# 必要なもの: git / gcc / x86_64-w64-mingw32-gcc（Debian 系は apt install mingw-w64）/ python3
#
#   ./build-native.sh            全 RID をビルド
#   ./build-native.sh linux-x64  指定 RID のみ
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="${TMPDIR:-/tmp}/ai-harness-native-src"
RIDS=("${@:-linux-x64 win-x64}")
read -r -a RIDS <<< "${RIDS[*]}"

# id|repo|tag|サブディレクトリ（リポジトリ直下なら .）
CORE="tree-sitter/tree-sitter|v0.26.11"
CORE_SONAME="libtree-sitter.so.0.26"
GRAMMARS=(
  "bash|tree-sitter/tree-sitter-bash|v0.25.1|."
  "c|tree-sitter/tree-sitter-c|v0.24.2|."
  "cpp|tree-sitter/tree-sitter-cpp|v0.23.4|."
  "go|tree-sitter/tree-sitter-go|v0.25.0|."
  "java|tree-sitter/tree-sitter-java|v0.23.5|."
  "python|tree-sitter/tree-sitter-python|v0.25.0|."
  "rust|tree-sitter/tree-sitter-rust|v0.24.2|."
  "typescript|tree-sitter/tree-sitter-typescript|v0.23.2|typescript"
  "tsx|tree-sitter/tree-sitter-typescript|v0.23.2|tsx"
  "hcl|MichaHoffmann/tree-sitter-hcl|v1.2.0|."
)

cc_for() { case "$1" in linux-x64) echo gcc ;; win-x64) echo x86_64-w64-mingw32-gcc ;;
  *) echo "未対応の RID: $1（macOS は対象外）" >&2; return 1 ;; esac; }
core_name() { case "$1" in linux-*) echo libtree-sitter.so ;; win-*) echo tree-sitter.dll ;; esac; }
grammar_name() { case "$1" in linux-*) echo "libtree-sitter-$2.so" ;; win-*) echo "tree-sitter-$2.dll" ;; esac; }

fetch() { # dir repo tag
  local dir="$WORK/$1"
  [ -d "$dir" ] && return 0
  git clone -q --depth 1 --branch "$3" "https://github.com/$2.git" "$dir"
}

mkdir -p "$WORK"

# 1. ソース取得（tag 固定）
IFS='|' read -r core_repo core_tag <<< "$CORE"
fetch core "$core_repo" "$core_tag"
for g in "${GRAMMARS[@]}"; do
  IFS='|' read -r id repo tag _ <<< "$g"
  fetch "$id" "$repo" "$tag"
done

# 2. RID ごとにビルド
for rid in "${RIDS[@]}"; do
  cc="$(cc_for "$rid")"
  command -v "$cc" >/dev/null || { echo "$cc が見つからない（$rid をスキップ）" >&2; continue; }
  out="$HERE/$rid"
  mkdir -p "$out"
  echo "== $rid ($cc)"

  # RID 別のリンカオプション。
  #   linux: 同梱版と同じ SONAME を付ける（既ロード解決の互換のため）。
  #   win  : PE ヘッダの TimeDateStamp を打たない（付くとビルドごとに sha256 が変わり
  #          versions.json のハッシュが再現しない）。
  link_opt=()
  case "$rid" in
    linux-*) link_opt=(-Wl,-soname,"$CORE_SONAME") ;;
    win-*)   link_opt=(-Wl,--no-insert-timestamp) ;;
  esac

  "$cc" -shared -fPIC -O2 -static-libgcc \
    -I "$WORK/core/lib/include" -I "$WORK/core/lib/src" "$WORK/core/lib/src/lib.c" \
    "${link_opt[@]}" -o "$out/$(core_name "$rid")"
  echo "   $(core_name "$rid")"

  for g in "${GRAMMARS[@]}"; do
    IFS='|' read -r id repo tag sub <<< "$g"
    dir="$WORK/$id"
    [ "$sub" != "." ] && dir="$dir/$sub"
    srcs=("$dir/src/parser.c")
    [ -f "$dir/src/scanner.c" ] && srcs+=("$dir/src/scanner.c")
    grammar_link_opt=()
    [[ "$rid" == win-* ]] && grammar_link_opt=(-Wl,--no-insert-timestamp)
    "$cc" -shared -fPIC -O2 -static-libgcc -I "$dir/src" "${srcs[@]}" \
      "${grammar_link_opt[@]}" -o "$out/$(grammar_name "$rid" "$id")"
    echo "   $(grammar_name "$rid" "$id")"
  done
done

# 3. versions.json の sha256 と languageVersion を更新
python3 - "$HERE" "$WORK" <<'PY'
import hashlib, json, pathlib, re, sys
here, work = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
doc = json.loads((here/"versions.json").read_text(encoding="utf-8"))
subdir = {"typescript": "typescript/typescript", "tsx": "typescript/tsx"}

def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()

for rid, entry in doc["core"]["files"].items():
    f = here/rid/entry["name"]
    if f.exists():
        entry["sha256"] = sha(f)
for gid, g in doc["grammars"].items():
    src = work/subdir.get(gid, gid)/"src"/"parser.c"
    if src.exists():
        m = re.search(r"#define LANGUAGE_VERSION (\d+)", src.read_text(encoding="utf-8", errors="replace"))
        if m:
            g["languageVersion"] = int(m.group(1))
    for rid, entry in g["files"].items():
        f = here/rid/entry["name"]
        if f.exists():
            entry["sha256"] = sha(f)
(here/"versions.json").write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("versions.json を更新")
PY

echo "完了。ソースは $WORK に残る（再実行時に再利用。版を上げるときは削除する）"
