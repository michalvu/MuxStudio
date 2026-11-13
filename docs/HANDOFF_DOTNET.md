# Bàn giao: port MuxStudio sang C# / WPF (.NET 10)

Nhánh: `dotnet-gui` (tách từ `dotnet-port` ở GĐ5; nhánh làm việc mặc định từ 13/11/2026). Quy ước làm việc: [`dotnet/CLAUDE.md`](../dotnet/CLAUDE.md). Hướng dẫn tổng thể: [`PORT_DOTNET.md`](PORT_DOTNET.md).

## Tiến độ

| GĐ | Nội dung | Trạng thái |
|---|---|---|
| 0 | Solution `dotnet/` (Core, App WPF-UI, Tests xUnit), `Directory.Build.props`, analyzer locale | ✅ 13/11/2026 |
| 1 | `timing pgs srt encoding episodes naming config secrets undo i18n` | ✅ 13/11/2026: 343 test C# (Python cùng module: 305) |
| 2 | `tools` (Process + huỷ) `probe tmdb tvdb subsource opensubs titulky subfetch` + phần còn lại của `naming` | ✅ 13/11/2026 (582 test C#) |
| 3 | `subprep` | ✅ 13/11/2026 (455 test C#) — kèm thay đổi ô "Ngôn ngữ" theo yêu cầu (xem mục GĐ3) |
| 4 | `mux donor` | ✅ 13/11/2026 (641 test C#) |
| 5 | GUI theo bản thiết kế "Concept 3 Pro Workbench" (cột trái 5 thẻ, Log, Cài đặt, ①②③, Giới thiệu, Hoàn tác) | ✅ 13/11/2026 (691 test C#) |
| 6 | Đóng gói (`dotnet publish` win-x64 + win-arm64), `--kiem-tra`, `loi_khoi_dong.txt` | ✅ 13/11/2026 (710 test C#; sau sửa giao diện: 717) |

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

## GĐ2: ánh xạ module

| Python | C# (`src/MuxStudio.Core/`) | Test C# | Python / C# |
|---|---|---|---|
| `tools.py` | `Tools.cs` (`Job`, `Cancelled`, `RunResult`, `Tools.*`) | `ToolsTests` | 11 / 20 |
| `probe.py` | `Probe.cs` | `ProbeTests` | 8 / 15 |
| `tmdb.py` | `Tmdb.cs` (`TmdbClient`, `ConnStatus`, `INameDb`) | `TmdbTests` | 10 / 11 |
| `tvdb.py` | `Tvdb.cs` (`TvdbClient`) | `NamingDbTests` | (trong `test_naming_db`) |
| `naming.py` phần database | `NamingDb.cs` (`OutputName`, `ResolveOutputName`, `ChooseCandidate`, `SearchCandidates`…) | `NamingDbTests` | 16 / 14 (+1 bài cần `mux` → GĐ4) |
| `subsource.py` | `SubSource.cs` (`SubSourceClient`, `SubSource.*`) | `SubSourceTests` | 31 / 32 |
| `opensubs.py` | `OpenSubs.cs` (`OpenSubtitlesClient`, `OpenSubs.*`) | `OpenSubsTests` | 25 / 26 |
| `titulky.py` | `Titulky.cs` (`TitulkyClient`, `Titulky.*`, `SearchRow`, `DownloadPage`) | `TitulkyTests` | 16 / 16 |
| `subfetch.py` | `SubFetch.cs` (`FetchSession`, `Candidate`, `ChoiceMemory`, `RefProvider`, `SubFetch.*`) | `SubFetchTests` | 27 / 28 |
| `requests` + `tests/fakehttp.py` | `Http.cs` (`Http`, `HttpResult`, `HttpNetworkException`, `PyUrl`) + `Support/FakeHttp.cs` | | |
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

HTTP (thay `requests`):
- `Http`: một `HttpClient` dùng chung (`Http.Shared`, tạo khi cần lần đầu), **đồng bộ** (`HttpClient.Send`, lõi chạy ở luồng phụ như
  bản Python), giữ cookie, tự giải nén, theo chuyển hướng (≤ 30 như requests). `Request(method, url, query, headers, json, form,
  timeout, ct)`: tham số mã hoá như requests (`quote_plus`; `null` bị bỏ, "" vẫn gửi); `json` = `PyJson.Dumps`; lỗi mạng / quá giờ →
  `HttpNetworkException` (= `_NET_ERR`); `ct` (= `Job.Token`) bị huỷ → `Cancelled`.
- `HttpResult.Text` giải mã như requests (charset trong `Content-Type`; `text/*` không ghi → ISO-8859-1; JSON → UTF-8); **khác**:
  không có `Content-Type` → UTF-8 (requests đoán bằng `charset_normalizer`). `JsonOrNull()` = `try r.json() except ValueError: None`.
- `html.unescape` → `WebUtility.HtmlDecode` (bộ thực thể HTML 4; Python có ~2200 thực thể HTML5 và nhận cả thực thể thiếu `;`).
- User-Agent khi nơi gọi không đặt: `MuxStudio/<bản>` (requests gửi `python-requests/x.y`); Titulky và OpenSubtitles tự đặt như Python.
- Hàm chờ mặc định của client (`Http.Sleep`) dừng ngay khi bấm "Dừng ngay" (Python `time.sleep` không ngắt được); lượt gọi tiếp ném
  `Cancelled`. Test truyền `Sleeper.Sleep` / `Sleeper.Clock`.
- Máy chủ giả `FakeHttp` (`HttpMessageHandler`): `Add(method, regex, FakeResponse | dãy phản hồi | hàm)`, `Calls`, `CallsTo`.
  `FakeCall.Url` là địa chỉ nơi gọi truyền vào (chưa ghép tham số — lấy từ `Http.UrlOption`), `Params` đọc lại từ chuỗi đã mã hoá,
  `Json` / `Data` đọc lại từ thân POST. Handler ném `HttpRequestException` = lỗi mạng.

Khác Python (nhỏ, ghi lại):
- `SafeExtract` đọc tên trong ZIP như `zipfile` (cờ UTF-8 → UTF-8, không có → cp437); thư mục đích trùng tên file → xoá file.
- `FetchSession.Trash` (mặc định `Undo.Trash`) là chỗ test thay thế (Python `monkeypatch.setattr(undo, "trash", …)`).
- `naming` `OutputName.QueryKey` là chuỗi (`Naming.QueryKey(...)` nối các phần bằng ``, `None` → "None") thay cho tuple.
- `FetchSession` nhận `ToolPaths` (chỉ dùng `Mkvmerge`/`Mkvextract`); bỏ trống → `Tools.FindTool` cho hai công cụ đó.

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

## GĐ4: ánh xạ module

| Python | C# (`src/MuxStudio.Core/`) | Test C# | Python / C# |
|---|---|---|---|
| `donor.py` | `Donor.cs` (`Donor.*`, `DonorPlan`, `DonorTrack`, `DonorTiming`, `TrackTimes`, `Fraction`) | `DonorTests` | 24 / 25 (+ `Fraction`) |
| `mux.py` | `Mux.cs` (`MuxSession`, `MuxPlan`, `Mux.*`, `AudioRule`, `ExtSub`, `IntSub`, `SubItem`, `UsedFile`…) | `MuxTests` | 27 / 28 (+1 bài của `test_naming_db`) |
| `test_golden_mux.py` (so với .bat qua PowerShell) | — | `MuxCrossCheckTests` (so với Python: `Data/python_mux_cases.json`) | 12 / 3 bài: 80 kế hoạch + 60 ca ghép audio + 4 nhóm hàm thuần |
| `test_e2e_mux.py` (công cụ thật) | — | `MuxE2ETests` | 3 / 3 (chạy thật trên máy người dùng: lệch < 10 ms) |

- **`ToolRunner`** (`delegate RunResult (args, job, timeout)`): `MuxSession(…, runner:)` / `.Runner`, `TrackTimes.Runner`,
  `Donor.Prepare(…, runner)` — thay `monkeypatch.setattr(tools, "run", …)` của test Python (mkvmerge giả ghi file `-o`:
  `MuxLib.FakeRun`). `MuxSession(…, mkvmergeVersion:)` thay `monkeypatch.setattr(tools, "mkvmerge_version", …)`. Không biến tĩnh.
- `MuxSession.Trash` (mặc định `Undo.Trash`) thay `monkeypatch.setattr(undo, "trash", …)`. `MuxSession.Ver` đặt lại → tự đổi
  `FDef`/`FFor` (Python đặt riêng `ver`, `f_def`, `f_for`). `UiLang` = `_ui_lang`, `TrackTimes` thay được bằng `TrackTimesFn` (= `times_fn`).
- `Fraction`: `fractions.Fraction(float)` CHÍNH XÁC (giá trị nhị phân, `BigInteger`) + `limit_denominator` (cùng cách chọn cận như
  Python) — cho `StretchFilter` (TĐ-34: không dùng `double`).
- `MuxSession.Execute` bắt `OperationCanceledException` (Python bắt `tools.Cancelled`) — gồm cả huỷ qua `Job.Token` lúc gọi HTTP.
- Test Python `test_mux.py` có 3 bài của `probe` (tên codec, kênh, bitrate, HDR): đã ở `ProbeTests` (GĐ2).
- `MuxCrossCheckTests` dựng lại CÙNG thư mục phim giả cho từng ca rồi so TỪNG trường kế hoạch, TỪNG dòng nhật ký, TỪNG tham số mkvmerge /
  ffmpeg (đường dẫn gốc → `<ROOT>`). Đã thử ngược: sửa một giá trị trong dữ liệu là test báo đúng chỗ lệch.

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
- Bước ③ (`MuxCrossCheckTests`): 80 kế hoạch mux + 60 ca ghép audio — kế hoạch, nhật ký, lệnh mkvmerge/ffmpeg giống Python từng chuỗi.
- Bước ③ với công cụ thật (`MuxE2ETests`, 3/3 chạy thật): tiếng bíp PAL +1,5 s và cùng FPS −2 s, audio ghép lệch < 10 ms.
- Bước ② với công cụ thật trên máy người dùng (`SubprepE2ETests`, 7/7 chạy thật): trích phụ đề gốc (cả PGS), WAV mono 16 kHz,
  thứ tự thử alass, alass thật khôi phục đúng độ lệch 7,5 s, alass thật nhận WAV.

## Khác biệt đã biết (cố ý hoặc chưa làm)

- `secrets.ensure_backend`: chỉ cần cho PyInstaller, nên không port.
- i18n: bài "mọi câu hiển thị đều có bản dịch" của Python quét mã bằng `tools/i18n_extract.py`; bản C# chưa có bài quét tự động tương đương — GĐ5 đã dò bằng tay (dùng `core.i18n` quét mọi chuỗi tiếng Việt trong `MuxStudio.App` + `Core/Ui`) và thêm 26 câu mới vào `tools/i18n/extra.json`; `GuiLogicTests.New_wpf_strings_have_english_translations` giữ các câu mới. Biến `recorded` (công cụ phát triển) chưa port.
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

## Dữ liệu đối chiếu (cố định)

`tests/MuxStudio.Core.Tests/Data/python_*.json` do bản Python xuất (script `tools/gen_dotnet_*.py`, đã xoá cùng bản Python). Không sinh
lại được nữa: đó là "đáp án" hành vi cũ — chỉ sửa khi CỐ Ý đổi hành vi. `Data/python_codepages.json` được sinh bằng lệnh:
`bytes(range(256)).decode(f"cp{cp}", errors="muxstudio_c1")` cho cp ∈ {874, 1250…1258}, lưu dạng `{cp: [mã ký tự…]}`.

## GĐ5: giao diện WPF

Bố cục theo bản thiết kế trong `design/` ("Concept 3 Pro Workbench": thanh tiêu đề Fluent → thanh thư mục phim + Sáng / Tối / theo Windows →
cột trái 5 thẻ theo thứ tự quy trình + thẻ "WORKBENCH STATE" → nội dung → khung log → thanh trạng thái có tiến độ + Dừng). Phần GIẢ của bản
thiết kế (Single/Batch, cài đặt Claude Code, SubDL, nút ±50 ms, OS Hash…) KHÔNG có trong lõi nên đã thay bằng đúng tính năng của bản Python.

| Python (`gui/`) | C# |
|---|---|
| phần thuần của `widgets`, `app`, `log_panel`, `fetch_view`, `subprep_view`, `mux_view`, `settings_view`, `about_view` | `src/MuxStudio.Core/Ui/*.cs` (không WPF, test được): `UiText`, `Steps`, `GridModel`, `WindowLayout`, `LogUi`, `UndoUi`, `FetchUi`, `PrepUi`, `MuxUi`, `SettingsUi`, `AboutUi` |
| `App` + `UndoDialog` | `MainWindow.xaml(.cs)`, `Views/UndoDialog.cs` |
| `W.Runner` | `Infra/Runner.cs` (luồng phụ, log DỊCH ở luồng phụ, gom 80 ms; `Ask` = hỏi luồng giao diện từ luồng phụ — CAPTCHA) |
| `W.Table` / `W.CheckGrid` / `W.StepBar` | `Infra/Table.cs` (bọc `DataGrid`; cột tên file `Name: true` không dịch như `fit="name"`), `Infra/CheckGrid.cs`, `Infra/StepBar.cs` |
| `W.Dialog`, `info/warn/ask_*`, `confirm_list`, `show_text`, `CaptchaDialog` | `Infra/Dlg.cs` |
| `LogPanel` | `Views/LogPanel.xaml(.cs)` (`ListBox` ảo hoá; Ctrl+C chép dòng) |
| `FetchView` / `SubprepView` / `MuxView` (+ `NameDialog`, `TrashDialog`) / `SettingsView` (+ `ImportDialog`) / `AboutView` | `Views/*.xaml(.cs)` |
| màu `LEVEL_COLORS` / `ROW_BG` / `GRID_COLORS` | `Themes/Light.xaml`, `Themes/Dark.xaml` (`ThemeService` thay theo chế độ); `Themes/Workbench.xaml` (màu nhấn, `WbGrid`, `WbCell`) |

- Phần trình bày "Concept 3" (làm lại theo ảnh thiết kế, CHỈ dữ liệu thật — không đổi lõi): ngôn ngữ / nguồn / database / mốc đồng bộ dạng
  nhãn bật-tắt (`Pill`, `PillCyan`, `PillEmerald` trong `Themes/Workbench.xaml`); nhãn màu trong ô bảng (`Col(Badge: true)` + giá trị ẩn
  `"~khoá"` của dòng); nút "+ Chọn / ✓ Đã chọn" trên dòng ứng viên (`Table.AddActionColumn`); thẻ "TARGET VIDEO", thanh "GIỎ PHỤ ĐỀ"
  (①); ô thống kê + nhãn đếm TỐT / CẦN XEM / CÓ VẤN ĐỀ (②); thẻ từng luồng VIDEO / AUDIO (tick XOÁ → gạch ngang) / SUB / NGOÀI / AUDIO GHÉP
  + khung "Output & mkvmerge" với lệnh thật (`PreviewCommands`, dựng ở luồng phụ cùng lúc lập kế hoạch) và tiến độ của lượt mux (③).
  Dữ liệu cho phần này: `Core/Ui/WorkbenchUi.cs`; màu: `Infra/Chips.cs`. Những thứ trong ảnh mà lõi KHÔNG có (sóng âm, chỉnh offset ±50 ms,
  OS Hash, Single/Batch, chọn nhiều bản / ngôn ngữ) không làm — theo lựa chọn của người dùng (13/11/2026).
- Test: `GuiLogicTests` (50 bài) = các bài thuần của `test_gui_logic.py`. Bài `Runner` (Dispatcher) và `test_gui_app.py` (lái Tk) chưa port —
  đã chạy tay bằng UI Automation trên thư mục phim mẫu: ① quét, ② mốc audio → đổi tên → alass thật → dọn dẹp, ③ mux thật 2 file → dọn
  file nguồn, Hoàn tác, Cài đặt, Giới thiệu, Sáng / Tối, giao diện tiếng Anh.
- Khác Python (cố ý): nút Sáng / Tối / theo Windows ở đầu cửa sổ (đổi + lưu `UiAppearance` ngay); "Xem lệnh" ở thẻ ② chạy ở luồng phụ;
  bước ② dọn dẹp truyền ô "Ngôn ngữ" vào `CleanupTargets(lang)` (GĐ3); trang Giới thiệu liệt kê thư viện của bản .NET (.NET, WPF, WPF-UI,
  CommunityToolkit.Mvvm) thay cho Python/Tk/…; trạng thái nguồn được dịch cả câu rồi mới bỏ tiền tố tên nguồn.
- Bẫy WPF đã gặp: chữ nút có `_` (`srt_goc`) bị nuốt làm phím tắt → dùng `TextBlock` làm nội dung; template dòng / ô của WPF-UI không dùng
  `Background` → kiểu ô riêng `WbCell` (BasedOn `DefaultDataGridCellStyle`), kiểu dòng BasedOn `DefaultDataGridRowStyle` (không phải kiểu mặc
  định của Windows: nền trắng ở chế độ Tối); khoá phụ `UiGeometry`/`UiZoomed`/`UiLogHeight`/`UiLastFolder` không có trong bảng khoá →
  đọc bằng `MainWindow.OptStr/OptBool/OptInt` (= `cfg.get`).

## GĐ6: đóng gói

| Python | C# |
|---|---|
| `build.bat` + `MuxStudio.spec` (PyInstaller, onedir) | `dotnet/build.bat` + `src/MuxStudio.App/Properties/PublishProfiles/win-x64.pubxml`, `win-arm64.pubxml` |
| `assets/kiem_tra_MuxStudio.bat` | `src/MuxStudio.App/kiem_tra_MuxStudio.bat` (publish chép cạnh `.exe`: `CopyToPublishDirectory` + `ExcludeFromSingleFile`) |
| `self_check*`, `CheckItem`, `_run_check`, `_chk_*` (phần không cần Tk) | `Core/Ui/SelfCheck.cs` (`SelfCheck.Items/Report/Execute/Run/IsSelfCheck`, `CheckItem`, `CheckResult`) |
| `_chk_gui_libs`, `_chk_icon`, `_chk_captcha_image`, `_chk_window` | `Infra/SelfCheckWpf.cs` (truyền vào lõi qua `SelfCheckUi`) |
| `write_startup_error`, `build_info`, `os_name`, `W.is_frozen` | `AppInfo.WriteStartupError / BuildInfo / OsName / IsFrozen / ProgramDir` (`Core/Ui/AppUi.cs`); `App.BuildInfo()` thêm bản WPF-UI |
| `W.ICON_ICO`, `W.ICON_PNG`, `resource_path` | `Infra/AppIcon.cs`: `src/MuxStudio.App/Assets/muxstudio.ico` / `.png` (bản chép từ `assets/` của Python) nhúng dạng tài nguyên WPF + `ApplicationIcon` của .exe |
| `test_packaging.py` | `PackagingTests` (19 bài) |

- **Bản phát hành:** `build.bat` → `[1/4]` có .NET 10 SDK → `[2/4]` MuxStudio.exe không đang mở → `[3/4]` publish `win-x64` và `win-arm64`
  vào `dotnet\dist\win-*\` (xoá bản cũ trước) → `[4/4]` chạy `--kiem-tra` bản hợp với máy (`PROCESSOR_ARCHITECTURE`), in báo cáo
  `kiem_tra_exe.txt`, mã thoát ≠ 0 → `[LOI]`. Đã chạy thật trên máy người dùng (x64): ĐẠT. Bản arm64 chỉ kiểm được là file PE ARM64 (0xAA64)
  — **chưa chạy thử trên máy ARM** (không có máy).
- **Chọn cấu hình publish (đo 13/11/2026, x64, 5 lần, cấu hình tạm, tới khi cửa sổ nhận input):**

  | Biến thể | Kích thước | Khởi động (trung vị) |
  |---|---|---|
  | **ReadyToRun, không nén (đang dùng)** | 158 MB (arm64: 176 MB) | **0,72 s** |
  | ReadyToRun + `EnableCompressionInSingleFile` | 71 MB | 0,92 s |
  | không ReadyToRun | 139 MB | 0,85 s |

  Lần chạy đầu tiên trên một máy chậm hơn ~0,3 s (giải nén thư viện native của WPF vào `%TEMP%\.net`, `IncludeNativeLibrariesForSelfExtract`).
  Muốn file nhỏ để gửi: nén ZIP (không đổi tốc độ khởi động). Không dùng được trim (WPF không hỗ trợ).
- **`--kiem-tra` / `--self-check [báo cáo.txt]`** (mặc định `%APPDATA%\MuxStudio\kiem_tra.txt`, UTF-8, CRLF; mã thoát 0 = đạt): các mục như
  bản Python, đổi theo .NET: "Giao diện (WPF + WPF-UI)" (tài nguyên Fluent + bảng màu Sáng / Tối), "Windows Credential Manager" (đọc thử mục
  không có), "Thùng rác" (`SHFileOperationW` có mặt), "Kết nối HTTPS" (kho chứng chỉ gốc của Windows — .NET không có file `certifi`), "Ảnh
  CAPTCHA (JPEG)" (tạo JPEG rồi đọc bằng `Dlg.DecodeImage` — hàm của hộp thoại CAPTCHA), "Cửa sổ chính" (dựng `MainWindow` NGOÀI màn hình,
  không focus, không lên thanh tác vụ, cấu hình tạm, `SelfTest = true` = không phóng to; đóng bằng `CloseQuiet` = không hỏi, không lưu),
  "Phông chữ tiếng Việt" (KIỂM ĐỦ GLYPH chữ có dấu của phông đầu tiên có trên máy — Python chỉ kiểm tên phông), "Icon cửa sổ".
  Chế độ này không mở hộp thoại nào: lỗi giao diện chưa bắt được gom vào mục "Cửa sổ chính".
- **Bắt lỗi toàn cục (`App.xaml.cs`):** lỗi giao diện khi cửa sổ đã mở → khung log (như GĐ5); khi CHƯA mở xong → `loi_khoi_dong.txt` + hộp thoại
  + thoát (trước đây: chương trình đóng không báo); `AppDomain.UnhandledException` (luồng phụ ngoài `Runner`, .NET buộc đóng) → ghi
  `loi_khoi_dong.txt` + hộp thoại "MuxStudio gặp lỗi và phải đóng"; `TaskScheduler.UnobservedTaskException` → khung log, chạy tiếp.
- **Khác Python (cố ý):** bản phát hành là MỘT file (Python: thư mục `_internal`); `loi_khoi_dong.txt` ghi CRLF và cả lỗi làm chương trình phải
  đóng sau khi đã mở; thanh tiêu đề có icon (ảnh 16 px từ `muxstudio.png`), hộp thoại cũng đặt icon; `DebugType=embedded` (số dòng trong lỗi của
  bản một file); `MUXSTUDIO_SECRET_BACKEND=null` → `NullSecretBackend` (= `PYTHON_KEYRING_BACKEND=keyring.backends.null.Keyring`, chỉ để test
  chạy .exe thật). Các bài `test_packaging.py` không port: `ensure_backend` (không cần), `MuxStudio.spec`, `requirements*.txt`, so từng điểm
  ảnh icon với `tools/make_icon.py` (script không có ở nhánh này — `PackagingTests` kiểm cấu trúc file `.ico`: 7 cỡ 16–256, 32 bit, 256 = PNG),
  dọn file `core/pool.py` cũ.
- Đã chạy tay: mở bản `dist\win-x64\MuxStudio.exe` (cấu hình tạm), chụp màn hình — icon hiện trên thanh tiêu đề.

## Bỏ bản Python (13/11/2026, theo yêu cầu người dùng)

1. `dotnet/` tự đủ: icon `assets/muxstudio.*` → `dotnet/src/MuxStudio.App/Assets/`; `reference/*.bat` (`ConfigTests`) →
   `tests/MuxStudio.Core.Tests/Data/reference/` (giữ nguyên từng byte: `.gitattributes` `binary`, `.gitignore` cho phép);
   `tests/data/titulky/*` (`TitulkyTests`) → `Data/titulky/`; `RepoPaths.Root` tìm gốc repo theo `dotnet/MuxStudio.slnx`.
2. Đã xoá khỏi repo: `core/ gui/ tests/ tools/ assets/ reference/`, `MuxStudio.pyw`, `chay_muxstudio.bat`, `run_tests.bat`, `pyproject.toml`,
   `requirements*.txt` (và `.venv/`, `.pytest_cache/` trên máy). Sau khi xoá: dựng 0 cảnh báo, 710/710 test xanh. Lịch sử bản Python: nhánh
   `claude/new-session-eckanp`. Giữ `docs/SPEC.md` (đặc tả tính năng; code C# trỏ tới).
3. Từ nay `ConfigFields.cs` và `Resources/i18n_en.json` sửa tay (cách làm: `dotnet/CLAUDE.md`).

## Sau GĐ6: sửa giao diện theo góp ý (13/11/2026)

- **Chữ màu mờ trên nền Acrylic:** 5 màu nhấn trước dùng chung sắc sáng (Tailwind 500) cho cả Sáng / Tối → trên nền sáng chỉ ~2–2,5:1. Nay:
  màu CHỮ riêng theo chế độ (`*TextBrush` trong Light.xaml / Dark.xaml), màu nhấn sáng chỉ còn làm nền / viền; chữ phụ dùng
  `TextFillColorSecondaryBrush` thay cho `Opacity`; ô tick dạng nhãn (Pill) khi bật giữ màu chữ thường; khi bật Acrylic / Mica, vùng nội dung
  đặt trên lớp nền 85% đục (`ContentLayer`) — kính chỉ còn ở khung ngoài + cột trái (như app Cài đặt của Windows 11). Đổi Sáng / Tối lúc đang
  chạy: màu tự đổi (đã thử).
- **WORKBENCH STATE đứng yên:** "Ngôn ngữ cần tải" trước đọc từ Cài đặt đã lưu, số file chỉ đếm lại khi mở thẻ ②. Nay theo lựa chọn hiện tại
  (`WorkbenchUi.State`: ngôn ngữ đang tick ở ①, mốc ở ②, database ở ③), số file đếm lại sau mỗi việc (việc đếm là việc tự động của
  `Runner` — `quiet` — không đè dòng "Xong: …"); số đếm tách ba dòng (Phim / Phụ đề trong 'srt_can_sync' / Phụ đề gốc trong 'srt_goc', đỏ khi
  mốc = phụ đề gốc mà thư mục trống).
- **TARGET VIDEO:** ô "Phụ đề gốc (mốc)" → "Thời lượng" (`VideoInfo.Duration` từ `mkvmerge -J` lúc quét, `UiText.Duration` → `1:54:36`).
  Phụ đề mốc vẫn xem ở cột "Phụ đề gốc (mốc)" của bảng.
- Đã thử bằng UI Automation (phim mẫu tự tạo, cấu hình tạm, kho mật khẩu `null`): Sáng + Acrylic, Tối + Acrylic, đổi chế độ lúc chạy; thẻ trạng
  thái đổi đúng theo từng thao tác. Test: 717 (thêm `Workbench_state_follows_current_choices`, `Duration_text`).

## Gợi ý tiếp theo

1. Chạy thử `dist\win-arm64\MuxStudio.exe --kiem-tra` trên một máy Windows on ARM.
2. Ký số file `.exe` (giảm cảnh báo SmartScreen / diệt virus) nếu sau này phát hành rộng.
