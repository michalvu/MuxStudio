@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem ==========================================================
rem  Tu kiem tra MuxStudio.exe (thu vien, Credential Manager, Thung rac,
rem  chung chi HTTPS, cong cu ngoai, dung thu cua so).
rem  Bao cao: %APPDATA%\MuxStudio\kiem_tra.txt - tu mo bang Notepad.
rem  Gui file bao cao nay kem khi bao loi.
rem ==========================================================
set "BAOCAO=%APPDATA%\MuxStudio\kiem_tra.txt"
if not exist "%~dp0MuxStudio.exe" goto :khong_thay
echo Dang kiem tra MuxStudio.exe ...
if exist "%BAOCAO%" del "%BAOCAO%"
start "" /wait "%~dp0MuxStudio.exe" --kiem-tra "%BAOCAO%"
if not exist "%BAOCAO%" goto :khong_chay
start "" notepad "%BAOCAO%"
exit /b 0

:khong_thay
echo [LOI] Khong thay MuxStudio.exe canh file nay. Dat file nay cung thu muc voi MuxStudio.exe.
pause
exit /b 1

:khong_chay
echo [LOI] MuxStudio.exe khong chay duoc. Xem file %APPDATA%\MuxStudio\loi_khoi_dong.txt
echo       va muc "Bi chan khi chay" trong HUONG_DAN.md.
pause
exit /b 1
