"""Điểm vào của MuxStudio.

* Bản đóng gói: PyInstaller dựng ``MuxStudio.exe`` từ file này (nhánh ``dotnet-port`` đã bỏ phần đóng gói — bản .exe
  mới do bản C# trong ``dotnet/`` tạo).
* Từ mã nguồn: bấm đúp file này (Windows mở bằng ``pythonw``, không có cửa sổ đen) hoặc ``python -m gui.app``.
* ``MuxStudio.exe --kiem-tra [báo cáo.txt]``: tự kiểm tra bản đóng gói rồi thoát (mã 0 = đạt).
"""
import os
import sys

if not getattr(sys, "frozen", False):  # chay tu ma nguon tu thu muc khac: van tim thay core/ va gui/
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from gui.app import main  # noqa: E402

if __name__ == "__main__":
    raise SystemExit(main())
