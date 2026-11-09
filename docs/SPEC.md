# MuxStudio — Đặc tả kỹ thuật (Giai đoạn 0)

> Tài liệu này mô tả **MuxStudio sẽ làm gì và thay thế từng phần của ba file .bat ra sao**.
> Chưa có dòng code Python nào được viết. Bạn duyệt xong (đặc biệt là **mục 10 – Câu hỏi**) thì tôi mới sang Giai đoạn 1.

Nguồn đã đọc toàn bộ:

| File | Số dòng | Số hàm | Phần cấu hình |
|---|---|---|---|
| `SubFetch_v30.bat` | 2.135 | 62 | dòng 50–166 |
| `SubPrep_v20.bat`  | 2.003 | 55 | dòng 21–92 |
| `MuxTool_v17.bat`  | 2.862 | 78 | dòng 32–121 (+ 3 biến ẩn ở dòng 1901–1903) |

Yêu cầu gốc: [`docs/PROMPT_MuxStudio.md`](PROMPT_MuxStudio.md). Số mục kiểu "§6.4" bên dưới là số mục trong file yêu cầu đó.

---

## Mục lục

1. [Tóm tắt nhanh (đọc trong 3 phút)](#1-tóm-tắt-nhanh)
2. [Kiến trúc](#2-kiến-trúc)
3. [Hàm trùng giữa các file → bản gộp trong `core/`](#3-hàm-trùng-giữa-các-file--bản-gộp)
4. [Bảng từng hàm PowerShell → Python](#4-bảng-từng-hàm-powershell--python)
5. [Bảng biến cấu hình](#5-bảng-biến-cấu-hình)
6. [`Read-Host` → màn hình GUI](#6-read-host--màn-hình-gui)
7. [Thay đổi so với .bat](#7-thay-đổi-so-với-bat)
8. [Kế hoạch kiểm thử](#8-kế-hoạch-kiểm-thử)
9. [Các giai đoạn & môi trường làm việc](#9-các-giai-đoạn--môi-trường)
10. [Câu hỏi cần bạn quyết](#10-câu-hỏi-cần-bạn-quyết)

---

## 1. Tóm tắt nhanh

**Kết luận chính khi đọc ba file:**

1. **Có một lỗi nặng chung cả ba file — `Format-SrtStamp` ghi sai giờ.** PowerShell `[int]` *làm tròn* chứ không cắt, nên `[int]$ts.TotalHours` biến 0:45:00 thành `01:45:00`. Mọi file SRT do script **tự ghi ra** đều dính: phụ đề gốc đọc từ PGS (`srt_goc`), file đã cắt recap, file đã sửa khối 00:00:00. Đã chạy lại hàm gốc để chứng minh (TĐ-01). Bản mới sửa.
2. **Ba bộ nhận diện tên tập khác nhau và đều có chỗ hỏng** (SubFetch không biết số tập anime; SubPrep coi `1917 - 2019` là tập 2019; MuxTool đọc `Show Season 2 Episode 5` thành tập tuyệt đối 5). Bản gộp lấy phần mạnh của từng bản và qua được mọi ví dụ (mục 3.4).
3. **Một số tuỳ chọn trong .bat không có tác dụng:** `$SkipIfExists`, `$KeepPaidDownloads` (SubFetch) chỉ được khai báo; `$FpsAutoAllowGuess` (SubPrep) và `$SkipOnMultipleEnAudio` (MuxTool) với cấu hình mặc định không bao giờ chạy tới; hàm `Get-OsScore` (phần "trừ điểm dịch máy") không bao giờ được gọi. Tôi **không bỏ âm thầm** các tính năng này — mục 7 và mục 10 đề xuất cách làm cho chúng chạy thật.
4. Vài chỗ **ghi đè file nguồn không hoàn tác được** (SubPrep sửa thẳng file trong `srt_can_sync` khi chuyển mã và cắt recap) — trái §7. Bản mới làm trên bản sao.
5. Chưa phát hiện API nào đổi. Việc **kiểm chứng API thật** thuộc Giai đoạn 2 (script dò `tools/probe_*.py`, bạn chạy bằng key thật).

**Bạn có thể tự kiểm chứng các lỗi ở mục 7.A trên Windows** bằng script `tools/verify_bat/KiemChung_LoiBat.ps1` (chỉ nạp hàm từ .bat, không chạy phần chính, không gọi mạng, không đọc key):

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify_bat\KiemChung_LoiBat.ps1 -BatDir "C:\thư mục chứa 3 file .bat"
```

---

## 2. Kiến trúc

### 2.1. Thư mục mã nguồn

Đúng như §4 yêu cầu (không thêm module ngoài danh sách):

```
muxstudio/
  core/                      # KHÔNG import tkinter/customtkinter — test được không cần GUI
    config.py    secrets.py   tools.py     probe.py     episodes.py   naming.py
    timing.py    pgs.py       srt.py       encoding.py  undo.py
    subsource.py opensubs.py  tmdb.py      tvdb.py
    pool.py      subfetch.py  subprep.py   mux.py       donor.py
  gui/
    app.py  fetch_view.py  subprep_view.py  mux_view.py  settings_view.py  widgets.py  log_panel.py
  tools/                     # script dò API (§2) + script kiểm chứng .bat
  tests/
  docs/SPEC.md
  build.bat
```

Vai trò từng module `core/`:

| Module | Nội dung |
|---|---|
| `encoding.py` | Nhận diện bảng mã (BOM UTF-8/UTF-16, UTF-8 nghiêm ngặt, bảng mã cũ), đọc văn bản phụ đề, chuyển về UTF-8 không BOM **ra file mới**, tên bảng mã cho `--sub-charset` |
| `srt.py` | Đọc/ghi khối SRT, `format_stamp`/`parse_stamp`, đọc mốc thời gian SRT/ASS/SSA/VTT, mốc từ `preview` SubSource |
| `pgs.py` | Đọc mốc thời gian từ PGS `.sup` (không OCR), ghi SRT mốc |
| `timing.py` | `to_double` (không phụ thuộc vùng miền), `fmt_inv` (luôn dấu chấm), bảng tỉ lệ FPS, `even_sample`, `offset_vote`, `timing_relation`, `quick_offset`, bảng băm ô 1 giây |
| `episodes.py` | Nhận diện tập (bản gộp), nhãn tập, chặn nhầm tập, so khớp tập giữa hai tên |
| `naming.py` | Từ khoá tên phim + điểm giống, tách tên phim/năm, bảng ngôn ngữ chung (mã 2/3 chữ, tên, bảng mã cũ…), tên file an toàn Windows, dựng tên/đường dẫn Jellyfin (phim lẻ, phim bộ) dùng `tmdb.py`/`tvdb.py` |
| `tools.py` | Tìm công cụ ngoài ("Tự tìm"), chạy tiến trình con an toàn (danh sách tham số, `CREATE_NO_WINDOW`, UTF-8, huỷ được), phiên bản mkvmerge + tên cờ theo phiên bản, cờ alass được hỗ trợ, chép file không dùng mẫu glob |
| `probe.py` | `mkvmerge -J` (có cache), `ffprobe`, FPS/thời lượng, tên codec/kênh, bitrate, HDR/DV, nhận diện track (Forced/SDH/commentary/audio description/phụ đề ảnh), chọn track phụ đề làm mốc |
| `undo.py` | Nhật ký thao tác CSV cho cả ba bước, giữ 20 lần gần nhất, hoàn tác nhiều lần chạy, xoá vào Thùng rác (`send2trash`) |
| `config.py` | Đọc/ghi `%APPDATA%\MuxStudio\config.json`, giá trị mặc định, **nhập cấu hình từ .bat** |
| `secrets.py` | Đọc/ghi 7 bí mật qua `keyring` (Windows Credential Manager), hàm che (vd. `sk_ab…1234`) |
| `subsource.py` / `opensubs.py` / `tmdb.py` / `tvdb.py` | Client API, mỗi cái có bộ nhớ đệm trong lượt chạy và hàm "Kiểm tra kết nối" |
| `pool.py` | Kho `srt_pool`: lập chỉ mục theo ngôn ngữ, đo thật từng ứng viên |
| `subfetch.py` | Bước ①: phụ đề gốc làm mốc, dùng file có sẵn, gom + xếp hạng ứng viên, nhớ lựa chọn, tải |
| `subprep.py` | Bước ②: trích phụ đề gốc, chỉ mục mốc, ghép tên, đổi tên, cắt recap, alass, sửa khối 00:00:00, sức khoẻ, dọn dẹp |
| `mux.py` | Bước ③: quy tắc audio/phụ đề, thẻ codec, dựng lệnh `mkvmerge`, thực thi + kiểm tra mã thoát |
| `donor.py` | Ghép audio từ file phim khác: tìm file nguồn, đo độ lệch, lệnh `ffmpeg` đổi tốc độ |

### 2.2. Lập kế hoạch rồi mới thực thi

Mỗi bước có hai hàm: `plan_*()` (chỉ đọc, không ghi gì ra đĩa, không tốn quota) và `execute_*(plan)`.
Kế hoạch là `dataclass`, GUI hiện và cho sửa trước khi bấm chạy. Nút **"Xem lệnh"** (thay `$DryRun`) in đúng lệnh `ffmpeg`/`mkvmerge`/`alass` sẽ chạy mà không ghi file nào.

| Bước | Kế hoạch (dataclass) | Sửa được trên GUI |
|---|---|---|
| ① | `FetchItem` cho mỗi *tập × ngôn ngữ*: file đích, phụ đề gốc dùng làm mốc (từ đâu), file có sẵn được nhận (kèm lý do), danh sách `Candidate` đã xếp hạng, ứng viên được chọn, `skip` | chọn ứng viên khác, bỏ qua ngôn ngữ, "dùng lại cho các tập sau cùng season" |
| ② | `RenameRow` (cũ → mới, nhãn tập, cảnh báo, lý do không ghép được); `SyncPair` (phụ đề, mốc, loại mốc, audio track, tham số alass, đường dẫn kết quả, dự báo lệch/FPS) | tick bỏ từng dòng; chọn mốc/track |
| ③ | `MuxPlan` cho mỗi file: tên/đường dẫn đầu ra, `--title`, track video/audio/phụ đề giữ/xoá kèm lý do, phụ đề ngoài, audio ghép (file nguồn, độ lệch ms, cách đo, có đổi FPS không), Default/Forced từng track, thẻ codec, thứ tự track, **danh sách tham số mkvmerge cuối cùng**, các lệnh ffmpeg cần chạy trước, trạng thái | sửa tên mới, tick track audio xoá, bảng chọn phụ đề theo tập, sửa độ lệch tay |

`Read-Host` trong .bat không còn là câu hỏi chặn giữa chừng: mọi quyết định nằm trong kế hoạch (mục 6).

### 2.3. Luồng phụ, tiến độ, nút Dừng

- Việc nặng chạy trong `threading.Thread`. Lõi nhận một đối tượng `Job` gồm: hàm log (mức + chữ), hàm tiến độ (tổng / file hiện tại), cờ dừng.
- Log và tiến độ đi qua `queue.Queue`; GUI đọc bằng `after(100 ms)`. GUI không bao giờ đợi tiến trình con.
- **Dừng sau file này:** đặt cờ; lõi kiểm tra cờ giữa các file.
- **Dừng ngay:** `tools.run()` ghi nhận mọi tiến trình con đang chạy; nút này kết thúc chúng (`terminate()` rồi `kill()` sau 3 s), xoá file đầu ra dở dang và ghi vào log.
- (GĐ5) `gui/widgets.Runner`: **một việc mỗi lúc** (nút của mọi thẻ khoá khi đang chạy); việc tự động (đếm file, kiểm tra key/nguồn, đọc audio của file mẫu) xếp hàng chạy ngay sau việc đang chạy; "Dừng ngay" bỏ luôn các việc đang xếp hàng. Phiên của mỗi thẻ (`FetchSession`/`PrepSession`/`MuxSession`) sống qua nhiều lần bấm nút; mỗi lần bấm là một `Job` mới, gắn vào phiên bằng `set_job(job)` (hàm mới ở lõi).

### 2.4. Cấu hình, bí mật, nhật ký, bộ nhớ đệm

- **Cấu hình:** `%APPDATA%\MuxStudio\config.json`, có khoá `"_version"`. Khoá giữ đúng tên biến trong .bat (mục 5) để nhập từ .bat không phải dịch. Khoá lạ được giữ nguyên khi ghi lại.
- **Bí mật** (`SubSourceApiKey`, `TmdbApiKey`, `TvdbApiKey`, `TvdbPin`, `OsApiKey`, `OsUsername`, `OsPassword`): `keyring`, dịch vụ `MuxStudio`, mỗi bí mật một mục. **Không** nằm trong `config.json`, không bao giờ in ra log (log đi qua bộ lọc thay mọi bí mật đang có bằng bản che), không có trong mã nguồn, ô nhập dạng `•••` có nút "Hiện".
- **Nhật ký + hoàn tác:** `_subfetch_logs\actions_*.csv`, `_subprep_logs\run_*.csv`, `_muxtool_logs\mux_*.csv`; cột `Time, Action, From, To, Detail` (MuxTool thêm `Status`). Cùng định dạng với .bat nên **màn hình Hoàn tác đọc được cả nhật ký cũ của .bat**. Giữ 20 lần gần nhất mỗi bước. Hoàn tác: file tạo ra → Thùng rác; file đã đổi tên/di chuyển → trả về chỗ cũ (theo thứ tự ngược).
- **Bộ nhớ đệm trong một lượt chạy** (đối tượng `RunCache`, xoá khi hết lượt): kết quả `mkvmerge -J`, mốc phụ đề gốc, IMDb ID, mục SubSource, danh sách phụ đề SubSource, gói ZIP đã tải (giải nén vào thư mục tạm), kết quả TMDB/TVDB, mốc thời gian track phụ đề của file nguồn audio.

### 2.5. Quy tắc an toàn (§6.1, §7)

- Đường dẫn dùng `pathlib`/`os.scandir`/`os.walk` nguyên văn. **Không** dùng `glob` trên đường dẫn của bạn (tên có `[YTS.MX]`, `[Judas]`).
- `subprocess.run/Popen([...])` với danh sách tham số, không `shell=True`, `creationflags=CREATE_NO_WINDOW`, đọc `encoding='utf-8', errors='replace'`.
- Mọi số thập phân đưa cho `ffmpeg`/`mkvmerge` qua `timing.fmt_inv()` (luôn dấu chấm); mọi số đọc từ API/JSON qua `timing.to_double()`.
- Không xoá/ghi đè file gốc: phụ đề tải về vào `srt_can_sync\`, phụ đề đã sync ghi cạnh file phim, kết quả mux vào `Output\`, tất cả ghi nhật ký hoàn tác.
- Mạng chỉ tới: `api.subsource.net`, `*.opensubtitles.com` (kể cả link tải do API trả về), `api.themoviedb.org`, `api4.thetvdb.com`.

---

## 3. Hàm trùng giữa các file → bản gộp

Ký hiệu: **SF** = SubFetch, **SP** = SubPrep, **MT** = MuxTool. "Giống hệt" = đã so từng dòng.

### 3.1. Phần console — bỏ hết (GUI thay)

| Hàm | Có ở | Thay bằng |
|---|---|---|
| `Initialize-Console` | SF, SP, MT (MT còn tắt MOUSE_INPUT để cuộn được) | Không cần: GUI không có QuickEdit/ANSI |
| `W`, `Write-Step` | SF, SP, MT / MT | `gui/log_panel.py`: mỗi dòng log có mức (`info`, `ok`, `warn`, `error`, `dim`) → màu |
| `Initialize-BoxChars`, `Get-BoxLine` | SF, SP, MT | Bỏ (khung bảng do Treeview vẽ) |
| `Set-ConsoleSize` | SF, MT | Bỏ |
| `Disable-QuickEdit` | SP | Bỏ |
| `Get-TableWidth`, `Show-CandidateTable` | SF | `gui/fetch_view.py` (bảng ứng viên, `FetchView.cands`) |
| `Show-AudioStreams` | SP | `gui/subprep_view.py` (bảng audio của file mẫu) |
| `Show-SubPickerGrid` (WinForms) | MT | `gui/mux_view.py` + `gui/widgets.CheckGrid` (bảng chọn phụ đề theo tập: lưới ☑/☐ vẽ trên Canvas — Treeview không tô màu được từng ô xám/vàng) |

### 3.2. Số và thời gian

| Bản gộp | SF | SP | MT | Chọn bản nào / khác nhau |
|---|---|---|---|---|
| `timing.to_double` | `ConvertTo-Double` | `ConvertTo-Double` | `ConvertTo-Double` | Giống hệt: thử InvariantCulture, rồi đổi `,`→`.`; hỏng trả 0.0 |
| `timing.fmt_inv` | — | — | `Format-Inv` | Một bản. Định dạng `0.######` ⇒ Python: làm tròn 6 chữ số, bỏ số 0 thừa, luôn dấu chấm |
| `timing.FPS_RATIOS` | `$script:FpsRatios` | như SF | như SF | Cùng 7 tỉ lệ, cùng thứ tự; chỉ khác tên mô tả. Lấy tên dài của SP/MT ("24 → 23.976 (hoặc 30 → 29.97)") |
| `timing.even_sample` | `Get-EvenSample` | giống | giống | Giống hệt. **Chú ý khi port:** `$arr[[int]($i*$step)]` — `[int]` của PowerShell *làm tròn về số chẵn* (2.5→2, 7.5→8). Python dùng `int(round(i*step))` (cũng làm tròn về số chẵn) — đã kiểm cho ra cùng chỉ số (C5) |
| `timing.offset_vote` | `Get-OffsetVote` | giống | giống | Giống hệt: ô 0,15 s, ±400 s, `[math]::Round` (về số chẵn), phiếu nhiều nhất = khoá **gặp trước** khi bằng phiếu (Dictionary .NET giữ thứ tự chèn — Python `dict` cũng vậy), trung bình các hiệu trong ±0,35 s quanh tâm |
| `timing.timing_relation` | `Get-TimingRelation` (cứng 400/100) | dùng `$FpsSampleRef/$FpsSampleTgt` | dùng biến, gán cứng 400/100 ở dòng 1901 | Gộp: tham số `n_ref=FpsSampleRef(400)`, `n_tgt=FpsSampleTgt(100)`. Cần ≥ 30 mốc mỗi bên. Tỉ lệ thắng = nhiều phiếu nhất, bằng nhau lấy tỉ lệ đứng trước. `FpsMismatch = \|Scale−1\| > 0.0002`; `Drift = \|Scale−1\| × mốc cuối` |
| `timing.quick_offset` | `Get-QuickOffset` | — | — | Một bản: ô 0,25 s, ±180 s, ≥ 3 mốc preview, **mọi** mốc phải cùng phiếu |
| `timing.RefBins` | — | trong `Remove-LeadingExtra` | — | Bảng băm `floor(giây)`; tra 4 ô `c, c−1, c+1, c−2` (đúng như .bat) |
| `srt.format_stamp` | `Format-SrtStamp` | giống | giống | Giống hệt **và cùng lỗi giờ** (TĐ-01). Bản mới: giờ = `tổng_giây // 3600`; mili-giây làm tròn gần nhất như .NET Framework (PowerShell 5.1) |
| `srt.parse_stamp` | (trong `Get-CueTimes`) | `ConvertFrom-Stamp` (chỉ nhận dấu phẩy) | — | Gộp: nhận `,` hoặc `.`, 2–3 chữ số lẻ (`"0."+lẻ`, như .bat) |

### 3.3. Đọc mốc thời gian, PGS, mã hoá

| Bản gộp | SF | SP | MT | Chọn bản nào / khác nhau |
|---|---|---|---|---|
| `srt.cue_times` | `Get-CueTimes`: `ReadAllText` (nhận BOM), ASS `Dialogue:\s*[^,]*,` | `Get-Content -Raw` (PowerShell 5.1 đọc theo bảng mã ANSI nếu không có BOM), ASS `Dialogue:\s*\d+,` | như SP | **Chọn SF** (mẫu ASS rộng hơn: nhận cả SSA v4 `Dialogue: Marked=0,…`), đọc văn bản qua `encoding.read_text` (nhận UTF-16). Thêm mốc VTT dạng `mm:ss.ttt` (không có giờ) vì SP coi `.vtt` là phụ đề hợp lệ |
| `pgs.pgs_to_timing_srt` | `Convert-PgsToTimingSrt` (chữ `*` cứng) | dùng `$PgsTimingText` | dùng `$PgsTimingText` (gán cứng `*`) | Giống hệt phần đọc segment: `PG`, PTS 4 byte / 90000, loại `0x16`, payload ≥ 11, bỏ PCS có cờ đổi bảng màu (`payload[8] ≠ 0`), `payload[10]` > 0 = hiện, = 0 = xoá, dòng chưa đóng gặp dòng mới thì đóng, bỏ dòng ≤ 0,1 s. Chữ giữ chỗ = `PgsTimingText`. Ghi qua `srt.format_stamp` đã sửa |
| `encoding.detect_kind` | — | `Get-SubEncodingKind` | `Get-SubEncodingKind` | Giống hệt: `utf8bom`, `utf16le`, `utf16be`, `utf8` (nghiêm ngặt), `legacy`, `empty`, `error` |
| `encoding.legacy_codepage` | — | `Get-LegacyCodePage` | (trong `Get-SubCharsetArg`) | Gộp một bảng: vi 1258, cs 1250, ja 932, ko 949, zh 936, th 874, ru 1251, pl 1250, tr 1254, ar 1256, còn lại 1252 |
| `encoding.to_utf8` | — | `ConvertTo-Utf8Sub` (ghi đè file) | — | Giữ cách giải mã; **ghi ra file mới**, không ghi đè nguồn (TĐ-12) |
| `encoding.mkvmerge_charset` | — | — | `Get-SubCharsetArg` | Một bản; tên bảng mã lấy từ cùng bảng trên (`windows-1258`, `windows-1250`, `shift_jis`, `euc-kr`, `gbk`, `windows-874`, `UTF-16LE/BE`…) |

### 3.4. Nhận diện tập — bản gộp `episodes.parse_episode`

Ba bản gốc:

| | SF `Get-EpisodeInfo` | SP `Get-EpisodeInfo` | MT `Split-EpisodeName` |
|---|---|---|---|
| `S01E05`, `S21E1085`, `1x05` | ✔ | ✔ | ✔ |
| `S01E05-E06` / `S01E05E06` (tập đôi) | chỉ lấy E05 / ✘ | chỉ lấy E05 / ✘ (đoán bừa tập 6) | ✔ / ✔ |
| `Season 2 Episode 5` | ✘ | ✔ | ✘ (thành tập tuyệt đối 5, series "Show Season 2") |
| `Ep 5`, `E05`, `Tap 12` | ✘ | ✔ | chỉ `EP`/`Episode` |
| `One Piece - 0643`, `- 05v2` | ✘ | ✔ | ✔ |
| `One Piece - 1085[1080p]` (dính ngoặc) | ✘ | "không chắc" | ✔ |
| `1917 - 2019` | ✔ phim lẻ | ✘ **tập 2019** | ✔ phim lẻ |
| Bỏ mã CRC `[ABCD1234]`, bỏ `[Nhóm]` đầu tên | ✘ | ✔ | chỉ `[Nhóm]` đầu |
| Trả tên series + năm | ✘ | ✘ | ✔ |
| Đoán số "không chắc" (để báo, không ghép) | ✘ | ✔ | ✘ |

**Bản gộp** (lấy cấu trúc MT + các mẫu bổ sung của SP + cờ "chắc chắn" của SP):

1. Tiền xử lý: bỏ `[8 ký tự hex]`; bỏ `[…]` ở đầu; `._` → dấu cách; gộp khoảng trắng.
2. Thử lần lượt (mẫu đầu tiên khớp thắng):
   1. `S(\d{1,2})\s?E(\d{1,4})` + tập cuối tuỳ chọn `(-?E\d{1,4})` → có season, chắc chắn.
   2. `(?<!\d)(\d{1,2})x(\d{2})(?!\d)` → có season.
   3. `Season\s?N … (Episode|Ep|Tap)\s?M` → có season.
   4. Số tuyệt đối sau ` - `: `\s-\s(?:E|EP|Episode\s?)?(\d{1,4})(?:v\d)?(?=\s|$|\[|\()`.
   5. `\b(?:Episode|Ep|E|Tap)\s?(\d{1,4})(?:v\d)?\b`.
   - Ở 4 và 5: số 4 chữ số trong 1900–2099 là **năm** → không nhận, thử mẫu kế tiếp; số phải > 0.
3. Không mẫu nào khớp → phép đoán cuối của SP (bỏ năm, độ phân giải, codec… rồi lấy số cuối) với `confident=False`. **Chỉ dùng để hiển thị/báo lỗi, không bao giờ dùng để ghép hay chặn.**
4. Series = phần trước mẫu khớp; năm = năm cuối trong phần đó (quy tắc năm ở 3.5); bỏ ngoặc, cắt ` -_`.

Kết quả `EpisodeInfo(series, year, season|None, episode, episode_end|None, absolute, has_season, confident)` với hai nhãn:
`label` = `S01E05` / `E643` (kiểu SP, dùng trong bảng) và `ref_label` = `S01E05` / `tap #643` (kiểu MT, dùng làm khoá `DonorOffsetMs`).

**Chặn nhầm tập** `episodes.episode_conflict(a, b)` (thay `Test-EpisodeConflict` của SF, dùng ở cả ba bước): chỉ xét khi **cả hai** tên nhận diện chắc chắn; cả hai có season → khác season hoặc khác tập là xung đột; một bên không có season → khác số tập là xung đột. (`S01E10`/`S01E01` → chặn; `- 0643`/`- 0644` → chặn; `S16E643`/`- 0643` → không chặn.)

**Cùng tập** `episodes.same_episode(a, b)` (dùng khi chọn file trong gói season, chọn file nguồn audio): cả hai có season → trùng season và tập; cả hai tuyệt đối → trùng tập; một bên có season một bên tuyệt đối → trùng số tập **và** (season = 1 hoặc số tập ≥ 100) — đúng quy tắc của `Find-DonorFile`.

Bản gộp phải qua **mọi ví dụ trong ba file và trong §6.6**: `S01E05`, `S21E1085`, `1x05`, `One Piece - 0643`, `[Judas] One Piece - 1085 [1080p]`, `Naruto EP 055`, `One Piece S16E643`, `S01E05-E06`, `S02E05`, `1917 - 2019` (phim lẻ), `Blade Runner 2049` (phim lẻ), `Movie - 1080p` (phim lẻ), `…DDP5.1…` (không được thành tập 1), `S01E10` vs `S01E01` (chặn), không bao giờ xuất `Season 00`.

### 3.5. Tên phim, từ khoá, năm

| Bản gộp | SF | SP | MT | Chọn bản nào / khác nhau |
|---|---|---|---|---|
| `naming.title_tokens` | `Get-Tokens` | `Get-TitleTokens` | — | Giống nhau trừ danh sách từ nhiễu: SP thêm `avi` và thể loại (`action drama comedy…`). **Chọn SP** |
| `naming.title_score` | `Get-Score` | `Get-TitleScore` | — | Giống hệt: số từ chung / số từ của bên nhiều hơn |
| `naming.split_release_name` | `Get-CleanTitle` + `Get-Year` | `Get-TitleKey` + `Get-TitleYear` | `Split-ReleaseName` | **Chọn MT làm khung** (năm = năm *cuối cùng* trước phần kỹ thuật), danh sách từ kỹ thuật = hợp của ba bản, bỏ mọi `[…]` như SF, dừng ở mã tập. Quy tắc năm mới (TĐ-05): năm ở đầu tên mà bỏ đi thì tên rỗng → là một phần tên (`2001 A Space Odyssey`); năm > năm hiện tại + 1 → là một phần tên (`Blade Runner 2049`) |
| `naming.title_year` | `Get-Year` (năm **đầu tiên**) | `Get-TitleYear` (năm đầu tiên) | (năm cuối) | Theo `split_release_name`. Khác SF/SP: `Blade.Runner.2049.2017` → 2017 (cũ: 2049) |
| `naming.title_key` | — | `Get-TitleKey` | — | Một bản (SP chỉ dùng để hiển thị) |
| `naming.norm_title` | — | — | `Get-NormTitle` | Một bản: chỉ giữ chữ + số, chữ thường |
| `naming.safe_filename` | — | — | `Get-SafeFileName` | Một bản: `:` → ` -`, bỏ `\/:*?"<>\|`, gộp khoảng trắng, bỏ dấu chấm cuối |

### 3.6. Ngôn ngữ — một bảng chung `naming.LANGS`

Bảy chỗ khác nhau trong .bat đang làm cùng một việc với danh sách khác nhau:

| Hàm/biến gốc | Có ở | Ngôn ngữ biết |
|---|---|---|
| `$LangMap` (tên SubSource) | SF | 18: en vi cs ja ko zh fr de es th id ar pt ru it pl nl tr |
| `ConvertTo-PoolLangCode` | SF | vi cs en ja ko zh fr de es th |
| `ConvertTo-LangCode`, `$KnownLangTags`, `Split-LangTag`, `Get-FileLangTag` | SP | vi cs(cz) en ja ko zh fr de es th |
| `Test-LangMatch` | SP | so 2↔3 chữ — **có lỗi** (TĐ-04) |
| `Get-LangCode`, `Get-LangName`, `Get-LangForMux` | MT | vi cs en ja ko zh fr de es th (+ trả nguyên mã lạ) |
| Hậu tố sub ngoài trong `Get-SubAutoPlan` / BƯỚC 3 / `Measure-DonorTiming` | MT | **chỉ** vi cs en |
| `Get-LegacyCodePage` / `Get-SubCharsetArg` | SP / MT | xem 3.3 |

Bản gộp: một bảng gồm 18 ngôn ngữ của `$LangMap` + mọi mã trong `LangExtra`. Mỗi dòng: mã 2 chữ, mã ISO 639-2/B và /T (`cze`/`ces`, `ger`/`deu`…), các cách viết (`viet`, `vietnamese`, `tiengviet`, `vn`, `cz`, `sec`, `anh`…), tên tiếng Anh (`Vietnamese`), tên SubSource (`vietnamese`), bảng mã cũ, tên bảng mã cho mkvmerge.
Các hàm: `lang_from_word` (thay `ConvertTo-PoolLangCode`/`ConvertTo-LangCode`), `lang_from_track(language, language_ietf)` (thay `Get-LangCode`; mã lạ trả nguyên, không rõ → `und`), `lang_match` (thay `Test-LangMatch`, đã sửa), `lang_name` (thay `Get-LangName`), `lang_for_mux(code, mkv_ver)` (thay `Get-LangForMux`: v50+ dùng 2 chữ, cũ hơn dùng ISO 639-2/B), `split_lang_tag`, `lang_tag_of_file(file, root)` (thay `Get-FileLangTag` + `Get-PoolLang`: thư mục con cấp 1 → hậu tố cuối tên → phần cuối sau `._-`).

### 3.7. Công cụ ngoài, thông tin file

| Bản gộp | SF | SP | MT | Chọn bản nào / khác nhau |
|---|---|---|---|---|
| `tools.join_safe` | `Join-Safe` | `Join-Safe` | `Join-Safe` | Giống hệt |
| `tools.find_tool(name)` | vòng lặp đầu file (3 chỗ) | `Find-MkvTool`, `Find-Alass`, `Find-Near` | `Find-Exe` + danh sách 8 thư mục + registry `HKLM\SOFTWARE\*\MKVToolNix*` | **Hợp của mọi danh sách**, theo thứ tự: đường dẫn trong Cài đặt → `MkvToolNixDir`/`AlassDir`/`ExtraToolDirs` → Desktop thật (chống OneDrive) → `%USERPROFILE%\Desktop\…` → thư mục phim (+`\mkvtoolnix`, `\alass`) → Program Files (x86) → `%LOCALAPPDATA%\Programs\MKVToolNix` → registry → `PATH`. `ffmpeg`/`ffprobe` tìm cạnh alass trước (như `Find-Near`) |
| `tools.mkvmerge_version` + cờ | — | — | khối "Doi ten option theo phien ban" | Một bản: `--version`, v74+ → `--default-track-flag`/`--forced-display-flag` |
| `tools.alass_supported_flags` | — | `Get-SupportedAlassFlags` | — | Một bản |
| `tools.copy_file` | `Save-ToDest` | — | — | Một bản (tạo thư mục đích, chép bằng đường dẫn nguyên văn) |
| `probe.mkv_json` | `mkvmerge -J` ở 2 chỗ | 3 chỗ | 3 chỗ + `$script:JsonCache` | Một bản có cache; mã thoát ≤ 1 là đọc được |
| `probe.video_fps` | `Get-VideoFps` (làm tròn 3 số) | — | `Get-VideoFpsOf` (không làm tròn) | Trả số chính xác; chỗ so hạng FPS (SF) làm tròn 3 số như cũ |
| `probe.duration` | — | `Get-VideoDuration` | `Get-DurationOf` | Giống nhau (`container.properties.duration` / 1e9) |
| `probe.ffprobe_streams` | — | `Get-AudioStreams` | `Get-FfprobeBitrates`, `Get-DynamicRange` | Một lần gọi ffprobe/file, có cache |
| `probe.is_forced/is_sdh/is_commentary/is_descriptive/is_image_sub` | viết tay trong `Get-RefTimes` | viết tay trong `Invoke-ExtractRefSubs` | `Test-CommentaryTrack`, `Test-DescriptiveTrack`, `Test-ImageSub` + viết tay 4 chỗ | Một bản; mẫu chữ giữ nguyên (`forced`, `\bSDH\b\|hearing`, cờ Matroska…) |
| `probe.pick_ref_sub_track(json, lang_priority)` | trong `Get-RefTimes` (văn bản trước PGS, SDH +5, **không** xét ngôn ngữ) | trong `Invoke-ExtractRefSubs` (ngôn ngữ ×10, SDH +5, PGS +1000, nhận VTT) | `Get-RefSubTracks` (theo ngôn ngữ, hạng văn bản/SDH/PGS) | **Chọn SP** (mạnh nhất) cho bước ① và ②; MT vẫn chọn theo ngôn ngữ chung (cần cho đo audio ghép) qua cùng hàm với tham số ngôn ngữ |

### 3.8. Nhật ký, hoàn tác, xoá file

| Bản gộp | SF | SP | MT |
|---|---|---|---|
| `undo.RunLog.add/save` | `Add-Action`, `Save-ActionLog` (giữ 20) | `Add-RunLog`, `Save-RunLog` (giữ `$KeepUndoRuns`) | `Add-MuxLog`, `Save-MuxLog` (không giới hạn, không hoàn tác) |
| `undo.list_runs` / `plan_undo` / `execute_undo` / `undo_runs` | `Invoke-UndoRun` (xoá **hẳn**) | `Invoke-Undo` (xoá **hẳn**) | — (TĐ-17) |
| `undo.trash` | — | `Remove-FileSafe` (Thùng rác qua VisualBasic) | — |

Báo cáo không dùng cho hoàn tác vẫn giữ: SF `fetch_*.csv`, SP `health_*.csv`.

---

## 4. Bảng từng hàm PowerShell → Python

"gộp" = xem mục 3. "bỏ" = có lý do ở cột ghi chú. Tên Python là dự kiến, có thể đổi nhẹ khi viết nhưng sẽ cập nhật lại bảng này.

### 4.1. `SubFetch_v30.bat` (62 hàm)

| # | Hàm (dòng) | → Python | Ghi chú |
|---|---|---|---|
| 1 | `Set-ConsoleSize` (24) | bỏ | console |
| 2 | `Initialize-Console` (201) | bỏ | console |
| 3 | `W` (252) | bỏ → `gui/log_panel` | màu theo mức log |
| 4 | `Initialize-BoxChars` (264) | bỏ | console |
| 5 | `Get-BoxLine` (275) | bỏ | console |
| 6 | `Get-Tokens` (296) | `naming.title_tokens` | gộp (3.5) |
| 7 | `Get-Score` (311) | `naming.title_score` | gộp |
| 8 | `Get-Year` (318) | `naming.title_year` | gộp, quy tắc năm mới (TĐ-05) |
| 9 | `Get-EpisodeInfo` (324) | `episodes.parse_episode` | gộp (3.4) |
| 10 | `Get-CleanTitle` (334) | `naming.split_release_name().title` | gộp |
| 11 | `Join-Safe` (348) | `tools.join_safe` | gộp |
| 12 | `Get-VideoFps` (365) | `probe.video_fps` | gộp |
| 13 | `ConvertTo-Double` (386) | `timing.to_double` | gộp |
| 14 | `Get-CueTimes` (399) | `srt.cue_times` | gộp — bản này được chọn |
| 15 | `Get-EvenSample` (427) | `timing.even_sample` | gộp |
| 16 | `Get-OffsetVote` (435) | `timing.offset_vote` | gộp |
| 17 | `Get-TimingRelation` (461) | `timing.timing_relation` | gộp |
| 18 | `Format-SrtStamp` (485) | `srt.format_stamp` | gộp, **sửa lỗi giờ** (TĐ-01) |
| 19 | `Convert-PgsToTimingSrt` (492) | `pgs.pgs_to_timing_srt` | gộp |
| 20 | `Find-RefInFolder` (538) | `subfetch.RefProvider._find_in_folder` | lọc tập trước, rồi điểm tên ≥ 0,35 (+1 nếu đúng tập); nhận `.sup` → chuyển SRT mốc |
| 21 | `Get-RefTimes` (579) | `subfetch.RefProvider.get` | thứ tự mới: `srt_goc` trước, rồi mới trích MKV (TĐ-08); giữ đuôi thật `.ass` (TĐ-09) |
| 22 | `Get-PoolLang` (685) | `naming.lang_tag_of_file` | gộp (3.6) |
| 23 | `ConvertTo-PoolLangCode` (703) | `naming.lang_from_word` | gộp |
| 24 | `Initialize-Pool` (720) | `pool.build_index` | bỏ file Forced/SDH theo tên; `PoolDefaultLang` |
| 25 | `Invoke-Sub` (757) | `subsource.SubSourceClient.get` | `X-API-Key`, nghỉ `ApiPauseMs` giữa lượt, 429 → chờ 20 s, 4xx → dừng, lỗi mạng thử 3 lần |
| 26 | `Get-ImdbId` (797) | `tmdb.TmdbClient.imdb_id` | dùng chung TMDB (hỗ trợ `eyJ…`, chọn series có tập) (TĐ-10) |
| 27 | `Get-ImdbIdCached` (819) | `tmdb` + `RunCache` | |
| 28 | `Find-Entry` (828) | `SubSourceClient.find_entry` | IMDb trước, rồi text; lọc season / năm ±1; giống tên nhất |
| 29 | `Get-AllSubs` (880) | `SubSourceClient.list_all` | **không lọc phía máy chủ**; `limit=100` → lùi 20; tối đa `MaxPages` |
| 30 | `Test-SubLang` (905) | `subsource.lang_matches` | so tên đầy đủ/mã/biến thể `viet*`, `vi-*`… (mở rộng cho mọi ngôn ngữ trong bảng chung) |
| 31 | `Get-SubList` (919) | `SubSourceClient.list_for_lang` | |
| 32 | `Get-LangSummary` (926) | `subsource.lang_summary` | hiện "SubSource có: english(40), vietnamese(3)…" |
| 33 | `Get-FpsTier` (941) | `subfetch.fps_tier` | chênh lệch FPS khai báo và FPS video < 0,05 → hạng 1 |
| 34 | `Get-FpsTierText` (949) | `subfetch.Candidate.fps_label` | không được gọi trong .bat; GUI dùng cho cột FPS |
| 35 | `Get-CandidateScore` (957) | bỏ | **không được gọi** trong .bat; việc này do `title_score` làm |
| 36 | `Get-ErrBody` (980) | bỏ | `requests` trả thẳng nội dung phản hồi lỗi |
| 37 | `Invoke-Os` (998) | `opensubs.OpenSubtitlesClient.request` | `Api-Key`, User-Agent cấu hình được, token Bearer, máy chủ theo `base_url`; 403 `user-agent` → tắt nguồn; 401 → đăng nhập lại tối đa 2 lần; 429 → chờ 5 s |
| 38 | `Connect-OpenSubtitles` (1078) | `OpenSubtitlesClient.login` + `refresh_quota` | |
| 39 | `Test-OsQuota` (1113) | `OpenSubtitlesClient.quota_ok` / `quota_level` | còn ≤ `OsStopRemaining` → ngừng; ≤ `OsWarnRemaining` → cảnh báo (dùng `≤` như .bat) |
| 40 | `Get-OsCandidates` (1130) | `OpenSubtitlesClient.search` | 3 lần thử: IMDb + `order_by` → bỏ sắp xếp → theo tên + năm |
| 41 | `Get-OsScore` (1174) | bỏ | **không được gọi** trong .bat. Phần "trừ điểm dịch máy" → câu hỏi Q2 |
| 42 | `Test-OsUsable` (1199) | `opensubs.usable` | |
| 43 | `Save-OsSubtitle` (1213) | `OpenSubtitlesClient.download` | `POST /download` → `link`, `remaining`, `reset_time`/`message`; file < 64 byte = rỗng |
| 44 | `Get-SubtitlePack` (1262) | `SubSourceClient.download_pack` | cache theo `subtitleId`; chỉ ZIP (RAR/7z báo "cần 7-Zip", bỏ qua) |
| 45 | `Select-FromPack` (1298) | `subsource.select_from_pack` | bỏ `forced/sdh/hi/hearing/cc`; lọc **đúng tập** bằng `same_episode` (TĐ-02) |
| 46 | `Save-ToDest` (1337) | `tools.copy_file` | |
| 47 | `Get-PreviewTimes` (1353) | `srt.preview_times` | |
| 48 | `Get-QuickOffset` (1365) | `timing.quick_offset` | |
| 49 | `New-Cand` (1383) | `subfetch.Candidate` (dataclass) | |
| 50 | `Get-PoolCandidates` (1391) | `pool.pool_candidates` | lọc tập **trước** rồi mới cắt `PoolTryLimit`; < `PoolMinVotes` bỏ (in tên); < `MinVerifyVotes` "nghi ngờ"; lệch FPS → hạng 3 |
| 51 | `Get-OnlineCandidates` (1467) | `FetchSession.online_candidates` | gói đã tải → đo thẳng file của tập; chưa tải → ước tính từ preview |
| 52 | `Add-Action` (1530) | `undo.RunLog.add` | gộp |
| 53 | `Save-ActionLog` (1536) | `undo.RunLog.save` | gộp |
| 54 | `Invoke-UndoRun` (1551) | `undo.plan_undo` + `undo.execute_undo` + màn hình Hoàn tác | Thùng rác (TĐ-16) |
| 55 | `Test-EpisodeConflict` (1603) | `episodes.episode_conflict` | gộp, nhận cả số tuyệt đối (TĐ-02) |
| 56 | `Use-ExistingSub` (1610) | `FetchSession.match_existing` (lập kế hoạch) + `_apply_existing` (thực thi) | đo được → chỉ tin đo (≥ 20 mốc); không đo được → tên ≥ 0,35; **chép**, không di chuyển (mặc định `copy`) |
| 57 | `Get-TableWidth` (1708) | bỏ | console |
| 58 | `Limit-Text` (1718) | `gui/widgets.ellipsize_middle` | cắt giữa, giữ đuôi (tên nhóm release). Test so với hàm gốc ✅ |
| 59 | `Show-CandidateTable` (1727) | `gui/fetch_view.FetchView` (bảng ứng viên, `candidate_row`) | §5.1 mục 4 |
| 60 | `Select-Candidate` (1770) | `subfetch.ChoiceMemory` + `FetchSession.auto_select`/`choose` + GUI | nhớ theo `movieId\|ngôn ngữ`; không nhớ bản riêng của tập khác |
| 61 | `Get-ChosenSubtitle` (1853) | `FetchSession._fetch` + `_fetch_item` | |
| 62 | `Report-Timing` (1873) | `FetchSession.report_timing` | **chỉ báo**, không sửa thời gian |
| — | Phần chính (1901–2135) | `FetchSession.scan` → `gather` → `auto_select`/`choose`/`skip` → `execute` | thứ tự nguồn §6.11; TĐ-06, TĐ-07, TĐ-11 |

### 4.2. `SubPrep_v20.bat` (55 hàm)

| # | Hàm (dòng) | → Python | Ghi chú |
|---|---|---|---|
| 1 | `Initialize-Console` (101) | bỏ | console |
| 2 | `W` (152) | bỏ → `gui/log_panel` | |
| 3 | `Initialize-BoxChars` (164) | bỏ | console |
| 4 | `Get-BoxLine` (175) | bỏ | console |
| 5 | `Disable-QuickEdit` (186) | bỏ | console |
| 6 | `Add-RunLog` (216) | `undo.RunLog.add` | gộp |
| 7 | `Save-RunLog` (222) | `undo.RunLog.save` | gộp |
| 8 | `ConvertTo-Double` (241) | `timing.to_double` | gộp |
| 9 | `ConvertFrom-Stamp` (256) | `srt.parse_stamp` | gộp |
| 10 | `Read-SrtBlocks` (261) | `srt.read_blocks` | giữ phần đuôi sau mốc (vị trí `X1:…`), nhận cả `.` mili-giây |
| 11 | `Write-SrtBlocks` (277) | `srt.write_blocks` | UTF-8 không BOM, đánh số lại; dùng `format_stamp` đã sửa |
| 12 | `Remove-LeadingExtra` (297) | `subprep.remove_leading_extra` | ≥ 40 dòng; quan hệ trên 70% cuối (≥ `MinVerifyVotes`); tìm dòng đầu có 5 dòng liên tiếp khớp; giới hạn tìm 30% đầu; huỷ nếu giữ < 60%. Hàm thuần trên khối SRT; `execute_sync` gọi trên **bản sao** (TĐ-12). Test đối chiếu hàm gốc ✅ |
| 13 | `Repair-CollapsedCues` (339) | `subprep.repair_collapsed_cues` | > `MaxZeroStartCues` dòng bắt đầu < 0,05 s → bỏ, nếu còn ≥ 10 dòng. Test đối chiếu ✅ |
| 14 | `Get-VideoDuration` (352) | `probe.duration` | gộp |
| 15 | `Test-SubHealth` (366) | `subprep.sub_health` → `Health` | 7 dấu hiệu, mức 0/1/2, kèm mã dấu hiệu (`codes`) cho GUI; đọc mốc chung cho cả ASS/VTT qua `srt.cue_spans` (TĐ-14). Test đối chiếu ✅ |
| 16 | `Get-EpisodeInfo` (442) | `episodes.parse_episode` | gộp — giữ cờ `confident` |
| 17 | `Get-TitleTokens` (496) | `naming.title_tokens` | gộp — bản này được chọn |
| 18 | `Get-TitleYear` (513) | `naming.title_year` | gộp |
| 19 | `Get-TitleScore` (520) | `naming.title_score` | gộp |
| 20 | `Get-TitleKey` (527) | `naming.title_key` | gộp |
| 21 | `Get-EpLabel` (539) | `EpisodeInfo.label` | gộp |
| 22 | `Split-LangTag` (546) | `naming.split_lang_tag` | dùng bảng chung; `cz` → `cs` (TĐ-15) |
| 23 | `Get-SubEncodingKind` (558) | `encoding.detect_kind` | gộp |
| 24 | `Get-LegacyCodePage` (572) | `encoding.legacy_codepage` | gộp |
| 25 | `ConvertTo-Utf8Sub` (580) | `encoding.to_utf8` | ghi ra file mới (TĐ-12) |
| 26 | `Invoke-NormalizeEncoding` (606) | trong `PrepSession.execute_sync` (`encoding.to_utf8` nguồn → bản sao tạm) | chuyển mã **bản sao tạm** trước alass, báo "cp1258 → UTF-8"; file nguồn không đổi (TĐ-12) |
| 27 | `ConvertTo-LangCode` (622) | `naming.lang_from_word` | gộp |
| 28 | `Get-FileLangTag` (642) | `naming.lang_tag_of_file` | gộp — bản này được chọn |
| 29 | `Get-EvenSample` (678) | `timing.even_sample` | gộp |
| 30 | `Get-OffsetVote` (687) | `timing.offset_vote` | gộp |
| 31 | `Get-TimingRelation` (713) | `timing.timing_relation` | gộp |
| 32 | `Join-Safe` (744) | `tools.join_safe` | gộp |
| 33 | `Find-Alass` (749) | `tools.find_tool('alass')` | gộp |
| 34 | `Find-Near` (765) | `tools.find_near` | gộp |
| 35 | `Get-AudioStreams` (781) | `probe.ffprobe_streams(kind='a')` + `probe.audio_streams` | gộp; có bộ nhớ đệm |
| 36 | `Show-AudioStreams` (791) | `PrepSession.sample_audio_streams` + `gui/subprep_view` | bảng audio của file mẫu (`AudioStream.describe`) |
| 37 | `Find-MkvTool` (802) | `tools.find_tool` | gộp |
| 38 | `Get-SubExtByCodec` (818) | `probe.sub_ext_by_codec` | SRT/ASS/SSA/VTT; PGS nếu `PgsTimingFallback` |
| 39 | `Format-SrtStamp` (836) | `srt.format_stamp` | gộp, sửa lỗi giờ |
| 40 | `Convert-PgsToTimingSrt` (842) | `pgs.pgs_to_timing_srt` | gộp |
| 41 | `Get-CueTimes` (890) | `srt.cue_times` | gộp |
| 42 | `Get-MaxShift` (909) | `srt.max_shift` | lấy lớn hơn giữa độ dịch ở dòng đầu và ở dòng cuối |
| 43 | `Test-LangMatch` (920) | `naming.lang_match` | gộp, **sửa lỗi** (TĐ-04) |
| 44 | `Convert-LooseSupFiles` (941) | `RefIndex.build(sup_fn=…)` + `PrepSession._write_pending_ref` | `.sup` bạn tự bỏ vào (chưa có `.srt` cùng tên) → lập kế hoạch đọc mốc ra thư mục tạm; **lúc chạy** mới ghi `.srt` cạnh `.sup` (ghi nhật ký để hoàn tác) |
| 45 | `Test-ExcludedPath` (955) | `PrepSession.is_excluded` / `excluded_dirs` | + tự loại `DonorFolder`, `PoolFolder`, `SubFolderName`, thư mục nhật ký (TĐ-13) |
| 46 | `Get-RefIndex` (965) | `PrepSession.build_index` + `subprep.RefIndex.build` | hiện "Mốc lấy từ: …(n)". `$byTitle` của .bat không được dùng ở đâu → bỏ |
| 47 | `Select-RefVariant` (1064) | `subprep.select_ref_variant` | video > phụ đề không hậu tố > còn lại |
| 48 | `Resolve-Ref` (1073) | `subprep.resolve_ref` → `RefMatch` | đúng 4 tầng của .bat; trả kèm **lý do** + chi tiết khi không ghép được (cho cột "Lý do"); sửa 3 lỗi ghép nhầm tập (TĐ-31). Test đối chiếu ✅ |
| 49 | `Invoke-ExtractRefSubs` (1181) | `PrepSession.plan_extract_refs` / `extract_refs` | non-SDH, theo `RefSubLangs` (`probe.pick_ref_sub_track`), PGS chỉ khi không còn gì; trích qua thư mục tạm rồi chép vào `srt_goc` (nhật ký) |
| 50 | `Invoke-Rename` (1299) | `PrepSession.plan_rename` / `execute_rename` → `RenameRow` | lý do: "không đoán được ngôn ngữ", "nhập nhằng", "không tìm được mốc", "trùng đích", "đích đã tồn tại", "đã đúng tên" |
| 51 | `Get-SupportedAlassFlags` (1391) | `tools.alass_supported_flags` | gộp; cờ không hỗ trợ thì bỏ cả giá trị đi kèm (TĐ-32d) |
| 52 | `Invoke-Sync` (1413) | `PrepSession.plan_sync` / `execute_sync` → `SyncPair`, `SyncResult`; `preview_commands` ("Xem lệnh") | TĐ-12, TĐ-14, TĐ-18, TĐ-19, TĐ-20, TĐ-32 |
| 53 | `Invoke-Undo` (1746) | `undo.plan_undo` + `undo.execute_undo` + màn hình Hoàn tác | Thùng rác |
| 54 | `Remove-FileSafe` (1794) | `undo.trash` | `send2trash`; tắt `DeleteToRecycleBin` mới xoá hẳn |
| 55 | `Invoke-Cleanup` (1810) | `subprep.cleanup_targets` → `CleanupPlan` + `PrepSession.execute_cleanup` + hộp thoại dọn dẹp | chỉ đề xuất file nguồn của cặp đã có kết quả > 32 byte cạnh phim |
| — | Menu chính (1873–2004) | `gui/subprep_view` | mục 6 |

### 4.3. `MuxTool_v17.bat` (78 hàm)

| # | Hàm (dòng) | → Python | Ghi chú |
|---|---|---|---|
| 1 | `Initialize-Console` (133) | bỏ | console |
| 2 | `W` (188) | bỏ → `gui/log_panel` | |
| 3 | `Initialize-BoxChars` (200) | bỏ | console |
| 4 | `Get-BoxLine` (211) | bỏ | console |
| 5 | `Write-Step` (220) | bỏ → log | |
| 6 | `Select-AudioTracks` (224) | `mux.AudioRule` + `MuxSession.set_audio_choice` / `keep_all_audio` + GUI bảng audio | quy tắc "nhớ cái GIỮ" (ngôn ngữ + hệ kênh khi ngôn ngữ vừa giữ vừa xoá); quy tắc xoá hết audio → bỏ qua quy tắc cho file đó |
| 7 | `Get-AudioRuleText` (341) | `mux.AudioRule.describe` | "chỉ giữ English (5.1), Czech" |
| 8 | `Add-MuxLog` (357) | `undo.RunLog.add` | gộp + hành động `Create` để hoàn tác (TĐ-17) |
| 9 | `Save-MuxLog` (363) | `undo.RunLog.save` | gộp, giữ 20 |
| 10 | `Find-Exe` (372) | `tools.find_tool` | gộp |
| 11 | `Join-Safe` (382) | `tools.join_safe` | gộp |
| 12 | `Get-LangCode` (388) | `naming.lang_from_track` | gộp |
| 13 | `Get-LangName` (404) | `naming.lang_name` | gộp |
| 14 | `Get-LangForMux` (416) | `naming.lang_for_mux` | gộp |
| 15 | `Get-CodecName` (425) | `probe.audio_codec_name(track)` | TrueHD, DTS-HD MA/HRA, DTS-ES, DTS, E-AC-3, Dolby Digital, AAC, FLAC, Opus, Vorbis, MP3, PCM. **Sửa lỗi TĐ-33** (đọc `codec` cấp track). Test đối chiếu ✅ |
| 16 | `Test-CommentaryTrack` (448) | `probe.is_commentary` | gộp |
| 17 | `Test-DescriptiveTrack` (455) | `probe.is_descriptive` | gộp |
| 18 | `Get-SubEncodingKind` (463) | `encoding.detect_kind` | gộp |
| 19 | `Get-SubCharsetArg` (477) | `encoding.mkvmerge_charset` | gộp |
| 20 | `Test-ImageSub` (490) | `probe.is_image_sub` | gộp |
| 21 | `Get-ChannelName` (494) | `probe.channel_name` | 1→1.0, 2→2.0, 3→2.1, 6→5.1, 7→6.1, 8→7.1, khác → `n.0` |
| 22 | `Get-AudioBitrate` (503) | `probe.audio_bitrate` | tag `BPS` → ffprobe → ước lượng codec × kênh/6 |
| 23 | `Get-FfprobeBitrates` (529) | `probe.ffprobe_bitrates` | gộp với một lần gọi ffprobe |
| 24 | `Format-Kbps` (552) | `probe.format_kbps` | |
| 25 | `Get-VideoCodecName` (567) | `probe.video_codec_name` | AV1, HEVC, H264, VP9, MPEG2 |
| 26 | `Get-ResolutionName` (577) | `probe.resolution_name` | theo chiều cao: 2160p/1440p/1080p/720p/576p/480p |
| 27 | `Get-DynamicRange` (590) | `probe.dynamic_range(stream_ffprobe, tên_file)` | `color_transfer` (smpte2084 → HDR, arib-std-b67 → HLG), `side_data_list` (DOVI → DV, HDR Dynamic Metadata → HDR10+), thiếu → đọc tên file (`HDR10` cũng là HDR — TĐ-33c). Test đối chiếu ✅ |
| 28 | `Get-CodecTag` (625) | `mux.codec_tag` | `2160p DV HDR HEVC TrueHD7.1 Atmos`; chỉ vào **tên file** |
| 29 | `Get-SafeFileName` (672) | `naming.safe_filename` | gộp |
| 30 | `Get-NormTitle` (679) | `naming.norm_title` | gộp |
| 31 | `Split-ReleaseName` (682) | `naming.split_release_name` | gộp — bản này làm khung |
| 32 | `Invoke-TmdbApi` (701) | `tmdb.TmdbClient.get` | key 32 ký tự → `?api_key=`; `eyJ…` → `Authorization: Bearer`; 401 → tắt TMDB cả lượt, báo **một lần**; 404 im lặng khi dò tập; lỗi mạng thử `TmdbRetry` lần, chờ 2ⁱ s |
| 33 | `Find-TmdbMovie` (742) | `TmdbClient.search_movie` | có năm không ra → bỏ năm tìm lại |
| 34 | `Select-TmdbCandidate` (757) | `naming.pick_match` + `OutputName.status = need_choice` + `naming.choose_candidate` | khớp tên chuẩn hoá + năm ±1 → lấy; không chắc + `TmdbConfirm` → trạng thái **"Cần chọn"** trên bảng (mục 6) |
| 35 | `Split-EpisodeName` (779) | `episodes.parse_episode` | gộp — bản này làm khung |
| 36 | `Format-EpRef` (824) | `EpisodeInfo.ref_label` | gộp |
| 37 | `Find-TmdbTv` (829) | `TmdbClient.search_series` | |
| 38 | `Get-TmdbEpisodeTitle` (843) | `TmdbClient.episode_title` | 404 im lặng |
| 39 | `Get-MovieFileName` (854) | `naming.movie_name(db, …)` / `movie_name_from` | `Tên (Năm) [tmdbid-…]`/`[tvdbid-…]`, bỏ ngoặc trong tên |
| 40 | `Get-TmdbTvDetails` (910) | `tmdb.tv_details` | cache |
| 41 | `Select-TmdbTvShow` (920) | `TmdbClient.select_show` / `pick_show` | nhiều series trùng tên, không có năm → chọn series **có** season/tập đó (điểm: có season +2, tổng tập ≥ số tập +1) |
| 42 | `Resolve-TmdbEpisode` (953) | `TmdbClient.resolve_episode` → `naming.EpisodeHit` | tra thẳng → nếu ngoài phạm vi season: cộng dồn `episode_count` → thử số tương đối, rồi số tuyệt đối trong season |
| 43 | `Get-TmdbTvName` (991) | `naming.tv_name(db=TmdbClient)` / `tv_name_from` | không bao giờ `Season 00` (≤ 0 → 1); tập đôi `S01E05-E06` |
| 44 | `Test-DbUsable` (1052) | `naming.db_usable` | |
| 45 | `Get-TvNameFrom` (1058) | `naming.resolve_output_name` | |
| 46 | `Resolve-OutputName` (1064) | `naming.resolve_output_name` | chỉ chuyển sang database kia khi `NameFallbackOther`; **không tạo thư mục khi lập kế hoạch** (TĐ-21) |
| 47 | `Set-ConsoleSize` (1107) | bỏ | console |
| 48 | `Get-SubAutoPlan` (1131) | `mux.sub_auto_plan` | **nguồn quy tắc phụ đề duy nhất** (trong .bat quy tắc này bị viết hai lần: ở đây và ở BƯỚC 3/4) |
| 49 | `Show-SubPickerGrid` (1224) | `gui/mux_view.MuxView` + `widgets.GridModel`/`CheckGrid` | §5.3 mục 5 |
| 50 | `Invoke-TvdbApi` (1453) | `tvdb.TvdbClient.request` | đọc UTF-8; 401 → đăng nhập lại một lần; 404 im lặng khi được yêu cầu |
| 51 | `Connect-Tvdb` (1500) | `TvdbClient.login` | `POST /login {apikey, pin?}` → `data.token` |
| 52 | `Get-TvdbLocalName` (1513) | `TvdbClient.local_name` | `TvdbLang` → `eng` → tên gốc |
| 53 | `Find-TvdbMovie` (1524) | `TvdbClient.search_movie` | `/search?type=movie&year=` → đưa về dạng giống TMDB |
| 54 | `Find-TvdbSeries` (1547) | `TvdbClient.search_series` | `/search?type=series` |
| 55 | `Get-TvdbEpisodes` (1571) | `TvdbClient.episodes` | `/series/{id}/episodes/{TvdbSeasonType}?page=N`, theo `links.next`, tối đa 30 trang |
| 56 | `Get-TvdbEpisodeName` (1585) | `TvdbClient.episode_name` | `/episodes/{id}/translations/{lang}` |
| 57 | `Resolve-TvdbEpisode` (1596) | `TvdbClient.resolve_episode` | `seasonNumber`+`number`, không có thì `absoluteNumber`. Test đối chiếu ✅ |
| 58 | `Select-TvdbShow` (1617) | `TvdbClient.pick_show` | chọn series có tập |
| 59 | `Get-TvdbTvName` (1637) | `naming.tv_name(db=TvdbClient)` | |
| 60 | `Select-NameDatabase` (1691) | `MuxSession.db_status` + `plan_all(db)` (`ValueError` nếu chưa chọn) + `gui/mux_view` (3 lựa chọn) | **bắt buộc chọn, không mặc định** |
| 61 | `ConvertTo-Double` (1733) | `timing.to_double` | gộp |
| 62 | `Get-EvenSample` (1755) | `timing.even_sample` | gộp |
| 63 | `Get-OffsetVote` (1763) | `timing.offset_vote` | gộp |
| 64 | `Get-TimingRelation` (1788) | `timing.timing_relation` | gộp |
| 65 | `Format-SrtStamp` (1816) | `srt.format_stamp` | gộp, sửa lỗi giờ |
| 66 | `Convert-PgsToTimingSrt` (1822) | `pgs.pgs_to_timing_srt` | gộp |
| 67 | `Get-CueTimes` (1869) | `srt.cue_times` | gộp |
| 68 | `Get-DonorTmpDir` (1905) | `MuxSession.temp` (thư mục tạm của lượt chạy) | |
| 69 | `Format-Inv` (1914) | `timing.fmt_inv` | gộp |
| 70 | `Get-DonorFiles` (1918) | `donor.donor_files` | `.mkv .mka .mp4 .m4v .ts` trong `DonorFolder` (đệ quy) |
| 71 | `Find-DonorFile` (1930) | `donor.find_donor_file` | `episodes.same_episode`; phim lẻ theo tên + năm; chỉ 1 file nguồn phim lẻ → lấy |
| 72 | `Get-RefSubTracks` (1962) | `probe.ref_sub_tracks` | bỏ Forced; hạng văn bản 0 / SDH 1 / PGS 2 |
| 73 | `Get-TrackCueTimes` (1983) | `donor.TrackTimes` | `mkvextract` → (PGS → SRT mốc) → `cue_times`, cache |
| 74 | `Get-VideoFpsOf` (2002) | `probe.video_fps` | gộp |
| 75 | `Get-DurationOf` (2010) | `probe.duration` | gộp |
| 76 | `Measure-DonorTiming` (2018) | `donor.measure_timing` | ngôn ngữ chung, ưu tiên `en`; mốc phía bạn: sub trong MKV, rồi sub ngoài `.en.srt`; ≥ `DonorMinVotes`; không đo được → FPS + thời lượng (≤ `DonorMaxDurationDiff`) |
| 77 | `Get-DonorPlan` (2071) | `donor.plan_donor` (chỉ lập kế hoạch) + `donor.prepare` (ffmpeg lúc thực thi) + `donor.stretch_filter` (TĐ-34) | `DonorOffsetMs` theo tên file / `S01E03` / `tap #643` / `643`; sửa tay trong bảng; chọn track nhiều kênh nhất rồi bitrate cao nhất |
| 78 | `Remove-DonorTemp` (2178) | `MuxSession.execute` (dọn thư mục tạm) | |
| — | Phần chính (2185–2862) | `MuxSession.scan` → `plan_all(db)` / `plan_file` → sửa kế hoạch → `build_args` / `preview_commands` → `execute` | tìm công cụ, quét, chọn database, đọc trước, quy tắc audio/phụ đề, thẻ codec, audio ghép, dựng lệnh, chạy (ghi tên tạm rồi đổi tên), mã thoát 0/1/≥2 |

### 4.4. Quy tắc dựng lệnh mkvmerge (giữ nguyên từ phần chính MuxTool)

Ghi lại để test được từng dòng (§6.2):

1. `-o <đầu ra>` rồi `--track-order …` (nếu `UseTrackOrder`), `--title <tên file>` (nếu `SetTitleFromName`).
2. `--audio-tracks a,b` chỉ khi có track bị bỏ; phụ đề trong: không giữ gì → `--no-subtitles`, giữ một phần → `--subtitle-tracks`.
3. `--language ID:en` cho track `und` duy nhất được gán `en` (mã qua `lang_for_mux`).
4. `--track-name` cho mọi track được giữ có tên (video = tên ngôn ngữ; audio = `Ngôn ngữ Codec Kênh`; phụ đề = `Ngôn ngữ [SDH] [Forced]`).
5. Cờ Default **tường minh cho mọi** audio/phụ đề giữ lại; Forced tường minh cho mọi phụ đề giữ lại; tên cờ theo phiên bản (v74+).
6. File phim; rồi từng phụ đề ngoài: `--sub-charset 0:<bảng mã>` (nếu không phải UTF-8), `--language 0:…`, `--track-name 0:…`, Default/Forced; rồi từng audio ghép: `--no-video --no-subtitles --no-chapters --no-attachments --no-global-tags --audio-tracks TID --language … --track-name … Default/Forced [--sync TID:ms] <file>`.
7. Thứ tự track: video → (audio ghép nếu `DonorSetDefault`) → audio Default → audio còn lại → (audio ghép) → phụ đề Default → phụ đề theo `SubDefaultPriority` (ngoài trước trong cùng ngôn ngữ) → phụ đề còn lại.
8. Mã thoát 0 = OK; 1 = OK có cảnh báo; ≥ 2 = lỗi, xoá file đầu ra dở dang.

---

## 5. Bảng biến cấu hình

Khoá = tên biến trong .bat (để nhập thẳng). Nhóm = nhóm trên màn hình Cài đặt. Chú thích trên màn hình lấy từ comment trong .bat, viết lại có dấu.
🔒 = bí mật → `keyring`, không vào `config.json`. ⇢ = gộp từ nhiều file.

### 5.1. Nhóm "Chung — thư mục & công cụ"

| Khoá | Từ biến (.bat) | Mặc định | Ô trên Cài đặt | Ghi chú |
|---|---|---|---|---|
| `MkvToolNixDir` ⇢ | SF `$MkvToolNixDir`, SP `$MkvToolNixDir`, MT `$UserMkvToolNix` | `C:\Users\fenha\Desktop\mkvtoolnix` | chọn thư mục + "Tự tìm" | |
| `AlassDir` | SP `$AlassDir` | `C:\Users\fenha\Desktop\alass-windows64\bin\alass` | chọn thư mục + "Tự tìm" | |
| `ExtraToolDirs` | MT `$ExtraToolDirs` | `['C:\Users\fenha\Desktop\alass-windows64\bin\alass']` | danh sách thư mục | nơi tìm thêm ffprobe/ffmpeg |
| `MkvmergePath`, `MkvextractPath`, `FfmpegPath`, `FfprobePath`, `AlassPath` | *(mới)* | `''` = tự tìm | đường dẫn file + "Tự tìm" | hiện phiên bản `mkvmerge` đọc được; báo nếu tìm thấy mà không chạy (thiếu DLL / bị Windows chặn — như MT dòng 2251) |
| `UiScale` | *(mới, GĐ5)* | `auto` | lựa chọn `auto/90/100/110/125/150` | cỡ chữ và giao diện; `auto` = 110% trên màn hình 1440p / 4K đặt tỉ lệ 100%, còn lại 100%. Có hiệu lực khi mở lại. (Khoá phụ `UiGeometry`, `UiZoomed`, `UiLastFolder` do chương trình tự ghi, không có ô) |
| `ScanSubFolders` ⇢ | SF, SP, MT | `true` | ô tick | |
| `VideoExts` ⇢ | SF `.mkv .mp4 .avi .m4v .ts`; SP thêm `.mov`; MT viết cứng `mkv mp4 m4v avi ts` | `.mkv .mp4 .avi .m4v .ts .mov` | danh sách | hợp các bản |
| `SubFolderName` ⇢ | SF, SP | `srt_can_sync` | ô chữ | |
| `ExtractFolder` ⇢ | SF, SP | `srt_goc` | ô chữ | |
| `PoolFolder` | SF | `srt_pool` | ô chữ | |
| `DonorFolder` | MT | `audio_nguon` | ô chữ | |
| `MinVerifyVotes` ⇢ | SF, SP | `20` | số | ngưỡng tin cậy; comment: khớp đúng 25–35 phiếu, khớp sai 5–15 |
| `FpsSampleRef` ⇢ | SP (MT viết cứng) | `400` | số (Nâng cao) | |
| `FpsSampleTgt` ⇢ | SP (MT viết cứng) | `100` | số (Nâng cao) | |
| `PgsTimingFallback` | SP | `true` | ô tick | |
| `PgsTimingText` ⇢ | SP (MT/SF viết cứng `*`) | `*` | ô chữ (Nâng cao) | |
| `KeepUndoRuns` ⇢ | SP (SF viết cứng 20) | `20` | số | áp dụng cả ba bước |
| `DeleteToRecycleBin` | SP | `true` | ô tick | áp dụng mọi chỗ xoá |
| `SubFetchLogDir` | SF viết cứng `_subfetch_logs` | `_subfetch_logs` | ô chữ (Nâng cao) | |
| `SubPrepLogDir` | SP `$LogDirName` | `_subprep_logs` | ô chữ (Nâng cao) | **cùng tên biến khác giá trị** ở SP/MT → tách hai khoá |
| `MuxLogDir` | MT `$LogDirName` | `_muxtool_logs` | ô chữ (Nâng cao) | |

### 5.2. Nhóm "API & tài khoản"

| Khoá | Từ biến (.bat) | Mặc định | Ô trên Cài đặt | Ghi chú |
|---|---|---|---|---|
| 🔒 `SubSourceApiKey` | SF | — | ô mật + "Kiểm tra kết nối" | subsource.net › My Profile › API Key |
| 🔒 `TmdbApiKey` ⇢ | SF, MT | — | ô mật + "Kiểm tra kết nối" | key 32 ký tự hoặc token `eyJ…` |
| 🔒 `TvdbApiKey` | MT | — | ô mật + "Kiểm tra kết nối" | |
| 🔒 `TvdbPin` | MT | — | ô mật | chỉ key loại "user-supported" |
| 🔒 `OsApiKey` | SF | — | ô mật + "Kiểm tra kết nối" | |
| 🔒 `OsUsername` | SF | — | ô mật | |
| 🔒 `OsPassword` | SF | — | ô mật | |
| `OsUserAgent` | SF | `MyApp v1.0.0` | ô chữ | phải dạng `TênApp vX.Y.Z`; tên riêng cần đăng ký API Consumer |
| `SubSourceApiBase` | SF `$ApiBase` | `https://api.subsource.net/api/v1` | ô chữ (Nâng cao) | |
| `ApiPauseMs` | SF | `1100` | số | trần 60 lượt/phút |
| `TmdbRetry` | MT | `3` | số | dùng cho cả TVDB (như .bat) |
| `TmdbLang` | MT | `en-US` | ô chữ | `vi-VN` để lấy tên tiếng Việt |
| `TvdbLang` | MT | `eng` | ô chữ | mã 3 chữ |
| `TvdbSeasonType` | MT | `default` | lựa chọn `default/dvd/official` | |

### 5.3. Nhóm "① Tải phụ đề"

| Khoá | Từ biến (.bat) | Mặc định | Ô trên Cài đặt | Ghi chú |
|---|---|---|---|---|
| `Languages` ⇢ | `$Lang_vi … $Lang_ar` (12 biến) + `$LangExtra` | `['vi','en','cs']` | 12 ô tick + ô nhập mã khác | thứ tự = thứ tự xử lý (vi, en, cs, ja, ko, zh, fr, de, es, th, id, ar, rồi mã thêm); cũng hiện ở đầu thẻ ① |
| `UseLangSubFolders` | SF | `true` | ô tick | `srt_can_sync\vi\`… |
| `MaxShowCandidates` | SF | `12` | số | số dòng tối đa của bảng ứng viên |
| `VerifyTiming` | SF | `true` | ô tick | đo với phụ đề gốc |
| `Interactive` | SF | `true` | ô tick "Tự chọn trong bảng" | tắt = tự lấy bản đứng đầu |
| `RememberChoice` | SF | `true` | ô tick | mặc định của ô "Dùng lại lựa chọn này…" |
| `AutoPickWhenSingle` | SF | `true` | ô tick | |
| `UseExistingSubs` | SF | `true` | ô tick | |
| `ExistingMinScore` | SF | `0.35` | số | chỉ dùng khi không đo được |
| `ExistingRenameMode` | SF | `copy` | lựa chọn `copy/move` | `move` có cảnh báo ghi đè dây chuyền |
| `SkipIfExists` | SF (không được dùng) | `true` | ô tick | xem TĐ-22 |
| `KeepExtractedRef` | SF | `true` | ô tick | giữ phụ đề gốc trong `srt_goc` |
| `StrictFpsMatch` | SF | `true` | ô tick | |
| `AllowUnknownFps` | SF | `true` | ô tick | |
| `KeepPaidDownloads` | SF (không được dùng) | `true` | ô tick | xem TĐ-22, Q5 |
| `UsePool` | SF | `true` | ô tick | |
| `PoolTryLimit` | SF | `8` | số | |
| `PoolMinVotes` | SF | `8` | số | |
| `PoolFirst` | SF | `true` | ô tick | |
| `PoolDefaultLang` | SF | `''` | lựa chọn ngôn ngữ / trống | |
| `ExcludeSdh` | SF | `true` | ô tick | |
| `ExcludeForced` | SF | `true` | ô tick | |
| `MaxPages` | SF | `10` | số | |
| `UseOpenSubtitles` | SF | `true` | ô tick | |
| `OsWarnRemaining` | SF | `50` | số | |
| `OsStopRemaining` | SF | `5` | số | |
| `OsExcludeMachine` | SF | `true` | ô tick | |
| *(bảng tên SubSource)* | SF `$LangMap` | 18 ngôn ngữ | không có ô | thành một phần bảng ngôn ngữ chung (3.6) |
| — | SF `$TableWidth`, `$TableMinWidth`, `$ConsoleCols`, `$ConsoleRows`, `$ConsoleScrollback`, `$UseColors`, `$UseGridView` | | **bỏ** | console |
| — | SF `$UndoLastRun` | | **bỏ** | thay bằng nút "Hoàn tác…" |

### 5.4. Nhóm "② Chuẩn bị phụ đề"

| Khoá | Từ biến (.bat) | Mặc định | Ô trên Cài đặt | Ghi chú |
|---|---|---|---|---|
| `RefFolderName` | SP | `''` | ô chữ | `''` = quét thư mục phim |
| `OutFolderName` | SP | `''` | ô chữ | `''` = ghi cạnh file phim |
| `RefExcludeFolders` | SP | `srt_pool srt_can_sync srt_hoan_thanh _subprep_logs _subfetch_logs _subsource_probe Output` | danh sách | luôn cộng thêm `DonorFolder`, `PoolFolder`, `SubFolderName` (TĐ-13) |
| `RefSubLangs` | SP | `['en','vi','cs']` | danh sách | |
| `SkipExistingRef` | SP | `true` | ô tick | |
| `SubExts` | SP | `.srt .ass .ssa .vtt` | danh sách (Nâng cao) | |
| `KnownLangTags` | SP | `vi cs cz en ja ko zh fr de es th` | không có ô | thay bằng bảng ngôn ngữ chung (luôn ⊇ danh sách cũ) |
| `PreviewFirst` | SP | `true` | **bỏ** | GUI luôn hiện bảng xem trước, phải bấm "Đổi tên" |
| `SkipExistingOut` | SP | `true` | ô tick | |
| `AlassExtraArgs` | SP | `[]` | danh sách (Nâng cao) | |
| `SyncAudioMode` | SP | `ask` | lựa chọn `ask/auto/index/lang` | lựa chọn sẵn trên thẻ ② (`ask` = hiện bảng audio, chưa chọn) |
| `SyncAudioIndex` | SP | `0` | số | |
| `SyncAudioLang` | SP | `en` | ô chữ | |
| `MaxShiftSeconds` | SP | `130` | số | chỉ **từ chối** khi mốc là audio |
| `AlassArgsSubMode` | SP | `[]` | danh sách (Nâng cao) | |
| `AlassArgsAudioMode` | SP | `['--no-split','--disable-fps-guessing']` | danh sách (Nâng cao) | |
| `NormalizeEncoding` | SP | `true` | ô tick | |
| `TitleMinScore` | SP | `0.5` | số | |
| `TitleMinMargin` | SP | `0.15` | số | |
| `TrimLeadingExtra` | SP | `true` | ô tick | |
| `MaxZeroStartCues` | SP | `2` | số | |
| `HealthReport` | SP | `true` | ô tick | xuất `health_*.csv` |
| `AnalyzeTiming` | SP | `true` | ô tick | |
| `FpsAutoAllowGuess` | SP | `true` | ô tick | xem TĐ-18 |
| `AskCleanup` | SP | `true` | ô tick | hiện hộp thoại dọn dẹp sau khi chạy |
| — | SP `$UseColors` | | **bỏ** | console |

### 5.5. Nhóm "③ Mux"

| Khoá | Từ biến (.bat) | Mặc định | Ô trên Cài đặt | Ghi chú |
|---|---|---|---|---|
| `SkipOnMultipleEnAudio` | MT | `true` | ô tick | xem TĐ-23, Q3 |
| `RemoveCommentaryAudio` | MT | `true` | ô tick | |
| `RemoveDescriptiveAudio` | MT | `true` | ô tick | |
| `RemoveCommentarySubs` | MT | `true` | ô tick | |
| `RemoveImageSubs` | MT | `true` | ô tick | |
| `AskWhichAudio` | MT | `true` | ô tick | hiện bảng audio để tick xoá |
| `AskAudioMinTracks` | MT | `2` | số | |
| `RememberAudioChoice` | MT | `ask` | lựa chọn `ask/always/never` | `ask` = ô "Áp dụng cho mọi file còn lại" bật sẵn; `always` = luôn áp dụng; `never` = không có ô |
| `UseSubPicker` | MT | `true` | ô tick | hiện bảng chọn phụ đề theo tập |
| `SubPickerMinFiles` | MT | `1` | số | |
| `AddCodecTag` | MT | `true` | ô tick | |
| `CodecTagForTv` | MT | `false` | ô tick | |
| `CodecTagParts` | MT | `res hdr vcodec acodec` | 4 ô tick | |
| `WriteRunLog` | MT | `true` | ô tick | (nhật ký hoàn tác luôn ghi; ô này chỉ tắt cột chi tiết) |
| `SubKeepLangs` | MT | `['vi','cs','en']` | danh sách | |
| `SubDefaultPriority` | MT | `['vi','en','cs']` | danh sách | |
| `RemoveForcedSubs` | MT | `true` | ô tick | |
| `SkipOnAmbiguousUnd` | MT | `true` | ô tick | |
| `DefaultExternalLang` | MT | `vi` | lựa chọn ngôn ngữ | |
| `ExternalSubOverridesInternal` | MT | `true` | ô tick | |
| `SkipIfOutputExists` | MT | `false` | ô tick | |
| `UseTrackOrder` | MT | `true` | ô tick | |
| `SetTitleFromName` | MT | `true` | ô tick | |
| `SubExtPriority` | MT | `.srt .ass .ssa .sup .vtt` | danh sách | |
| `TmdbConfirm` | MT | `true` | ô tick | không chắc → "Cần chọn" |
| `TmdbCreateFolder` | MT | `true` | ô tick | |
| `TvFetchEpisodeTitle` | MT | `true` | ô tick | |
| `TvSeasonFolder` | MT | `true` | ô tick | |
| `TvAbsoluteToSeason` | MT | `true` | ô tick | |
| `NameFallbackOther` | MT | `false` | ô tick | |
| `UseDonorAudio` | MT | `true` | ô tick | |
| `DonorLangs` | MT | `['cs']` | danh sách | |
| `DonorSkipIfPresent` | MT | `true` | ô tick | |
| `DonorSetDefault` | MT | `false` | ô tick | |
| `DonorMinVotes` | MT | `20` | số | |
| `DonorAcceptDurationMatch` | MT | `true` | ô tick | |
| `DonorMaxDurationDiff` | MT | `1.0` | số (giây) | |
| `DonorOffsetMs` | MT | `{}` | bảng khoá → ms | độ lệch tay lưu lâu dài; ngoài ra sửa được từng tập ngay trong bảng của thẻ ③ (chỉ cho lượt chạy đó) |
| — | MT `$DryRun` | | **bỏ** | nút "Xem lệnh" |
| — | MT `$UseColors`, `$UseGridView`, `$ConsoleCols/Rows/Scrollback` | | **bỏ** | console |
| *(không lưu)* | database đặt tên | | — | **bắt buộc chọn mỗi lần** trên thẻ ③, không lưu vào cấu hình |

### 5.6. Nút "Nhập cấu hình từ file .bat"

- Chọn một hoặc nhiều file `.bat`. Nhận diện file theo nội dung (có `$SubSourceApiKey` → SubFetch; có `$AlassArgsAudioMode` → SubPrep; có `$DonorFolder` → MuxTool), không theo tên.
- Chỉ đọc các dòng gán **ở cấp ngoài cùng** (sau `#>`, không nằm trong hàm hay khối `{ }`) và **chỉ những tên có trong bảng trên** — không chạy/không thông dịch PowerShell. (Không dùng quy tắc "đọc tới hàm đầu tiên" vì SubFetch có `Set-ConsoleSize` đứng trước khối cấu hình.) Các biến được *tính ra* như `$Languages`, `$LangMap`, `$NoiseWords`, `$StopWords`, `$subDir`… bị bỏ qua.
- Hiểu được: `'chuỗi'`, `"chuỗi"`, `$true/$false`, số (dấu chấm), `@('a','b')` (kể cả viết trên nhiều dòng như `$RefExcludeFolders`), `@()`, `@{ 'S01E03' = 1200; 'tap #643' = -500 }`.
- Đổi tên khi nhập: `$UserMkvToolNix` → `MkvToolNixDir`; `$Lang_xx`/`$LangExtra` → `Languages`; `$LogDirName` → `SubPrepLogDir` hoặc `MuxLogDir` theo file; `$ApiBase` → `SubSourceApiBase`.
- Bí mật đọc được → ghi thẳng vào `keyring`; bí mật rỗng thì **không** xoá bí mật đang có.
- Trước khi ghi: hiện bảng "Khoá — giá trị hiện tại — giá trị từ .bat" (bí mật che), bấm "Nhập" mới ghi. Hai file cùng có một khoá mà khác giá trị (vd. `MkvToolNixDir`) → hiện cả hai cho chọn.

---

## 6. `Read-Host` → màn hình GUI

22 chỗ `Read-Host` + 3 cửa sổ hỏi khác (Out-GridView ×2, WinForms ×1). Không chỗ nào còn chặn giữa lúc chạy: mọi quyết định đã nằm trong kế hoạch trước khi bấm chạy.

| # | File (dòng) | Câu hỏi trong console | Thay bằng |
|---|---|---|---|
| 1 | SF 1565 | "Hoàn tác lần nào? (Enter = mới nhất, 0 = huỷ)" | Màn hình **Hoàn tác** (chung ba bước): danh sách tối đa 20 lần chạy, số file tạo/chép/di chuyển/đổi tên mỗi lần, chọn một hoặc nhiều lần |
| 2 | SF 1576 | "Xác nhận? (Y/N)" | Hộp thoại xác nhận liệt kê file sẽ vào Thùng rác / trả về chỗ cũ |
| 3 | SF 1837 | "Chọn số (Enter = số 1, s = bỏ qua ngôn ngữ này)" | **Bảng ứng viên** (bấm đúp để chọn, nút "Bỏ qua ngôn ngữ này", ô "Dùng lại lựa chọn này cho các tập sau cùng season") |
| — | SF 1828 | Out-GridView chọn ứng viên | như #3 |
| 4 | SP 1369 | "Tiến hành đổi tên? (Y/N)" | **Bảng xem trước đổi tên** + nút "Đổi tên" |
| 5 | SP 1504 | "Các cặp trên đã đúng chưa? (Y/N)" | **Bảng cặp đồng bộ** + nút "Chạy đồng bộ" |
| 6 | SP 1760 | "Hoàn tác lần nào?" | Màn hình Hoàn tác (như #1) |
| 7 | SP 1771 | "Xác nhận? (Y/N)" | như #2 |
| 8 | SP 1842 | Dọn dẹp: 1 cả hai / 2 `srt_can_sync` / 3 `srt_goc` / 0 không xoá | **Hộp thoại dọn dẹp**: 4 nút, danh sách file, ghi rõ "vào Thùng rác" / "xoá hẳn" |
| 9 | SP 1853 | "Xác nhận xoá? (Y/N)" | gộp vào hộp thoại #8 (danh sách đầy đủ + nút xác nhận) |
| 10 | SP 1898 | Đồng bộ theo: 1 audio / 2 phụ đề gốc / 0 hoàn tác | Thẻ ②: ô chọn **Mốc đồng bộ** (*Phụ đề gốc trong `srt_goc`* / *Audio của file phim*); hoàn tác là nút riêng |
| 11 | SP 1910 | Ngôn ngữ: 1 vi / 2 cs / 3 en / 4 tự nhập / 5 tự nhận diện (Enter = 5) | Thẻ ②: ô chọn **Ngôn ngữ**, mặc định *Tự nhận diện theo thư mục con hoặc hậu tố*; ép cố định → file nằm trong thư mục ngôn ngữ khác hiện **đỏ** trên bảng |
| 12 | SP 1915 | "Nhập hậu tố (vd: ja)" | mục "Khác…" trong ô chọn #11 (chọn từ bảng ngôn ngữ hoặc gõ mã) |
| 13 | SP 1944 | "Trích tự động phụ đề non-SDH từ các file MKV? (Y/N)" | Nút **"Trích phụ đề gốc từ MKV"**; khi `srt_goc` trống thì có dòng nhắc màu vàng ngay trên nút |
| 14 | SP 1949 | "Trích bổ sung cho các tập còn thiếu? (Y/N)" | cùng nút #13 (luôn chỉ trích tập còn thiếu khi `SkipExistingRef`) |
| 15 | SP 1970 | Audio làm mốc: 1 tự động / 2 số thứ tự / 3 ngôn ngữ | Thẻ ②: bảng audio track của file mẫu + 3 lựa chọn |
| 16 | SP 1973 | "Nhập số thứ tự audio track" | bấm chọn dòng trong bảng #15 (hoặc ô số) |
| 17 | SP 1978 | "Nhập mã ngôn ngữ" | ô chọn ngôn ngữ cạnh #15 |
| 18 | MT 286 | "Nhập ID các audio muốn XOÁ" | Thẻ ③: **bảng audio** của file có ≥ `AskAudioMinTracks` track (ID, ngôn ngữ, codec, kênh, bitrate, tên track; ☑ = xoá) |
| — | MT 280 | Out-GridView chọn audio xoá | như #18 |
| 19 | MT 329 | "Áp dụng cho MỌI file còn lại? (Y/n)" | ô **"Áp dụng cho mọi file còn lại"** dưới bảng #18 (theo `RememberAudioChoice`); bảng tổng quan cập nhật ngay cho các file sau |
| 20 | MT 773 | (TV) "Chọn 1–5, hoặc Enter để giữ tên file gốc" | Bảng tổng quan: dòng **"Cần chọn"** (vàng) → bấm đúp mở hộp thoại 5 kết quả gần nhất + "Tìm lại với tên…" + "Giữ tên gốc". Bấm Bắt đầu khi còn dòng "Cần chọn" → hỏi một lần: giữ tên gốc cho các dòng đó hay quay lại chọn. "Tìm lại với tên…" (không có trong .bat) = hàm mới `naming.search_candidates` + `MuxSession.search_name` (GĐ5) — kết quả mới cùng khoá, chọn một lần áp dụng cho mọi tập của series |
| 21 | MT 887 | (phim lẻ) như #20 | như #20 |
| 22 | MT 1713 | "Nhập 1, 2 hoặc 0" (database) | Thẻ ③: 3 nút chọn **TMDB / TVDB / Không đổi tên**, **không chọn sẵn**, nút "Bắt đầu" khoá tới khi chọn; cạnh mỗi nút: *sẵn sàng* / *chưa điền key* / *key bị từ chối* / *đăng nhập lỗi* (kiểm tra ngay khi mở thẻ, ở luồng phụ) |
| — | MT 1224 | WinForms `Show-SubPickerGrid` | Thẻ ③: **bảng chọn phụ đề theo tập** (Treeview, ☑/☐; xám = không có; vàng = khác quy tắc tự động; bấm tiêu đề cột/tên tập để đảo cả cột/dòng; "Trả về mặc định", "Tick tất cả", "Bỏ tick tất cả"; rê chuột xem chi tiết track + lý do) |

---

## 7. Thay đổi so với .bat

Mỗi mục có mã **TĐ-xx** để tham chiếu. Cột "Kiểm chứng" ghi cách chứng minh. Theo §2 bước 1: lỗi "đọc code" sẽ có test chứng minh trước khi sửa, ở giai đoạn ghi trong ngoặc.

### 7.A. Lỗi đã chứng minh bằng cách chạy hàm gốc

Chạy bằng PowerShell 7.6 (máy Linux của tôi) qua `tools/verify_bat/KiemChung_LoiBat.ps1`: script nạp **đúng định nghĩa hàm** từ ba file .bat bằng bộ phân tích cú pháp của PowerShell. Các lỗi dưới đây thuộc về ngữ nghĩa ngôn ngữ (ép kiểu `[int]`, thứ tự `-and/-or`, regex), giống nhau ở PowerShell 5.1 trên máy bạn; bạn chạy lại script để chắc chắn.

**TĐ-01 — `Format-SrtStamp` ghi sai giờ (cả 3 file).**
- *Vì sao hỏng:* `[int]$ts.TotalHours` làm tròn về số chẵn gần nhất: 0,75 giờ → 1; 1,5 giờ → 2; (đúng 0,5 giờ → 0 nên 00:30:00 lại không sai — test đối chiếu ở Giai đoạn 1 đã xác nhận quy tắc này).
- *Kiểm chứng:*
  ```
  1799 giây -> 00:29:59,000     (đúng)
  2700 giây -> 01:45:00,000     (đúng: 00:45:00)
  3599.9 giây -> 01:59:59,900   (đúng: 00:59:59)
  6000.25 giây -> 02:40:00,250  (đúng: 01:40:00)
  PGS hiện ở 1900 s -> 01:31:40 (đúng: 00:31:40)
  ```
  Đọc rồi ghi lại một SRT qua `Repair-CollapsedCues`: dòng `00:45:00` thành `01:45:00`.
- *Ảnh hưởng thật:* (a) `srt_goc` đọc từ PGS — mọi dòng ở phút 30–59 mỗi giờ lệch +1 giờ ⇒ mốc gốc sai cho bước ①, ②, ③ (đo vẫn ra nhưng ít phiếu hơn; alass dùng mốc này thì kết quả hỏng nửa sau mỗi giờ); (b) phụ đề đã cắt recap hoặc đã bỏ khối 00:00:00 ở bước ② — nửa sau mỗi giờ lệch +1 giờ (có thể chính là nguyên nhân một số file bị "CÓ VẤN ĐỀ" mà khó hiểu).
- *Cách mới:* giờ = phần nguyên của `giây / 3600`. Test: vòng đọc → ghi giữ nguyên mọi mốc 0–3 giờ.
- *Gợi ý cho bạn:* phụ đề trong `srt_goc` tạo từ PGS và phụ đề bước ② đã báo "Đã cắt … dòng" / "Đã bỏ … dòng bị dồn về 00:00:00" trong các lần chạy cũ nên làm lại bằng MuxStudio.

**TĐ-02 — SubFetch không nhận số tập anime → chọn nhầm tập trong gói season.**
- *Kiểm chứng:* `Get-EpisodeInfo('One Piece - 0643')` = `(null)`; `Test-EpisodeConflict('One Piece - 0643','One Piece - 0644')` = `False`.
- *Ảnh hưởng:* `Select-FromPack` chỉ lọc tập khi nhận được số tập; với tên anime nó bỏ bước lọc và lấy file **giống tên nhất** — các file trong gói gần như trùng tên ⇒ có thể lấy cùng một file cho mọi tập. Kho `srt_pool`, file có sẵn và bộ nhớ lựa chọn cũng không loại được tập khác theo tên.
- *Cách mới:* bản gộp 3.4 + `same_episode`. Test: gói giả lập `One Piece - 0641…0645.srt`, video `- 0643` → chọn đúng 0643.

**TĐ-03 — SubPrep coi `1917 - 2019` là tập 2019; đọc sai `S01E05E06` và `- 1085[1080p]`.**
- *Kiểm chứng:* `Get-EpisodeInfo('1917 - 2019')` = `Season=0; Episode=2019; Confident=True`; `'Show.S01E05E06'` = tập 6 (không chắc); `'One Piece - 1085[1080p]'` = không chắc. MuxTool đọc `'Show Season 2 Episode 5'` thành tập tuyệt đối 5 của series "Show Season 2".
- *Cách mới:* bản gộp 3.4 (chặn năm 1900–2099 ở mẫu số tuyệt đối, tập đôi, ngoặc dính, `Season N Episode M`).

**TĐ-04 — SubPrep `Test-LangMatch` sai do thứ tự `-and`/`-or`.**
- *Vì sao hỏng:* trong PowerShell `-and` và `-or` ngang hàng, tính từ trái sang ⇒ `(A -or B) -and (w -eq 'cs')`.
- *Kiểm chứng:* `'cze cs'`→`cs` = False, `'cze '`→`cs` = False, `'ger'`→`de` = False, `'jpn'`→`ja` = False, `'chi'`→`zh` = False, `'spa'`→`es` = False. (`Invoke-ExtractRefSubs` truyền vào chuỗi `"$language $language_ietf"`, vd. `cze cs`.)
- *Ảnh hưởng:* khi trích phụ đề gốc, track tiếng Séc không bao giờ được xếp theo `RefSubLangs`; chọn audio làm mốc theo ngôn ngữ `de/ja/zh/es` không bao giờ trúng.
- *Cách mới:* chuẩn hoá cả hai phía về mã 2 chữ bằng bảng chung rồi so.

**TĐ-05 — Tách tên phim + năm sai với tên có số giống năm.**
- *Kiểm chứng:*
  ```
  Blade Runner 2049                MuxTool: 'Blade Runner' (2049)        SubFetch: 'Blade Runner' (2049)
  2001 A Space Odyssey             MuxTool: '' (2001)                    SubFetch: 'A Space Odyssey' (2001)
  2001.A.Space.Odyssey.1968.1080p  MuxTool: '2001 A Space Odyssey' (1968) SubFetch: 'A Space Odyssey' (2001)
  ```
- *Ảnh hưởng:* MuxTool tìm TMDB "Blade Runner" năm 2049 → không ra → bỏ năm → ra bản 1982 → không khớp năm → hỏi lại (hoặc lấy nhầm nếu tắt `TmdbConfirm`); tên rỗng → giữ tên gốc. SubFetch tra IMDb và SubSource bằng tên sai.
- *Cách mới:* năm = năm cuối cùng **hợp lệ**: không phải năm nếu bỏ đi làm tên rỗng, hoặc lớn hơn năm hiện tại + 1. → câu hỏi Q4 (đã duyệt).
- *Phát hiện thêm ở Giai đoạn 1 (test đối chiếu hàm gốc):* danh sách từ kỹ thuật của `Split-ReleaseName` có những từ trùng với tên phim thật (`web`, `uncut`, `dual`, `extended`…), và MuxTool cắt tên **trước** khi tìm năm:
  ```
  Uncut.Gems.2019.1080p       MuxTool: '' (không năm)       → giữ tên file gốc
  Charlotte's.Web.2006.720p   MuxTool: "Charlotte's" (không năm)
  ```
  Bản mới chia từ kỹ thuật làm hai loại: loại **chắc chắn** (`1080p`, `x265`, `BluRay`, `WEB-DL`, `DDP5`…) luôn cắt tên tại đó; loại **có thể trùng tên phim** (`web`, `uncut`, `extended`, `dual`, `proper`, `multi`…) chỉ dùng để cắt khi tên **không có năm**. Có năm hợp lệ thì tên = phần trước năm. Kết quả: `Uncut Gems` (2019), `Charlotte's Web` (2006). Các tên release thông thường (`Tên.Năm.1080p…`, `[YTS.MX] Tên (Năm) [2160p]`) cho kết quả **giống hệt** MuxTool — có test đối chiếu.

### 7.B. Lỗi tìm thấy khi đọc code (test chứng minh ở giai đoạn ghi trong ngoặc)

| Mã | File (dòng) | Lỗi | Cách mới |
|---|---|---|---|
| TĐ-06 | SF 1995–2001 | SubSource không tìm thấy phim **và** OpenSubtitles tắt/hỏng → `continue`: bỏ qua luôn **kho `srt_pool` và file có sẵn** của video đó | Kho nội bộ và file có sẵn luôn được xét (GĐ2 ✅) |
| TĐ-07 | SF 1909–1913 | Thiếu `SubSourceApiKey` → dừng toàn bộ script | Chỉ tắt nguồn SubSource, các nguồn khác vẫn chạy (GĐ2 ✅) |
| TĐ-08 | SF 583–615 | `Get-RefTimes` chỉ xét `srt_goc` khi file là `.mkv`, có mkvmerge/mkvextract **và** MKV có track phụ đề ⇒ phụ đề gốc bạn tự đặt vào `srt_goc` bị bỏ qua với MKV không có phụ đề và với MP4 | Xét `srt_goc` trước (trùng tên → theo số tập → theo tên), rồi mới trích từ MKV (GĐ2 ✅) |
| TĐ-09 | SF 662–665, 2006, 1687 | (a) Phụ đề gốc dạng ASS được **đổi đuôi thành `.srt`** trong `srt_goc` (nội dung vẫn là ASS) ⇒ alass ở bước ② đọc sai; SubPrep trích lại thành `.ass` ⇒ hai file trùng tên khác đuôi. (b) Phụ đề tải về/kho/có sẵn dạng `.ass/.ssa` luôn được lưu thành `<tên>.<mã>.srt` | Giữ đuôi thật (`<tên>.ass`, `<tên>.<mã>.ass`) — bước ② và ③ đã đọc được ASS. **Đã duyệt (Q7): ASS giữ là ASS ở mọi bước** (GĐ2 ✅) |
| TĐ-10 | SF 797–810 | `Get-ImdbId` không hỗ trợ token TMDB `eyJ…` (dùng chung `TmdbApiKey` với MuxTool) và luôn lấy kết quả đầu (One Piece anime / live-action có thể ra nhầm IMDb) | Dùng chung `tmdb.py`: Bearer + chọn series thực sự có season/tập (GĐ2 ✅) |
| TĐ-11 | SF 1964–1969 | SubFetch quét video chỉ bỏ `srt_can_sync` ⇒ chạy lại sau MuxTool sẽ tải phụ đề cho cả file trong `Output\` và file nguồn trong `audio_nguon\` | Bỏ qua `Output`, `DonorFolder`, `PoolFolder`, `ExtractFolder`, thư mục log — đã duyệt Q6 (GĐ2 ✅) |
| TĐ-12 | SP 1483, 1573 | `Invoke-NormalizeEncoding` và `Remove-LeadingExtra` **ghi đè file nguồn** trong `srt_can_sync`; nhật ký `Encoding` không được hoàn tác | Làm trên bản sao tạm; file nguồn không đổi (GĐ3 ✅) |
| TĐ-13 | SP 37–38 | `RefExcludeFolders` thiếu `audio_nguon` ⇒ khi dùng ghép audio, mỗi tập có 2 video cùng số tập ⇒ "NHẬP NHẰNG" hàng loạt | Luôn loại `DonorFolder` (và `PoolFolder`, `SubFolderName`, thư mục nhật ký) (GĐ3 ✅) |
| TĐ-14 | SP 371–375, 1568 | Kiểm tra sức khoẻ và cắt recap chỉ đọc SRT ⇒ kết quả `.ass/.vtt` luôn bị báo "CÓ VẤN ĐỀ: không đọc được nội dung" | Sức khoẻ đọc mốc chung SRT/ASS/VTT; cắt recap và sửa khối 00:00:00 chỉ với SRT, file khác ghi "không cắt recap (ASS)" / "không sửa khối 00:00:00 (ASS)" (Q7: không đổi định dạng). Với ASS bỏ dấu hiệu "chồng lấn" (ASS hiện nhiều dòng cùng lúc là bình thường) (GĐ3 ✅) |
| TĐ-15 | SP 48, 549 | Hậu tố `.cz` được giữ nguyên ⇒ kết quả `Phim.cz.srt` | Chuẩn hoá thành `.cs` (MuxTool vốn hiểu cả hai) (GĐ3 ✅) |
| TĐ-16 | SF 1581, SP 1776 | Hoàn tác **xoá hẳn** file | Vào Thùng rác (§4) (GĐ1 – `undo.py`) |
| TĐ-17 | MT | MuxTool không có hoàn tác | `_muxtool_logs\mux_*.csv` ghi `Create` cho file đầu ra + `Mkdir` thư mục mới tạo (+ `Skip`/`Fail` để xem lại); hoàn tác → Thùng rác, thư mục mới tạo bị xoá nếu rỗng (§4) (GĐ4 ✅) |
| TĐ-18 | SP 1469, 1588 | `FpsAutoAllowGuess` không bao giờ có tác dụng với cấu hình mặc định: mốc phụ đề thì không có cờ để bỏ; mốc audio thì không đo được lệch FPS | Mốc audio mà `srt_goc` có phụ đề gốc của phim đó → đo quan hệ để phát hiện lệch FPS rồi bỏ `--disable-fps-guessing` cho file đó; không có thì như cũ (GĐ3 ✅) |
| TĐ-19 | SP 1594–1600 | Lần thử lại "không kèm tham số" đưa **MKV gốc** cho alass thay vì WAV ⇒ gặp lại lỗi `missing field codec_long_name` với MKV có font | Thứ tự thử: WAV + cờ → WAV không cờ → MKV + cờ → MKV không cờ. Kèm §6.5 "luôn trích WAV": số thứ tự / ngôn ngữ audio không có trong file → dùng track Default (không có thì track đầu) và vẫn trích WAV (bản cũ đưa thẳng MKV cho alass) (GĐ3 ✅) |
| TĐ-20 | SP 1617 | `MaxShiftSeconds = 0` thì bỏ qua cả sửa khối 00:00:00 lẫn kiểm tra sức khoẻ | Luôn sửa + kiểm tra; `0` chỉ tắt ngưỡng từ chối (GĐ3 ✅) |
| TĐ-21 | MT 1081, 1100, 2146 | `$DryRun` vẫn tạo thư mục trong `Output`; lệnh in ra thiếu bước ffmpeg đổi tốc độ nên không đúng lệnh thật | "Xem lệnh" không ghi gì, lập kế hoạch không tạo thư mục; in đủ lệnh ffmpeg + mkvmerge (đường dẫn tạm ghi rõ) (GĐ4 ✅) |
| TĐ-22 | SF 132, 145 | `$KeepPaidDownloads`, `$SkipIfExists` chỉ khai báo, không dùng. Thực tế .bat **luôn giữ** mọi file đã tải (chỉ cảnh báo) và luôn bỏ qua khi đích đã có (nếu `UseExistingSubs`) | `SkipIfExists` điều khiển đúng việc "đích đã có → bỏ qua" (tách khỏi `UseExistingSubs`; mặc định cả hai bật = như cũ). `KeepPaidDownloads` đã duyệt Q5: tắt → file OpenSubtitles kiểm chứng yếu vào Thùng rác, thử bản kế tiếp (GĐ2 ✅) |
| TĐ-23 | MT 2479 | `$SkipOnMultipleEnAudio` với cấu hình mặc định không bao giờ chạy: điều kiện "người dùng đã chọn" thực ra là "đã được hỏi" (đúng cả khi bạn bấm Huỷ/giữ tất cả hay khi quy tắc nhớ tự áp dụng) | Q3: dòng "Cần chọn" cho tới khi bạn tick trong bảng audio của file đó hoặc bấm "Giữ cả hai, Default = bitrate cao nhất"; tắt `SkipOnMultipleEnAudio` → tự lấy bitrate cao nhất (GĐ4 ✅) |
| TĐ-24 | SF 1174 | `Get-OsScore` (trừ điểm dịch máy, cộng lượt tải, đánh giá…) không bao giờ được gọi ⇒ khi `OsExcludeMachine` tắt, bản dịch máy xếp ngang bản thường | Đã duyệt Q2: bản dịch máy xếp sau bản thường cùng hạng FPS và cùng tình trạng đo (GĐ2 ✅) |
| TĐ-25 | MT 2542–2546, 1146–1150 | Phụ đề ngoài chỉ nhận hậu tố vi/cs/en ⇒ thêm `ja` vào `SubKeepLangs` cũng không ghép được `Phim.ja.srt` | Dùng bảng ngôn ngữ chung (`naming.lang_of_suffix`) cho phụ đề ngoài và cho mốc đo audio ghép (GĐ4 ✅) |
| TĐ-26 | SP 899, MT 1878 | `Get-CueTimes` (SP/MT) không nhận `Dialogue: Marked=0,…` (SSA v4) và đọc file không BOM theo bảng mã ANSI | Dùng bản SF + `encoding.read_text` (GĐ1 ✅) |
| TĐ-27 | SP 1794–1808 | `Remove-FileSafe`: đưa vào Thùng rác lỗi thì **tự chuyển sang xoá hẳn** | Thùng rác lỗi → báo "không xoá được", không xoá hẳn. Chỉ xoá hẳn khi bạn tắt `DeleteToRecycleBin` (GĐ1 ✅) |
| TĐ-28 | SF 1793–1799 | `AutoPickWhenSingle` tự lấy cả ứng viên duy nhất mà kho đo được chỉ 8–19 mốc ("nghi ngờ, cần kiểm tra") ⇒ có thể âm thầm lấy phụ đề của PHIM KHÁC (test GĐ2 tái hiện được: file kho của phim khác đạt 8–9 mốc với tập phim bộ) | Ứng viên duy nhất mà "nghi ngờ" → không tự lấy, hiện trên bảng để bạn quyết (GĐ2 ✅) |
| TĐ-29 | SF 1981–1995 | Tên anime đánh số tuyệt đối (`[Judas] One Piece - 0643`): SubFetch coi là **phim lẻ** (không nhận số tập) ⇒ tìm SubSource bằng tên `One Piece - 0643`, không lọc season | Tên series lấy từ bộ nhận diện tập; có TMDB key → đổi số tuyệt đối sang season (cộng dồn `episode_count`) để chọn đúng `movieId` season trên SubSource và chọn file trong gói (nhận cả `- 0643` lẫn `S16E643`); không xác định được season → bỏ qua SubSource cho tập đó (kho + OpenSubtitles vẫn chạy). OpenSubtitles: gửi season/tập đã đổi, không có thì chỉ `episode_number` — **cần kiểm bằng script dò** (GĐ2 ✅, chờ kết quả dò) |
| TĐ-30 | SF 618–653 | Phụ đề gốc trích từ MKV được ghi vào `srt_goc` ngay khi quét | Lập kế hoạch không ghi gì vào thư mục phim: phụ đề gốc trích ra thư mục tạm, chỉ chép vào `srt_goc` khi bấm chạy (ghi nhật ký hoàn tác) (GĐ2 ✅) |
| TĐ-31 | SP 1073–1179 (1165) | `Resolve-Ref` ghép nhầm tập — **đã chứng minh bằng chạy hàm gốc** (`tests/test_golden_subprep.py`): (a) tầng 2 (so tên phim) không xét số tập ⇒ anime đánh số tuyệt đối (`[Judas] One Piece - 0643/0644/0645`) luôn "NHẬP NHẰNG theo tên phim", không bao giờ ghép được; (b) thiếu file phim tập 5, chỉ còn tập 6 ⇒ phụ đề `Show.S01E05.vi` được ghép vào `Show.S01E06` (điểm tên 0,5) — bị đổi tên thành tập 6 và đồng bộ theo tập 6; tầng 4 "1 phụ đề – 1 mốc" cũng ghép dù số tập khác nhau; (c) ở tầng 3, `Select-RefVariant` luôn trả **một** file trước khi kiểm tra nhập nhằng ⇒ kiểm tra không bao giờ chạy, `Sousou no Frieren - 05` được ghép bừa vào `S01E05` hoặc `S02E05` | Trước tầng 2 và tầng 4: loại các mốc ghi số tập (chắc chắn) **khác** số tập của phụ đề — đúng quy tắc §6.6 "hai tên đều ghi số tập mà khác nhau → không bao giờ ghép". Tầng 3: gom theo phim rồi mới chọn bản đại diện ⇒ nhiều phim khác nhau cùng khớp → "nhập nhằng tập E05" (bỏ qua + báo). Mọi ca khác cho **cùng kết quả** với hàm gốc (có test đối chiếu) (GĐ3 ✅) |
| TĐ-32 | SP 1452, 1608, 1262, 1402 | Vài chỗ ghi đè / mất thông tin nhỏ ở bước ②: (a) phụ đề chưa đổi tên (không có hậu tố ngôn ngữ, vd nằm trong `srt_can_sync\cs\`) cho kết quả `Phim.srt` không hậu tố ⇒ bước ③ coi là `DefaultExternalLang` (vi); (b) hai phụ đề cùng ra một đích ⇒ cái sau ghi đè cái trước; (c) tắt `SkipExistingOut` (hoặc `SkipExistingRef`) ⇒ file đích cũ cạnh phim / trong `srt_goc` bị ghi đè mất, không hoàn tác được; (d) cờ alass không hỗ trợ bị bỏ nhưng **giá trị** đi kèm (vd `--split-penalty 7`) vẫn bị gửi, alass hiểu `7` là tên file | (a) không có hậu tố → lấy ngôn ngữ đã chọn / thư mục con; (b) cái sau bỏ qua, lý do "trùng đích với …"; (c) file cũ vào **Thùng rác** trước (ghi `Delete` vào nhật ký); (d) bỏ cả giá trị đi kèm. Ngoài ra: kết quả alass ghi ra thư mục tạm, xử lý xong mới chép cạnh phim (bấm Dừng không để lại file dở dang); đổi tên + đồng bộ + dọn dẹp của một lượt ghi chung **một** nhật ký (như .bat), lưu sau mỗi bước (GĐ3 ✅) |
| TĐ-33 | MT 425–446, 647, 590–620 | (a) `Get-CodecName` đọc `properties.codec` — nhưng `mkvmerge -J` ghi `codec` ở **cấp track** (đã kiểm với mkvmerge v82 thật) ⇒ hàm luôn rơi về `codec_id`: tên track ra `English EAC3 5.1`, `English TRUEHD 7.1`, DTS-HD MA chỉ còn `DTS` (yêu cầu §6.7: `English E-AC-3 5.1`); ước lượng bitrate của AC3/E-AC-3/PCM rơi về mặc định 300 kbps ⇒ chọn Default "bitrate cao nhất" có thể sai khi thiếu tag BPS và ffprobe. (b) Thẻ codec ra `TRUEHD7.1`, `DTS` thay vì `TrueHD7.1`, `DTS-HD`. (c) `Get-DynamicRange` khi không có ffprobe: tên file `HDR10` (không có `+`) không được nhận là HDR (`\bHDR\b` không khớp `HDR10`) | Đọc `codec` cấp track (đúng ý định ghi trong .bat "ưu tiên field codec của mkvmerge"), không có thì dịch `codec_id` qua bảng (`A_EAC3` → E-AC-3, `A_AC3` → Dolby Digital…); `HDR10` → HDR. Chứng minh bằng test đối chiếu: hàm gốc với JSON mkvmerge thật trả `EAC3`/`DTS`; cùng hàm khi được đưa `codec` cho kết quả **giống hệt** bản mới — **đã duyệt** (GĐ4 ✅) |
| TĐ-34 | MT 2156–2159 | Đổi tốc độ audio ghép khác FPS bằng `asetrate=<số nguyên>,aresample=<sr>,atempo=<bù>`: **`atempo` làm audio sớm ~21 ms cố định** (đo bằng tiếng bíp: `asetrate+aresample` lệch 0,01 ms; thêm `atempo` lệch −21,3 ms) ⇒ ghép PAL vào phim 23,976 lệch ~20–21 ms, **không đạt yêu cầu < 20 ms** (§8, §10) | Viết tỉ lệ thành phân số p/q (25/23,976 = 3125/2997), đổi tần số trung gian R = p·k gần `sr` rồi `asetrate` = q·k — số nguyên chính xác, không cần `atempo`: `aresample=46875,asetrate=44955,aresample=48000` (PAL → 23,976). Đo lại: lệch < 0,2 ms với mọi tỉ lệ; test tiếng bíp đầu–cuối: < 2 ms ở đầu và cuối phim. Tỉ lệ không viết gọn thành phân số được (hiếm) → dùng lại chuỗi cũ. **Khác chữ của §6.9** (chuỗi lọc cụ thể) để giữ đúng kết quả — **đã duyệt** (GĐ4 ✅) |

### 7.C. Bỏ vì GUI thay thế (không mất tính năng)

Phần console (`Initialize-Console`, `Disable-QuickEdit`, `Set-ConsoleSize`, `W`, `Write-Step`, `Initialize-BoxChars`, `Get-BoxLine`, `Get-TableWidth`), các bảng console/cửa sổ được thay bằng bảng GUI (`Show-CandidateTable`, `Show-AudioStreams`, `Out-GridView`, WinForms `Show-SubPickerGrid`; `Limit-Text` chuyển sang `gui/widgets`), các biến `$UseColors`, `$UseGridView`, `$TableWidth`, `$TableMinWidth`, `$ConsoleCols/Rows/Scrollback`, `$UndoLastRun`, `$DryRun`, `$PreviewFirst`. Hai hàm không bao giờ được gọi: `Get-CandidateScore`, `Get-ErrBody` (không cần với `requests`). `Get-OsScore`: xem Q2.

### 7.D. Mở rộng nhỏ (giữ kết quả cũ, chỉ bao thêm trường hợp)

- Bảng ngôn ngữ chung 18+ ngôn ngữ cho mọi bước (trước: bước ② chỉ 10, bước ③ phụ đề ngoài chỉ 3).
- `cue_times` nhận VTT `mm:ss.ttt`.
- `find_tool` tìm ở hợp mọi vị trí của ba file (và registry) cho mọi công cụ.
- Thêm bảng mã cũ: `ar` (1256), `tr` (1254), `pl` (1250), `ru` (1251) cho `--sub-charset`.
- Bước ① chọn track phụ đề gốc theo `RefSubLangs` như bước ② (trước: lấy track văn bản đầu tiên không Forced, không xét ngôn ngữ).
- Mọi chỗ nhận diện tập/tên/ngôn ngữ của ba bước dùng chung một hàm ⇒ hành vi nhất quán.

### 7.E. API bên ngoài

Chưa kiểm với máy chủ thật (cần key của bạn). Giai đoạn 2 sẽ có `tools/probe_subsource.py`, `probe_opensubs.py`, `probe_tmdb.py`, `probe_tvdb.py` theo §2: in status, header quan trọng (giới hạn lượt), JSON thô đã che key/token, thử các biến thể tham số. Bạn chạy một lần với key thật; chỗ nào khác tài liệu trong §6.10–6.11 sẽ được ghi vào đây (hàm, vì sao hỏng, cách mới, kiểm chứng ra sao).

Việc cần kiểm cụ thể (script dò đã có ở GĐ2: `chay_do_api.bat`): mã ngôn ngữ OpenSubtitles cho 18 ngôn ngữ (vd `zh` hay `zh-cn`, `pt` hay `pt-pt/pt-br`); OpenSubtitles tìm anime theo `episode_number` không kèm season (TĐ-29); tên header giới hạn lượt của SubSource (để hiện "còn 58/60 lượt/phút"); `limit=100` còn được nhận không; tên trường `language`, `releaseInfo`, `framerate`, `preview`; `base_url` của OpenSubtitles VIP; `GET /infos/user`; TMDB `/configuration` với key và với token; TVDB `/login` có/không `pin`.

**Lần dò 1 (2026-11-09):** `ket_qua_do_api.txt` báo "Bí mật tìm thấy trong Windows Credential Manager: (không có)" ⇒ cả 4 API
"chưa có key → bỏ qua", **chưa kiểm được gì** với máy chủ thật; giả định trong `subsource.py`, `opensubs.py`, `tmdb.py` giữ nguyên.
Nguyên nhân: Bước 1 của `chay_do_api.bat` không nhập được key (bấm Enter bỏ qua, hoặc chỉ tới thư mục không có bản .bat có key
— kể cả thư mục `reference\` đã xoá key). Đã sửa script: thư mục không có key → báo rõ và hỏi lại (trước đây báo nhầm lỗi
"cài Python" hoặc chạy tiếp không key); kết quả dò không có key thì in hướng dẫn chạy lại ở đầu và cuối file.

Ngoài ra (Giai đoạn 4 ✅): mkvmerge in thông báo theo ngôn ngữ Windows, nên lọc dòng "warning/error" bằng chữ tiếng Anh như .bat có thể không bắt được trên Windows tiếng Séc/Việt. Bản mới quyết định theo **mã thoát** và hiện output; `mkvmerge --ui-language list` có `en_US` (đã kiểm với v82) thì thêm `--ui-language en_US` để log tiếng Anh dễ đọc.

---

## 8. Kế hoạch kiểm thử

- `pytest` cho toàn bộ `core/`, dữ liệu tự sinh, không cần phim thật, không gọi mạng (mock `requests`), `keyring` dùng bộ nhớ tạm trong test.
- **Test đối chiếu với hàm gốc (nếu có `pwsh`)**: chạy cùng dữ liệu ngẫu nhiên qua hàm PowerShell gốc (nạp từ .bat như script kiểm chứng) và hàm Python, so kết quả — cho `even_sample`, `offset_vote`, `timing_relation`, `quick_offset`, `pgs_to_timing_srt` (trừ lỗi giờ đã sửa), `title_tokens`, `title_score`, `resolve_ref`… Không có `pwsh` hoặc không có file .bat tham chiếu thì test tự bỏ qua. → Q1.
- **Test đầu–cuối có công cụ thật** (`ffmpeg`, `mkvmerge`, `mkvextract`, `alass`): tự bỏ qua nếu thiếu công cụ. Tôi chạy trên máy Linux; bạn chạy lại trên Windows bằng `run_tests.bat` (sẽ có ở Giai đoạn 6).

| Hành vi (§) | Test |
|---|---|
| §6.1 tên có `[ ]` | mọi test file dùng thư mục `[YTS.MX] Phim (2020)` và `[Judas] One Piece - 0643 [1080p]` |
| §6.1 tham số subprocess | `tools.run` nhận list, không `shell=True`, có `CREATE_NO_WINDOW` (mock `subprocess`) |
| §6.1 dấu thập phân | `to_double('23,976')`, `fmt_inv(1.0427…)` dưới locale `cs_CZ`/`vi_VN` |
| §6.2 mkvmerge | dựng lệnh với giả lập v73/v74/v49: tên cờ, mã ngôn ngữ, `--sub-charset`, cờ Default/Forced cho **mọi** track, `--title`, `--track-order`; mã thoát 1 = OK, 2 = xoá đầu ra |
| §6.3 mốc thời gian | SRT (`,` và `.`), ASS (`Dialogue: 0,` và `Marked=0,`), VTT; PGS tự sinh: PCS bảng màu bị bỏ, đóng dòng khi gặp dòng mới, bỏ dòng ≤ 0,1 s; chuẩn hoá UTF-8 (BOM UTF-8/16, cp1258, cp1250) |
| §6.4 so khớp | phụ đề giả lập lệch + co giãn đã biết cho cả 7 tỉ lệ → đúng tỉ lệ, lệch sai < 0,1 s, ≥ 20 phiếu; phụ đề "phim khác" → < 20 phiếu; ca bằng phiếu → giống thứ tự .bat; bảng băm cho cùng kết quả với duyệt tuyến tính và nhanh hơn |
| §6.5 recap | 30 dòng recap + 300 dòng thật → cắt đúng 30; recap > 40% → huỷ; khối 00:00:00 (> 2 dòng) bị bỏ; vòng đọc→ghi giữ đúng giờ (TĐ-01); sức khoẻ: mỗi dấu hiệu một ca, đúng mức 0/1/2 |
| §6.5 alass | dựng tham số: mốc phụ đề không cờ; mốc audio `--no-split --disable-fps-guessing`; lệch FPS bỏ cờ; luôn trích WAV (`-map 0:a:N -vn -ac 1 -ar 16000 -c:a pcm_s16le`); `MaxShiftSeconds` chỉ từ chối với audio; thứ tự thử lại (TĐ-19) |
| §6.5 ghép tên | nhập nhằng → bỏ qua + lý do; `srt_pool`/`Output`/`audio_nguon` không bị quét làm mốc |
| §6.6 tên tập | toàn bộ danh sách ở 3.4; `S01E10`/`S01E01` chặn; không bao giờ `Season 00` |
| §6.7 audio | commentary/AD bị xoá; lọc hết → giữ hết; `und` 1 track → `en`; `und` ≥ 2 → bỏ qua file; ≥ 2 tiếng Anh; Default = tiếng Anh bitrate cao nhất; bitrate BPS → ffprobe → ước lượng; quy tắc nhớ "cái giữ" + hệ kênh; quy tắc làm mất hết audio → bỏ qua; tên track `English E-AC-3 5.1` |
| §6.8 phụ đề | `SubKeepLangs`, sub ngoài thay sub trong, Forced/ảnh/commentary/English SDH; chọn sub ngoài theo `SubExtPriority`; không hậu tố = `DefaultExternalLang`; Default theo `SubDefaultPriority` |
| §6.9 ghép audio | ghép tập `S16E643` ↔ `- 0643`; đo bằng phụ đề chung; cùng FPS → `--sync TID:ms`; khác FPS → chuỗi lọc đổi tốc độ đúng toán (`asetrate` số nguyên, hệ số thời lượng = tỉ lệ FPS; TĐ-34); không đo được → chỉ ghép khi chênh ≤ 1 s; độ lệch tay |
| §6.9 **tiếng bíp** | ffmpeg tạo phim 23,976 fps có phụ đề tiếng Anh + "bản nguồn" 25 fps có audio tiếng Séc là tiếng bíp đặt đúng từng câu thoại, lệch +1,5 s; ghép; đo vị trí bíp trong kết quả bằng `silencedetect`: sai số < 20 ms ở đầu và cuối phim (so với tiếng bíp của audio gốc trong cùng lần giải mã — đo tuyệt đối bị ffmpeg dời theo độ trễ mồi AAC). Thêm ca cùng FPS, lệch âm. Kết quả GĐ4: < 2 ms ✅ |
| §6.10 đặt tên | phim lẻ/phim bộ đúng mẫu Jellyfin; bỏ ngoặc + ký tự cấm; thẻ codec chỉ ở tên file, tắt cho phim bộ; TMDB key 32 ký tự ↔ `eyJ`; 401 → tắt cả lượt + báo một lần; 404 im lặng; One Piece hai series trùng tên; số tuyệt đối 643 (TMDB cộng dồn, TVDB `absoluteNumber`); TVDB đăng nhập lại khi 401, phân trang, tên tập theo ngôn ngữ rồi `eng` |
| §6.11 tải phụ đề | (mock) bản tiếng Việt để trống `hearingImpaired/foreignParts` vẫn được nhận; không gửi tham số lọc lên máy chủ; `limit=100` bị từ chối → 20; gói ZIP chọn đúng tập; preview: bản đúng nhận, bản ngẫu nhiên bị từ chối; quota 50/5; OpenSubtitles 401 → đăng nhập lại ≤ 2 lần; 403 user-agent → tắt nguồn; VIP `base_url`; xếp hạng FPS → đã đo → độ lệch nhỏ → giống tên; nhớ lựa chọn theo season + ngôn ngữ, không nhớ bản riêng tập khác, bản nhớ hỏng → thử bản kế + xoá khỏi bộ nhớ; kho nội bộ < 8 / 8–19 / ≥ 20 mốc; `PoolFirst`; file có sẵn: đo được thì chỉ tin đo, **chép** không di chuyển; bước ① **không** sửa thời gian |
| Cấu hình | nhập từ ba file .bat thật (đã che key) → đúng mọi khoá, `@()` nhiều dòng, `@{}`; bí mật vào keyring, không vào `config.json`, không ra log |
| Hoàn tác | tạo / chép / di chuyển / đổi tên → hoàn tác đúng thứ tự ngược, file tạo ra vào Thùng rác (mock `send2trash`); đọc được nhật ký cũ của .bat; giữ 20 lần |
| Giao diện (GĐ5) | `test_gui_logic.py` (không cần màn hình): dòng bảng / trạng thái / màu của ba thẻ, `GridModel` (ô/cột/dòng/"Trả về mặc định"), `Runner` (một việc mỗi lúc, xếp hàng, Dừng sau file / Dừng ngay kết thúc tiến trình con thật, lỗi không làm chết vòng đọc), chữ ↔ giá trị cho MỌI khoá cấu hình, "Kiểm tra kết nối" (API giả), bảng nhập từ .bat (bí mật luôn che), `ellipsize_middle` = `Limit-Text` gốc. tên file dài (`fit_text`: giữ số tập + đuôi file ở mọi độ rộng), cỡ cửa sổ cho 720p / 768p / 1080p (100%, 125%, 150%) / 1440p / 4K (`plan_window`), `ui_factor`. `test_gui_app.py` (Tk thật, tự bỏ qua nếu không có màn hình): bảng / cột cây / bảng chọn phụ đề hiện tên dài vẫn phân biệt được tập, rê chuột thấy tên đầy đủ, kéo rộng cột thì hiện thêm; cửa sổ vừa màn hình, nhớ cỡ theo đơn vị logic, hộp thoại tự thu cho vừa; ③ MKV thật 2 audio tiếng Anh → Cần chọn → tick xoá + áp dụng cho file còn lại → bảng chọn phụ đề → Xem lệnh → mux → Hoàn tác; "Cần chọn" tên (Tìm lại / Chọn / Sửa tay / Giữ tên gốc / hỏi khi bấm Bắt đầu); ① kho → chọn trong bảng ứng viên → tải; ② đổi tên → đồng bộ (alass giả) → CSV; Cài đặt: mọi ô đọc/ghi đúng, bí mật vào keyring |

Trước khi báo xong mỗi giai đoạn: chạy hết test và dán kết quả.

---

## 9. Các giai đoạn & môi trường

| GĐ | Nội dung | Bạn cần làm |
|---|---|---|
| **0** | ✅ Đặc tả này + script kiểm chứng — **đã duyệt** | ~~Đọc, trả lời mục 10~~ |
| **1** | ✅ `timing`, `pgs`, `srt`, `encoding`, `episodes`, `naming`, `config`, `secrets`, `undo` + 298 test (gồm 17 test đối chiếu hàm gốc qua PowerShell) — **đã duyệt** | Xem kết quả test; (tuỳ chọn) chạy `run_tests.bat` trên Windows |
| **2** | ✅ `tools`, `probe` (phần bước ①), `tmdb` (IMDb + chọn series + số tập tuyệt đối), `subsource`, `opensubs`, `pool`, `subfetch` + 4 script dò + `chay_do_api.bat` + test (API giả + MKV thật) — **đã duyệt** (kết quả dò API thật vẫn chờ, xem 7.E) | **Bấm đúp `chay_do_api.bat`**, gửi tôi `ket_qua_do_api.txt` (đã che key) |
| **3** | ✅ `subprep` (chỉ mục mốc, ghép tên, đổi tên, trích phụ đề gốc, alass, cắt recap, sửa 00:00:00, sức khoẻ, dọn dẹp, nhật ký/hoàn tác) + test (alass giả, MKV thật, đối chiếu hàm gốc) — **đã duyệt** | Xem TĐ-31, TĐ-32; (tuỳ chọn) `run_tests.bat` — trên máy bạn chạy được cả test alass thật |
| **4** | ✅ `mux`, `donor`, `tvdb`, phần bước ③ của `probe`/`tmdb`/`naming` + test (JSON mkvmerge giả, API TMDB/TVDB giả, mkvmerge/ffmpeg thật, test tiếng bíp, đối chiếu hàm gốc) — **đã duyệt** (cả TĐ-33, TĐ-34) | ~~Xem TĐ-33, TĐ-34~~ |
| **5** | ✅ Giao diện 4 thẻ (`gui/`) + khung log + luồng phụ + nút Dừng + màn hình Hoàn tác + test giao diện — **chờ duyệt** | Bấm đúp `chay_muxstudio.bat`, thử trên Windows |
| 6 | PyInstaller `--onedir --windowed`, `build.bat`, icon, `HUONG_DAN.md` | Build và chạy `MuxStudio.exe` |

Môi trường của tôi: máy Linux trong đám mây. Tôi đã cài PowerShell 7 ở đó để chạy hàm gốc; các phần chỉ có trên Windows (Credential Manager, Thùng rác, `CREATE_NO_WINDOW`, đường dẫn `C:\`) được test bằng giả lập, và bạn thử thật ở Giai đoạn 5–6. Thư viện: `requests`, `keyring`, `send2trash`, `customtkinter`; test thêm `pytest` (chỉ để chạy test, không nằm trong .exe); đóng gói thêm `pyinstaller`.

---

## 10. Câu hỏi cần bạn quyết

> **Đã quyết (2026-11-08):** bạn đồng ý tất cả đề xuất dưới đây. Riêng **Q7** bạn nhấn mạnh: *phụ đề ASS giữ là phụ đề ASS* — ở mọi bước (tải về, kho, file có sẵn, phụ đề gốc trích từ MKV, kết quả alass, mux) phụ đề ASS/SSA giữ nguyên định dạng và đuôi, **không** chuyển sang SRT. Các thao tác chỉ làm được với SRT (cắt recap, ghi lại khối) sẽ bỏ qua file ASS và ghi rõ lý do trong bảng, không âm thầm đổi định dạng. Q8 không bắt buộc, chưa có danh sách tên file.
>
> Q1 đã thực hiện: bản sao ba file .bat đã xoá 7 bí mật nằm trong `reference/` (tạo bằng `tools/sanitize_bat.py`).

Mỗi câu có **đề xuất của tôi**. Bạn chỉ cần trả lời "đồng ý hết" hoặc ghi số câu muốn khác.

| # | Câu hỏi | Đề xuất |
|---|---|---|
| Q1 | Cho tôi đưa **bản sao ba file .bat đã xoá sạch 7 bí mật** vào repo (`reference/`) để test đối chiếu tự động với hàm gốc? (Repo hiện chưa có file .bat nào; bản gốc có key thật nên tôi **không** đưa lên.) | **Có** |
| Q2 | Khi **tắt** `OsExcludeMachine`, bản dịch máy/AI của OpenSubtitles nên xếp thế nào? (.bat định trừ điểm nhưng hàm đó không bao giờ chạy) | Xếp **sau** bản thường có cùng hạng FPS và cùng tình trạng đo; cột Thông tin ghi "dịch máy" |
| Q3 | File có ≥ 2 audio tiếng Anh: .bat hiện thực tế **không** bỏ qua (Default = bitrate cao nhất). Bản mới làm sao? | Dòng đó hiện **"Cần chọn"** (vàng) trong bảng tổng quan, không mux cho tới khi bạn tick trong bảng audio của file đó hoặc bấm "Giữ cả hai, Default = bitrate cao nhất"; tắt `SkipOnMultipleEnAudio` thì tự lấy bitrate cao nhất như hiện tại |
| Q4 | Quy tắc năm mới: "năm" lớn hơn năm hiện tại + 1 (vd. 2049) hoặc đứng đầu tên mà bỏ đi thì tên rỗng (vd. `2001 A Space Odyssey`) thì coi là một phần tên phim? | **Có** |
| Q5 | `KeepPaidDownloads` (không được dùng trong .bat): khi **tắt**, file OpenSubtitles đã tải mà kiểm chứng yếu thì làm gì? | Bật (mặc định): giữ + cảnh báo như hiện tại. Tắt: chuyển file vào Thùng rác và tự thử ứng viên kế tiếp. Nguồn miễn phí (SubSource, kho) vẫn như .bat: giữ + cảnh báo |
| Q6 | Bước ① bỏ qua video trong `Output\`, `audio_nguon\`, `srt_pool\`, `srt_goc\` và thư mục log? | **Có** |
| Q7 | Phụ đề tải về / phụ đề gốc dạng ASS giữ đuôi `.ass` (thay vì đổi thành `.srt` mà nội dung vẫn là ASS)? | **Có** |
| Q8 | Có những thư mục phim nào bạn muốn tôi dùng làm **ví dụ tên file thật** để thêm vào test nhận diện tên tập (chỉ cần danh sách tên file, không cần phim)? | Không bắt buộc — gửi nếu tiện |
