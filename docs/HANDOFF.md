# Bàn giao giữa các phiên làm việc (đọc file này TRƯỚC)

Cập nhật: 2026-11-10, **Giai đoạn 0–6 ĐÃ DUYỆT (1.0.0); bản 1.1.0 (mục 12) ĐÃ DUYỆT; bản 1.2.0 (mục 13) — CHỜ duyệt.**
Repo `michalvu/MuxStudio`. Phiên sau (nếu có): bảo trì — sửa lỗi người dùng báo, yêu cầu mới (xem "Việc kế tiếp").
Nhánh: chỉ còn **`claude/new-session-eckanp`** = nhánh chính (nhánh mặc định của repo). Các nhánh phiên cũ
(`ccr-770f1e3e-dqsvcp`, `ccr-e18d248b-a69t65`, `claude/gifted-keller-fx1jey`) đã xoá ngày 2026-11-10 theo yêu cầu người
dùng — mọi commit của chúng đều đã nằm trong nhánh chính.
Phiên sau: làm trên nhánh phiên được giao, đẩy cả nhánh chính `claude/new-session-eckanp`; xong việc (người dùng
duyệt) thì xoá nhánh phiên trên GitHub, chỉ giữ nhánh chính.

## 1. Đọc gì, theo thứ tự

1. `docs/PROMPT_MuxStudio.md` — yêu cầu gốc của người dùng (bắt buộc tuân theo, nhất là §4 kiến trúc, §6 hành vi, §9 giai đoạn).
2. `docs/SPEC.md` — đặc tả ĐÃ DUYỆT: bảng 195 hàm → Python (mục 4), cấu hình (mục 5), `Read-Host` → GUI (mục 6),
   **Thay đổi so với .bat TĐ-01…TĐ-34** (mục 7, cột cuối ghi giai đoạn làm + ✅ nếu xong), quyết định Q1–Q8 (mục 10).
3. File này.

Người dùng **không phải lập trình viên**: trả lời + giao diện **tiếng Việt có dấu**, hướng dẫn từng bước ngắn.
**Dừng ở cuối mỗi giai đoạn** chờ duyệt; trước khi báo xong: chạy hết test và dán kết quả.

## 2. Trạng thái

| GĐ | Nội dung | Trạng thái |
|---|---|---|
| 0 | SPEC + script kiểm chứng lỗi .bat | ✅ đã duyệt |
| 1 | `timing pgs srt encoding episodes naming config secrets undo` + test | ✅ đã duyệt |
| 2 | `tools probe tmdb subsource opensubs pool subfetch` + script dò API + test | ✅ đã duyệt (2026-11-09). Lần dò 1 không có key (SPEC 7.E) → **vẫn chờ** `ket_qua_do_api.txt` có key |
| 3 | Bước ② `subprep` + test | ✅ đã duyệt (2026-11-09, gồm TĐ-31, TĐ-32) |
| 4 | Bước ③ `mux donor tvdb` + phần bước ③ của `probe tmdb naming` + test | ✅ đã duyệt (2026-11-09, gồm TĐ-33, TĐ-34) |
| 5 | GUI `gui/` (4 thẻ + log + luồng phụ + Dừng + Hoàn tác) + test giao diện + tối ưu 720p/1080p/1440p + khối "Công cụ ngoài" | ✅ đã duyệt (2026-11-09) |
| 6 | Đóng gói: `MuxStudio.pyw`, `MuxStudio.spec`, `build.bat`, icon, 1.0.0, `--kiem-tra`, `HUONG_DAN.md` + test; sau duyệt lần 1: 15 test Windows, dò API thật, thanh các bước, màu lưới phụ đề, kéo Nhật ký, dọn file nguồn sau mux | ✅ đã duyệt (2026-11-09, sau 2 lần duyệt; người dùng chạy `run_tests.bat` + `build.bat` trên Windows) (mục 10, 11) |

**Việc kế tiếp:** dự án đã hoàn thành (PROMPT §10). Khi người dùng quay lại:
- **Báo lỗi** → xin: số phiên bản (thanh tiêu đề / dòng 2 khung log), file "Lưu log…", ảnh, báo cáo tự kiểm tra
  (`kiem_tra_MuxStudio.bat` cạnh .exe → `%APPDATA%\MuxStudio\kiem_tra.txt`), nếu không mở được:
  `%APPDATA%\MuxStudio\loi_khoi_dong.txt`. Chứng minh lỗi bằng test trước khi sửa (PROMPT §2); lỗi chỉ lộ trên Windows
  → thử tái hiện bằng `tools/winemu.py` (mục 6). Sửa ở mã nguồn + test, chạy 3 môi trường + winemu, đẩy cả hai nhánh.
- **Phát hành bản mới**: tăng `core.__version__` + `pyproject.toml` (1.0.1 = sửa lỗi, 1.1.0 = tính năng; `test_packaging`
  kiểm hai chỗ khớp nhau). Người dùng: tải mã mới → `build.bat` → chép đè thư mục `dist\MuxStudio`.
- Công cụ ngoài trên máy người dùng: `Desktop\mkvtoolnix`, `Desktop\alass-windows64\bin\alass` (ffmpeg 4.2 năm 2019 —
  bản CŨ, xem mục 5 "bài học" (3)). Người dùng từng chép alass/ffmpeg vào thư mục mã nguồn — đã khuyên xoá (không cần).
- `run_tests.bat` lần 2 trên Windows: 649 passed, 1 skipped (test `tool_info` chỉ tìm trong PATH) → đã sửa (commit
  `1cbd170`), lần sau phải không còn test nào bị bỏ qua.

Kết quả dò API thật (lần 2, có key) đã áp dụng — SPEC 7.E. Còn có thể dò thêm nếu cần: OpenSubtitles tìm tập tuyệt đối
theo **IMDb** (không season) — lần dò chỉ thử theo tên (0 kết quả); mã hiện vẫn thử theo IMDb 2 lượt.


## 3. Quyết định đã chốt (đừng hỏi lại)

- Q1–Q8 đồng ý hết đề xuất (SPEC mục 10). **Q7: phụ đề ASS giữ là ASS ở MỌI bước** (không đổi đuôi, không chuyển SRT).
- TĐ-05 mở rộng (từ kỹ thuật "mạnh"/"yếu" khi tách tên phim: `Uncut Gems`, `Charlotte's Web`) — người dùng đã đồng ý sang GĐ2.
- TĐ-28, TĐ-29, TĐ-30 duyệt cùng GĐ2 (2026-11-09).
- TĐ-31, TĐ-32 duyệt cùng GĐ3.
- TĐ-33 (tên codec đọc `codec` cấp track; `HDR10`) và TĐ-34 (bỏ `atempo` — làm audio ghép sớm ~21 ms, đổi tốc độ bằng
  phân số chính xác; KHÁC chữ chuỗi lọc trong §6.9 của PROMPT) — **đã duyệt** cùng GĐ4.

## 4. Code đã có

```
core/   timing pgs srt encoding episodes naming config secrets undo   (GĐ1)
        tools probe tmdb subsource opensubs subfetch                    (GĐ2; pool ĐÃ XOÁ ở 1.2.0)
        i18n i18n_en                                                     (1.2.0 — hai ngôn ngữ, mục 13)
        subprep                                                          (GĐ3)
        mux donor tvdb (+ phần bước ③ của probe tmdb naming)              (GĐ4)
        + set_job (3 phiên), naming.search_candidates, MuxSession.search_name (GĐ5)
gui/    app widgets log_panel settings_view fetch_view subprep_view mux_view (GĐ5)
        + build_info / self_check (--kiem-tra) / write_startup_error trong app; resource_path / set_window_icon (GĐ6)
tests/  pytest — 683 test (2 test alass thật tự bỏ qua nếu không có alass; 15 test test_gui_app.py + 1 test_packaging
        tự bỏ qua nếu không có tkinter/màn hình); test_packaging.py (GĐ6); test_gui_logic.py (logic giao diện, không cần Tk); fakehttp.py (API giả),
        synth.py (phụ đề/PGS giả), fake_alass.py (alass GIẢ: ghi lại từng lần gọi, dịch phụ đề theo cấu hình),
        test_golden_powershell.py + test_golden_subprep.py + test_golden_mux.py + golden/ps_harness.ps1 (chạy HÀM GỐC
        trong reference/*.bat bằng pwsh và so; Invoke-TmdbApi/Invoke-TvdbApi thay bằng API giả cùng dữ liệu)
tools/  verify_bat/KiemChung_LoiBat.ps1, sanitize_bat.py, probe_*.py, probe_all.py, nhap_tu_bat.py, make_icon.py (GĐ6),
        winemu.py (plugin pytest giả lập Windows)
reference/  3 file .bat gốc ĐÃ XOÁ 7 bí mật (dùng cho test đối chiếu)
run_tests.bat, chay_do_api.bat, chay_muxstudio.bat (CRLF, cho Windows; chay_muxstudio = chạy giao diện từ mã nguồn)
GĐ6: MuxStudio.pyw (điểm vào), MuxStudio.spec, build.bat, requirements-build.txt, HUONG_DAN.md,
     assets/ muxstudio.ico (16–256: BMP + PNG 256) muxstudio.png kiem_tra_MuxStudio.bat (spec chép cạnh .exe)
```

- Bước ① theo mẫu **lập kế hoạch → thực thi**: `FetchSession.scan → gather → auto_select/choose/skip → execute`.
- Bước ② cùng mẫu, lớp `subprep.PrepSession`: `counts`, `sample_audio_streams`, `plan_extract_refs → extract_refs`,
  `build_index(mode)`, `plan_rename(mode, lang) → execute_rename`, `plan_sync(mode, AudioChoice, lang) → execute_sync`,
  `preview_commands(pair)` ("Xem lệnh"), `cleanup_targets → execute_cleanup(plan, 'both'|'subs'|'refs'|'none')`.
  `mode` = `'sub'` (mốc = phụ đề gốc) | `'audio'`. Dòng kế hoạch (`ExtractRow`, `RenameRow`, `SyncPair`) có `reason`
  ('' = sẽ làm), `selected` (ô tick), `status`; `SyncPair.result: SyncResult` (sức khoẻ `Health`, cắt, sửa, lệch).
  Một `RunLog` cho cả lượt, lưu lại cùng file sau mỗi bước (`RunLog.save` ghi lại `self.path`).
  Hàm thuần dùng chung: `remove_leading_extra`, `repair_collapsed_cues`, `sub_health`, `RefIndex.build`, `resolve_ref`.
- Bước ③ cùng mẫu, lớp `mux.MuxSession`: `db_status()` (TMDB/TVDB: sẵn sàng / chưa điền key / key bị từ chối / đăng
  nhập lỗi), `scan()`, `plan_all(db)` — `db` BẮT BUỘC `'tmdb'|'tvdb'|'none'` (`ValueError` nếu không), `plan_file(path)`.
  `MuxPlan`: `name` (`naming.OutputName`: `status` ok/need_choice/keep, `candidates`, `rel_dir`), `audio` (`AudioTrack`
  keep/reason/default), `int_subs`/`ext_subs`, `default_sub`, `sub_items` (ô bảng chọn phụ đề), `donor`
  (`donor.DonorPlan`), `codec_tag`, `status` ok / need_choice ("Cần chọn") / skip / exists / fail, `reasons`.
  Sửa kế hoạch rồi `plan_file` lại: `set_audio_choice(plan, drop_ids, apply_to_rest)` (quy tắc "nhớ cái GIỮ"),
  `keep_all_audio(plan|None)` (Q3), `set_sub_choice`, `reset_sub_choices`, `choose_name(plan, id)`, `keep_original_name`,
  `keep_original_names(plans)`, `set_name`, `set_donor_offset`. `build_args(plan)` (SPEC 4.4), `preview_commands(plan)`,
  `execute(plans)` (ghi `<tên>.muxstudio-tmp.mkv` rồi đổi tên; mã 0/1 OK, ≥ 2 xoá; nhật ký `Create`/`Mkdir`/`Skip`/`Fail`).
- Đặt tên: `naming.resolve_output_name(orig, db, {"tmdb":…, "tvdb":…}, cfg, cache)` nhận client theo duck typing
  (`db_name`, `usable`, `search_movie`, `search_series`, `pick_show`, `resolve_episode`) — naming không import tmdb/tvdb.
- Mọi bước: `plan_*` không ghi gì vào thư mục phim; `execute` ghi + nhật ký hoàn tác `undo.RunLog`; kết quả ghi ra tên
  tạm / thư mục tạm, xong mới chép tới đích; đích đã có → file cũ vào Thùng rác trước.
- Log/tiến độ/dừng qua `tools.Job`; tiến trình con qua `tools.run` (danh sách tham số, không shell, `CREATE_NO_WINDOW`, UTF-8).
- `core/` KHÔNG import tkinter.

## 5. Quy ước bắt buộc + bài học

- **Bí mật:** không bao giờ trong repo, `config.json`, log, test. **Không dùng key thật làm dữ liệu test** (đã xảy ra sự cố
  ở GĐ1, đã xử lý bằng viết lại lịch sử trước khi đẩy). Chuỗi giả đặt dạng `demo-…`; **đừng** dùng `sk_test_…`/`sk_live_…`/`AKIA…`
  (GitHub push protection chặn). Trước khi đẩy: quét bí mật (xem mục 6).
- Đường dẫn của người dùng không bao giờ đưa vào `glob`; dùng `tools.walk_files`/`os.scandir`. Test dùng thư mục có `[ ]`.
- Số thập phân cho ffmpeg/mkvmerge qua `timing.fmt_inv`; đọc số qua `timing.to_double`.
- Port chính xác: `[int]` PowerShell = làm tròn về số chẵn → Python `round()`.
- Test đọc file CRLF: dùng `read_bytes()` (đừng `read_text()` — Python tự đổi `\r\n`).
- File `.bat` mới tạo phải là CRLF.
- Test phải qua trên **Python 3.11** (máy người dùng) và 3.13.
- Commit: tiếng Việt, kết thúc bằng 2 dòng attribution (Co-Authored-By + Claude-Session) theo hướng dẫn của phiên.
  Không ghi tên model trong commit/code. Đẩy lên nhánh phiên được giao: `git push -u origin <nhánh>`.
- Dữ liệu test giả lập phải "giống thật": recap thật là thoại tập khác (mốc ngẫu nhiên), đừng rải đều 2 s trùng vùng có mốc gốc;
  phụ đề giả dùng dòng dài 1 s (khoảng cách tối thiểu 1,2 s) để khỏi bị báo "chồng lấn". `cp1258` không có chữ dựng sẵn như
  "ế", "ệ" — chữ mẫu cho test bảng mã cũ dùng "Đây là tiên".
- **Không chạy được file .exe/nhị phân tải từ mạng** trong container (bộ phân loại an toàn chặn, kể cả alass bản Linux) →
  test alass thật tự bỏ qua ở đây; dùng `tests/fake_alass.py`. Trên Windows `run_tests.bat` tìm alass thật như nút "Tự tìm".
  (Bản đóng gói Linux do PyInstaller build NGAY trong container thì chạy được — mục 10.)
- **Test chạy trên Windows của người dùng (bài học lần 1: 15 test hỏng, không lỗi nào ở mã chương trình):**
  (1) `Path.write_text` / `open(..., "w")` trên Windows đổi `\n` → `\r\n`; dữ liệu SRT giả đã có `\r\n` → `\r\r\n`
  (SRT hỏng) ⇒ trong test LUÔN `write_text(..., encoding="utf-8", newline="")`. (2) Python con (alass giả, `python -c`) ghi
  ra ống dẫn bằng bảng mã ANSI (cp1250 ở máy tiếng Séc) → chữ có dấu làm chết tiến trình ⇒ Python con phải
  `sys.stdout.reconfigure(encoding="utf-8")`. (3) ffmpeg cũ (bản đi kèm alass) không cắt "đoạn mồi" của AAC (~21 ms) /
  E-AC-3 (~5 ms) khi giải mã ⇒ test tiếng bíp dùng audio gốc PCM, ngưỡng thực tế 10 ms (yêu cầu 20 ms). (4) Test alass
  THẬT chỉ chạy được trên Windows — dữ liệu phải hợp lý (phim giả 3 s + phụ đề 40 phút ⇒ sức khoẻ báo đúng "vượt thời
  lượng"). (5) Test tìm công cụ phải dùng `tools.find_tool` (trên Windows mkvmerge thường không có trong PATH).
  Kiểm tra trước khi đẩy: `tools/winemu.py` (mục 6) giả lập (1) + (2) trên Linux.
- **Test giao diện phụ thuộc cỡ màn hình:** `xvfb-run -a` ở container này cho màn hình 1280×1024 (< 1400×860) → cửa sổ tự
  phóng to hết màn hình; Xvfb không có trình quản lý cửa sổ nên Tk KHÔNG bỏ phóng to được (`-zoomed` kẹt ở 1). Test nào
  đặt cỡ cửa sổ phải xét `a.window.zoomed` (đã sửa `test_window_fits_screen_and_remembers_logical_size` ở GĐ6 — trước đó
  hỏng ở màn hình nhỏ, kể cả laptop 1366×768 của người dùng). Thử nhiều cỡ: `xvfb-run -a -s "-screen 0 1366x768x24"`.

## 6. Dựng lại môi trường (container cloud bị xoá sau mỗi phiên)

```bash
cd MuxStudio
python3 -m venv .venv && .venv/bin/pip install -q -r requirements-dev.txt   # python3 = 3.13, KHÔNG có tkinter
# PowerShell 7 cho test đối chiếu hàm gốc
curl -sSL -o /tmp/pm.deb https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
  && dpkg -i /tmp/pm.deb && apt-get update -qq && apt-get install -y -qq powershell
apt-get install -y -qq mkvtoolnix python3-tk xvfb imagemagick python3.12-venv ffmpeg   # ffmpeg + Xvfb thường có sẵn
pip install -q uv && uv python install 3.11   # chạy test trên 3.11 (bản uv có tkinter, Tk 9):
uv venv -p 3.11 /tmp/v311 && uv pip install -p /tmp/v311/bin/python keyring send2trash requests pytest customtkinter
# Python 3.12 của Ubuntu + Tk 8.6 (GIỐNG Windows nhất — dùng để chụp ảnh giao diện):
/usr/bin/python3.12 -m venv /tmp/v312 && /tmp/v312/bin/pip install -q customtkinter keyring send2trash requests pytest
.venv/bin/python -m pytest -q              # không có Tk: 665 passed, 18 skipped (16 test cần Tk + 2 alass thật)
xvfb-run -a /tmp/v311/bin/python -m pytest -q -p no:cacheprovider   # 681 passed, 2 skipped
xvfb-run -a /tmp/v312/bin/python -m pytest -q -p no:cacheprovider   # 681 passed, 2 skipped
# Giả lập Windows (ghi file chữ \n→\r\n, Python con in cp1250) — phải qua hết:
PYTHONPATH=tools xvfb-run -a /tmp/v312/bin/python -m pytest -q -p no:cacheprovider -p winemu   # 681 passed, 2 skipped
# Thử đóng gói (GĐ6) — PyInstaller trên Python 3.12 của Ubuntu (Tk 8.6), kết quả để ngoài repo:
/usr/bin/python3.12 -m venv /tmp/vbuild && /tmp/vbuild/bin/pip install -q -r requirements-build.txt
/tmp/vbuild/bin/python -m PyInstaller --noconfirm --clean --distpath /tmp/pyi/dist --workpath /tmp/pyi/build MuxStudio.spec
xvfb-run -a /tmp/pyi/dist/MuxStudio/MuxStudio --kiem-tra /tmp/pyi/kt.txt; echo $?; cat /tmp/pyi/kt.txt   # 0 + "ĐẠT"
```

Quét bí mật trước khi đẩy (cần bản .bat gốc có key — chỉ có nếu người dùng tải lên lại; nếu không có thì ít nhất
`git grep -nE "sk_(test|live)_|AKIA|ghp_"` và kiểm tra không có chuỗi key nào ngoài `demo-…`).

Chụp ảnh giao diện (để tự kiểm tra bố cục): chạy App dưới `xvfb-run -a -s "-screen 0 1400x1000x24"` bằng **/tmp/v312**,
rồi `import -window root ảnh.png`. **Đừng** chụp bằng /tmp/v311: Tk 9 của python-build-standalone không dùng fontconfig
→ mất dấu tiếng Việt và ô tick hiện chữ "Z" (chỉ là lỗi hiển thị ở máy này; logic và test vẫn chạy đúng).

## 7. Giai đoạn 3 đã làm (bước ② — SubPrep)

`core/subprep.py` (xem mục 4) + `srt.cue_spans`, `probe.ffprobe_streams`/`audio_streams`, `tools.alass_supported_flags`.
Đã làm TĐ-12, 13, 14, 15, 18, 19, 20 và hai mục mới:
- **TĐ-31** — `Resolve-Ref` ghép nhầm tập, chứng minh bằng hàm gốc: (a) anime `- 0643` luôn "nhập nhằng theo tên"; (b) thiếu
  phim tập 5 thì phụ đề tập 5 ghép vào tập 6 (cả tầng 4); (c) kiểm tra nhập nhằng ở tầng 3 không bao giờ chạy (lấy bừa).
  Sửa: loại mốc khác số tập trước tầng 2/4; tầng 3 gom theo phim rồi mới chọn bản đại diện.
- **TĐ-32** — không hậu tố ngôn ngữ → lấy theo lựa chọn/thư mục; hai phụ đề cùng đích → bỏ cái sau; đích đã có (khi tắt
  "bỏ qua") → file cũ vào Thùng rác trước; cờ alass không hỗ trợ → bỏ cả giá trị đi kèm.
Test: `tests/test_subprep.py` (53), `tests/test_e2e_subprep.py` (4 + 2 alass thật), `tests/test_golden_subprep.py` (5,
đối chiếu `Remove-LeadingExtra`, `Repair-CollapsedCues`, `Test-SubHealth`, `Resolve-Ref`/`Get-RefIndex`).

## 8. Giai đoạn 4 đã làm (bước ③ — MuxTool)

`core/mux.py`, `core/donor.py`, `core/tvdb.py`; thêm vào `probe` (audio/HDR/codec/bitrate, `ref_sub_tracks`), `tmdb`
(`search_movie/series`, `episode_title`, `resolve_episode`, `pick_show`), `naming` (đặt tên theo database, `lang_of_suffix`).
Đã làm TĐ-17, 21, 23 (Q3), 25 và hai mục mới:
- **TĐ-33** — `Get-CodecName` đọc `properties.codec` nhưng mkvmerge ghi `codec` ở cấp track ⇒ `.bat` luôn ra `EAC3`, `DTS`…
  (đã chứng minh bằng hàm gốc + JSON mkvmerge v82 thật); `HDR10` không được nhận là HDR khi thiếu ffprobe.
- **TĐ-34** — `atempo` làm audio ghép khác FPS sớm ~21 ms (đo bằng tiếng bíp) ⇒ thay bằng `aresample=R,asetrate=q·k,
  aresample=sr` (phân số chính xác) ⇒ < 2 ms. KHÁC chuỗi lọc ghi trong §6.9 của PROMPT → cần người dùng duyệt.
- Đo tiếng bíp phải so TƯƠNG ĐỐI với bíp của audio gốc trong cùng lần giải mã: ffmpeg dời mốc cả file theo độ trễ mồi
  của track AAC (−21 ms), đo tuyệt đối (có/không `-copyts`) cho kết quả sai.
Test: `test_mux.py` (28), `test_naming_db.py` (13), `test_donor.py` (25), `test_e2e_mux.py` (3, gồm 2 ca tiếng bíp),
`test_golden_mux.py` (12).

## 9. Giai đoạn 5 đã làm (giao diện)

Chạy: `python -m gui.app [thư mục phim]` — trên Windows bấm đúp `chay_muxstudio.bat` (tạo `.venv`, cài thư viện, mở bằng
`pythonw`; lỗi khởi động ghi vào `%APPDATA%\MuxStudio\loi_khoi_dong.txt`).

- **Kiến trúc:** mỗi thẻ là một lớp dùng *composition* (`self.frame`), không kế thừa lớp Tk ở cấp module ⇒ mọi module
  `gui/*` import được khi máy không có tkinter (test logic chạy trên .venv 3.13). Tk chỉ dùng trong phương thức.
- `widgets.py`: `Runner` (luồng phụ + `queue` + `after(100)`; **một việc mỗi lúc**, nút mọi thẻ khoá khi chạy;
  `start(..., when_free=True)` = xếp hàng cho việc tự động; `stop_after_current` / `cancel_now` (luồng riêng, bỏ việc đang
  xếp hàng); lỗi trong `on_done` chỉ ghi log), `Table` (Treeview + cột ☑/☐, bấm tiêu đề cột tick = cả bảng, `tip`),
  `GridModel` + `CheckGrid` (Canvas — để tô ô xám/vàng), `Tooltip`, `Dialog`, `show_text`, `confirm_list`, `ask_string`,
  `setup_fonts` (Segoe UI trên Windows — đủ dấu tiếng Việt), `setup_style` (màu Treeview sáng/tối), `ellipsize_middle`.
- `app.py`: thư mục phim (lưu khoá lạ `UiLastFolder`, `UiGeometry` trong config.json — Config giữ khoá lạ), 4 thẻ, thanh
  tiến độ (tổng + "file hiện tại" chạy liên tục), Dừng sau file này / Dừng ngay, khung log (xếp `side="bottom"` để luôn
  hiện), `UndoDialog` (ba bước, mới nhất trước), hỏi lưu khi rời thẻ Cài đặt có thay đổi, `destroy()` huỷ `after` bằng
  `tk.call("after","cancel",id)` (**không** dùng `after_cancel`: nó xoá luôn lệnh Tcl của widget → lỗi khi destroy).
- `settings_view.py`: nhóm dựng lười (lần đầu mở), `FieldRow` theo `field_kind` (bool/int/float/str/dir/file/list/
  dict/choice/langs/parts), `ExtraToolDirs`/`RefExcludeFolders` mỗi dòng một mục, `DonorOffsetMs` dạng `khoá = số`,
  `check_connection(which, vals, cfg)` (dùng chung cho thẻ ① và ③), `ImportDialog` (☑ từng khoá, khoá xung đột chọn một).
- `fetch_view.py`: `gather_all` (gom + tự chọn, KHÔNG hỏi), `after_choice` (chọn gói season → tải trước → đo lại các tập
  cùng season → dùng lại lựa chọn), cây phim → ngôn ngữ, bảng ứng viên.
- `subprep_view.py`: một `PrepSession` cho cả lượt (một nhật ký), tab con "Đổi tên"/"Đồng bộ", bảng audio file mẫu khi
  mốc = audio, hộp thoại trích phụ đề gốc / dọn dẹp, `export_csv`.
- `mux_view.py`: database bắt buộc chọn (radio không chọn sẵn), `_replan(title, mutate, only)` (sửa kế hoạch trong luồng
  phụ rồi `plan_file` lại, giữ ô tick), `NameDialog` (chọn / Tìm lại với tên… / Giữ tên gốc / Sửa tay), file đã mux xong
  tự bỏ tick.
- Lõi thêm (không đổi API cũ): `set_job(job)` cho 3 phiên; `naming.search_candidates`, `MuxSession.search_name`;
  `tools.tool_info` + `tools.TOOL_ROLES` (khối "Công cụ ngoài" trong Cài đặt: 5 ô đường dẫn ở ĐẦU nhóm Chung, dòng
  trạng thái = file đang dùng + phiên bản; kiểm tra lúc mở chương trình, log công cụ thiếu). MKVToolNix BẮT BUỘC:
  bước ③ không chạy được nếu thiếu mkvmerge; bước ① ② chạy được một phần.
- `requirements.txt`: `customtkinter>=5.2.2,<7` (đã thử 5.2.2 và 6.0.0, Tk 8.6 và 9.0).
- **Màn hình 720p / 1080p / 1440p + tên dài** (sau duyệt lần 1, người dùng yêu cầu):
  - `W.plan_window(rộng, cao)` theo đơn vị LOGIC (= pixel ÷ tỉ lệ phóng to Windows): < 1400×860 → phóng to hết màn
    hình + bố cục `compact` (log 4 dòng, lề nhỏ); 1080p → ~86% màn hình; ≥ 1250 cao → `large` (log cao hơn). Cỡ lần
    trước chỉ dùng lại nếu còn vừa. `UiScale` (`auto` = 110% trên 1440p/4K đặt 100%).
  - Độ rộng cột Treeview / lưới Canvas tính bằng đơn vị logic × `W.scaling()` (Windows 125/150% thì cột to theo chữ).
  - `Col.fit`: `name` (tên file — `fit_text` giữ số tập + đuôi, bỏ phần giữa) / `end` (cắt đuôi + "…"); tự rút gọn lại
    khi đổi cỡ cửa sổ hoặc kéo cột; rê chuột ô bị rút gọn → chữ đầy đủ; `Table.full(iid)` = giá trị đầy đủ (test đọc
    `full()`, đừng đọc `tv.item(...,"values")` của cột có rút gọn). `CheckGrid`: cột tên tập rộng tới 45%.
  - Nhãn dài nằm cạnh nút: xếp nút (`side="right"`) TRƯỚC, nhãn sau với `fill="x", expand=True` — nhãn không bao
    giờ đẩy nút ra ngoài. Hộp thoại không to hơn màn hình; nút hộp thoại xếp `side="bottom"` trước thân.
  - Bài học: (1) đổi tỉ lệ CustomTkinter (`set_widget_scaling`) khoá `maxsize` = cỡ cũ 1 giây → đặt cỡ cửa sổ bị bỏ
    qua; chỉ đổi khi khác + `root.maxsize(100000, 100000)`. (2) Lưu cỡ cửa sổ bằng `root.geometry()` (logic), KHÔNG
    `winfo_geometry()` (pixel) — sai thì cửa sổ to dần mỗi lần mở trên máy 125/150%. (3) Đổi `wraplength` theo
    `<Configure>` của chính khung chứa nhãn → vòng lặp vô hạn; nghe khung ngoài + trễ `after(80)`. (4) Cửa sổ bị
    `withdraw()` thì Tk không báo cỡ thật — test cỡ phải `deiconify()`.

## 10. Giai đoạn 6 đã làm (đóng gói)

PROMPT §3, §9 mục 6, §10. Hướng dẫn người dùng: `HUONG_DAN.md` (tạo .exe, công cụ ngoài, cấu hình lần đầu, quy trình,
Hoàn tác, báo lỗi, SmartScreen/antivirus/Unblock, run_tests, dò API).

- **Phiên bản 1.0.0**: `core.__version__` = `pyproject.toml`; `gui.app.APP_TITLE` = "MuxStudio 1.0.0" (thanh tiêu đề);
  hai dòng ĐẦU khung log: "MuxStudio 1.0.0 — chọn thư mục phim…" + `build_info()` (bản .exe / mã nguồn, Python,
  `os_name()` — Python 3.11 báo Windows 11 là "10" → đọc số build ≥ 22000, Tk patchlevel, CustomTkinter). Cũng ghi ở
  đầu `loi_khoi_dong.txt` (`write_startup_error`) và trong Properties › Details của .exe (spec tạo file version info).
- **Điểm vào** `MuxStudio.pyw` → `gui.app.main()`. `main(["--kiem-tra", file])` → `self_check`: 10 mục + dựng thử
  cửa sổ (ẩn, cấu hình tạm qua `MUXSTUDIO_CONFIG_DIR`, trả lại sau): module core/gui, theme + phông hình CustomTkinter,
  keyring (tên backend + các backend thấy qua entry points; ĐỌC thử một mục không có, không ghi), send2trash (trên
  Windows phải là `send2trash.win.*`), certifi + ssl, ghi được `%APPDATA%\MuxStudio`, `tools.run` (`cmd /c ver` với
  CREATE_NO_WINDOW), `os.startfile` + Desktop, icon, công cụ ngoài (theo cấu hình thật, chỉ cảnh báo), cửa sổ 4 thẻ +
  phông Segoe UI + icon. Báo cáo `[OK]`/`[!]`/`[LỖI]` + "KẾT QUẢ: ĐẠT / KHÔNG ĐẠT"; mã thoát 0/1. Không gọi mạng.
- **Keyring dự phòng** `core.secrets.ensure_backend`: Windows + backend `fail` (bản .exe thiếu metadata gói) → đặt thẳng
  `WinVaultKeyring`. Gọi một lần ở `_keyring()` đầu tiên. Test: keyring giả giữ nguyên.
- **Icon**: `tools/make_icon.py` (Python thuần, SDF + làm mượt; không cần Pillow) → `assets/muxstudio.ico` (16, 24, 32, 48,
  64, 128 BMP 32 bit + mặt nạ AND; 256 PNG — như icon Windows; icon của CustomTkinter cũng là PNG 256 nên Tk đọc được)
  + `assets/muxstudio.png`. `W.set_window_icon(win)`: Windows `iconbitmap(.ico)` gọi NGAY khi tạo cửa sổ (CTk chỉ đặt
  icon của nó sau 200 ms nếu `iconbitmap` chưa được gọi — cờ `_iconbitmap_method_called`); Linux `iconphoto(.png)`.
  Gọi cho `App.root` và mọi `W.Dialog`. `W.resource_path` = `sys._MEIPASS` khi `sys.frozen`, không thì gốc repo.
- **`MuxStudio.spec`**: onedir, `console=False`, `upx=False`, `collect_data_files("customtkinter")`,
  `copy_metadata("keyring")`, `collect_submodules("keyring.backends")`; Windows thêm `keyring.backends.Windows`,
  `win32ctypes.core(.ctypes.*)`, `win32ctypes.pywin32.*`, `send2trash.win(.legacy)`; excludes pytest/PIL/numpy…;
  icon + version file chỉ trên Windows (`pefile` chỉ có trên Windows); sau COLLECT chép `kiem_tra_MuxStudio.bat` cạnh
  .exe. PyInstaller 6.22 + hooks-contrib 2026.9 cũng tự có hook cho keyring, win32ctypes.core, customtkinter, certifi.
- **`build.bat`** (ASCII, CRLF): `.venv-build` RIÊNG (không lẫn pytest/Pillow của `.venv`) → kiểm Python ≥ 3.11 → `pip
  install -r requirements-build.txt` → báo nếu MuxStudio.exe đang mở (`tasklist | findstr` — KHÔNG dùng `find`, Git for
  Windows có `find` của Unix) → `PyInstaller --noconfirm --clean MuxStudio.spec` → `start "" /wait MuxStudio.exe --kiem-tra
  kiem_tra_exe.txt` (cmd không đợi chương trình cửa sổ nếu thiếu `start /wait`; `%errorlevel%` = mã thoát) → `type` báo
  cáo → mở Explorer. Mỗi lỗi một nhãn `:loi_*` có hướng dẫn.
- **Đã kiểm trên Linux**: build PyInstaller thật (11 s, 52 MB) → bản đóng gói `--kiem-tra` ĐẠT (keyring thấy backend
  SecretService, Windows, kwallet, libsecret, macOS qua entry points); mở giao diện thật dưới Xvfb, thư mục có `[ ]` +
  dấu: log đầu "MuxStudio 1.0.0 (bản .exe) | Python 3.12.3 | … | Tk 8.6.14 | CustomTkinter 6.0.0"; archive chỉ có
  `core.*` + `gui.*`. CHƯA kiểm được trên Windows thật: Credential Manager, Thùng rác, SmartScreen/Defender, DPI,
  Segoe UI, `cmd /c ver` không nháy cửa sổ — `build.bat` bước [4/4] và `kiem_tra_MuxStudio.bat` kiểm những thứ này.
- Sửa test (không đổi mã chương trình): `test_window_fits_screen_and_remembers_logical_size` (mục 5).

Tiêu chí hoàn thành của cả dự án: PROMPT §10 (mở .exe bằng bấm đúp, không cần cài Python, …) — chờ người dùng xác nhận
trên Windows.

## 11. Sau duyệt GĐ6 lần 1 (2026-11-09)

Người dùng gửi: ảnh `run_tests.bat` (15 failed, 618 passed), `ket_qua_do_api.txt` (có key), 4 yêu cầu giao diện.

- **15 test hỏng trên Windows** — đều do test (mục 5 "bài học"): 64 lời gọi `write_text` trong `tests/` thêm `newline=""`;
  `fake_alass.py` ép stdout UTF-8; test `tools.run` cho Python con in UTF-8; test tiếng bíp audio gốc PCM + ngưỡng 10 ms;
  test alass thật: phim dài bằng phụ đề; `test_e2e_mkv.py` tìm công cụ như "Tự tìm". Đã tái hiện 12/15 bằng
  `tools/winemu.py`, 3 ca còn lại (ffmpeg cũ ×2, alass thật) chứng minh bằng mô phỏng (ghi trong commit).
- **Dò API thật** (SPEC 7.E): `opensubs.os_languages` (`zh`→`zh-cn,zh-tw`, `pt`→`pt-br,pt-pt`), tập tuyệt đối không
  season không tìm theo tên, tham số theo chữ cái (tránh 301); SubSource `_rate_text` đọc `X-RateLimit-*` (phút/giờ/ngày),
  tên ngôn ngữ thật `chinese_bg_code` / `big_5_code` / `brazilian_portuguese`.
- **(1) Màu lưới chọn phụ đề**: `W.GRID_COLORS`; ô tick vẽ thành hộp (`CheckGrid._box`), dòng xen kẽ, khung rê chuột
  (`_set_hover`, tag `hover`), tiêu đề cột `E|…` (phụ đề NGOÀI) màu riêng, chữ 11. `ROW_BG["need"]` tối sáng hơn.
- **(2) Nút các bước**: `W.StepBar` (+ hàm thuần `step_states`, `step_label`): mỗi thẻ một hàng nút to đánh số, mũi tên;
  bước kế tiếp = bước CHƯA xong đầu tiên nếu bấm được (▶ xanh, viền), xong = ✓ xanh lá; tự thu chữ khi hẹp
  (`fit_size`, đo bằng `tkfont` × tỉ lệ CTk). ① quét/tìm/tải/sang ②; ② trích gốc/xem trước/đổi tên/lập cặp/đồng bộ/
  dọn dẹp/sang ③ (thẻ con đổi tên `INNER_RENAME`, `INNER_SYNC`); ③ lập kế hoạch/mux/dọn file nguồn/mở Output. Thuộc
  tính nút cũ (`scan_btn`, `plan_btn`, …) trỏ vào nút trên thanh. 4 thẻ chính chữ 15 đậm.
- **(3) Kéo khung Nhật ký**: tay kéo `LogPanel.grip`, `drag_height` (thuần), nhớ khoá lạ `UiLogHeight`, tối đa = cửa sổ − 300.
- **(4) Mux xong hỏi dọn file nguồn**: `mux.used_sources(plans, out_dir)` + `MuxSession.trash_sources` (log `Delete`
  vào nhật ký lượt mux, lưu lại); `mux_view.TrashDialog` (tick sẵn, nút "Đưa các file đã tick vào Thùng rác"; tắt
  `DeleteToRecycleBin` → chữ đỏ "XOÁ HẲN"); khoá mới `AskTrashSourcesAfterMux` (mặc định bật). Test giao diện thay
  `TrashDialog` bằng bản giả (fixture `ui`, `shown["TrashDialog"].answer`) — hộp thoại thật sẽ chặn test.

## 12. Bảo trì đợt 1 — bản 1.1.0 (2026-11-10)

Người dùng (đang dùng bản .exe 1.0.0) gửi 4 yêu cầu + 1 yêu cầu thêm giữa chừng; SPEC mục 7.F (TĐ-35, 36, 37).
Đã hỏi và chốt: Reset thẻ ③ bỏ cả lựa chọn database; thêm Reset cho cả thẻ ①; Reset KHÔNG xoá khung Nhật ký;
"phim đã có phụ đề" = file cạnh phim HOẶC track chữ trong MKV, **SDH có tính**.

- **Nút "↺ Làm lại từ đầu"** (3 thẻ, `reset_all`, hằng `RESET_TEXT`): gọi `reset()` sẵn có + (③) `db.set("")`, (②)
  `refresh_counts` + nạp lại bảng audio. Hỏi xác nhận khi còn dòng đã chọn chưa chạy. Không xoá file.
- **Thẻ ② bảng audio mọi file** (TĐ-35): `subprep.all_audio_streams()` → `VideoAudio(video, ep_label, streams)`;
  `pick_audio_stream(streams, choice)` (thuần — `_pick_audio` gọi nó, chỉ thêm log + cache); `audio_layout_groups`.
  Giao diện: `PV.audio_rows` / `audio_summary` (thuần), iid dòng `a<file>_<số thứ tự>` (`a<file>_x` = không đọc được),
  `fill_audio` (dựng bảng, chiều cao 2…3/4/8 dòng theo `density`), `mark_audio` (chỉ đổi ô "Làm mốc" bằng `set_cell` —
  **không** dựng lại bảng: chọn lại dòng sẽ bắn `on_select` → tự chuyển sang "Theo số thứ tự"). `sample_audio_streams`
  cũ vẫn còn (test cũ dùng).
- **Thẻ ② ẩn bước trích khi mốc = audio** (TĐ-37): `StepBar.set_hidden(keys)` (ẩn nút + mũi tên đứng trước; cột lưới
  của nút ẩn phải `weight=0, uniform=""`, không thì vẫn chiếm chỗ), `visible_step_states` (trạng thái `hidden`);
  `update()` vẫn nhận danh sách đủ 7 bước. Bước 2 tên mới "Quét phụ đề"; thẻ con `INNER_RENAME`/`INNER_SYNC` bỏ số.
  "Dọn dẹp" chỉ ✓ khi `_cleaned` (đã qua hộp thoại); quét lại = lượt mới.
- **Thẻ ① bỏ qua ngôn ngữ phim đã có** (TĐ-36): `subfetch.embedded_sub_langs(json)`, `sidecar_sub_langs(video)` (dùng
  `os.scandir`, không glob), `VideoInfo.has_subs`, `FetchItem.already` + `already_note` ("cạnh phim" / "trong MKV: …"),
  khoá mới `SkipIfVideoHasSub` (mặc định bật; `test_config` có danh sách khoá mới không đọc từ .bat). Đủ mọi ngôn ngữ →
  không gọi `refs.get` (khỏi trích phụ đề gốc).
- Chụp ảnh kiểm tra bố cục: thẻ ② mốc = audio ở 1366×768 và 1920×1080 (mục 6).
- Người dùng cập nhật: tải mã mới → `build.bat` → chép đè `dist\MuxStudio`.

## 13. Bảo trì đợt 2 — bản 1.2.0 (2026-11-10)

Yêu cầu: hai ngôn ngữ (English / Tiếng Việt), bỏ kho `srt_pool`, giao diện kiểu "kính" + Sáng/Tối, trang tác giả +
bản quyền phần mềm bên khác, thanh tiến độ xanh lá có phần trăm. SPEC 7.F: TĐ-38, TĐ-39. Người dùng chốt: dịch TOÀN BỘ
(kể cả Nhật ký), mặc định tiếng Việt, có làm nền mờ Windows 11, tác giả = Vũ Hoàng Anh (các công cụ thuộc bản quyền
bên làm ra chúng). Nhánh phiên `claude/stoic-fermat-vbr5cy` CHƯA xoá được (môi trường chặn xoá nhánh) — người dùng tự xoá.

- **Hai ngôn ngữ — nguyên tắc: mã nguồn GIỮ chữ tiếng Việt, chỉ dịch ở chỗ hiển thị.** Đừng bọc chuỗi trong mã bằng
  hàm dịch và đừng dịch dữ liệu: rất nhiều chỗ so sánh / ghi file bằng chữ tiếng Việt (`r.status == "đã trích"`,
  `"Khớp" in note`, cột Detail của nhật ký hoàn tác) — làm vậy thì bản English chạy khác logic mà test không thấy.
  - `core/i18n.py`: `set_language`, `tr(text)` (vi → trả nguyên), `translate_to`, `missing`, `recorded`. `tr` nhận câu
    ĐÃ ghép giá trị nên so ngược với MẪU (`{}` = chỗ chèn): trùng nguyên văn → khớp mẫu (regex, mẫu có chữ cố định
    dài nhất thắng; giá trị dịch tiếp đệ quy) → mẫu "mảnh" (bắt đầu / kết thúc bằng ký tự không phải chữ-số, ≥ 5 ký
    tự: được phép có chữ thừa phía đó — cho các chuỗi nối bằng `+`) → tách theo dòng / bỏ nhãn đầu `[!]`, `▶ 1.` / dấu
    ngăn cách ` | `, `; `, `: `… / ngoặc / số đếm đầu câu. Không dịch được thì giữ nguyên.
  - Bảng dịch `core/i18n_en.py` là FILE TỰ SINH: `tools/i18n/vi.json` + `en_00…08.txt` (đợt dịch đầu, mỗi dòng một
    chuỗi JSON, cùng thứ tự) + `extra.json` (câu thêm sau; câu KHÔNG DẤU như "Sang ③ Mux →", "Xong: {}." mà công cụ
    rút chữ không thấy). **Thêm / sửa câu chữ trong mã:** `python tools/i18n_extract.py --missing` → điền
    `tools/i18n/extra.json` (`""` = cố ý không dịch) → `python tools/i18n_build.py`. `test_i18n` hỏng nếu quên.
  - Soát chỗ sót: `MUXSTUDIO_I18N_RECORD=/tmp/rec.json xvfb-run -a /tmp/v312/bin/python -m pytest -q -p no:cacheprovider`
    rồi `python tools/i18n_residue.py /tmp/rec.json` (hiện còn 4 câu, đều là dữ liệu giả của test / tên file).
  - Giao diện: `W.install_i18n()` vá `__init__` / `configure` của CTkLabel, CTkButton, CTkCheckBox, CTkRadioButton,
    CTkSwitch, CTkEntry (dịch `text`, `placeholder_text`) và `CTkTabview` (`add/tab/set/delete` dịch tên, `get()` trả
    tên tiếng Việt). `W.TrChoice` cho OptionMenu / SegmentedButton có giá trị dùng trong logic (`prep.lang`,
    `settings.group_var`). `Table._shown` dịch ô (trừ cột `fit="name"` = tên file), tiêu đề cột; `Tooltip`, `Dialog`,
    `W.info/warn/ask_yes_no/ask_yes_no_cancel`, `LogPanel.write_many`. `cget("text")` trả chữ ĐÃ dịch.
  - Test: fixture tự động `vietnamese_ui` (conftest) trả ngôn ngữ về `vi` sau mỗi test.
- **Bỏ kho `srt_pool`** (TĐ-38): xoá `core/pool.py`, `tests/test_pool.py`; `tools.LEGACY_POOL_FOLDER` vẫn nằm trong
  danh sách loại trừ khi quét; 6 khoá `Pool*` vào `config.IGNORED_VARS`. Test giao diện thẻ ① giờ dùng OpenSubtitles
  giả (vá `FV.FetchSession` để truyền `FakeHTTP`).
- **Bộ màu kính**: `tools/make_theme.py` → `assets/glass.json` (theme `blue` của CustomTkinter + phần đè; spec đã gom).
  `W.load_glass_theme`, `W.appearance_mode`, `App.set_appearance` (đổi ngay) + `W.on_restyle` / `W.restyle` (tô lại
  Treeview, Canvas, khung log, PanedWindow — các phần không phải widget CTk); `AppearanceModeTracker` gọi lại khi
  Windows đổi Sáng/Tối. `W.apply_backdrop` (DWM: thuộc tính 20 + 38, kéo khung vào vùng nội dung, nền gốc đen) —
  **CHƯA thử trên Windows thật**; `UiBackdrop` mặc định `none`. Nếu người dùng báo chữ có viền / cửa sổ đen: đó là chỗ này.
- **Thẻ Giới thiệu** `gui/about_view.py`: `AUTHOR`, `CREDITS` (đã đối chiếu giấy phép với metadata các gói đang cài:
  CustomTkinter MIT theo file LICENSE dù trường License ghi CC0), `ATTRIBUTIONS` (TMDB, TheTVDB), `about_lines`.
  Thêm thư viện mới vào `requirements.txt` → thêm một `Credit`.
- **Thanh tiến độ**: `W.PROGRESS_GREEN`, `W.progress_text` → nhãn `App.prog_pct`.
- Đã thử đóng gói PyInstaller trên Linux: `--kiem-tra` ĐẠT, `assets/glass.json` có trong bản đóng gói.
- **Sau khi người dùng chạy `run_tests.bat` lần 1 (4 failed, 684 passed):** không phải lỗi mã — họ chép đè mã mới lên
  thư mục cũ nên `core/pool.py` + `tests/test_pool.py` (đã xoá khỏi repo) vẫn nằm lại: 3 test của `test_pool.py` hỏng
  `KeyError: 'PoolTryLimit'`, và `test_i18n` thấy 12 câu chữ của `pool.py` chưa dịch. Sửa: `run_tests.bat` và
  `build.bat` tự xoá hai file đó trước khi chạy. **Bài học: xoá file khỏi repo thì phải thêm nó vào danh sách dọn
  trong hai file .bat** (người dùng cập nhật bằng cách tải ZIP rồi chép đè, không dùng git).
