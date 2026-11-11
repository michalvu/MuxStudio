@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ==============================================================
echo   MuxStudio - DO TITULKY.COM (nguon phu de tieng Sec)
echo ==============================================================
echo.
echo Script nay chay dung phan ma ma MuxStudio dung de tim va tai phu de tu
echo Titulky.com, voi tai khoan cua ban, de xac nhan no chay dung:
echo   - dang nhap, tim, xem chi tiet, va TAI THU MOT phu de (ton 1 luot tai/ngay);
echo   - gap CAPTCHA: anh duoc mo ra, ban go ma vao cua so nay.
echo Ten dang nhap, mat khau va cookie KHONG duoc ghi vao file ket qua.
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
".venv\Scripts\python.exe" -m tools.probe_titulky %*
echo.
echo Xong. Gui file ket_qua_do_titulky.txt (nam canh file nay) cho Claude.
pause
exit /b

:loi
echo.
echo [LOI] Co loi o tren. Neu chua cai Python: cai Python 3.11+ tu python.org
echo       (nho tick "Add python.exe to PATH"), roi chay lai file nay.
pause
exit /b 1
