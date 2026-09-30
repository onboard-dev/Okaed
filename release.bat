@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem --- GitHub CLI (gh) の存在確認 ---
where gh >nul 2>nul
if errorlevel 1 (
    echo GitHub CLI (gh) が見つかりません。
    echo 以下からインストールしてください:
    echo https://cli.github.com/
    echo インストール後、このファイルをもう一度実行してください。
    pause
    exit /b 1
)

rem --- ログイン状態の確認 ---
gh auth status >nul 2>nul
if errorlevel 1 (
    echo GitHub にログインしていません。
    echo 認証を開始します。画面の指示に従ってください...
    gh auth login
    if errorlevel 1 (
        echo ログインに失敗しました。中止します。
        pause
        exit /b 1
    )
)

rem --- Okaed.exe の存在確認 ---
if not exist "Okaed.exe" (
    echo Okaed.exe が見つかりません。先に build.bat でビルドしてください。
    pause
    exit /b 1
)

rem --- バージョン番号の入力 ---
set /p VERSION=バージョン番号を入力してください(例: 1.0.0): 
if "%VERSION%"=="" (
    echo バージョン番号が入力されませんでした。中止します。
    pause
    exit /b 1
)

set TAG=v%VERSION%
set TITLE=Okaed %TAG%

echo.
echo タグ %TAG% で Release を作成し、Okaed.exe を添付します...
gh release create %TAG% "Okaed.exe" --repo onboard-dev/Okaed --title "%TITLE%" --generate-notes

if errorlevel 1 (
    echo.
    echo Release の作成に失敗しました。上記のエラー内容を確認してください。
    echo (同じタグ名が既に存在する場合は、別のバージョン番号を指定してください)
    pause
    exit /b 1
)

echo.
echo 公開しました: https://github.com/onboard-dev/Okaed/releases/tag/%TAG%
pause
