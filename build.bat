@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ===================================================== > build_log.txt
echo Okaed ビルドログ  %DATE% %TIME% >> build_log.txt
echo ===================================================== >> build_log.txt
echo. >> build_log.txt

rem --- csc.exe (Windows 標準の C# コンパイラ) を探す ---
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

echo 使用するコンパイラ: %CSC% >> build_log.txt
if not exist "%CSC%" (
    echo [エラー] csc.exe が見つかりません。.NET Framework 4.x が入っていない可能性があります。 >> build_log.txt
    echo csc.exe が見つかりませんでした。build_log.txt を確認してください。
    type build_log.txt
    pause
    exit /b 1
)

echo. >> build_log.txt
echo --- ビルド前の Okaed.exe --- >> build_log.txt
if exist Okaed.exe (
    for %%F in (Okaed.exe) do echo   更新日時: %%~tF  サイズ: %%~zF バイト >> build_log.txt
) else (
    echo   (Okaed.exe はまだ存在しません) >> build_log.txt
)

echo. >> build_log.txt
echo --- コンパイル対象 (*.cs) --- >> build_log.txt
dir /b *.cs >> build_log.txt

echo. >> build_log.txt
echo --- コンパイル実行 --- >> build_log.txt
set ICONOPT=
if exist Okaed.ico set ICONOPT=/win32icon:Okaed.ico

echo コマンド: "%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe %ICONOPT% /out:Okaed.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll *.cs >> build_log.txt
echo. >> build_log.txt

"%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe %ICONOPT% /out:Okaed.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll *.cs >> build_log.txt 2>&1
set RESULT=%ERRORLEVEL%

echo. >> build_log.txt
echo --- 結果 --- >> build_log.txt
echo 終了コード: %RESULT% >> build_log.txt

if %RESULT% NEQ 0 (
    echo [失敗] コンパイルエラーが発生しました。上のログを確認してください。 >> build_log.txt
) else (
    echo [成功] Okaed.exe を作成しました。 >> build_log.txt
    echo. >> build_log.txt
    echo --- ビルド後の Okaed.exe --- >> build_log.txt
    for %%F in (Okaed.exe) do echo   更新日時: %%~tF  サイズ: %%~zF バイト >> build_log.txt
)

echo. >> build_log.txt
echo ===================================================== >> build_log.txt

rem --- 画面にもログを表示 ---
type build_log.txt
echo.
echo ログは build_log.txt にも保存されています。
echo.
if %RESULT% NEQ 0 (
    echo ビルドに失敗しました。上の内容をそのまま送ってください。
    pause
) else (
    echo ビルドに成功しました。Okaed.exe を実行してください。
    timeout /t 2 >nul
)
