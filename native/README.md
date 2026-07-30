# native（tree-sitter の自前ビルド native）

`libtree-sitter` 本体と全 grammar を**自前ビルドしてこのリポジトリで配布する**。`ai-harness-main.csproj`
が発行対象 RID のものだけを publish 出力の `runtimes/<rid>/native/` へ配り、host が起動時にフルパスで
事前ロードする（`Program.PreloadNativeLibraries`）。プラグイン側は `new Language("<id>")` で使う。

## なぜ NuGet 同梱を使わないか

- 使用者側の環境に入るのは **dotnet だけ**。それ以外が必要になるものは同梱するか採用しない
- `.NET SDK` に C コンパイラは無く、grammar（C）は dotnet ではビルドできない
- `TreeSitter.DotNet` の NuGet 同梱 native は、単一ファイル発行（`IncludeNativeLibrariesForSelfExtract=true`）で
  **exe に吸われて `runtimes/<rid>/native/` に出ない**（実測）。`Content` ＋ `ExcludeFromSingleFile` で配れる
  リポジトリ同梱の方が、発行・`--update` の経路が 1 本で済む
- hcl / yaml の grammar はそもそも NuGet に無い（`tree-sitter-*.runtime.<rid>` は c / cpp / json / lua のみ、Windows RID なし）

C# バインディング（`TreeSitter.dll`）だけは NuGet の `TreeSitter.DotNet` を使い続ける。各 tree-sitter
プラグインの csproj がこれを参照し、`lib/` へ同居させる。**native はこのディレクトリのものが使われる。**

## 対応 RID

| RID | 本体 | grammar | ビルド |
|---|---|---|---|
| `linux-x64` | `libtree-sitter.so` | `libtree-sitter-<id>.so` | `gcc` |
| `win-x64` | `tree-sitter.dll` | `tree-sitter-<id>.dll` | `x86_64-w64-mingw32-gcc`（クロス） |

**macOS は対象外**。arm64 も現状は含めない。増やす場合は `build-native.sh` の `cc_for` に RID を追加し、
`versions.json` の `rids` と `files` を増やす（`Content` は `native/<rid>/` の有無で自動判定するため
csproj の変更は不要）。

ファイル名は OS の慣習に合わせる。`TreeSitter.DotNet` の `new Language(id)` はライブラリ名を
`tree-sitter-<id>`、関数名を `tree_sitter_<id>` として解決し、`NativeLibrary.Load` がベア名から
プラットフォーム慣習（`lib` プレフィックス・拡張子）を補完する。

## 版の管理

**`versions.json` が単一の真実。** upstream の tag / commit / ライセンス / `LANGUAGE_VERSION` /
RID 別ファイルの sha256 を持つ。`build-native.sh` が sha256 と `LANGUAGE_VERSION` を再計算して書き戻すため、
手で編集するのは upstream の tag を上げるときだけ（スクリプト冒頭の `CORE` / `GRAMMARS`）。

`--update` で本体が更新されると native も一緒に届くが、**版が動くのはこのリポジトリで tag を上げた
コミットだけ**。`--update` を実行しても upstream の最新に勝手に追従することはない。

## 収録物

本体は `tree-sitter/tree-sitter` の `v0.26.11`（MIT、SONAME `libtree-sitter.so.0.26`）。
grammar は 10 種（tag は `versions.json` を参照）。

| id | upstream | ライセンス |
|---|---|---|
| `bash` | tree-sitter/tree-sitter-bash | MIT |
| `c` | tree-sitter/tree-sitter-c | MIT |
| `cpp` | tree-sitter/tree-sitter-cpp | MIT |
| `go` | tree-sitter/tree-sitter-go | MIT |
| `java` | tree-sitter/tree-sitter-java | MIT |
| `python` | tree-sitter/tree-sitter-python | MIT |
| `rust` | tree-sitter/tree-sitter-rust | MIT |
| `typescript` | tree-sitter/tree-sitter-typescript | MIT |
| `tsx` | tree-sitter/tree-sitter-typescript | MIT |
| `hcl` | MichaHoffmann/tree-sitter-hcl | Apache-2.0 |

収録する grammar は**プラグインが実際に使う言語だけ**に絞る（`new Language(...)` に渡す id ＋ 各
プラグインの拡張子マップの値）。使わない grammar を増やすとリリースサイズがそのまま増える。

## ビルド

開発側でのみ実行する。必要なもの: `git` / `gcc` / `x86_64-w64-mingw32-gcc`（Debian 系は
`apt install mingw-w64`）/ `python3`。

```sh
./native/build-native.sh              # 全 RID
./native/build-native.sh linux-x64    # 指定 RID のみ
```

出力は `native/<rid>/` へ直接置かれ、`versions.json` が更新される。**同じ tag からのビルドは
sha256 まで再現する**（win-x64 は `-Wl,--no-insert-timestamp` で PE のタイムスタンプを打たない）。
ソースは `$TMPDIR/ai-harness-native-src` に残り再実行時に再利用されるため、tag を上げるときは削除する。

## 更新時の確認

`libtree-sitter` 本体と grammar の ABI 整合（grammar の `LANGUAGE_VERSION` が本体の対応範囲内）は、
実際にパースさせて確認する。`hasError=False` になり、`new Language(id)` が例外を投げないこと。
`--doctor` の `native (tree-sitter)` 行でロード可能な個数と版の整合も確認できる。
