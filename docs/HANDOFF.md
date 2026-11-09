# Bàn giao giữa các phiên làm việc (đọc file này TRƯỚC)

Cập nhật: 2026-11-09, sau Giai đoạn 4. Repo `michalvu/MuxStudio`.
Nhánh: `claude/new-session-eckanp` và `claude/gifted-keller-fx1jey` giống nhau (người dùng yêu cầu cập nhật cả hai sau GĐ3).
Phiên sau: làm trên nhánh phiên đó được giao; người dùng muốn thì đẩy cả `claude/new-session-eckanp`.

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
| 4 | Bước ③ `mux donor tvdb` + phần bước ③ của `probe tmdb naming` + test | ✅ xong, **chờ duyệt** (báo TĐ-33, TĐ-34 mới) |
| 5 | GUI | chưa làm |
| 6 | Đóng gói | chưa làm |

**Việc kế tiếp:** chờ người dùng duyệt GĐ4 → sang GĐ5 (mục 9). Bất cứ lúc nào người dùng gửi `ket_qua_do_api.txt` → so với giả định trong `core/subsource.py`, `core/opensubs.py`,
`core/tmdb.py` (và `tvdb` sau). Chỗ nào khác: viết lại hàm giữ nguyên tên/chữ ký, thêm test, ghi vào SPEC mục 7.E
(hàm nào, vì sao hỏng, cách mới, kiểm chứng ra sao). Những điểm đang chờ dò:
- mã ngôn ngữ OpenSubtitles (`zh` hay `zh-cn`, `pt`…) — bước 2 của `probe_opensubs`;
- OpenSubtitles tìm anime theo `episode_number` không kèm season (TĐ-29) — bước 7;
- tên header giới hạn lượt của SubSource (để "Kiểm tra kết nối" hiện "còn 58/60 lượt/phút");
- `limit=100`, tên trường SubSource, `base_url` VIP OpenSubtitles.


## 3. Quyết định đã chốt (đừng hỏi lại)

- Q1–Q8 đồng ý hết đề xuất (SPEC mục 10). **Q7: phụ đề ASS giữ là ASS ở MỌI bước** (không đổi đuôi, không chuyển SRT).
- TĐ-05 mở rộng (từ kỹ thuật "mạnh"/"yếu" khi tách tên phim: `Uncut Gems`, `Charlotte's Web`) — người dùng đã đồng ý sang GĐ2.
- TĐ-28, TĐ-29, TĐ-30 duyệt cùng GĐ2 (2026-11-09).
- TĐ-31, TĐ-32 duyệt cùng GĐ3.
- TĐ-33 (tên codec đọc `codec` cấp track; `HDR10`) và TĐ-34 (bỏ `atempo` — làm audio ghép sớm ~21 ms, đổi tốc độ bằng
  phân số chính xác; KHÁC chữ chuỗi lọc trong §6.9 của PROMPT) báo cùng GĐ4, **chờ duyệt**.

## 4. Code đã có

```
core/   timing pgs srt encoding episodes naming config secrets undo   (GĐ1)
        tools probe tmdb subsource opensubs pool subfetch               (GĐ2)
        subprep                                                          (GĐ3)
        mux donor tvdb (+ phần bước ③ của probe tmdb naming)              (GĐ4)
tests/  pytest — 550 test (+2 test alass thật tự bỏ qua nếu không có alass); fakehttp.py (API giả),
        synth.py (phụ đề/PGS giả), fake_alass.py (alass GIẢ: ghi lại từng lần gọi, dịch phụ đề theo cấu hình),
        test_golden_powershell.py + test_golden_subprep.py + test_golden_mux.py + golden/ps_harness.ps1 (chạy HÀM GỐC
        trong reference/*.bat bằng pwsh và so; Invoke-TmdbApi/Invoke-TvdbApi thay bằng API giả cùng dữ liệu)
tools/  verify_bat/KiemChung_LoiBat.ps1, sanitize_bat.py, probe_*.py, probe_all.py, nhap_tu_bat.py
reference/  3 file .bat gốc ĐÃ XOÁ 7 bí mật (dùng cho test đối chiếu)
run_tests.bat, chay_do_api.bat (CRLF, cho Windows)
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

## 6. Dựng lại môi trường (container cloud bị xoá sau mỗi phiên)

```bash
cd MuxStudio
python3 -m venv .venv && .venv/bin/pip install -q -r requirements-dev.txt
# PowerShell 7 cho test đối chiếu hàm gốc
curl -sSL -o /tmp/pm.deb https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
  && dpkg -i /tmp/pm.deb && apt-get update -qq && apt-get install -y -qq powershell
apt-get install -y -qq mkvtoolnix          # ffmpeg thường có sẵn
pip install -q uv && uv python install 3.11   # chạy test trên 3.11:
uv venv -p 3.11 /tmp/v311 && uv pip install -p /tmp/v311/bin/python keyring send2trash requests pytest
.venv/bin/python -m pytest -q              # phải: 550 passed, 2 skipped (2 test alass thật)
/tmp/v311/bin/python -m pytest -q -p no:cacheprovider
```

Quét bí mật trước khi đẩy (cần bản .bat gốc có key — chỉ có nếu người dùng tải lên lại; nếu không có thì ít nhất
`git grep -nE "sk_(test|live)_|AKIA|ghp_"` và kiểm tra không có chuỗi key nào ngoài `demo-…`).

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

## 9. Kế hoạch GĐ5 (giao diện) + ghi chú GĐ6

- GĐ5: `gui/app.py fetch_view.py subprep_view.py mux_view.py settings_view.py widgets.py log_panel.py` (PROMPT §5, SPEC
  mục 6 — mỗi `Read-Host` → màn hình/hộp thoại). CustomTkinter, Treeview ☑/☐ (không tạo CTkCheckBox từng ô), luồng phụ +
  `queue` + `after(100)`, nút Dừng (`Job.stop_after_current` / `Job.cancel_now`), khung log màu (mức info/ok/warn/error/
  dim/head), "Lưu log", "Mở thư mục". Thư mục phim chọn một lần ở đầu cửa sổ. Cài đặt: `config.FIELDS` (nhóm, chú thích),
  ô bí mật `•••` + "Hiện" + "Kiểm tra kết nối" (`TmdbClient.check`, `TvdbClient.check`, SubSource/OpenSubtitles của GĐ2),
  "Khôi phục mặc định", "Nhập từ .bat" (`config.import_from_bat` + `apply_bat_import`). Màn hình Hoàn tác chung
  (`undo.list_runs` / `plan_undo` / `execute_undo`). `core/` không đổi API; GUI test được phần logic không cần màn hình.
- GĐ6: PyInstaller `--onedir --windowed`, gom theme CustomTkinter, `build.bat`, `HUONG_DAN.md`.
