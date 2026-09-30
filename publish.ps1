# Okaed ソースコード公開スクリプト
# 使い方: このファイルを右クリック → 「PowerShellで実行」(または publish.bat をダブルクリック)

$ErrorActionPreference = "Stop"

$RepoUrl = "https://github.com/onboard-dev/Okaed.git"
$Branch  = "main"

# スクリプトのあるフォルダに移動
Set-Location -Path $PSScriptRoot

# git の存在確認
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "git が見つかりません。https://git-scm.com/ からインストールしてください。" -ForegroundColor Red
    exit 1
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
}

# ブランチ名を統一
git branch -M $Branch

# GitHub へ push(初回は認証(ブラウザ or トークン)を求められます)
Write-Host ""
Write-Host "GitHub へ公開します: $RepoUrl"
git push -u origin $Branch

Write-Host ""
Write-Host "完了しました: https://github.com/onboard-dev/Okaed" -ForegroundColor Green
