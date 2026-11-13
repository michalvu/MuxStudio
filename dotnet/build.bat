@echo off
chcp 65001 >nul
cd /d "%~dp0"
setlocal
rem ==========================================================
rem  Dong goi MuxStudio (ban .NET) thanh MuxStudio.exe (Giai doan 6).
rem  - Can .NET 10 SDK de BUILD: https://dotnet.microsoft.com/download/dotnet/10.0
rem    Ban .exe tao ra chay KHONG can cai .NET (self-contained, mot file).
rem  - Ket qua (moi thu muc: MuxStudio.exe + kiem_tra_MuxStudio.bat):
rem      dist\win-x64\    may Windows thuong (Intel / AMD)
rem      dist\win-arm64\  may Windows on ARM (vd Snapdragon X)
rem  - Lan dau mat vai phut (tai thu vien .NET cho ca hai loai may).
rem ==========================================================
set "BAOCAO=%~dp0kiem_tra_exe.txt"
set "ARCH=x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "ARCH=arm64"
set "EXE=dist\win-%ARCH%\MuxStudio.exe"

echo [1/4] Kiem tra .NET 10 SDK ...
where dotnet >nul 2>nul
if errorlevel 1 goto :loi_sdk
dotnet --list-sdks | findstr /b /c:"10." >nul
if errorlevel 1 goto :loi_sdk

echo [2/4] Kiem tra MuxStudio.exe cu co dang mo khong ...
tasklist /fi "imagename eq MuxStudio.exe" /nh 2>nul | findstr /i /c:"MuxStudio.exe" >nul
if not errorlevel 1 goto :loi_dangmo

echo [3/4] Dong goi ban win-x64 va win-arm64 (1-3 phut) ...
for %%R in (win-x64 win-arm64) do (
    if exist "dist\%%R" rmdir /s /q "dist\%%R"
    dotnet publish src\MuxStudio.App -c Release -p:PublishProfile=%%R --nologo
    if errorlevel 1 goto :loi_build
)
if not exist "%EXE%" goto :loi_build

echo [4/4] Chay thu ban win-%ARCH% vua tao (tu kiem tra, khong mo cua so) ...
if exist "%BAOCAO%" del "%BAOCAO%"
start "" /wait "%EXE%" --kiem-tra "%BAOCAO%"
set "KQ=%errorlevel%"
echo.
if exist "%BAOCAO%" (type "%BAOCAO%") else (echo [LOI] Ban .exe khong ghi duoc bao cao tu kiem tra.)
if not exist "%BAOCAO%" goto :loi_kiemtra
if not "%KQ%"=="0" goto :loi_kiemtra

echo.
echo ==========================================================
echo  THANH CONG:
echo    %~dp0dist\win-x64\MuxStudio.exe
echo    %~dp0dist\win-arm64\MuxStudio.exe
echo  Chep MuxStudio.exe (kem kiem_tra_MuxStudio.bat) dung loai may
echo  toi noi ban muon, vd Desktop, roi bam dup de chay.
echo ==========================================================
start "" explorer "%~dp0dist\win-%ARCH%"
pause
exit /b 0

:loi_sdk
echo.
echo [LOI] Chua co .NET 10 SDK. Cai tu https://dotnet.microsoft.com/download/dotnet/10.0
echo       (muc SDK, ban x64 hoac Arm64 dung voi may), roi chay lai file nay.
goto :het

:loi_dangmo
echo.
echo [LOI] MuxStudio.exe dang mo. Dong MuxStudio roi chay lai file nay.
goto :het

:loi_build
echo.
echo [LOI] dotnet publish bao loi (xem cac dong ngay phia tren). Chup man hinh cua so nay
echo       gui cho nguoi ho tro. Lan dau can mang Internet de tai thu vien .NET. Neu phan
echo       mem diet virus vua chan/xoa file trong thu muc dist: cho phep thu muc nay roi
echo       chay lai.
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
