# Xây dựng MuxStudio — phần mềm Windows (Python + CustomTkinter) thay cho SubFetch, SubPrep và MuxTool

## 0. Bối cảnh và cách làm việc với tôi

Tôi đang tự dựng một máy chủ phim gia đình chạy **Jellyfin** trên NAS. Hiện tôi chuẩn bị file bằng ba công cụ dòng lệnh tự viết, nằm cùng thư mục với file này, chạy theo đúng thứ tự:

| Bước | File | Việc |
|---|---|---|
| 1 | **`SubFetch_v30.bat`** | Tìm và tải phụ đề (SubSource → kho nội bộ `srt_pool` → OpenSubtitles), chọn bản đúng FPS và khớp thời gian nhất, hạ vào `srt_can_sync\<ngôn ngữ>\`. Trích phụ đề gốc từ MKV vào `srt_goc\`. |
| 2 | **`SubPrep_v20.bat`** | Đổi tên phụ đề cho khớp tên file phim, đồng bộ thời gian bằng `alass`, cắt đoạn recap, kiểm tra sức khoẻ. |
| 3 | **`MuxTool_v17.bat`** | Dọn audio/phụ đề trong MKV, ghép phụ đề ngoài và audio từ file phim khác, đặt tên theo TMDB/TVDB chuẩn Jellyfin, mux bằng `mkvmerge`. |

Cả ba là PowerShell gói trong file .bat (phần PowerShell nằm sau dòng `#>`). Tôi muốn thay chúng bằng **một phần mềm .exe có giao diện**, tên tạm là **MuxStudio**.

Cách làm việc:
- Tôi **không phải lập trình viên**. Trả lời và viết giao diện **bằng tiếng Việt có dấu**. Hướng dẫn chạy/thử phải từng bước, ngắn gọn.
- Làm theo **từng giai đoạn** ở mục 9. Dừng ở cuối mỗi giai đoạn để tôi kiểm tra, đừng làm hết một mạch.
- Ba file .bat quy định **phần mềm phải làm được gì** (kết quả, quy tắc, ngưỡng). Chúng **không** bắt buộc cách viết code — xem mục 2.

## 1. Đọc hết ba file .bat trước khi viết code

1. Đọc **toàn bộ** `SubFetch_v30.bat` (~2.140 dòng, 62 hàm), `SubPrep_v20.bat` (~2.000 dòng, 55 hàm) và `MuxTool_v17.bat` (~2.860 dòng, 78 hàm). Đừng đọc lướt: nhiều quy tắc nhỏ là kết quả sửa lỗi thực tế.
2. Viết `docs/SPEC.md` gồm:
   - Bảng **từng hàm PowerShell → module/hàm Python** sẽ thay nó, hoặc ghi "bỏ" kèm lý do (vd. `Initialize-Console`, `Disable-QuickEdit`, `Set-ConsoleSize`, `W`, `Write-Step`, `Show-CandidateTable`, `Get-TableWidth`: thuộc console, GUI thay hết).
   - **Hàm trùng giữa các file** (vd. `Get-EpisodeInfo` ở SubFetch/SubPrep với `Split-EpisodeName` ở MuxTool; `Get-TimingRelation`, `Get-CueTimes`, `Convert-PgsToTimingSrt`, `ConvertTo-Double`, `Initialize-Console`…): gộp thành **một** bản trong `core/`, lấy bản mạnh nhất, ghi rõ bản nào được chọn và khác nhau ở đâu. Bản gộp phải qua **toàn bộ** ví dụ của cả ba file.
   - Bảng **từng biến cấu hình** (`$Ten = giá trị` ở đầu mỗi file) → khoá trong cấu hình, giá trị mặc định, ô nhập trên màn hình Cài đặt. Biến trùng tên giữa các file (vd. `MinVerifyVotes`, `ExtractFolder`, `TmdbApiKey`, `ScanSubFolders`) gộp làm một.
   - Mỗi chỗ `Read-Host` (câu hỏi trong console) → màn hình hoặc hộp thoại GUI thay nó.
   - Mục **"Thay đổi so với .bat"** (xem mục 2).
3. Gửi tôi `SPEC.md` để duyệt **trước khi** viết code (Giai đoạn 0).

## 2. Khi một hàm port sang không chạy: viết hàm mới cho tương thích

Mục tiêu là **giữ kết quả**, không phải chép từng dòng. Nếu một hàm sau khi port sang Python không chạy đúng — vì khác biệt giữa PowerShell và Python, vì công cụ ngoài đổi phiên bản, hay vì **API bên ngoài đã thay đổi** (SubSource, OpenSubtitles, TMDB, TVDB đổi endpoint, tên trường, cách xác thực) — thì:

1. **Chứng minh nó hỏng** bằng một test hoặc một lần gọi thật, đừng đoán.
2. Với API: viết **script dò** trong `tools/probe_<api>.py`. Script gọi endpoint, in nguyên phản hồi (status, header quan trọng, JSON thô, đã che key/token), thử các biến thể tham số, rồi rút ra cách gọi đúng. Tôi từng tự viết `SubSource_Probe.bat` và `OS_Probe.bat` theo đúng cách này, và nhờ vậy mới tìm ra `searchType`, tên trường `language`, User-Agent hợp lệ…
3. **Viết hàm mới** đạt cùng kết quả, giữ nguyên tên và chữ ký mà phần còn lại của chương trình dùng (để chỗ khác không phải sửa).
4. Ghi vào `SPEC.md`, mục "Thay đổi so với .bat": hàm nào, vì sao hỏng, cách mới làm gì, đã kiểm chứng ra sao.
5. **Không âm thầm bỏ tính năng.** Nếu thật sự không làm được, dừng lại hỏi tôi.

Mỗi API có nút **"Kiểm tra kết nối"** trong Cài đặt: chạy phần dò ở trên và hiện kết quả dễ hiểu (vd. "SubSource: key hợp lệ, còn 58/60 lượt/phút"; "OpenSubtitles: đăng nhập VIP, máy chủ vip-api, còn 987/1000 lượt tải").

## 3. Công nghệ

- **Python 3.11+**, **CustomTkinter** cho giao diện. Các bảng có cột dùng `ttk.Treeview` đã chỉnh màu cho hợp CustomTkinter. Ô tick trong bảng dùng ký hiệu ☑/☐, bấm để đổi, vì bảng 100 tập × 8 cột mà tạo từng `CTkCheckBox` sẽ rất chậm.
- Thư viện được dùng: `requests`, `keyring` (lưu mật khẩu/key vào Windows Credential Manager), `send2trash`. Thêm thư viện khác phải nói lý do.
- **Công cụ ngoài, gọi qua `subprocess`:** `mkvmerge`, `mkvextract`, `ffmpeg`, `ffprobe`, `alass`. Không viết lại các công cụ này. Trên máy tôi chúng nằm ở:
  - `C:\Users\fenha\Desktop\mkvtoolnix\` (mkvmerge, mkvextract, ffprobe)
  - `C:\Users\fenha\Desktop\alass-windows64\bin\alass\` (alass.exe, ffmpeg.exe, ffprobe.exe)
  - Màn hình Cài đặt cho chọn lại đường dẫn, có nút "Tự tìm" làm giống `Find-Exe` / `Find-Near`, và hiện phiên bản `mkvmerge` đọc được.
- Đóng gói bằng **PyInstaller `--onedir --windowed`** (không dùng `--onefile`: khởi động chậm và hay bị phần mềm diệt virus báo nhầm). Nhớ gom dữ liệu theme của CustomTkinter. Kèm `build.bat` để tôi tự build lại.
- Chạy trên Windows 10/11, không cần quyền Administrator.

## 4. Kiến trúc bắt buộc

```
muxstudio/
  core/        # KHONG import tkinter/customtkinter o day - phai test duoc khong can GUI
    config.py     secrets.py    tools.py      probe.py      episodes.py   naming.py
    timing.py     pgs.py        srt.py        encoding.py   undo.py
    subsource.py  opensubs.py   tmdb.py       tvdb.py
    pool.py       subfetch.py   subprep.py    mux.py        donor.py
  gui/
    app.py  fetch_view.py  subprep_view.py  mux_view.py  settings_view.py  widgets.py  log_panel.py
  tools/       # script do API (muc 2)
  tests/
  docs/SPEC.md
  build.bat
```

- **Lập kế hoạch rồi mới thực thi.** Mỗi file đi qua hai bước:
  1. **Lập kế hoạch:** sinh ra một `dataclass` mô tả việc sẽ làm — tải bản phụ đề nào, đổi tên gì, giữ/xoá track nào, ghép audio nào lệch bao nhiêu ms, lệnh `mkvmerge` cuối cùng.
  2. **Thực thi:** chạy đúng kế hoạch đó.

  GUI hiện kế hoạch để tôi xem và sửa trước khi bấm chạy. Mọi `Read-Host` trong .bat thành chỗ sửa kế hoạch, không phải câu hỏi chặn giữa chừng. Chế độ `DryRun` thành nút "Xem lệnh".
- **Việc nặng chạy ở luồng phụ.** Truyền log và tiến độ về giao diện qua `queue` + `after()`. Có nút **Dừng**: dừng sau file đang chạy, hoặc dừng ngay bằng cách kết thúc tiến trình con. Giao diện không bao giờ được đơ.
- **Cấu hình** lưu ở `%APPDATA%\MuxStudio\config.json`.
- **Bí mật** (`SubSourceApiKey`, `TmdbApiKey`, `TvdbApiKey`, `TvdbPin`, `OsApiKey`, `OsUsername`, `OsPassword`) lưu bằng `keyring` (Windows Credential Manager), **không** lưu trong `config.json`, không bao giờ in ra log, không đưa vào mã nguồn, che khi hiển thị.
- Nút **"Nhập cấu hình từ file .bat"**: đọc các dòng `$Ten = giá trị` ở đầu cả ba file (kể cả key và tài khoản OpenSubtitles) để tôi khỏi gõ lại. Bí mật đọc được thì đưa thẳng vào `keyring`.
- **Nhật ký và hoàn tác** cho cả ba bước: `_subfetch_logs\actions_*.csv`, `_subprep_logs\run_*.csv`, `_muxtool_logs\mux_*.csv`. Hoàn tác nhiều lần chạy, giữ 20 lần gần nhất. Xoá file thì vào Thùng rác.
- **Bộ nhớ đệm trong một lượt chạy** cho mọi lần gọi API và mọi gói ZIP đã tải.

## 5. Giao diện

Một cửa sổ, bốn thẻ theo đúng thứ tự quy trình, cộng một **khung log màu** luôn hiện ở dưới (thu gọn được, có nút "Lưu log", "Mở thư mục").

Thư mục phim chọn **một lần** ở đầu cửa sổ và dùng chung cho cả ba thẻ. Mỗi thẻ có nút **"Sang bước tiếp →"**.

### 5.1. Thẻ "① Tải phụ đề" (thay SubFetch)
1. **Ngôn ngữ cần tải**: các ô tick (vi, en, cs bật sẵn; ja, ko, zh, fr, de, es, th, id, ar tắt) + ô nhập mã khác, giống các biến `$Lang_xx` và `$LangExtra`.
2. **Nguồn**: SubSource, kho nội bộ `srt_pool`, OpenSubtitles — mỗi nguồn bật/tắt được, hiện tình trạng (key hợp lệ? còn bao nhiêu lượt tải OpenSubtitles hôm nay?).
3. Quét thư mục: liệt kê từng phim/tập, FPS video, phụ đề gốc đọc được từ đâu (`srt_goc` dùng lại, trích mới từ MKV, đọc từ PGS, hoặc không có), và phụ đề đã có sẵn trong `srt_can_sync` (dùng lại, kèm lý do).
4. **Bảng ứng viên cho từng tập × ngôn ngữ** (thay `Show-CandidateTable`): cột Nguồn, FPS (KHỚP / không khai báo / LỆCH), Giống tên, **Lệch** (đo thật / ước tính từ preview "~" / "?"), Bản release, Thông tin (số file, lượt tải, "gói season", "dịch máy"). Sắp xếp theo mục 6.11. Bấm đúp để chọn, nút "Bỏ qua ngôn ngữ này". Tự lấy khi chỉ có 1 ứng viên. Có ô "Dùng lại lựa chọn này cho các tập sau cùng season".
5. Chạy tải: tiến độ, log, bảng tổng kết (tải được bao nhiêu, dùng file có sẵn bao nhiêu, thiếu bao nhiêu, quota OpenSubtitles còn lại).

### 5.2. Thẻ "② Chuẩn bị phụ đề" (thay SubPrep)
1. Hiện số phim, số phụ đề trong `srt_can_sync`, số phụ đề gốc trong `srt_goc`.
2. Chọn **mốc đồng bộ**: *Phụ đề gốc trong `srt_goc`* hoặc *Audio của file phim*. Chọn audio thì hiện audio track của file mẫu để chọn (tự động / theo số thứ tự / theo ngôn ngữ).
3. Chọn **ngôn ngữ**: mặc định *Tự nhận diện theo thư mục con `vi\ cs\ en\` hoặc hậu tố tên file*, có lựa chọn ép cố định. File nằm trong thư mục ngôn ngữ khác lựa chọn ép thì cảnh báo đỏ.
4. Nút "Trích phụ đề gốc từ MKV" (bản không SDH, theo thứ tự ưu tiên ngôn ngữ; chỉ có PGS thì đọc mốc thời gian, mục 6.3).
5. **Bảng xem trước đổi tên**: tên cũ → tên mới, tick để bỏ từng dòng, cột lý do khi không ghép được ("nhập nhằng", "không tìm được mốc", "trùng đích"…).
6. **Bảng cặp đồng bộ** để duyệt, rồi chạy. Sau khi chạy mỗi dòng có: mức sức khoẻ (TỐT xanh / CẦN XEM vàng / CÓ VẤN ĐỀ đỏ), alass dịch đầu phim, dịch cuối phim, chênh đầu–cuối, còn lệch so với phụ đề gốc, số mốc khớp, số dòng recap đã cắt, cờ cảnh báo. Lọc "chỉ hiện file cần xem". Xuất CSV.
7. Hộp thoại dọn dẹp (xoá `srt_can_sync` / `srt_goc` / cả hai / không xoá) và màn hình hoàn tác.

### 5.3. Thẻ "③ Mux" (thay MuxTool)
1. Quét thư mục (cả thư mục con nếu bật), bỏ qua `Output\` và thư mục audio nguồn.
2. **Chọn database đặt tên: TMDB / TVDB / Không đổi tên — BẮT BUỘC chọn mỗi lần, KHÔNG có mặc định**, cho cả phim lẻ lẫn phim bộ. Nút "Bắt đầu" bị khoá cho tới khi chọn. Cạnh mỗi lựa chọn hiện tình trạng key, kiểm tra ngay khi mở thẻ (*sẵn sàng* / *chưa điền key* / *key bị từ chối* / *đăng nhập lỗi*). Không tự chuyển sang database còn lại trừ khi bật `NameFallbackOther`.
3. **Bảng tổng quan**: tên gốc → tên mới (sửa được), số audio/phụ đề giữ, audio ghép thêm, thẻ codec, trạng thái.
4. **Audio**: file có ≥2 audio thì hiện bảng track (ID, ngôn ngữ, codec, kênh, bitrate, tên track) để tick track muốn **xoá**, có "Áp dụng cho mọi file còn lại" (quy tắc ở mục 6.7).
5. **Bảng chọn phụ đề theo tập** (giống `Show-SubPickerGrid`): mỗi dòng một tập, mỗi cột một loại phụ đề (sub ngoài trước, sub trong MKV sau). Tick sẵn theo quy tắc tự động; ô xám = tập đó không có; ô vàng = khác quy tắc tự động. Bấm tiêu đề cột → tick/bỏ cả cột; bấm tên tập → tick/bỏ cả dòng. Nút "Trả về mặc định". Rê chuột lên ô hiện chi tiết track và lý do.
6. **Audio ghép từ file khác** (mục 6.9): file nguồn khớp, độ lệch đo được, cách đo, có khác FPS không. Sửa độ lệch bằng tay ngay trong bảng.
7. Chạy: tiến độ tổng + tiến độ file hiện tại, log trực tiếp, bảng tổng kết.

### 5.4. Thẻ "Cài đặt"
Mọi biến cấu hình của ba .bat, chia nhóm, chú thích tiếng Việt (lấy từ comment trong .bat). Ô bí mật che ký tự, có nút "Kiểm tra kết nối" cho từng API. Nút "Khôi phục mặc định" và "Nhập từ .bat".

## 6. Hành vi BẮT BUỘC giữ — những lỗi đã phải sửa nhiều lần

Đây là phần quan trọng nhất. Mỗi mục là một lỗi thật đã gặp. Viết test cho từng mục test được.

### 6.1. Đường dẫn và tiến trình con
- Tên phim hay chứa `[ ]` (`[YTS.MX]`, `[Judas]`). Dùng `pathlib`/`os` với đường dẫn nguyên văn; **không bao giờ** đưa đường dẫn của tôi vào `glob` hay coi nó là mẫu.
- `subprocess` gọi bằng **danh sách tham số**, không `shell=True`, thêm `creationflags=CREATE_NO_WINDOW`.
- Đọc output `mkvmerge -J` / `ffprobe` dạng UTF-8 (`errors='replace'`): Windows của tôi đặt tiếng Séc/Việt nên thông báo lỗi có dấu.
- Số thập phân truyền cho `ffmpeg`/`mkvmerge`, hoặc đọc từ API, luôn dùng dấu **chấm**, không phụ thuộc locale (bản .bat từng hỏng phép so FPS vì Windows dùng dấu phẩy).

### 6.2. mkvmerge
- Từ **v74** đổi tên cờ: `--default-track-flag` / `--forced-display-flag` (cũ là `--default-track` / `--forced-track`). Đọc phiên bản bằng `--version`.
- Mã ngôn ngữ 2 chữ cần **v50+**; bản cũ dùng ISO 639-2 (`vie`, `cze`, `eng`…) — xem `Get-LangForMux`.
- Mã thoát **1 = có cảnh báo nhưng thành công**; ≥2 mới là lỗi, khi đó xoá file đầu ra dở dang.
- Phụ đề ngoài không phải UTF-8: `--sub-charset 0:<bảng mã>` (`windows-1258` tiếng Việt, `windows-1250` tiếng Séc…).
- Đặt cờ Default/Forced **tường minh cho mọi track được giữ**, `--title` theo tên file, `--track-order` như `UseTrackOrder`.

### 6.3. Đọc mốc thời gian
- `Get-CueTimes`: SRT và `Dialogue:` của ASS/SSA.
- **PGS (.sup) không cần OCR**: đọc segment `PG` + PTS (90 kHz), PCS loại `0x16`, bỏ PCS chỉ đổi bảng màu, đối tượng > 0 = bắt đầu hiện, = 0 = xoá; dòng chưa đóng gặp dòng mới thì tự đóng — xem `Convert-PgsToTimingSrt`.
- Chuẩn hoá phụ đề về **UTF-8 không BOM**: nhận BOM UTF-8/UTF-16, thử UTF-8 nghiêm ngặt, hỏng thì bảng mã cũ theo ngôn ngữ (`cp1258` vi, `cp1250` cs…).

### 6.4. So khớp thời gian (lõi của mọi phép đo, dùng chung ba bước)
- Port **chính xác** `Get-EvenSample`, `Get-OffsetVote`, `Get-TimingRelation` và bảng tỉ lệ FPS (1.0, 24/23.976, 23.976/24, 25/23.976, 23.976/25, 25/24, 24/25). Lấy mẫu đều 400 mốc gốc / 100 mốc cần đo; với mỗi tỉ lệ, bỏ phiếu độ lệch theo ô 0,15 s trong ±400 s; chọn tỉ lệ nhiều phiếu nhất.
- **Ngưỡng tin cậy = 20 mốc khớp.** Ngưỡng 8 từng làm ghép nhầm tập: khớp sai cho 5–15 phiếu, khớp đúng 25–35.
- Tra "mốc này có trong phim không" bằng **bảng băm theo ô 1 giây**, không duyệt tuyến tính (bản cũ chậm tới mức trông như treo).

### 6.5. Đồng bộ bằng alass (bước ②)
- Mỗi file: chuẩn hoá UTF-8 → **cắt khối recap đầu file** (`Remove-LeadingExtra`: đo quan hệ trên **70% cuối**; cắt tới dòng đầu tiên có 5 dòng liên tiếp khớp mốc gốc trong ±1,5 s; huỷ nếu cắt quá 40%) → alass → **sửa khối dồn về 00:00:00** (`Repair-CollapsedCues`, > 2 dòng) → kiểm tra sức khoẻ.
- Tham số alass: mốc là **phụ đề** thì không thêm cờ; mốc là **audio** thì `--no-split --disable-fps-guessing`; phát hiện lệch FPS thì bỏ `--disable-fps-guessing` cho file đó. Kiểm tra cờ nào được hỗ trợ qua `--help`; lỗi thì thử lại không cờ.
- **Mốc là audio: luôn trích audio ra WAV trước** (`ffmpeg -map 0:a:N -vn -ac 1 -ar 16000 -c:a pcm_s16le`), kể cả chế độ tự động (lấy track Default, không có thì track đầu). Đưa thẳng MKV cho alass sẽ lỗi `missing field codec_long_name` khi MKV có font đính kèm (anime).
- `MaxShiftSeconds` (130 s) chỉ **từ chối** khi mốc là audio; mốc là phụ đề thì chỉ cảnh báo.
- **Sức khoẻ 7 dấu hiệu** (`Test-SubHealth`): dồn về 0, chồng lấn, mất > 5% dòng, vượt thời lượng phim > 60 s, khớp yếu với phụ đề gốc / còn lệch > 1 s / còn lệch FPS, co giãn đầu–cuối > 5 s, ký tự hỏng `U+FFFD`, < 50 dòng.
- **Ghép phụ đề với phim**: theo số tập; nhập nhằng thì **bỏ qua và báo**, không đoán. Khi quét thư mục gốc làm mốc, **loại trừ** `srt_pool`, `srt_can_sync`, `srt_hoan_thanh`, `Output`, các thư mục log (từng quét nhầm `srt_pool` khiến đổi tên sai hàng loạt). Hiện mốc lấy từ thư mục nào.

### 6.6. Nhận diện tên tập (bản gộp, dùng chung ba bước)
- Hỗ trợ `S01E05`, `S21E1085` (tới 4 chữ số), `1x05`, và **số tuyệt đối không có season** của anime: `One Piece - 0643`, `[Judas] One Piece - 1085 [1080p]`, `Naruto EP 055`.
- `1917 - 2019`, `Blade Runner 2049`, `Movie - 1080p` là **phim lẻ** (4 chữ số 1900–2099 là năm).
- **Chặn nhầm tập** (`Test-EpisodeConflict`): hai tên đều ghi số tập mà khác nhau → không bao giờ ghép.
- **Lọc theo số tập TRƯỚC, rồi mới cắt bớt danh sách ứng viên**: các tập cùng series có tên gần như y hệt nên cắt trước sẽ loại oan tập đúng.
- Không bao giờ xuất `Season 00` (Jellyfin coi là tập đặc biệt).

### 6.7. Quy tắc audio (bước ③)
- Xoá commentary và audio description (cờ Matroska + tên track). Lọc hết thì giữ nguyên tất cả.
- `und`: 1 audio thì gán `en`; ≥2 mà có `und` thì bỏ qua file (`SkipOnAmbiguousUnd`).
- ≥2 audio tiếng Anh mà tôi không tự chọn thì bỏ qua file (`SkipOnMultipleEnAudio`).
- Default: tiếng Anh (nhiều bản thì bitrate cao nhất), không có thì track đầu.
- Bitrate: tag `BPS` → `ffprobe` → ước lượng theo codec + số kênh.
- **Ghi nhớ lựa chọn = nhớ những gì GIỮ**: giữ ngôn ngữ nào; ngôn ngữ vừa giữ vừa xoá thì nhớ thêm hệ kênh được giữ ("English 5.1"). Nhờ vậy ngôn ngữ lạ ở file sau tự bị loại. Quy tắc làm mất hết audio một file thì bỏ qua quy tắc cho file đó.
- Tên track: `Ngôn ngữ Codec Kênh` (vd `English E-AC-3 5.1`).

### 6.8. Quy tắc phụ đề (bước ③)
- Giữ ngôn ngữ trong `SubKeepLangs`. Có sub ngoài ngôn ngữ X thì xoá sub trong MKV ngôn ngữ X. Xoá Forced, phụ đề dạng ảnh, commentary, English SDH nếu đã có English thường.
- Sub ngoài: `<tên phim>.<mã>.srt|ass|ssa|sup|vtt`, chọn theo `SubExtPriority`; không hậu tố thì là `DefaultExternalLang`.
- Default theo `SubDefaultPriority` (vi > en > cs).

### 6.9. Ghép audio từ file phim khác (bước ③)
- File nguồn trong `audio_nguon\`, ghép tập với tập (cả `S16E643` lẫn `- 0643`), phim lẻ theo tên + năm.
- **Đo độ lệch bằng phụ đề chung** của hai bản (ưu tiên tiếng Anh; dùng cả `.en.srt` ngoài cạnh phim; đọc được PGS), ngưỡng 20 mốc.
- Cùng FPS: ghép thẳng track gốc bằng `--sync TID:ms`, **không encode lại**.
- Khác FPS (PAL 25 → 23.976): `ffmpeg` `asetrate=<số nguyên>,aresample=<sr>,atempo=<phần lẻ bù lại>`, encode E-AC-3 640k (5.1) / 256k (stereo), tối đa 6 kênh.
- Không đo được thì chỉ ghép khi thời lượng hai bản chênh ≤ 1 s; còn lại **bỏ qua và báo**. Cho nhập độ lệch tay theo tập.

### 6.10. Đặt tên theo TMDB / TVDB (bước ③, chuẩn Jellyfin)
- Phim lẻ: thư mục + file `Tên phim (Năm) [tmdbid-123]` hoặc `[tvdbid-123]`.
- Phim bộ: `Tên series (Năm) [tmdbid-123]\Season 01\Tên series (Năm) - S01E05 - Tên tập.mkv`.
- Bỏ ngoặc trong tên phim và ký tự Windows cấm.
- **Thẻ codec chỉ gắn vào TÊN FILE** sau ` - ` (vd `… - 2160p DV HDR HEVC TrueHD7.1 Atmos`), **không bao giờ vào tên thư mục** (Jellyfin dùng phần sau ` - ` làm nhãn phiên bản). Mặc định tắt cho phim bộ. DV/HDR đọc từ `ffprobe` (`color_transfer`, `side_data_list`), thiếu thì đọc từ tên file.
- **TMDB:** key 32 ký tự qua `?api_key=`; token dài bắt đầu `eyJ` qua header `Authorization: Bearer`. **401** → tắt TMDB cả lượt, báo một lần. **404** khi dò tập là bình thường, không in lỗi.
- **Series trùng tên** (One Piece anime 1999 và bản live-action 2023): chọn series **thực sự có** season/tập đó.
- **Số tập tuyệt đối → season/tập**: TMDB cộng dồn `episode_count` các season; TVDB dùng `absoluteNumber`.
- **TVDB v4:** `POST /login` với `apikey` (+ `pin`) → bearer token; 401 thì đăng nhập lại một lần. Đọc phản hồi UTF-8. `GET /search?query=…&type=series|movie&year=…`; `GET /series/{id}/episodes/{seasonType}?page=N` (phân trang); tên tập qua `/episodes/{id}/translations/{lang}` (ngôn ngữ cấu hình, rồi `eng`).

### 6.11. Tải phụ đề (bước ①)

**Nguồn, theo thứ tự:**
1. **Phụ đề có sẵn trong `srt_can_sync`** (`Use-ExistingSub`): nhận nếu đo được ≥ 20 mốc khớp với phụ đề gốc; chỉ khi **không đo được** mới dựa vào tên (≥ 0,35). Tên ghi số tập khác thì loại ngay. **Chép**, không di chuyển (bản cũ di chuyển và gây ghi đè dây chuyền qua cả season).
2. **Kho nội bộ `srt_pool`**: ngôn ngữ theo thư mục con cấp 1 hoặc hậu tố tên file; đo thật với mốc gốc. Dưới 8 mốc → bỏ (in tên ra); 8–19 → "nghi ngờ, cần kiểm tra"; ≥ 20 → khớp. Có bản khớp (đúng FPS, đã đo) và `PoolFirst` bật thì **không hỏi mạng nữa**.
3. **SubSource**, rồi 4. **OpenSubtitles** (tốn quota nên để sau cùng).

**Phụ đề gốc làm mốc** (`Get-RefTimes`, `Find-RefInFolder`): dùng lại `srt_goc\<tên phim>.srt` nếu có; không trùng tên thì tìm trong `srt_goc` theo số tập rồi theo tên (nhận cả `.sup`); vẫn không có thì trích từ MKV (văn bản trước, PGS sau, bỏ Forced) và **giữ lại** trong `srt_goc` cho bước ②.

**SubSource API** (đã dò thực tế, kiểm lại bằng mục 2 nếu hỏng):
- Gốc `https://api.subsource.net/api/v1`, header `X-API-Key`. Giới hạn 60 lượt/phút → nghỉ ~1,1 s giữa các lượt.
- Tìm phim: `GET /movies/search?searchType=imdb&imdb=tt…` (ưu tiên; IMDb ID lấy qua TMDB `/search/{movie|tv}` → `/{kind}/{id}/external_ids`), dự phòng `searchType=text&q=…`. Phim bộ: **mỗi season là một `movieId` riêng**, có trường `season`.
- Danh sách: `GET /subtitles?movieId=…&page=N&limit=100` (lùi về 20 nếu bị từ chối), tối đa ~10 trang. **Không lọc phía máy chủ** bằng `language` / `hearingImpaired` / `foreignParts` / `sort`: bản do người dùng tự đăng để trống các trường đó nên bị loại oan (từng mất hết phụ đề tiếng Việt). Lấy hết rồi tự lọc.
- `language` là tên tiếng Anh viết thường (`english`, `vietnamese`, `czech`…); so khớp linh hoạt (`viet*`, `vi-vn`).
- Trường dùng: `subtitleId`, `releaseInfo[]`, `framerate`, `hearingImpaired`, `foreignParts`, `files`, `downloads`, `preview`.
- Tải: `GET /subtitles/{id}/download` → **ZIP**. Gói season chứa nhiều tập: tải một lần, cache, rồi chọn đúng file của tập (`Select-FromPack`), bỏ file có `forced`, `sdh`, `hi`, `cc` trong tên.
- **Ước tính độ lệch trước khi tải** (`Get-QuickOffset`): trường `preview` chứa vài dòng có mốc thời gian thật. Ô 0,25 s, phạm vi ±180 s, chỉ kết luận khi **tất cả** mốc trong preview cùng đồng ý (đã thử 60 lần: bản đúng 60/60, bản sai lọt 0/60). Preview của gói season chỉ thuộc một tập; sau khi gói đã tải thì **đo thẳng** file của tập đang xử lý trong gói.

**OpenSubtitles API:**
- Gốc `https://api.opensubtitles.com/api/v1`, header `Api-Key`. **User-Agent phải nằm trong danh sách cho phép**: `MyApp v1.0.0` chạy được; tên riêng chỉ chạy khi đã đăng ký API Consumer trên trang web (nếu không: 403 `kong-user-agent-block`). Cho chỉnh trong Cài đặt.
- `POST /login` (username/password) → `token` + `base_url`. Tài khoản VIP được chuyển sang `vip-api.opensubtitles.com` và **mọi request sau đó phải gửi kèm token**. 401 → đăng nhập lại, tối đa 2 lần.
- Tìm: `GET /subtitles?imdb_id=<không có tt>&season_number=&episode_number=&languages=<mã 2 chữ>`, có `order_by=download_count`; không ra thì bỏ sắp xếp; vẫn không ra thì tìm theo `query=<tên>&year=`.
- Lọc `hearing_impaired`, `foreign_parts_only`; bản `machine_translated` / `ai_translated` thì trừ điểm, hoặc loại hẳn nếu `OsExcludeMachine`.
- Tải: `POST /download {file_id}` → `link` + `remaining` + `reset_time`; rồi GET link. Hiện quota còn lại; **cảnh báo dưới 50, ngừng dưới 5** lượt. `GET /infos/user` khi khởi động để biết hạn mức.
- File đã tốn quota mà kiểm chứng yếu thì **vẫn giữ** kèm cảnh báo (`KeepPaidDownloads`), không vứt.

**Xếp hạng và chọn:**
- Thứ tự: **hạng FPS** (1 = khai báo khớp video, 2 = không khai báo, 3 = lệch → loại nếu `StrictFpsMatch`) → **đã đo được độ lệch** → **|độ lệch| nhỏ** → **giống tên** (token của tên bản release so với tên file phim). Không cộng điểm lượt tải hay codec vào xếp hạng; chỉ hiện để tham khảo.
- Ghi nhớ lựa chọn theo **season + ngôn ngữ**, nhưng **không** dùng lại nếu bản đã chọn là bản riêng của một tập khác. Bản chọn không lấy được tập này thì **tự thử ứng viên kế tiếp**, và xoá khỏi bộ nhớ.
- Tự lấy khi chỉ có 1 ứng viên (`AutoPickWhenSingle`). Bỏ SDH/Forced (`ExcludeSdh`, `ExcludeForced`).

**Kết quả:** `srt_can_sync\<mã>\<tên file phim>.<mã>.srt` (thư mục con theo ngôn ngữ nếu `UseLangSubFolders`). **Bước ① KHÔNG sửa thời gian phụ đề** — chỉ đo và báo. Bản cũ có `AutoFixFps` / `AutoFixOffset` từng kéo giãn phụ đề sai, làm file MKV báo dài gấp đôi; việc nắn thời gian là của bước ②.

## 7. KHÔNG làm

- Không viết lại alass, mkvmerge, ffmpeg.
- Không port phần console: màu ANSI, QuickEdit, kích thước cửa sổ, `Out-GridView`, WinForms — GUI thay hết.
- Không lưu bí mật trong mã nguồn, `config.json` hay log. Không gửi dữ liệu đi đâu ngoài SubSource, OpenSubtitles, TMDB, TVDB.
- Không xoá hay ghi đè file gốc của tôi: phụ đề tải về vào `srt_can_sync\`, phụ đề đã sync ghi cạnh file phim, kết quả mux vào `Output\`; mọi thao tác ghi vào nhật ký hoàn tác.

## 8. Kiểm thử

- `pytest` cho toàn bộ `core/`, dữ liệu **tự sinh**, không cần file phim thật:
  - Phụ đề giả lập có lệch + co giãn FPS đã biết → `Get-TimingRelation` tìm đúng tỉ lệ và độ lệch. Phụ đề của "phim khác" → dưới ngưỡng, bị loại.
  - Phụ đề có khối recap ở đầu → cắt đúng số dòng.
  - Danh sách tên file ở mục 6.6 → nhận diện đúng; cặp `S01E10` / `S01E01` → bị chặn.
  - SubSource / OpenSubtitles / TMDB / TVDB **giả lập** (mock `requests`): bản tiếng Việt có trường để trống vẫn được nhận; gói season ZIP chọn đúng tập; ước tính từ preview (bản đúng nhận, bản ngẫu nhiên bị từ chối); quota cảnh báo/ngừng đúng ngưỡng; 401 → đăng nhập lại; ca One Piece hai series trùng tên và số tập tuyệt đối 643.
- **Thử đầu-cuối bằng media tự tạo với `ffmpeg`:** phim 23.976 fps có phụ đề tiếng Anh, và "bản nguồn" 25 fps có audio tiếng Séc là **tiếng bíp đặt đúng tại từng câu thoại**, lệch +1,5 s. Chạy ghép audio, đo vị trí tiếng bíp trong file kết quả bằng `silencedetect`: sai số < 20 ms ở cả đầu lẫn cuối phim. Làm thêm ca cùng FPS, lệch âm.
- Script dò API (`tools/probe_*.py`) chạy được với key thật của tôi khi tôi tự chạy; test tự động không gọi mạng thật.
- Trước khi báo xong mỗi giai đoạn: chạy hết test và dán kết quả cho tôi.

## 9. Các giai đoạn (dừng chờ tôi duyệt sau mỗi giai đoạn)

0. **Đọc & đặc tả:** đọc ba .bat, viết `docs/SPEC.md` (mục 1). Gửi tôi duyệt.
1. **Lõi dùng chung:** `timing.py`, `pgs.py`, `srt.py`, `encoding.py`, `episodes.py`, `naming.py`, `config.py`, `secrets.py` + test.
2. **Bước ① lõi:** `subsource.py`, `opensubs.py`, `pool.py`, `subfetch.py` + script dò API + test (mock). Tôi chạy thử script dò với key thật.
3. **Bước ② lõi:** ghép tên, trích phụ đề gốc, sync alass, cắt recap, sức khoẻ, nhật ký/hoàn tác + test đầu-cuối nhỏ.
4. **Bước ③ lõi:** probe, quy tắc audio/phụ đề, TMDB/TVDB, thẻ codec, ghép audio, dựng lệnh mkvmerge + test (gồm test tiếng bíp).
5. **Giao diện** bốn thẻ + khung log + luồng phụ + nút Dừng.
6. **Đóng gói:** PyInstaller onedir, `build.bat`, icon, `HUONG_DAN.md` tiếng Việt (cài, cấu hình lần đầu, nhập từ .bat, quy trình ① → ② → ③).

## 10. Tiêu chí hoàn thành

- [ ] Mở `MuxStudio.exe` bằng bấm đúp, không cần cài Python.
- [ ] Mọi biến cấu hình của ba .bat có ô trong Cài đặt; nhập được từ .bat; bí mật nằm trong Windows Credential Manager.
- [ ] Bước ①: bảng ứng viên có cột Lệch; kho nội bộ chạy trước mạng; gói season đo được từng tập; quota OpenSubtitles hiện và cảnh báo đúng.
- [ ] Bước ②: cắt được recap, sửa được khối dồn về 0, báo cáo sức khoẻ đủ số giây.
- [ ] Bước ③: luôn bắt chọn TMDB/TVDB/Không đổi tên, không mặc định; bảng chọn phụ đề theo tập tick được cả cột/dòng; ghép audio PAL vào phim 23.976 sai số < 20 ms.
- [ ] Mọi hàm hỏng khi port đã được viết lại và ghi trong "Thay đổi so với .bat"; không tính năng nào bị bỏ âm thầm.
- [ ] Tên phim có `[ ]` chạy bình thường. Giao diện không đơ; nút Dừng hoạt động.
- [ ] Toàn bộ test qua.
