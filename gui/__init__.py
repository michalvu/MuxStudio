"""Giao diện MuxStudio (CustomTkinter). Chạy: ``python -m gui.app``.

Lõi (``core/``) không import tkinter; ở đây mỗi module chỉ nạp tkinter khi dựng cửa sổ, nên các hàm
logic của giao diện (dựng dòng bảng, mô hình bảng chọn phụ đề, bộ chạy luồng phụ…) test được cả trên
máy không có tkinter.
"""
