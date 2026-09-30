@echo off
chcp 65001 >nul
cd /d "%~dp0"

set /p VERSION=バージョン番号を入力してください(例: 1.0.0): 
if "%VERSION%"=="" (
    echo バージョン番号が入力されませんでした。中止します。
    pause
    exit /b 1
)

echo namespace Okaed> Version.cs
echo {>> Version.cs
echo     // set-version.bat によって自動生成・上書きされます。>> Version.cs
echo     static class AppVersion>> Version.cs
echo     {>> Version.cs
echo         public const string Value = "%VERSION%";>> Version.cs
echo     }>> Version.cs
echo }>> Version.cs

echo.
echo バージョンを %VERSION% に設定しました。(Version.cs を更新しました)
echo このあと build.bat を実行してビルドしてください。
echo メインウィンドウのタイトルに Okaed Version %VERSION% のように表示されます。
echo.
pause
