@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem ==========================================================
rem  Chay MuxStudio tu ma nguon (de thu Giai doan 5).
rem  Can Python 3.11+ (python.org, tick "Add python.exe to PATH").
rem  Lan dau se tao thu muc .venv va tai thu vien (vai phut).
rem  Ban .exe (khong can Python) se co o Giai doan 6.
rem ==========================================================
set "PY=python"
where py >nul 2>nul && set "PY=py -3"
if not exist ".venv\Scripts\pythonw.exe" (
    echo Dang tao moi truong Python rieng .venv ...
    %PY% -m venv .venv
    if errorlevel 1 goto :loi
)
echo Dang kiem tra thu vien ...
".venv\Scripts\python.exe" -m pip install -q -r requirements.txt
if errorlevel 1 goto :loi
start "" ".venv\Scripts\pythonw.exe" -m gui.app
exit /b

:loi
echo.
echo [LOI] Khong tao duoc moi truong Python. Hay cai Python 3.11+ tu python.org
echo       (nho tick "Add python.exe to PATH"), roi chay lai file nay.
pause
exit /b 1
