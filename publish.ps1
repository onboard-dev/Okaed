# Okaed ソースコード公開スクリプト
# 使い方: このファイルを右クリック → 「PowerShellで実行」(または publish.bat をダブルクリック)

$RepoUrl = "https://github.com/onboard-dev/Okaed.git"
$Branch  = "main"

# スクリプトのあるフォルダに移動
Set-Location -Path $PSScriptRoot

function Fail($msg) {
    Write-Host $msg -ForegroundColor Red
    exit 1
}

# git の存在確認
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Fail "git が見つかりません。https://git-scm.com/ からインストールしてください。"
}

# リポジトリ初期化(未初期化の場合のみ)
if (-not (Test-Path ".git")) {
    Write-Host "git リポジトリを初期化します..."
    git init | Out-Null
}

# リモート設定(なければ追加、あれば URL を更新)
$existingRemotes = git remote 2>$null
if ($existingRemotes -notcontains "origin") {
    git remote add origin $RepoUrl
} else {
    git remote set-url origin $RepoUrl
}

# ステージ(.gitignore に従い、build 成果物や作業用フォルダは自動的に除外されます)
git add -A

$hasChanges = git status --porcelain
if ([string]::IsNullOrWhiteSpace($hasChanges)) {
    Write-Host "コミットする変更はありません。"
} else {
    $msg = Read-Host "コミットメッセージを入力してください(空欄なら既定のメッセージを使用)"
    if ([string]::IsNullOrWhiteSpace($msg)) {
        $msg = "Update Okaed"
    }
    git commit -m $msg
    if ($LASTEXITCODE -ne 0) { Fail "コミットに失敗しました。上記のメッセージを確認してください。" }
}

# ブランチ名を統一
git branch -M $Branch

# GitHub へ push(初回は認証(ブラウザ or トークン)を求められます)
Write-Host ""
Write-Host "GitHub へ公開します: $RepoUrl"
git push -u origin $Branch

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "リモート側に、ローカルにない変更があるようです(GitHub でリポジトリ作成時に README や LICENSE が自動生成された場合など)。" -ForegroundColor Yellow
    Write-Host "リモートの内容を取り込んで、こちらの内容を優先する形で解決します..."

    git fetch origin $Branch
    if ($LASTEXITCODE -ne 0) { Fail "リモートの取得(fetch)に失敗しました。" }

    git merge "origin/$Branch" --allow-unrelated-histories -X ours --no-edit -m "Merge remote-tracking branch (keep local files)"
    if ($LASTEXITCODE -ne 0) {
        Fail "自動での取り込みに失敗しました。'git status' を確認し、手動で解決してから再度このスクリプトを実行してください。"
    }

    git push -u origin $Branch
    if ($LASTEXITCODE -ne 0) {
        Fail "GitHub への公開(push)に失敗しました。上記のエラー内容を確認してください。"
    }
}

Write-Host ""
Write-Host "完了しました: https://github.com/onboard-dev/Okaed" -ForegroundColor Green
