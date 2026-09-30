@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if errorlevel 1 (
  echo.
  echo エラーが発生しました。上記のメッセージを確認してください。
  pause
) else (
  echo.
  echo 公開処理が完了しました。
  pause
)
