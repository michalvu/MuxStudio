# MuxStudio

Phần mềm Windows (Python + CustomTkinter) thay cho ba công cụ dòng lệnh `SubFetch`, `SubPrep` và `MuxTool`:
tải phụ đề → đồng bộ phụ đề → dọn track và mux MKV, đặt tên chuẩn Jellyfin.

**Trạng thái:** Giai đoạn 5 xong — đã có **giao diện** (4 thẻ ① Tải phụ đề → ② Chuẩn bị phụ đề → ③ Mux → Cài đặt,
khung log màu, nút Dừng, màn hình Hoàn tác) trên lõi của cả ba bước — **đã duyệt**. Tiếp theo: Giai đoạn 6 (bản `.exe`
không cần cài Python).

- **Thử giao diện trên Windows (cần Python 3.11+):** bấm đúp `chay_muxstudio.bat`. Lần đầu mất vài phút để tạo thư mục
  `.venv` và tải thư viện; các lần sau mở ngay.
- **Dò API với key thật:** bấm đúp `chay_do_api.bat`, nhập thư mục chứa 3 file .bat cũ (để lấy key), rồi gửi file
  `ket_qua_do_api.txt` (key, token, mật khẩu đã được che).
- Chạy test trên Windows (tuỳ chọn, cần Python 3.11+): bấm đúp `run_tests.bat`.

- Yêu cầu gốc: [`docs/PROMPT_MuxStudio.md`](docs/PROMPT_MuxStudio.md)
- Đặc tả: [`docs/SPEC.md`](docs/SPEC.md)
- Bàn giao giữa các phiên: [`docs/HANDOFF.md`](docs/HANDOFF.md)
- Script kiểm chứng các lỗi tìm thấy trong .bat: [`tools/verify_bat/KiemChung_LoiBat.ps1`](tools/verify_bat/KiemChung_LoiBat.ps1)
- Bản sao .bat đã xoá bí mật (để test đối chiếu): [`reference/`](reference/) — tạo bằng `tools/sanitize_bat.py`
