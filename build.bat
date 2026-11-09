@echo off
chcp 65001 >nul
cd /d "%~dp0"
setlocal
rem ==========================================================
rem  Dong goi MuxStudio thanh MuxStudio.exe (Giai doan 6).
rem  - Can Python 3.11+ de BUILD (python.org, tick "Add python.exe to PATH").
rem    Ban .exe tao ra chay KHONG can cai Python.
rem  - Ket qua: dist\MuxStudio\MuxStudio.exe
rem    Chep CA thu muc dist\MuxStudio (gom thu muc _internal), khong chep rieng file .exe.
rem  - Lan dau mat vai phut (tao thu muc .venv-build va tai thu vien).
rem ==========================================================
set "PY=python"
where py >nul 2>nul && set "PY=py -3"
set "VPY=.venv-build\Scripts\python.exe"
set "EXE=dist\MuxStudio\MuxStudio.exe"
set "BAOCAO=%~dp0kiem_tra_exe.txt"

echo [1/4] Chuan bi moi truong build rieng (.venv-build) ...
if not exist "%VPY%" (
    %PY% -m venv .venv-build
    if errorlevel 1 goto :loi_python
)
"%VPY%" -c "import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)"
if errorlevel 1 goto :loi_phienban
"%VPY%" -m pip install -q --disable-pip-version-check -r requirements-build.txt
if errorlevel 1 goto :loi_pip

echo [2/4] Kiem tra MuxStudio.exe cu co dang mo khong ...
tasklist /fi "imagename eq MuxStudio.exe" /nh 2>nul | findstr /i /c:"MuxStudio.exe" >nul
if not errorlevel 1 goto :loi_dangmo

echo [3/4] Dong goi bang PyInstaller (1-3 phut, nhieu dong chu la binh thuong) ...
"%VPY%" -m PyInstaller --noconfirm --clean MuxStudio.spec
if errorlevel 1 goto :loi_build
if not exist "%EXE%" goto :loi_build

echo [4/4] Chay thu ban .exe vua tao (tu kiem tra, khong mo cua so) ...
if exist "%BAOCAO%" del "%BAOCAO%"
start "" /wait "%EXE%" --kiem-tra "%BAOCAO%"
set "KQ=%errorlevel%"
echo.
if exist "%BAOCAO%" (type "%BAOCAO%") else (echo [LOI] Ban .exe khong ghi duoc bao cao tu kiem tra.)
if not exist "%BAOCAO%" goto :loi_kiemtra
if not "%KQ%"=="0" goto :loi_kiemtra

echo.
echo ==========================================================
echo  THANH CONG: %~dp0%EXE%
echo  Bam dup MuxStudio.exe de chay. Muon de cho khac (vd Desktop):
echo  chep CA thu muc dist\MuxStudio, roi tao shortcut toi MuxStudio.exe.
echo ==========================================================
start "" explorer "%~dp0dist\MuxStudio"
pause
exit /b 0

:loi_python
echo.
echo [LOI] Khong tao duoc moi truong Python. Hay cai Python 3.11+ tu python.org
echo       (nho tick "Add python.exe to PATH"), roi chay lai file nay.
goto :het

:loi_phienban
echo.
echo [LOI] Can Python 3.11 tro len. Cai Python moi tu python.org,
echo       xoa thu muc .venv-build, roi chay lai file nay.
goto :het

:loi_pip
echo.
echo [LOI] Khong tai duoc thu vien (PyInstaller, customtkinter...). Kiem tra mang Internet
echo       roi chay lai file nay.
goto :het

:loi_dangmo
echo.
echo [LOI] MuxStudio.exe dang mo. Dong MuxStudio roi chay lai file nay.
goto :het

:loi_build
echo.
echo [LOI] PyInstaller bao loi (xem cac dong ngay phia tren). Chup man hinh cua so nay
echo       gui cho nguoi ho tro. Neu phan mem diet virus vua chan/xoa file trong thu muc
echo       build hoac dist: cho phep thu muc nay roi chay lai.
goto :het

:loi_kiemtra
echo.
echo [LOI] Da tao MuxStudio.exe nhung tu kiem tra KHONG DAT (xem cac dong [LOI] o tren).
echo       Gui file kiem_tra_exe.txt cho nguoi ho tro. Neu Windows Defender / phan mem
echo       diet virus chan MuxStudio.exe: xem HUONG_DAN.md, muc "Bi chan khi chay".
goto :het

:het
echo.
pause
exit /b 1
