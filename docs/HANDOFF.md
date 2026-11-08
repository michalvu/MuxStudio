# Bàn giao giữa các phiên làm việc (đọc file này TRƯỚC)

Cập nhật: 2026-11-08, sau Giai đoạn 2. Repo `michalvu/MuxStudio`, nhánh **`claude/new-session-eckanp`**.

## 1. Đọc gì, theo thứ tự

1. `docs/PROMPT_MuxStudio.md` — yêu cầu gốc của người dùng (bắt buộc tuân theo, nhất là §4 kiến trúc, §6 hành vi, §9 giai đoạn).
2. `docs/SPEC.md` — đặc tả ĐÃ DUYỆT: bảng 195 hàm → Python (mục 4), cấu hình (mục 5), `Read-Host` → GUI (mục 6),
   **Thay đổi so với .bat TĐ-01…TĐ-30** (mục 7, cột cuối ghi giai đoạn làm + ✅ nếu xong), quyết định Q1–Q8 (mục 10).
3. File này.

Người dùng **không phải lập trình viên**: trả lời + giao diện **tiếng Việt có dấu**, hướng dẫn từng bước ngắn.
**Dừng ở cuối mỗi giai đoạn** chờ duyệt; trước khi báo xong: chạy hết test và dán kết quả.

## 2. Trạng thái

| GĐ | Nội dung | Trạng thái |
|---|---|---|
| 0 | SPEC + script kiểm chứng lỗi .bat | ✅ đã duyệt |
| 1 | `timing pgs srt encoding episodes naming config secrets undo` + test | ✅ đã duyệt |
| 2 | `tools probe tmdb subsource opensubs pool subfetch` + script dò API + test | ✅ xong, **chờ người dùng**: chạy `chay_do_api.bat` và gửi `ket_qua_do_api.txt` |
| 3 | Bước ② SubPrep | chưa làm |
| 4 | Bước ③ MuxTool | chưa làm |
| 5 | GUI | chưa làm |
| 6 | Đóng gói | chưa làm |

**Việc kế tiếp:** khi có `ket_qua_do_api.txt` → so với giả định trong `core/subsource.py`, `core/opensubs.py`,
`core/tmdb.py` (và `tvdb` sau). Chỗ nào khác: viết lại hàm giữ nguyên tên/chữ ký, thêm test, ghi vào SPEC mục 7.E
(hàm nào, vì sao hỏng, cách mới, kiểm chứng ra sao). Những điểm đang chờ dò:
- mã ngôn ngữ OpenSubtitles (`zh` hay `zh-cn`, `pt`…) — bước 2 của `probe_opensubs`;
- OpenSubtitles tìm anime theo `episode_number` không kèm season (TĐ-29) — bước 7;
- tên header giới hạn lượt của SubSource (để "Kiểm tra kết nối" hiện "còn 58/60 lượt/phút");
- `limit=100`, tên trường SubSource, `base_url` VIP OpenSubtitles.

Rồi chờ duyệt GĐ2 → sang GĐ3.

## 3. Quyết định đã chốt (đừng hỏi lại)

- Q1–Q8 đồng ý hết đề xuất (SPEC mục 10). **Q7: phụ đề ASS giữ là ASS ở MỌI bước** (không đổi đuôi, không chuyển SRT).
- TĐ-05 mở rộng (từ kỹ thuật "mạnh"/"yếu" khi tách tên phim: `Uncut Gems`, `Charlotte's Web`) — người dùng đã đồng ý sang GĐ2.
- TĐ-28, TĐ-29, TĐ-30 (GĐ2) đã báo người dùng, **chưa có phản hồi** — coi như chờ duyệt cùng GĐ2.

## 4. Code đã có

```
core/   timing pgs srt encoding episodes naming config secrets undo   (GĐ1)
        tools probe tmdb subsource opensubs pool subfetch               (GĐ2)
tests/  pytest — 407 test; fakehttp.py (API giả), synth.py (phụ đề/PGS giả),
        test_golden_powershell.py + golden/ps_harness.ps1 (chạy HÀM GỐC trong reference/*.bat bằng pwsh và so)
tools/  verify_bat/KiemChung_LoiBat.ps1, sanitize_bat.py, probe_*.py, probe_all.py, nhap_tu_bat.py
reference/  3 file .bat gốc ĐÃ XOÁ 7 bí mật (dùng cho test đối chiếu)
run_tests.bat, chay_do_api.bat (CRLF, cho Windows)
```

- Bước ① theo mẫu **lập kế hoạch → thực thi**: `FetchSession.scan → gather → auto_select/choose/skip → execute`.
  GĐ3/GĐ4 làm theo cùng mẫu (`plan_*` không ghi gì vào thư mục phim; `execute` ghi + nhật ký hoàn tác `undo.RunLog`).
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
  Không ghi tên model trong commit/code. Đẩy: `git push -u origin claude/new-session-eckanp`.

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
.venv/bin/python -m pytest -q              # phải: 407 passed
/tmp/v311/bin/python -m pytest -q -p no:cacheprovider
```

Quét bí mật trước khi đẩy (cần bản .bat gốc có key — chỉ có nếu người dùng tải lên lại; nếu không có thì ít nhất
`git grep -nE "sk_(test|live)_|AKIA|ghp_"` và kiểm tra không có chuỗi key nào ngoài `demo-…`).

## 7. Kế hoạch Giai đoạn 3 (bước ② — SubPrep)

Nguồn: SPEC mục 4.2 (55 hàm), §6.5 của PROMPT, các TĐ ghi "(GĐ3)": **TĐ-12** (làm trên bản sao, không ghi đè nguồn),
**TĐ-13** (luôn loại `DonorFolder`, `PoolFolder`, `SubFolderName` khỏi tìm mốc), **TĐ-14** (sức khoẻ đọc mốc SRT/ASS/VTT; cắt recap
chỉ SRT, ASS ghi lý do), **TĐ-15** (`.cz` → `.cs`), **TĐ-18** (mốc audio + có srt_goc → đo lệch FPS để bỏ `--disable-fps-guessing`),
**TĐ-19** (thứ tự thử alass: WAV+cờ → WAV không cờ → MKV+cờ → MKV không cờ), **TĐ-20** (luôn sửa khối 00:00:00 + kiểm tra sức khoẻ).

`core/subprep.py` dự kiến: `RefIndex.build` (Get-RefIndex, hiện "Mốc lấy từ: …"), `resolve_ref` (4 tầng của Resolve-Ref, trả
**lý do** khi không ghép được), `plan_extract_refs/extract_refs` (Invoke-ExtractRefSubs; dùng `probe.pick_ref_sub_track`),
`plan_rename/execute_rename`, `plan_sync/execute_sync` (WAV `ffmpeg -map 0:a:N -vn -ac 1 -ar 16000 -c:a pcm_s16le` LUÔN, kể cả
tự động), `remove_leading_extra` (70% cuối, 5 dòng liên tiếp ±1,5 s qua `timing.RefBins`, giới hạn 30% đầu, huỷ nếu giữ < 60%),
`repair_collapsed_cues`, `sub_health` (7 dấu hiệu, mức 0/1/2), `cleanup_targets`, `MaxShiftSeconds` chỉ từ chối khi mốc audio.

Test: recap giả lập cắt đúng số dòng; khối 00:00:00; từng dấu hiệu sức khoẻ; ghép tên nhập nhằng → bỏ qua + lý do; test đối
chiếu hàm gốc (`Remove-LeadingExtra`, `Repair-CollapsedCues`, `Test-SubHealth`, `Resolve-Ref`) qua `tests/golden/ps_harness.ps1`
(thêm case); đầu–cuối nhỏ với alass thật nếu tải được bản Linux (có thể bị chặn mạng — khi đó test tự bỏ qua).

## 8. Ghi chú cho GĐ4–6

- GĐ4: `probe` (audio/HDR/thẻ codec), `mux.py` (quy tắc SPEC 4.4), `tvdb.py`, phần đặt tên còn lại của `tmdb.py`/`naming.py`,
  `donor.py`; test tiếng bíp (§8 PROMPT) với ffmpeg + `silencedetect`, sai số < 20 ms. TĐ-17, 21, 23 (Q3), 25.
- GĐ5: CustomTkinter, Treeview ☑/☐, luồng phụ + `queue` + `after()`, nút Dừng (`Job.stop_after_current` / `cancel_now`).
- GĐ6: PyInstaller `--onedir --windowed`, gom theme CustomTkinter, `build.bat`, `HUONG_DAN.md`.
