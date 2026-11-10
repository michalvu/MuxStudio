@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem ==========================================================
rem  Chay toan bo test cua MuxStudio tren Windows.
rem  Can Python 3.11+ (python.org, tick "Add python.exe to PATH").
rem  Lan dau se tao thu muc .venv va tai thu vien (vai phut).
rem ==========================================================
rem  File cua ban cu da bi xoa khoi ma nguon (kho srt_pool, bo tu ban 1.2.0). Chep de ma moi len thu
rem  muc cu thi chung van con nam lai va lam hong test -> don di.
for %%F in ("core\pool.py" "tests\test_pool.py") do if exist "%%~F" (
    echo Xoa file cu khong con dung: %%~F
    del /q "%%~F"
)
set "PY=python"
where py >nul 2>nul && set "PY=py -3"
if not exist ".venv\Scripts\python.exe" (
    echo Dang tao moi truong Python rieng .venv ...
    %PY% -m venv .venv
    if errorlevel 1 goto :loi
)
".venv\Scripts\python.exe" -m pip install -q -r requirements-dev.txt
if errorlevel 1 goto :loi
".venv\Scripts\python.exe" -m pytest -q
echo.
pause
exit /b

:loi
echo.
echo [LOI] Khong tao duoc moi truong Python. Hay cai Python 3.11+ tu python.org
echo       (nho tick "Add python.exe to PATH"), roi chay lai file nay.
pause
exit /b 1
