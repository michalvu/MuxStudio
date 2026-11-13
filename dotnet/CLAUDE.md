# MuxStudio .NET (WPF, .NET 10) — quy ước cho Claude

MuxStudio bản C# (port từ bản Python cũ — đã xong 6 giai đoạn, 13/11/2026). **Bản Python đã bị xoá khỏi repo** (13/11/2026, theo yêu cầu
người dùng; lịch sử còn ở nhánh `claude/new-session-eckanp`). Tiến độ và việc tiếp theo: [`docs/HANDOFF_DOTNET.md`](../docs/HANDOFF_DOTNET.md).
Đặc tả tính năng (viết thời bản Python, code C# vẫn trỏ tới): [`docs/SPEC.md`](../docs/SPEC.md).

- `dotnet/` tự đủ: icon ở `src/MuxStudio.App/Assets/`, dữ liệu test (3 file .bat gốc, trang Titulky mẫu, ca đối chiếu `python_*.json`) ở
  `tests/MuxStudio.Core.Tests/Data/`. Đừng thêm tham chiếu tới file ngoài `dotnet/`.
- Các mục dưới đây nhắc tới Python (tên hàm "port …", bảng bẫy Python → C#) là lịch sử để hiểu code. `Data/python_*.json` là dữ liệu CỐ
  ĐỊNH (do bản Python xuất, không sinh lại được nữa): đừng sửa cho test xanh trừ khi CỐ Ý đổi hành vi.

## Lệnh

```powershell
cd dotnet
dotnet build MuxStudio.slnx            # phải 0 lỗi, 0 cảnh báo
dotnet test  MuxStudio.slnx            # phải xanh hết
dotnet run --project src/MuxStudio.App # chạy app
build.bat                              # đóng gói: dist\win-x64\ + dist\win-arm64\ (MuxStudio.exe một file + kiem_tra_MuxStudio.bat), rồi tự kiểm tra
dotnet publish src/MuxStudio.App -c Release -p:PublishProfile=win-x64   # chỉ một bản (win-arm64 tương tự)
src\MuxStudio.App\bin\Debug\net10.0-windows\MuxStudio.exe --kiem-tra x.txt   # tự kiểm tra (mã thoát 0 = đạt)
```

## Quy tắc port

1. **Port sát, không tự thêm tính năng, không "sửa cho đẹp" logic.** Mỗi hàm C# ghi rõ nó port hàm Python nào. Chuỗi tiếng Việt (trạng thái, nhật ký, lỗi) giữ **nguyên văn**, vì i18n và nhật ký hoàn tác dựa vào chúng.
2. Mỗi module: port code → port test của nó (`tests/test_x.py` → `XTests.cs`) → `dotnet test` xanh → commit riêng.
3. Test đối chiếu với dữ liệu do Python xuất (`tests/MuxStudio.Core.Tests/Data/python_*.json`, sinh hồi còn bản Python bằng `tools/gen_dotnet_*.py` — đã xoá) giữ hành vi cũ: phải luôn xanh.
4. Test **không bao giờ** chạm Credential Manager thật, Thùng rác thật hay mạng thật. Dùng `MemorySecretBackend`, `FakeCredentialTable`, `Undo.SendToRecycleBin` giả (hoặc thuộc tính `Trash` của phiên) và máy chủ giả `FakeHttp` (truyền `fake.Client` vào client / phiên — quên truyền là gọi `Http.Shared` = mạng thật).
5. Test đụng state tĩnh dùng chung (bảng ngôn ngữ — kể cả chỉ đọc qua `Naming.LangMatch`/`Probe`, ngôn ngữ giao diện, biến môi trường như `PATH`, `Undo.SendToRecycleBin`) phải gắn `[Collection(GlobalStateCollection.Name)]` và trả state về như cũ.
6. `MuxStudio.Core` **không** tham chiếu WPF.

## Hiệu năng: nhẹ, gọn, mượt (yêu cầu của người dùng — không được đổi kết quả hay bỏ tính năng)

1. **Không chặn luồng giao diện.** Mọi việc của lõi (tiến trình con, đọc/ghi file, HTTP) chạy ở luồng phụ; lõi không biết tới Dispatcher. Log / tiến độ đi qua `IProgress<T>`; GĐ5 gom 50–100 ms một lần. Dịch log (`I18n.Tr`, đo được 60–130 µs/dòng) làm ở luồng phụ, trước khi đẩy sang giao diện.
2. **Regex:** luôn `[GeneratedRegex]`; không `new Regex(...)` lúc chạy (mẫu động thì dựng một lần rồi giữ lại).
3. **Không làm lại việc đắt:** giữ đủ bộ nhớ đệm của bản Python (`mkvmerge -J`, ffprobe, WAV, mốc thời gian…); không gọi lại tiến trình con / đọc lại file trong vòng lặp.
4. **Bảng tĩnh lớn:** `Lazy<T>` — dựng khi cần lần đầu, không dựng lúc mở app.
5. **Duyệt thư mục:** dùng thông tin `FileSystemInfo` đã có (thuộc tính, kích thước) trước khi gọi thêm API hệ thống cho từng mục.
6. **Tiến trình con:** đọc output bất đồng bộ (không kẹt), huỷ được qua `Job.Token`, diệt cả cây tiến trình.
7. **Không thêm gói NuGet** khi thư viện chuẩn làm được (bản phát hành nhỏ, khởi động nhanh).
8. **Đo trước khi tối ưu** (bản Release). Tối ưu xong, test đối chiếu Python phải vẫn xanh.

## Bẫy Python → C# đã gặp (giữ đúng)

| Python | C# |
|---|---|
| số ↔ chuỗi | luôn `CultureInfo.InvariantCulture` (máy dùng locale tiếng Séc; CA1305 = lỗi). Số cho ffmpeg/mkvmerge: `Timing.FmtInv`; đọc số: `Timing.ToDouble` |
| `round(x)` | `Math.Round(x)` (về số chẵn). **Không** dùng `(int)x` |
| `pattern.match(s, i)` | regex có `\G` ở đầu + `Match(s, i)` |
| `re.fullmatch` | `^…\z` (`$` của .NET khớp cả trước `\n` cuối) |
| `\Z` | `\z` |
| `\w`, `str.isalnum()` | `[\p{L}\p{N}_]`, `Py.IsAlnum` (Python coi ①, ² là chữ-số) |
| `re.I` | `RegexOptions.IgnoreCase \| RegexOptions.CultureInvariant` |
| `int("١٢")` (chữ số Unicode) | `Py.Int` / `Py.Long` |
| `dict` giữ thứ tự | `OrderedDictionary<K,V>` |
| `list.sort` (ổn định) | `OrderBy` (ổn định), **không** `List.Sort` khi có khoá bằng nhau |
| ghi `"utf-8"` / `"utf-8-sig"` | `Py.Utf8NoBom` / `new UTF8Encoding(true)` |
| text mode ghi `\n` → CRLF | C# không tự đổi: ghi `\r\n` tường minh (`Srt.NewLine`) |
| `json.dumps` / `json.loads` | `PyJson.Dumps` / `PyJson.Loads` (giữ `1.0`, thứ tự khoá) |
| `==` giữa giá trị cấu hình | `Py.Equal` (1 == 1.0, True == 1) |
| `csv` | `PyCsv` (QUOTE_ALL, CRLF, DictReader) |
| `keyring` | `Secrets` + `WinVaultBackend` (cùng target `MuxStudio` / `<tên>@MuxStudio`) |
| `send2trash` | `Undo.Trash` → `SHFileOperationW` |
| bảng mã cũ | `TextEncoding` (đã `RegisterProvider`; byte vùng riêng của Windows → mã byte như Python) |
| static field giữa 2 file partial | không có thứ tự bảo đảm → dùng `Lazy<T>` |
| `os.path.join` | `Py.Join` (**không** `Path.Combine`: `Combine("C:", "x")` = `C:\x`, Python ra `C:x`) |
| `str(Path(...))`, `Path(a) / b` | `Py.PurePath`, `Py.PathJoin` (`/` → `\`, bỏ `.` và dấu tách cuối) |
| `os.path.abspath` / `normcase` / `islink` | `Py.AbsPath` / `Py.NormCase` / `Py.IsLink` (junction không phải link) |
| `round(x, n)` | `Py.Round(x, n)` (**không** `Math.Round(x, n)`: lệch ở ca đúng giữa trên giá trị nhị phân, vd 0.0625) |
| `f"{x:.2f}"`, `f"{x:+.2f}"` | `Py.Fixed(x, 2)`, `Py.Fixed(x, 2, plus: true)` (làm tròn như Python, giữ `-0.00`) |
| `Path.stem / .suffix / .name / .parent` | `Subprep.Stem / Suffix / Name / Parent` (đường dẫn là `string`) |
| `int(x)` | `Py.ToInt` (cắt phần lẻ, NaN → lỗi) |
| `str.strip()` / `str.splitlines()` | `Py.StripWs` (Python coi `\x1c–\x1f` là khoảng trắng) / `Py.SplitLines` |
| `d.get(k)`, `(d.get(k) or {})`, `(… or [])`, `str(d.get(k) or "")` | `PyDict`: `d.Get(k)`, `d.GetDict(k)`, `d.GetList(k)`, `d.GetStr(k)` |
| `x is True` (cờ JSON) | `x is true` (1 không phải True) |
| `subprocess` + `returncode` | `Tools.Run` → `RunResult.Code` là `long` **không âm** như Python (0xC0000135 = 3221225781, không phải số âm) |
| `shutil.copyfile` / `shutil.which` | `Tools.CopyFile` (chỉ chép nội dung; `File.Copy` giữ cả thời gian sửa) / `Py.Which` |
| `tools.Job`, `Cancelled`, `cancel_now` | `Job` (`Token`, `CancelNow` diệt cả cây tiến trình), `Cancelled : OperationCanceledException` |
| test: `[sys.executable, "-c", …]`, `fake_alass.py` | `Child.Cmd(...)` (exe `tests/MuxStudio.TestChild`) |
| `requests` (`session.request`, `r.text`, `r.json()`) | `Http.Request` (đồng bộ, `Http.Shared`), `HttpResult.Text` / `JsonOrNull()`; lỗi mạng = `HttpNetworkException` |
| `urlencode` / `quote` / `urljoin` / `html.unescape` | `PyUrl.Encode` / `PyUrl.Quote` / `PyUrl.Join` / `WebUtility.HtmlDecode` |
| test: `tests/fakehttp.py` (`FakeHTTP`, `FakeResponse`, `Sleeper`, `zip_bytes`) | `Support/FakeHttp.cs` (`FakeHttp` = `HttpMessageHandler`, `FakeResponse.Json/Text/Bytes`, `Sleeper`, `ZipData.Of`) |
| test: `monkeypatch.setattr(tools, "run" / "mkvmerge_version", …)` | `ToolRunner` truyền vào phiên (`MuxSession.Runner`, `mkvmergeVersion:`) — không biến tĩnh |
| test: `monkeypatch.setattr(undo, "trash", …)` | thuộc tính `Trash` của phiên (`FetchSession`, `MuxSession`) |
| `fractions.Fraction(x).limit_denominator(n)` | `Fraction.FromDouble(x).LimitDenominator(n)` (chính xác, `BigInteger`) |
| `time.sleep` trong client mạng | tham số `sleep` (test: `Sleeper.Sleep`); mặc định `Http.Sleep` (dừng ngay khi bấm "Dừng ngay") |

## Giao diện (GĐ5)

- Logic THUẦN của giao diện (dòng bảng, chữ trạng thái, màu, CSV, bố cục…) nằm ở `src/MuxStudio.Core/Ui/` (không WPF) và có test
  (`GuiLogicTests`); `MuxStudio.App` chỉ dựng giao diện và gọi các hàm đó.
- Chữ hiển thị: tiếng Việt gốc, dịch ở chỗ hiển thị — XAML `{l:Tr 'chữ'}` (chữ có dấu `'` thì đặt bằng code), code `L.T(...)`; `Table` tự dịch
  ô (trừ cột `Name: true` = tên file), `Dlg.Text/Button` tự dịch. Câu MỚI → thêm thẳng vào
  `src/MuxStudio.Core/Resources/i18n_en.json` (mục `"EN"`: `"câu tiếng Việt": "English"`; phần giá trị thay đổi viết `{}` ở cả hai bên, như
  các câu đã có) và vào danh sách của `GuiLogicTests.New_wpf_strings_have_english_translations`. Câu cố ý không dịch → mục `"SKIP"`.
- Việc của lõi luôn qua `Runner.Start(...)` (luồng phụ); hỏi người dùng từ luồng phụ bằng `Runner.Ask`.
- Nội dung nút / ô chọn có thể chứa `_` (vd `srt_goc`) → dùng `TextBlock`, không gán chuỗi thẳng vào `Content` (WPF coi `_` là phím tắt).
- Bảng: `DataGrid` kiểu `WbGrid` + lớp `Table`; nền theo nhãn dòng (`need`/`bad`/`good`) do kiểu ô `WbCell` vẽ (template của WPF-UI bỏ qua
  `Background`). Màu Sáng / Tối: chỉ dùng `DynamicResource` của `Themes/Light.xaml` / `Dark.xaml`.
- Trình bày kiểu bản thiết kế: nhãn màu trong ô = `Col(Badge: true)` + giá trị `"~khoá"` (loại màu `good need bad info accent dim`); nút trên
  dòng = `Table.AddActionColumn` (giá trị `act` / `~act`); ô tick dạng nhãn = kiểu `Pill*`; nhãn / ô thống kê rời = `Chips.Make/Fill/Tile`.
  Dữ liệu hiển thị lấy từ `Core/Ui/WorkbenchUi.cs` — KHÔNG hiện số liệu giả (yêu cầu người dùng).
- **Màu chữ phải đọc được trên nền Acrylic / Mica (yêu cầu người dùng 13/11/2026):** chữ màu dùng `{DynamicResource Amber|Cyan|Emerald|Rose|
  Indigo|DimTextBrush}` (Light.xaml: sắc đậm ≥ 6:1 trên nền trắng; Dark.xaml: sắc sáng). `*AccentBrush` / `*SoftBgBrush` (Workbench.xaml,
  giống nhau ở hai chế độ) CHỈ làm nền / viền / chấm tròn — chữ trên nền nhấn đặc dùng `BadgeTextBrush`. Code: `Chips.TextKey(loại)` +
  `SetResourceReference` (tự đổi khi đổi Sáng / Tối); converter (`KindBrushConverter.Accent`) lấy màu lúc gắn → nơi dùng phải gắn lại khi
  `ThemeService.Changed`. Chữ phụ: `TextFillColorSecondaryBrush`, KHÔNG dùng `Opacity`. Khi bật kính, vùng nội dung nằm trên `ContentLayer`
  (`ContentLayerBrush`, 85% đục) — đừng đặt bảng / chữ thẳng lên nền kính.
- Thẻ "WORKBENCH STATE" theo lựa chọn HIỆN TẠI của các thẻ (`WorkbenchUi.State`): thẻ nào đổi lựa chọn thì gọi `_app.UpdateState()`; số file
  đếm lại sau MỖI việc của `Runner` (`PrepView.RefreshCounts`, việc tự động `quiet: true` = không đè dòng trạng thái "Xong: …").

## Đóng gói (GĐ6)

- Bản phát hành = publish profile `win-x64` / `win-arm64` (self-contained, MỘT file, ReadyToRun, không nén, không trim — WPF không hỗ trợ
  trim). `DebugType=embedded` (Directory.Build.props): pdb nằm trong dll → bản một file vẫn có số dòng trong lỗi.
- Bản một file: `Assembly.Location` rỗng (cảnh báo IL3000) → chỉ dùng qua `AppInfo.IsFrozen`; đường dẫn chương trình = `AppInfo.ProgramDir()`
  (`Environment.ProcessPath`), file đi kèm (icon…) = tài nguyên WPF (`pack://application:,,,/MuxStudio;component/…`), không đọc file cạnh .exe.
- Tự kiểm tra `--kiem-tra`: mục của lõi trong `Core/Ui/SelfCheck.cs` (test được), mục WPF + dựng thử cửa sổ trong `Infra/SelfCheckWpf.cs`.
  Thêm mục mới: hàm trả chuỗi (đạt) / `CheckResult.Warn(...)`, ném lỗi = `[LỖI]`. Báo cáo và `loi_khoi_dong.txt` giữ tiếng Việt (không dịch).
  Chế độ này KHÔNG được mở hộp thoại nào (`build.bat` đang chờ).
- Test chạy `MuxStudio.exe` thật (`PackagingTests.Exe_self_check_builds_window`) đặt `MUXSTUDIO_CONFIG_DIR` (cấu hình tạm) và
  `MUXSTUDIO_SECRET_BACKEND=null` (= `PYTHON_KEYRING_BACKEND` — không chạm Credential Manager thật).
- `build.bat`, `kiem_tra_MuxStudio.bat`: chữ KHÔNG dấu (ASCII) + CRLF, mọi `goto` có nhãn (`PackagingTests` kiểm).

## File từng sinh tự động (giờ sửa tay)

- `src/MuxStudio.Core/ConfigFields.cs` (bảng khoá cấu hình): thêm / đổi khoá thì sửa cả `Data/python_config.json` (ConfigTests so với nó).
- `src/MuxStudio.Core/Resources/i18n_en.json` (bảng dịch): xem mục Giao diện ở trên.
