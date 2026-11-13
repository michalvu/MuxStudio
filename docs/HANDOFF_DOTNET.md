# Bàn giao: port MuxStudio sang C# / WPF (.NET 10)

Nhánh: `dotnet-port`. Quy ước làm việc: [`dotnet/CLAUDE.md`](../dotnet/CLAUDE.md). Hướng dẫn tổng thể: [`PORT_DOTNET.md`](PORT_DOTNET.md).

## Tiến độ

| GĐ | Nội dung | Trạng thái |
|---|---|---|
| 0 | Solution `dotnet/` (Core, App WPF-UI, Tests xUnit), `Directory.Build.props`, analyzer locale | ✅ 13/11/2026 |
| 1 | `timing pgs srt encoding episodes naming config secrets undo i18n` | ✅ 13/11/2026: 343 test C# (Python cùng module: 305) |
| 2 | `tools` (Process + huỷ) `probe tmdb tvdb subsource opensubs titulky subfetch` + phần còn lại của `naming` | ⏳ `tools`, `probe` ✅ 13/11/2026 (386 test C#). Tiếp theo: HTTP (`tmdb tvdb subsource opensubs titulky`) → `naming` DB → `subfetch` |
| 3 | `subprep` | ✅ 13/11/2026 (455 test C#) — kèm thay đổi ô "Ngôn ngữ" theo yêu cầu (xem mục GĐ3) |
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

## GĐ2: ánh xạ module (đang làm)

| Python | C# (`src/MuxStudio.Core/`) | Test C# | Python / C# |
|---|---|---|---|
| `tools.py` | `Tools.cs` (`Job`, `Cancelled`, `RunResult`, `Tools.*`) | `ToolsTests` | 11 / 20 |
| `probe.py` | `Probe.cs` | `ProbeTests` | 8 / 15 |
| (hành vi chuẩn của Python) | `PyStd.cs` (`os.path`, `pathlib`, `shutil.which`, `round(x, n)`, `int()`, `splitlines`, `strip`), `PyDict.cs` (`d.get`, `or {}`…) | `PyStdTests` | – / 8 |

Test Python tương ứng: `tools` = 10 test của `test_tools_probe.py` + `test_unsupported_alass_flags` (phần không cần PrepSession);
`probe` = 4 test của `test_tools_probe.py` + 3 test probe của `test_mux.py` + `test_probe_ref_sub_tracks_ranking` (`test_donor.py`).
Ngoài ra **4981 ca đối chiếu** do Python xuất (`Data/python_probe_cases.json`): `tool_info`/`mkvmerge_version` (thay `tools.run`
bằng kết quả cho sẵn), `alass_supported_flags`, `join_safe`, nhận diện track, codec/kênh/bitrate, độ phân giải, HDR, FPS, chọn
track phụ đề mốc, `os.path.join`, `pathlib`, `round(x, n)`.

Ánh xạ `Job`:
- `job.log/info/ok/warn/error/dim/head/progress`, `job.lines` → cùng tên (`Lines` là bản sao `LogLine(Level, Message)`).
  Hàm tạo nhận `IProgress<LogLine>`, `IProgress<JobProgress>`, `redact`, và `CancellationToken` (huỷ token = bấm "Dừng ngay").
  Test / chỗ không có giao diện dùng `SyncProgress<T>` (gọi ngay, không qua SynchronizationContext).
- `stop_after_current.set()` → `StopAfterCurrent()`; `stop_now.is_set()` → `StopNowRequested`; `check()` → `Check()` ném
  `Cancelled` (kế thừa `OperationCanceledException`); `cancel_now()` → `CancelNow()`: huỷ `Token`, diệt **cả cây** tiến trình con
  (`Kill(entireProcessTree: true)`), chờ tối đa 3 s.
- `NULL_JOB` → `Job.Null`. `tool_info` → `Tools.GetToolInfo` (tên `ToolInfo` dành cho kiểu kết quả).

Hạ tầng test mới:
- `tests/MuxStudio.TestChild`: exe con thay `[sys.executable, "-c", ...]` và `fake_alass.py` (`print`, `args`, `hex`, `stderr`,
  `exit`, `sleep`, `tree`, `alass --help`, `mkvmerge -J` giả, `ffprobe` giả). Được chép cạnh DLL test; gọi qua `Child.Cmd(...)`.
  GĐ3 đã mở rộng chế độ `alass` thành bản port đầy đủ của `fake_alass.py`.
- `PyCases.Check(nhóm, hàm)`: chạy hàm C# trên mọi ca Python xuất, báo tối đa 15 ca lệch.
- `PyData.D/L/J` (dựng JSON kiểu Python), `MkvFake.V/A/S/J` (= `V A S J` của `test_mux.py`, dùng lại ở GĐ4).

## GĐ3: ánh xạ module

| Python | C# (`src/MuxStudio.Core/`) | Test C# | Python / C# |
|---|---|---|---|
| `subprep.py` (hàm cấp module, dataclass) | `Subprep.cs` (`Subprep.*`, `Health`, `RefItem`, `RefIndex`, `RefMatch`, `SyncPair`, `CleanupPlan`…) | `SubprepTests` | 53 / 62 |
| `subprep.PrepSession` | `PrepSession.cs` | `SubprepTests`, `SubprepE2ETests` | 7 (e2e) / 7 |
| `test_golden_subprep.py` (so với .bat qua PowerShell) | — | `*_same_as_python` (so với Python: `Data/python_subprep_cases.json`) | 5 / 4 bài, 684 ca |
| `tests/fake_alass.py` | chế độ `alass` của `tests/MuxStudio.TestChild` (`FakeAlass` trong `Support/SubprepLib.cs`) | | |

- `tool_paths` → `ToolPaths(Mkvmerge, Mkvextract, Ffmpeg, Ffprobe, Alass)`; `Alass` là LỆNH (danh sách), `ToolPaths.Find(cfg, td)` = tự tìm.
- `PrepSession` là `IDisposable` (= `close()` / `with`): xoá thư mục tạm. `processed` ghi thêm ngôn ngữ (`Processed.Lang`).
- alass giả nhận cấu hình qua tham số `@file.json` (không dùng biến môi trường như bản Python → test chạy song song được) và có thêm
  `sleep` (giả lập alass chạy lâu để thử "Dừng ngay" giữa lúc đồng bộ).
- `AudioChoice.describe` của Python bị thụt lề nhầm vào trong `audio_layout_groups` (mã chết); bản C# đặt đúng chỗ: `AudioChoice.Describe()`.
- `f"{x:+.2f}"` → `Py.Fixed(x, 2, plus: true)`.

**Thay đổi CỐ Ý so với Python (yêu cầu người dùng 13/11/2026, chỉ bản C#)** — ô "Ngôn ngữ" ở bước ②:
- **Tự nhận diện**: như Python — mọi ngôn ngữ trong `srt_can_sync` (thư mục con `vi\ cs\ en\…` hoặc hậu tố tên file).
- **Chọn một ngôn ngữ** (vd `vi`): đổi tên / đồng bộ CHỈ phụ đề của ngôn ngữ đó (thư mục con `vi\` hoặc hậu tố `.vi`; đổi tên xét
  thư mục trước, đồng bộ xét hậu tố trước — đúng thứ tự tự nhận diện của từng bước). Phụ đề ngôn ngữ khác **và** phụ đề chưa rõ
  ngôn ngữ bị bỏ qua (người dùng chọn "Bỏ qua"), nhật ký ghi một dòng "[i] Chỉ xử lý phụ đề 'vi': N file; bỏ qua M file…".
  Bản Python xử lý mọi phụ đề và gắn ép nhãn đã chọn (chỉ cảnh báo đỏ).
- **Dọn dẹp** (`CleanupTargets(lang)`): chỉ phụ đề nguồn của ngôn ngữ đang chọn (kể cả khi trong phiên đã đồng bộ nhiều ngôn ngữ);
  phụ đề gốc trong `srt_goc` vẫn cho xoá như cũ (người dùng chọn). GĐ5: giao diện phải truyền ô Ngôn ngữ hiện tại vào `CleanupTargets`.
- Test: `SubprepTests.Language_*`. Hai câu nhật ký mới đã có bản dịch (`tools/i18n/extra.json`).

## Đã kiểm chứng giống Python

- `config.json` mặc định do C# ghi **giống từng byte** `json.dumps(..., ensure_ascii=False, indent=2)` của Python.
- Nhập 3 file `reference/*.bat`: **toàn bộ** giá trị, số dòng, file nguồn, mục bỏ qua và lỗi đều giống Python.
- i18n: **3465/3465** câu ghép giá trị (bọc `[!]`, `▶ 1.`, `→`, nối `|`, nhiều dòng) dịch giống từng ký tự.
- Bảng mã một byte mà bảng ngôn ngữ dùng (874, 1250, 1251, 1252, 1254, 1256, 1258): đủ 256 byte giống Python.
- Credential Manager: Python `keyring` ghi → C# đọc được, và C# ghi → Python đọc được. Đã kiểm trên máy thật với service tạm `MuxStudioPortSelfTest`, đã xoá sau khi kiểm.
- Thùng rác thật (`SHFileOperationW`): đã thử một lần với file tạm `muxstudio-recycle-selftest.txt`.
- `Tools.GetToolInfo` / `MkvmergeVersion` với mkvmerge, mkvextract, ffmpeg, ffprobe thật trên máy (Desktop\mkvtoolnix,
  Desktop\alass-windows64): test `Tool_info_with_real_tools_if_installed` chạy thật, không bị bỏ qua.
- `os.walk` đi vào junction (`mklink /J`), không đi vào symlink: C# làm y vậy (`Py.IsLink` đọc thẻ reparse).
- Bước ② với công cụ thật trên máy người dùng (`SubprepE2ETests`, 7/7 chạy thật): trích phụ đề gốc (cả PGS), WAV mono 16 kHz,
  thứ tự thử alass, alass thật khôi phục đúng độ lệch 7,5 s, alass thật nhận WAV.

## Khác biệt đã biết (cố ý hoặc chưa làm)

- **`naming.py` phần đặt tên theo database** (`movie_name`, `tv_name`, `choose_candidate`, `search_candidates`, `resolve_output_name`, `movie_name_from`, `tv_name_from`, `keep_name`, `_fill_keep`, `_cands`, `db_usable`) **chưa port**. Phần này cần client tmdb/tvdb và có test trong `test_naming_db.py`, nên để GĐ2. Đã có sẵn các kiểu dữ liệu `EpisodeHit`, `NameCandidate`, `YearOf`. Còn thiếu `OutputName`: thêm ở GĐ2 cùng interface `INameDb` (`DbName`, `Usable`, `SearchMovie`, `SearchSeries`, `PickShow`, `ResolveEpisode`). Kết quả API nên là `OrderedDictionary<string, object?>` (từ `PyJson.Loads`) để port sát `r.get(...)`.
- `secrets.ensure_backend`: chỉ cần cho PyInstaller, nên không port.
- i18n: chưa có bài "mọi câu hiển thị đều có bản dịch" (`test_every_displayed_string_has_a_translation`). Bài này quét mã Python bằng `tools/i18n_extract.py`; bản C# sẽ làm bài tương tự khi port giao diện (GĐ5). Biến `recorded` (công cụ phát triển) cũng chưa port.
- `\b`/`\w` trong regex của `Episodes`/`Naming` dùng ngữ nghĩa .NET. Kết quả chỉ khác Python với dấu tổ hợp rời (chuỗi chưa chuẩn hoá NFC), ký tự số kiểu ①, ² và dấu nối khác `_`. Tên file thực tế hầu như không có các ký tự này. `I18n` thì đã dùng `[\p{L}\p{N}_]` cho giống hệt.
- `PyJson.Loads` không nhận `NaN`/`Infinity` (Python có nhận); số nguyên vượt 64 bit → `double`.
- `Coerce` số thực → số nguyên chỉ nhận |x| < 9.2e18; `float("1_000")` của Python có nhận dấu gạch dưới, C# thì không.
- `Secrets.Mask` đếm độ dài theo đơn vị UTF-16 (key thực tế là ASCII nên không ảnh hưởng).
- `Probe`: `\b` trong regex (Forced/SDH/commentary/HDR) dùng ngữ nghĩa .NET như `Episodes`/`Naming` (xem trên).
- `Tools.Run`: diệt cả cây tiến trình khi quá thời gian / "Dừng ngay" (Python chỉ diệt tiến trình con trực tiếp). Huỷ đúng lúc
  vừa mở tiến trình thì tiến trình bị diệt ngay (Python để nó chạy hết rồi mới ném `Cancelled`). Kết quả trả về vẫn như Python.
- `Tools.WalkFiles`: symlink thư mục **hỏng** vẫn coi là thư mục (Python coi là file). JSON lạ (gốc không phải bảng, phần tử
  `tracks` không phải bảng) bị bỏ qua thay vì ném lỗi như Python.
- Script dò API (`tools/probe_*.py`, `nhap_tu_bat.py`, `chay_do_*.bat`) và test của chúng (`test_probe_tools.py`) đã bỏ khỏi
  nhánh này (công cụ phát triển, không thuộc `core/`), cùng phần đóng gói PyInstaller — vẫn còn ở nhánh `claude/new-session-eckanp`.

## Làm lại dữ liệu đối chiếu

```powershell
.venv\Scripts\python.exe tools\gen_dotnet_config_fields.py   # ConfigFields.cs + Data/python_config.json
.venv\Scripts\python.exe tools\gen_dotnet_i18n.py            # Resources/i18n_en.json + Data/python_i18n_cases.json
.venv\Scripts\python.exe tools\gen_dotnet_probe.py           # Data/python_probe_cases.json (tools, probe, os.path, round…)
.venv\Scripts\python.exe tools\gen_dotnet_subprep.py         # Data/python_subprep_cases.json (cắt recap, sức khoẻ, ghép mốc…)
```
Sau khi sửa `tools/i18n/extra.json`: `set PYTHONIOENCODING=utf-8` rồi chạy `tools\i18n_build.py` và `tools\gen_dotnet_i18n.py`
(console tiếng Séc dùng cp1250, dòng `print` cuối của script sẽ lỗi nếu thiếu biến này).
`Data/python_codepages.json` được sinh một lần bằng lệnh sau:
`bytes(range(256)).decode(f"cp{cp}", errors="muxstudio_c1")` cho cp ∈ {874, 1250…1258}, lưu dạng `{cp: [mã ký tự…]}`.

## Gợi ý cho phần còn lại của GĐ2

1. ~~`tools.py`, `probe.py`~~ ✅.
2. HTTP: dùng chung một `HttpClient` và inject `HttpMessageHandler`. Port `tests/fakehttp.py` thành handler giả. Test **không bao giờ** gọi mạng thật (xem fixture `no_real_network` trong `conftest.py`). Các client nhận `Job` (log + `Job.Token` cho `HttpClient`). Kết quả JSON đọc bằng `PyJson.Loads` + `PyDict` (`r.Get("x")`, `r.GetDict("x")`, `r.GetStr("x")`).
3. Phần còn lại của `naming` (xem trên) + `test_naming_db.py`.
4. Dữ liệu mẫu `tests/data/titulky/*.html` đọc thẳng từ thư mục `tests/` của repo (`RepoPaths.Root`).
5. Test Python hay `monkeypatch.setattr(tools, "run" | "mkvmerge_version", ...)` (test_mux). GĐ3 không cần (alass / mkvmerge giả
   là exe con). GĐ4 (`test_mux.py` thay `tools.run` để giả mkvmerge ghi file `-o`): nên thêm chỗ thay theo luồng (`AsyncLocal`)
   hoặc truyền runner vào phiên, **không** dùng biến tĩnh toàn cục (test chạy song song). Phần đọc kết quả đã tách sẵn:
   `Tools.ToolInfoOf`, `Tools.MkvmergeVersionOf`.
