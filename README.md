# MuxStudio

Phần mềm Windows (Python + CustomTkinter) thay cho ba công cụ dòng lệnh `SubFetch`, `SubPrep` và `MuxTool`:
tải phụ đề → đồng bộ phụ đề → dọn track và mux MKV, đặt tên chuẩn Jellyfin.

**Trạng thái:** Giai đoạn 1 xong — lõi dùng chung (`core/`) + 298 test. Chờ duyệt trước khi sang Giai đoạn 2.
Chưa có giao diện (Giai đoạn 5).

Chạy test trên Windows (tuỳ chọn, cần Python 3.11+): bấm đúp `run_tests.bat`.

- Yêu cầu gốc: [`docs/PROMPT_MuxStudio.md`](docs/PROMPT_MuxStudio.md)
- Đặc tả: [`docs/SPEC.md`](docs/SPEC.md)
- Script kiểm chứng các lỗi tìm thấy trong .bat: [`tools/verify_bat/KiemChung_LoiBat.ps1`](tools/verify_bat/KiemChung_LoiBat.ps1)
- Bản sao .bat đã xoá bí mật (để test đối chiếu): [`reference/`](reference/) — tạo bằng `tools/sanitize_bat.py`
