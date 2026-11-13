# MuxStudio

Phần mềm Windows giúp chuẩn bị phim cho thư viện Jellyfin / Emby, gộp ba việc vào một cửa sổ:

1. **Tải phụ đề** — tìm và tải phụ đề nhiều ngôn ngữ từ SubSource, Titulky.com (tiếng Séc) và OpenSubtitles.
2. **Chuẩn bị phụ đề** — đổi tên cho khớp với phim, đồng bộ thời gian bằng alass (theo phụ đề gốc hoặc theo audio của phim).
3. **Mux** — dọn track audio / phụ đề thừa, ghép phụ đề và audio từ file khác, đặt tên chuẩn Jellyfin theo TMDB / TVDB, xuất file MKV.

Mọi thao tác đổi tên / xoá đều hoàn tác được (nút **Hoàn tác…**); file bị xoá đi vào Thùng rác (mặc định).

**Tác giả:** Vũ Hoàng Anh — © 2026. Phần mềm và dịch vụ của bên khác mà MuxStudio dùng (cùng giấy phép của chúng) được liệt kê ở thẻ **Giới thiệu** trong chương trình.

## Cài đặt

Bản dựng sẵn (`MuxStudio.exe`) chưa phát hành: tự đóng gói bằng `dotnet\build.bat` (mục 2), hoặc chạy từ mã nguồn (mục 3).

### 1. Cần có

- Windows 10 hoặc 11 (x64 hoặc ARM64).
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Công cụ ngoài (MuxStudio chỉ gọi, không đi kèm):
  - [MKVToolNix](https://mkvtoolnix.download) (`mkvmerge`, `mkvextract`) — **bắt buộc**.
  - [alass](https://github.com/kaegi/alass) — **bắt buộc** cho bước đồng bộ phụ đề (bản Windows thường kèm sẵn `ffmpeg` / `ffprobe`).
  - [FFmpeg](https://ffmpeg.org) (`ffmpeg`, `ffprobe`) — nên có: đồng bộ theo audio, đọc bitrate / HDR, đổi tốc độ audio ghép.

### 2. Tải mã nguồn và dựng

```powershell
git clone -b dotnet-gui https://github.com/michalvu/MuxStudio.git
cd MuxStudio\dotnet
dotnet build MuxStudio.slnx -c Release
```

**Đóng gói thành một file `MuxStudio.exe`** (chạy được trên máy không cài .NET): bấm đúp `dotnet\build.bat`. Kết quả:
`dotnet\dist\win-x64\MuxStudio.exe` (máy Intel / AMD) và `dotnet\dist\win-arm64\MuxStudio.exe` (Windows on ARM), mỗi bản kèm
`kiem_tra_MuxStudio.bat` (tự kiểm tra). Chi tiết: [HUONG_DAN.md](HUONG_DAN.md), mục 1.

### 3. Mở chương trình

```powershell
dotnet run --project src\MuxStudio.App -c Release
```

hoặc bấm đúp file đã dựng: `dotnet\src\MuxStudio.App\bin\Release\net10.0-windows\MuxStudio.exe` (hoặc bản đóng gói ở `dotnet\dist\…`)
(có thể truyền sẵn thư mục phim: `MuxStudio.exe "D:\Phim\Tên phim"`).

### 4. Thiết lập lần đầu (trong chương trình)

- **Cài đặt › Chung — thư mục & công cụ:** bấm **Tự tìm** (hoặc **Chọn…**) cho từng công cụ ở mục *Công cụ ngoài*.
- **Cài đặt › API & tài khoản:** điền key SubSource, TMDB, TVDB, OpenSubtitles (tài khoản Titulky.com không bắt buộc).
  Key và mật khẩu được lưu trong Windows Credential Manager, không ghi vào file cấu hình.
- Bấm **Lưu cài đặt**, chọn thư mục phim ở đầu cửa sổ, rồi làm lần lượt ① → ② → ③.
