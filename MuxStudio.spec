# -*- mode: python ; coding: utf-8 -*-
# PyInstaller: dung MuxStudio.exe dang THU MUC (--onedir), KHONG cua so den (--windowed). KHONG dung --onefile
# (PROMPT §3: khoi dong cham, hay bi phan mem diet virus bao nham). Chay bang build.bat (Windows), hoac:
#     python -m PyInstaller --noconfirm --clean MuxStudio.spec
# Ket qua: dist/MuxStudio/MuxStudio.exe + dist/MuxStudio/_internal/ (thu vien, Python, theme CustomTkinter, icon).
# Khong dong goi tests/, reference/, tools/ (script do API van chay bang chay_do_api.bat).
import os
import sys

from PyInstaller.utils.hooks import collect_data_files, collect_submodules, copy_metadata

ROOT = os.path.abspath(SPECPATH)  # noqa: F821 - PyInstaller dat san
sys.path.insert(0, ROOT)
from core import __version__ as VERSION  # noqa: E402

IS_WIN = sys.platform.startswith("win")
ICON = os.path.join(ROOT, "assets", "muxstudio.ico")

datas = [
    (os.path.join(ROOT, "assets", "muxstudio.ico"), "assets"),
    (os.path.join(ROOT, "assets", "muxstudio.png"), "assets"),
    (os.path.join(ROOT, "assets", "glass.json"), "assets"),  # bo mau "kinh" (1.2.0)
]
datas += collect_data_files("customtkinter")  # theme .json + phong hinh: thieu thi cua so khong mo duoc
datas += copy_metadata("keyring")  # keyring tim "backend" (Credential Manager) qua entry points cua goi

hiddenimports = collect_submodules("keyring.backends")
if IS_WIN:
    # keyring tren Windows: pywin32-ctypes (win32ctypes) nap module con theo kieu dong -> khai bao ro
    hiddenimports += ["keyring.backends.Windows", "win32ctypes.core", "win32ctypes.pywin32.win32cred",
                      "win32ctypes.pywin32.pywintypes"] + collect_submodules("win32ctypes.core.ctypes")
    # Thung rac: send2trash.win.modern can pywin32 (khong co) -> dung ban legacy (ctypes)
    hiddenimports += ["send2trash.win", "send2trash.win.legacy"]

# Thu vien chi dung khi test / khong dung. PIL (Pillow) KHONG con bi loai tu ban 1.3.0: can de hien anh CAPTCHA (JPEG)
# cua Titulky.com - Tk khong tu doc duoc JPEG.
excludes = ["pytest", "_pytest", "pluggy", "numpy", "IPython", "matplotlib"]


def version_file() -> str:
    """Thong tin phien ban cua file .exe (chuot phai > Properties > Details; Task Manager hien "MuxStudio")."""
    nums = tuple(int(x) for x in (VERSION.split(".") + ["0", "0", "0"])[:4])
    text = f"""VSVersionInfo(
  ffi=FixedFileInfo(filevers={nums}, prodvers={nums}, mask=0x3f, flags=0x0, OS=0x40004, fileType=0x1,
                    subtype=0x0, date=(0, 0)),
  kids=[
    StringFileInfo([StringTable('040904B0', [
      StringStruct('CompanyName', 'MuxStudio'),
      StringStruct('FileDescription', 'MuxStudio'),
      StringStruct('FileVersion', '{VERSION}'),
      StringStruct('InternalName', 'MuxStudio'),
      StringStruct('OriginalFilename', 'MuxStudio.exe'),
      StringStruct('ProductName', 'MuxStudio'),
      StringStruct('ProductVersion', '{VERSION}'),
      StringStruct('Comments', 'Tai phu de, dong bo phu de, mux MKV chuan Jellyfin')])]),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
"""
    d = globals().get("workpath") or os.path.join(ROOT, "build")
    os.makedirs(d, exist_ok=True)
    p = os.path.join(d, "muxstudio_version.txt")
    with open(p, "w", encoding="utf-8") as f:
        f.write(text)
    return p


a = Analysis(  # noqa: F821
    [os.path.join(ROOT, "MuxStudio.pyw")],
    pathex=[ROOT],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=excludes,
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)  # noqa: F821

exe = EXE(  # noqa: F821
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="MuxStudio",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,  # UPX hay bi phan mem diet virus bao nham
    console=False,  # --windowed: khong cua so den
    disable_windowed_traceback=False,
    icon=ICON if IS_WIN else None,
    version=version_file() if IS_WIN else None,
)
coll = COLLECT(  # noqa: F821
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="MuxStudio",
)

# Canh MuxStudio.exe: kiem_tra_MuxStudio.bat (bam dup -> tu kiem tra + mo bao cao bang Notepad, gui kem khi bao loi)
import shutil  # noqa: E402

shutil.copy2(os.path.join(ROOT, "assets", "kiem_tra_MuxStudio.bat"),
             os.path.join(DISTPATH, "MuxStudio", "kiem_tra_MuxStudio.bat"))  # noqa: F821
