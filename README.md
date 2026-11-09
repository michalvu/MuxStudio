# MuxStudio

Phần mềm Windows (Python + CustomTkinter) thay cho ba công cụ dòng lệnh `SubFetch`, `SubPrep` và `MuxTool`:
tải phụ đề → đồng bộ phụ đề → dọn track và mux MKV, đặt tên chuẩn Jellyfin.

**Trạng thái:** **hoàn thành — đã duyệt cả 7 giai đoạn (0–6)**. Phiên bản **1.0.0**: bản `MuxStudio.exe` (không cần cài
Python) tạo bằng `build.bat`; giao diện 4 thẻ ① Tải phụ đề → ② Chuẩn bị phụ đề → ③ Mux → Cài đặt, khung log màu, nút
Dừng, màn hình Hoàn tác.

**Bắt đầu từ đây: [`HUONG_DAN.md`](HUONG_DAN.md)** — tạo bản `.exe`, công cụ cần có, cấu hình lần đầu, quy trình
① → ② → ③, báo lỗi, xử lý khi Windows / phần mềm diệt virus chặn.

- **Tạo `MuxStudio.exe`:** bấm đúp `build.bat` (cần Python 3.11+ để build) → `dist\MuxStudio\MuxStudio.exe`. Chép
  **cả** thư mục `dist\MuxStudio` đi đâu cũng được.
- **Chạy từ mã nguồn (không build):** bấm đúp `chay_muxstudio.bat`.
- **Tự kiểm tra bản `.exe`** (gửi kèm khi báo lỗi): bấm đúp `kiem_tra_MuxStudio.bat` cạnh `MuxStudio.exe`.
- **Dò API với key thật:** bấm đúp `chay_do_api.bat`, nhập thư mục chứa 3 file .bat cũ (để lấy key), rồi gửi file
  `ket_qua_do_api.txt` (key, token, mật khẩu đã được che).
- Chạy test trên Windows (tuỳ chọn, cần Python 3.11+): bấm đúp `run_tests.bat`.

- Yêu cầu gốc: [`docs/PROMPT_MuxStudio.md`](docs/PROMPT_MuxStudio.md)
- Đặc tả: [`docs/SPEC.md`](docs/SPEC.md)
- Bàn giao giữa các phiên: [`docs/HANDOFF.md`](docs/HANDOFF.md)
- Script kiểm chứng các lỗi tìm thấy trong .bat: [`tools/verify_bat/KiemChung_LoiBat.ps1`](tools/verify_bat/KiemChung_LoiBat.ps1)
- Bản sao .bat đã xoá bí mật (để test đối chiếu): [`reference/`](reference/) — tạo bằng `tools/sanitize_bat.py`
