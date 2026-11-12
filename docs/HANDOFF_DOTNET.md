# Bàn giao: port MuxStudio sang C# / WPF (.NET 10)

Nhánh: `dotnet-port`. Quy ước làm việc: [`dotnet/CLAUDE.md`](../dotnet/CLAUDE.md). Hướng dẫn tổng thể: [`PORT_DOTNET.md`](PORT_DOTNET.md).

## Tiến độ

| GĐ | Nội dung | Trạng thái |
|---|---|---|
| 0 | Solution `dotnet/` (Core, App WPF-UI, Tests xUnit), `Directory.Build.props`, analyzer locale | ✅ 13/11/2026 |
| 1 | `timing pgs srt encoding episodes naming config secrets undo i18n` | ✅ 13/11/2026: 343 test C# (Python cùng module: 305) |
| 2 | `tools` (Process + huỷ) `probe tmdb tvdb subsource opensubs titulky subfetch` + phần còn lại của `naming` | ⏳ tiếp theo |
| 3 | `subprep` | |
| 4 | `mux donor` | |
| 5 | GUI (NavigationView 5 thẻ, Log, Cài đặt, ①②③, Giới thiệu) | |
| 6 | Đóng gói (`dotnet publish` win-x64 + win-arm64), `--kiem-tra`, `loi_khoi_dong.txt` | |

## GĐ1: ánh xạ module

| Python | C# (`src/MuxStudio.Core/`) | Test C# | Python / C# |
|---|---|---|---|
| `timing.py` | `Timing.cs` | `TimingTests` | 55 / 56 |
| `pgs.py` | `Pgs.cs` | `PgsTests` | 12 / 12 |
| `srt.py` | `Srt.cs` | `SrtTests` | 28 / 30 |
| `encoding.py` | `TextEncoding.cs` | `TextEncodingTests` | 38 / 46 |
| `episodes.py` | `Episodes.cs` | `EpisodesTests` | 48 / 48 |
| `naming.py` (phần không cần database) | `Naming.cs` | `NamingTests` | 72 / 79 |
| `config.py` | `Config.cs`, `BatImport.cs`, `ConfigFields.cs` (sinh) | `ConfigTests` | 11 / 26 |
| `secrets.py` | `Secrets.cs` | `SecretsTests` | 8 / 12 |
| `undo.py` | `Undo.cs` | `UndoTests` | 9 / 10 |
| `i18n.py` + `i18n_en.py` | `I18n.cs` + `Resources/i18n_en.json` | `I18nTests` | 24 / 24 |
| (hành vi chuẩn của Python) | `Py.cs`, `PyJson.cs` | dùng trong các test trên | |

Hạ tầng test (`tests/MuxStudio.Core.Tests/Support/`):
- `PyRandom`: bản sao **chính xác** `random.Random` (MT19937) của Python, nên dữ liệu giả lập ở C# trùng từng số với test Python.
- `Synth`: port của `tests/synth.py`.
- `TempDir`, kèm `MovieDir()` (thư mục tên có `[ ]`).
- `GlobalStateCollection`, `Approx` (= `pytest.approx`).

## Đã kiểm chứng giống Python

- `config.json` mặc định do C# ghi **giống từng byte** `json.dumps(..., ensure_ascii=False, indent=2)` của Python.
- Nhập 3 file `reference/*.bat`: **toàn bộ** giá trị, số dòng, file nguồn, mục bỏ qua và lỗi đều giống Python.
- i18n: **3465/3465** câu ghép giá trị (bọc `[!]`, `▶ 1.`, `→`, nối `|`, nhiều dòng) dịch giống từng ký tự.
- Bảng mã một byte mà bảng ngôn ngữ dùng (874, 1250, 1251, 1252, 1254, 1256, 1258): đủ 256 byte giống Python.
- Credential Manager: Python `keyring` ghi → C# đọc được, và C# ghi → Python đọc được. Đã kiểm trên máy thật với service tạm `MuxStudioPortSelfTest`, đã xoá sau khi kiểm.
- Thùng rác thật (`SHFileOperationW`): đã thử một lần với file tạm `muxstudio-recycle-selftest.txt`.

## Khác biệt đã biết (cố ý hoặc chưa làm)

- **`naming.py` phần đặt tên theo database** (`movie_name`, `tv_name`, `choose_candidate`, `search_candidates`, `resolve_output_name`, `movie_name_from`, `tv_name_from`, `keep_name`, `_fill_keep`, `_cands`, `db_usable`) **chưa port**. Phần này cần client tmdb/tvdb và có test trong `test_naming_db.py`, nên để GĐ2. Đã có sẵn các kiểu dữ liệu `EpisodeHit`, `NameCandidate`, `YearOf`. Còn thiếu `OutputName`: thêm ở GĐ2 cùng interface `INameDb` (`DbName`, `Usable`, `SearchMovie`, `SearchSeries`, `PickShow`, `ResolveEpisode`). Kết quả API nên là `OrderedDictionary<string, object?>` (từ `PyJson.Loads`) để port sát `r.get(...)`.
- `secrets.ensure_backend`: chỉ cần cho PyInstaller, nên không port.
- i18n: chưa có bài "mọi câu hiển thị đều có bản dịch" (`test_every_displayed_string_has_a_translation`). Bài này quét mã Python bằng `tools/i18n_extract.py`; bản C# sẽ làm bài tương tự khi port giao diện (GĐ5). Biến `recorded` (công cụ phát triển) cũng chưa port.
- `\b`/`\w` trong regex của `Episodes`/`Naming` dùng ngữ nghĩa .NET. Kết quả chỉ khác Python với dấu tổ hợp rời (chuỗi chưa chuẩn hoá NFC), ký tự số kiểu ①, ² và dấu nối khác `_`. Tên file thực tế hầu như không có các ký tự này. `I18n` thì đã dùng `[\p{L}\p{N}_]` cho giống hệt.
- `PyJson.Loads` không nhận `NaN`/`Infinity` (Python có nhận); số nguyên vượt 64 bit → `double`.
- `Coerce` số thực → số nguyên chỉ nhận |x| < 9.2e18; `float("1_000")` của Python có nhận dấu gạch dưới, C# thì không.
- `Secrets.Mask` đếm độ dài theo đơn vị UTF-16 (key thực tế là ASCII nên không ảnh hưởng).

## Làm lại dữ liệu đối chiếu

```powershell
.venv\Scripts\python.exe tools\gen_dotnet_config_fields.py   # ConfigFields.cs + Data/python_config.json
.venv\Scripts\python.exe tools\gen_dotnet_i18n.py            # Resources/i18n_en.json + Data/python_i18n_cases.json
```
`Data/python_codepages.json` được sinh một lần bằng lệnh sau:
`bytes(range(256)).decode(f"cp{cp}", errors="muxstudio_c1")` cho cp ∈ {874, 1250…1258}, lưu dạng `{cp: [mã ký tự…]}`.

## Gợi ý cho GĐ2

1. `tools.py`: `Process` + `ArgumentList`, `CreateNoWindow`, UTF-8. Thay `Job` bằng `CancellationToken` + `IProgress<T>`. Huỷ bằng `Kill(entireProcessTree: true)`. Đọc `tests/test_probe_tools.py` và `tests/test_tools_probe.py` trước.
2. HTTP: dùng chung một `HttpClient` và inject `HttpMessageHandler`. Port `tests/fakehttp.py` thành handler giả. Test **không bao giờ** gọi mạng thật (xem fixture `no_real_network` trong `conftest.py`).
3. Phần còn lại của `naming` (xem trên) + `test_naming_db.py`.
4. Dữ liệu mẫu `tests/data/titulky/*.html` đọc thẳng từ thư mục `tests/` của repo (`RepoPaths.Root`).
