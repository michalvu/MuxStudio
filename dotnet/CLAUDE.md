# MuxStudio .NET (WPF, .NET 10) — quy ước cho Claude

Bản port C# của MuxStudio Python. Code Python ở thư mục gốc repo (`core/`, `gui/`, `tests/`) là **bản chuẩn để đối chiếu** cho tới khi bản C# chạy đủ cả ba bước. Tiến độ và việc tiếp theo: [`docs/HANDOFF_DOTNET.md`](../docs/HANDOFF_DOTNET.md). Hướng dẫn port tổng thể: [`docs/PORT_DOTNET.md`](../docs/PORT_DOTNET.md).

## Lệnh

```powershell
cd dotnet
dotnet build MuxStudio.slnx            # phải 0 lỗi, 0 cảnh báo
dotnet test  MuxStudio.slnx            # phải xanh hết
dotnet run --project src/MuxStudio.App # chạy app
```

Baseline Python (so sánh hành vi): `.venv\Scripts\python.exe -m pytest tests -q` (ở thư mục gốc repo).

## Quy tắc port

1. **Port sát, không tự thêm tính năng, không "sửa cho đẹp" logic.** Mỗi hàm C# ghi rõ nó port hàm Python nào. Chuỗi tiếng Việt (trạng thái, nhật ký, lỗi) giữ **nguyên văn**, vì i18n và nhật ký hoàn tác dựa vào chúng.
2. Mỗi module: port code → port test của nó (`tests/test_x.py` → `XTests.cs`) → `dotnet test` xanh → commit riêng.
3. Ở chỗ hành vi khó đoán, thêm **test đối chiếu với dữ liệu do Python xuất** (`tests/MuxStudio.Core.Tests/Data/python_*.json`, sinh bằng `tools/gen_dotnet_*.py`). Đừng đoán kết quả của Python: chạy Python để lấy.
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

## File sinh tự động (đừng sửa tay)

- `src/MuxStudio.Core/ConfigFields.cs` ← `python tools/gen_dotnet_config_fields.py` (sau khi sửa `FIELDS` trong `core/config.py`)
- `src/MuxStudio.Core/Resources/i18n_en.json` ← `python tools/gen_dotnet_i18n.py` (sau khi chạy `tools/i18n_build.py`)
- `tests/MuxStudio.Core.Tests/Data/python_probe_cases.json` ← `python tools/gen_dotnet_probe.py` (sau khi sửa `core/tools.py` / `core/probe.py`)
- `tests/MuxStudio.Core.Tests/Data/python_subprep_cases.json` ← `python tools/gen_dotnet_subprep.py` (sau khi sửa `core/subprep.py`)
- `tests/MuxStudio.Core.Tests/Data/python_mux_cases.json` ← `python tools/gen_dotnet_mux.py` (sau khi sửa `core/mux.py` / `core/donor.py`)
- `tests/MuxStudio.Core.Tests/Data/python_*.json` còn lại ← hai script đầu. Riêng `python_codepages.json` sinh một lần, bằng lệnh ghi trong HANDOFF_DOTNET.md.
