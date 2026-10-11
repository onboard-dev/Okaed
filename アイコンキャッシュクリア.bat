ie4uinit.exe -show
taskkill /IM explorer.exe /F
del /A /F /Q "%localappdata%\IconCache.db"
del /A /F /Q "%localappdata%\Microsoft\Windows\Explorer\iconcache*"
start explorer.exe
