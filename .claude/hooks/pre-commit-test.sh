#!/usr/bin/env bash
# CLAUDE.md「建置與驗證」規定 commit 前一律先跑過完整測試套件——這支 hook 把這條規則變成
# 強制執行，而不是只能靠人記得。只在偵測到 Bash 工具要執行的指令包含 git commit 時才觸發，
# 其餘指令（git status、git diff 等）直接放行，不拖慢日常操作。
#
# 不依賴 jq／python（這台機器沒裝 jq），單純用 grep 判斷 stdin 的 JSON 裡 command 欄位
# 有沒有出現 "git commit"——夠用，不需要真的解析整份 JSON。

input=$(cat)

if ! echo "$input" | grep -q '"command"[[:space:]]*:.*git commit'; then
    exit 0
fi

repo_root=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
cd "$repo_root" || exit 0

echo "[pre-commit-test] 偵測到 git commit，先跑 dotnet test..." >&2

log_file=$(mktemp)
if dotnet test --nologo > "$log_file" 2>&1; then
    echo "[pre-commit-test] 測試通過，允許 commit。" >&2
    rm -f "$log_file"
    exit 0
fi

echo "[pre-commit-test] dotnet test 沒有全部通過，擋下這次 commit：" >&2
tail -30 "$log_file" >&2
rm -f "$log_file"
exit 2
