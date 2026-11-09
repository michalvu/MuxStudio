@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ==============================================================
echo   MuxStudio - DO API (SubSource, OpenSubtitles, TMDB, TVDB)
echo ==============================================================
echo.
rem Can Python 3.11+ (python.org, tick "Add python.exe to PATH").
set "PY=python"
where py >nul 2>nul && set "PY=py -3"
if not exist ".venv\Scripts\python.exe" (
    echo Dang tao moi truong Python rieng .venv ...
    %PY% -m venv .venv
    if errorlevel 1 goto :loi
)
".venv\Scripts\python.exe" -m pip install -q -r requirements.txt
if errorlevel 1 goto :loi

rem Buoc 1: nhap key/tai khoan tu 3 file .bat cu vao Windows Credential Manager
set "BATDIR=%~1"
if defined BATDIR goto :nhap
:hoi
echo.
echo Buoc 1: Dan duong dan THU MUC chua 3 file .bat CU cua ban - ban co key that:
echo         SubFetch_v30.bat, SubPrep_v20.bat, MuxTool_v17.bat
echo         ^(KHONG phai thu muc MuxStudio. Enter = bo qua, dung key da nhap lan truoc^)
set "BATDIR="
set /p "BATDIR=> "
if not defined BATDIR goto :do
:nhap
set "BATDIR=%BATDIR:"=%"
".venv\Scripts\python.exe" -m tools.nhap_tu_bat "%BATDIR%"
if errorlevel 1 (
    echo.
    echo [!] Chua lay duoc key tu thu muc tren ^(xem thong bao o tren^). Thu lai nhe.
    goto :hoi
)

:do
echo.
echo Buoc 2: Dang do API (mat khoang 1 phut) ...
".venv\Scripts\python.exe" -m tools.probe_all
echo.
echo Xong. Gui file ket_qua_do_api.txt (nam canh file nay) cho Claude.
echo Key, token va mat khau trong file da duoc che.
pause
exit /b

:loi
echo.
echo [LOI] Co loi o tren. Neu chua cai Python: cai Python 3.11+ tu python.org
echo       (nho tick "Add python.exe to PATH"), roi chay lai file nay.
pause
exit /b 1
