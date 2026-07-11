@echo off
SETLOCAL
set "SCRIPT_DIR=%~dp0"
"%SCRIPT_DIR%.venv\Scripts\python.exe" "%SCRIPT_DIR%app.py" %*
ENDLOCAL
