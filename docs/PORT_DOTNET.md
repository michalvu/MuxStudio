# Hướng dẫn port MuxStudio sang C# / WPF

Tiến độ thực tế: [`HANDOFF_DOTNET.md`](HANDOFF_DOTNET.md). Quy ước khi viết code: [`../dotnet/CLAUDE.md`](../dotnet/CLAUDE.md).

## 0. Nền tảng: .NET 10 (LTS)

- .NET 9 đã hết hỗ trợ từ 10/11/2026. .NET 10 là bản LTS, được hỗ trợ đến 11/2028.
- Mọi project dùng `net10.0-windows` (đặt trong `dotnet/Directory.Build.props`).
- Máy phát triển cần .NET SDK 10. Máy đích không cần cài .NET, vì bản phát hành là self-contained.

## 1. Vì sao port khá "sạch"

- `core/` không import tkinter, và chỉ dùng 3 thư viện ngoài: `requests`, `keyring`, `send2trash`.
- Mỗi bước theo mẫu **lập kế hoạch → thực thi** (`FetchSession`, `PrepSession`, `MuxSession`). Mẫu này khớp gần 1-1 với MVVM.
- Có hơn 700 test, cùng dữ liệu mẫu (`tests/data`, `reference/*.bat`) làm "đáp án" để so bản C# với bản Python.
- Khối lượng: khoảng 10.000 dòng `core/` và 6.600 dòng `gui/`. Phần GUI sẽ được **viết lại** theo WPF chứ không dịch từng dòng.

## 2. Cấu trúc

```
MuxStudio/                         (cùng repo; Python giữ nguyên ở gốc để đối chiếu)
└─ dotnet/
   ├─ MuxStudio.slnx
   ├─ Directory.Build.props        net10.0-windows, Nullable, AnalysisLevel latest
   ├─ .editorconfig                CA1305/CA1304/CA1310 = lỗi
   ├─ CLAUDE.md
   ├─ src/MuxStudio.Core/          ← core/*.py (class library, KHÔNG tham chiếu WPF)
   ├─ src/MuxStudio.App/           ← gui/*.py viết lại (WPF + WPF-UI + CommunityToolkit.Mvvm)
   └─ tests/MuxStudio.Core.Tests/  ← tests/*.py (xUnit)
```

## 3. Ánh xạ Python → C#

| Python | C# / .NET | Ghi chú |
|---|---|---|
| `requests` (tmdb, tvdb, opensubs, subsource, titulky) | một `HttpClient` dùng chung + `System.Net.Http.Json` | Titulky cần đăng nhập: `CookieContainer`. Test: `HttpMessageHandler` giả |
| `keyring` (`secrets.py`) | `Secrets.cs`: P/Invoke `CredReadW/CredWriteW/CredDeleteW` | Chạy **cùng thuật toán** WinVaultKeyring: target `MuxStudio`, mục cũ ở `<tên>@MuxStudio`, UTF-16. Key bản Python đã lưu vẫn đọc được |
| `send2trash` (`undo.py`) | `SHFileOperationW` (`FOF_ALLOWUNDO`, im lặng) | Không dùng `Microsoft.VisualBasic.FileSystem.DeleteFile`, vì hàm này có thể bật hộp thoại lỗi |
| `subprocess.Popen` + `CREATE_NO_WINDOW` | `Process` + `ArgumentList`, `CreateNoWindow = true`, `StandardOutputEncoding = UTF8` | `ArgumentList` tự xử lý ngoặc kép, kể cả đường dẫn có `[ ]` |
| `Job` (`stop_now`, `cancel_now`, `Cancelled`) | `CancellationToken` + `OperationCanceledException` + `IProgress<T>` | `Kill(entireProcessTree: true)` diệt cả tiến trình con của ffmpeg/alass |
| `winreg` (tìm MKVToolNix) | `Microsoft.Win32.Registry` | |
| `json` + giữ khoá lạ | `PyJson` + `Config` | `config.json` giống từng byte bản Python |
| `csv` (nhật ký hoàn tác) | `PyCsv` | Giữ đúng định dạng cũ để hoàn tác được cả lượt chạy từ bản Python |
| `zipfile` | `System.IO.Compression.ZipFile` | |
| `html.unescape` + regex (titulky) | `WebUtility.HtmlDecode` + regex | Giữ regex để port sát |
| `fractions.Fraction` (`donor.py`) | tự viết `readonly struct Fraction` (long + GCD) | TĐ-34 cần phân số chính xác, **không** được dùng `double` |
| `bisect` | `Timing.BisectLeft/Right` | |
| `dict` (giữ thứ tự) | `OrderedDictionary<K,V>` | |
| `Pillow` | `BitmapImage` của WPF | Ảnh captcha, icon |
| `customtkinter` + `glass.json` + DWM ctypes | **WPF-UI** (Mica/Acrylic, `NavigationView`, Sáng/Tối theo hệ thống) | Không phải tự gọi `DwmSetWindowAttribute` |

## 4. Bẫy khi port

Bảng đầy đủ nằm trong `dotnet/CLAUDE.md`. Những bẫy quan trọng nhất:

1. **Số thập phân:** máy dùng locale tiếng Séc, nên mọi số truyền cho ffmpeg/mkvmerge đi qua `Timing.FmtInv`. Analyzer CA1305 đặt ở mức lỗi.
2. **Làm tròn:** `Math.Round(x)` làm tròn về số chẵn (giống `round()` của Python). `(int)x` thì cắt bỏ phần lẻ.
3. **Xuống dòng và BOM:** C# không tự đổi `\n` thành `\r\n`, và `Encoding.UTF8` có ghi BOM. Dùng `Py.Utf8NoBom` và ghi `\r\n` tường minh.
4. **Bảng mã cũ:** `TextEncoding` tự đăng ký `CodePagesEncodingProvider`.
5. **Regex:** cần để ý `\G` (thay `match(s, i)`), `\z` (thay `\Z` và `fullmatch`), `(?<x>)` (thay `(?P<x>)`), `$1` (thay `\1`), và `[\p{L}\p{N}_]` (thay `\w` khi cần giống hệt Python).
6. **i18n:** giữ cơ chế `Tr(câu tiếng Việt)` dò ngược mẫu, **không** chuyển sang `.resx`. Toàn bộ trạng thái và nhật ký hoàn tác đang dựa vào chuỗi tiếng Việt. Trong XAML sẽ dùng markup extension `{l:Tr 'Tải phụ đề'}` (GĐ5).
7. Bản WPF không còn các lỗi riêng của Tk (`safe_destroy`, crash `update_idletasks`), nên **không** port phần chống lỗi đó.

## 5. Giúp app mượt và native hơn (GĐ5)

- **Luồng phụ:** thay `Runner` (Thread + queue + `after(100)`) bằng `async`/`await` + `Task.Run` + `IProgress<T>`. Nút Dừng chỉ cần gọi `cts.Cancel()`.
  ```csharp
  [RelayCommand(IncludeCancelCommand = true)]
  private async Task RunMuxAsync(CancellationToken ct) =>
      await Task.Run(() => _session.Execute(Plans, _log, _progress, ct), ct);
  ```
- **Khung log:** `ListBox` có virtualization, mỗi dòng mang một mức (info/ok/warn/error) và tô màu bằng `DataTemplate`. Gom log 50–100 ms một lần rồi mới thêm vào danh sách. **Đừng** dùng `RichTextBox`.
- **Bảng:** `DataGrid` bật `EnableRowVirtualization`, bind vào `ObservableCollection` của ViewModel dùng `[ObservableProperty]`.
- **DPI:** PerMonitorV2 đã khai báo trong `app.manifest`.
- **Khởi động nhanh:** bật `PublishReadyToRun`. WPF chưa hỗ trợ Native AOT.

## 6. Thứ tự port

Mỗi module gồm: port code → port test → `dotnet test` xanh → commit.

| GĐ | Module | Test đối chiếu |
|---|---|---|
| 1 | `timing pgs srt encoding episodes naming config secrets undo i18n` | `test_timing`, `test_srt`… (✅ xong) |
| 2 | `tools probe tmdb tvdb subsource opensubs titulky subfetch` + phần database của `naming` | `fakehttp.py` → `HttpMessageHandler` giả; `tests/data/titulky/*.html`; `test_naming_db` |
| 3 | `subprep` | `fake_alass.py`: gọi `python fake_alass.py` qua Process, hoặc viết bản C# |
| 4 | `mux donor` | `test_mux`, `test_golden_mux` |
| 5 | GUI: `MainWindow` (NavigationView 5 thẻ) → Log → Cài đặt → ①②③ → Giới thiệu | chạy tay + test ViewModel |
| 6 | Đóng gói, `--kiem-tra`, `loi_khoi_dong.txt` (bắt `DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) | |

**Mẹo đối chiếu:** với `build_args(plan)` (mux) và `preview_commands` (subprep), cho cả hai bản in danh sách tham số trên cùng một thư mục phim mẫu, rồi so từng chuỗi. Nếu tham số mkvmerge/ffmpeg giống nhau thì file đầu ra cũng giống nhau.

**Đóng gói** (thay `build.bat` + PyInstaller):
```powershell
dotnet publish src/MuxStudio.App -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true
dotnet publish src/MuxStudio.App -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

## 7. Làm việc cùng Claude trong VS Code

1. Mở thư mục repo trong VS Code. Mỗi phiên chỉ giao **một giai đoạn hoặc một module**, ví dụ: *"Đọc docs/HANDOFF_DOTNET.md rồi port GĐ2 (tools + probe) kèm test"*.
2. Việc lớn thì bật Plan mode (Shift+Tab) để xem kế hoạch trước.
3. Xong module nào thì commit module đó. Xong giai đoạn nào thì cập nhật `docs/HANDOFF_DOTNET.md`, push, rồi `/clear` để bắt đầu phiên mới.
