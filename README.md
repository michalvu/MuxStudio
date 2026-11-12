# MuxStudio

Phần mềm Windows (Python + CustomTkinter) thay cho ba công cụ dòng lệnh `SubFetch`, `SubPrep` và `MuxTool`:
tải phụ đề → đồng bộ phụ đề → dọn track và mux MKV, đặt tên chuẩn Jellyfin.

> **Nhánh `dotnet-port`:** đang viết lại MuxStudio bằng C# / WPF (.NET 10) trong thư mục [`dotnet/`](dotnet/).
> Mã Python (`core/`, `gui/`, `tests/`) giữ lại làm **bản chuẩn để đối chiếu**. Phần đóng gói PyInstaller và các script
> dò API đã bỏ khỏi nhánh này (vẫn còn ở nhánh `claude/new-session-eckanp`). Tiến độ: [`docs/HANDOFF_DOTNET.md`](docs/HANDOFF_DOTNET.md),
> hướng dẫn port: [`docs/PORT_DOTNET.md`](docs/PORT_DOTNET.md).

**Trạng thái bản Python:** **hoàn thành — đã duyệt cả 7 giai đoạn (0–6)**. Phiên bản **1.3.3**: giao diện 5 thẻ ① Tải phụ đề → ② Chuẩn bị phụ đề → ③ Mux → Cài đặt → Giới thiệu, hai ngôn ngữ (Tiếng Việt / English),
bộ màu kiểu kính Sáng / Tối, khung log màu, nút Dừng, màn hình Hoàn tác.

Tác giả: **Vũ Hoàng Anh**. Phần mềm của bên khác mà MuxStudio dùng (và giấy phép của chúng): xem thẻ **Giới thiệu**.

**Hướng dẫn dùng bản Python: [`HUONG_DAN.md`](HUONG_DAN.md)** — công cụ cần có, cấu hình lần đầu, quy trình
① → ② → ③, báo lỗi.

- **Chạy bản Python từ mã nguồn:** bấm đúp `chay_muxstudio.bat`.
- Chạy test Python trên Windows (tuỳ chọn, cần Python 3.11+): bấm đúp `run_tests.bat`.
- Bản C#: `cd dotnet`, `dotnet build MuxStudio.slnx`, `dotnet test MuxStudio.slnx` (xem [`dotnet/CLAUDE.md`](dotnet/CLAUDE.md)).

- Đặc tả: [`docs/SPEC.md`](docs/SPEC.md)
- Bản sao .bat đã xoá bí mật (để test đối chiếu): [`reference/`](reference/)
