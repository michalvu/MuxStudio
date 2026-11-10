# Hướng dẫn dùng MuxStudio

MuxStudio thay ba file `SubFetch_v30.bat`, `SubPrep_v20.bat`, `MuxTool_v17.bat` bằng **một chương trình có giao diện**:
① tải phụ đề → ② đồng bộ phụ đề → ③ dọn track + mux MKV, đặt tên chuẩn Jellyfin.

Mục lục: [1. Tạo MuxStudio.exe](#1-tạo-muxstudioexe-một-lần) · [2. Công cụ ngoài](#2-công-cụ-ngoài-cần-có) ·
[3. Cấu hình lần đầu](#3-cấu-hình-lần-đầu) · [4. Quy trình làm việc](#4-quy-trình-làm-việc) ·
[5. Dừng, Hoàn tác, Nhật ký](#5-dừng-hoàn-tác-khung-nhật-ký) · [6. Báo lỗi](#6-báo-lỗi) · [7. Bị chặn khi chạy](#7-bị-chặn-khi-chạy) ·
[8. Việc khác](#8-việc-khác-tuỳ-chọn)

---

## 1. Tạo MuxStudio.exe (một lần)

Bản `.exe` chạy **không cần cài Python**. Chỉ máy dùng để **tạo** (build) bản `.exe` mới cần Python.

1. Cài **Python 3.11 trở lên** từ <https://www.python.org/downloads/>. Ở màn hình đầu của bộ cài, **tick ô
   "Add python.exe to PATH"** rồi bấm *Install Now*.
2. Tải mã nguồn MuxStudio về (trên GitHub: nút **Code › Download ZIP**, rồi giải nén), hoặc dùng thư mục đã có.
3. Mở thư mục đó, **bấm đúp `build.bat`**. Một cửa sổ đen hiện ra và chạy 4 bước:
   - `[1/4]` tạo môi trường riêng `.venv-build` và tải thư viện (lần đầu vài phút);
   - `[2/4]` kiểm tra MuxStudio cũ có đang mở không (đang mở thì đóng nó rồi chạy lại);
   - `[3/4]` đóng gói bằng PyInstaller (1–3 phút, **nhiều dòng chữ là bình thường**);
   - `[4/4]` chạy thử bản `.exe` vừa tạo (tự kiểm tra, không mở cửa sổ) và in báo cáo.
4. Thành công: cuối cùng có dòng **`KẾT QUẢ: ĐẠT`** và **`THANH CONG`**, thư mục `dist\MuxStudio` tự mở.
   Không thành công: cửa sổ ghi `[LOI]` kèm việc cần làm — xem [mục 6](#6-báo-lỗi).
5. **Chép CẢ thư mục `dist\MuxStudio`** tới nơi bạn muốn (vd `C:\Users\fenha\Desktop\MuxStudio`).
   Đừng chép riêng file `MuxStudio.exe` — nó cần thư mục `_internal` nằm cạnh.
6. Tạo lối tắt: chuột phải `MuxStudio.exe` › *Send to* (*Gửi tới*) › *Desktop (create shortcut)*.
7. Bấm đúp `MuxStudio.exe` để chạy. Số phiên bản hiện trên thanh tiêu đề (vd **MuxStudio 1.2.0**) và ở dòng đầu
   khung Nhật ký.

**Cập nhật bản mới:** tải mã nguồn mới → bấm đúp `build.bat` lần nữa → chép đè thư mục `dist\MuxStudio` lên thư mục
cũ. Cấu hình và key **không mất** (nằm ở chỗ khác, xem [mục 3](#3-cấu-hình-lần-đầu)).

> Muốn chạy thẳng từ mã nguồn (không build): bấm đúp `chay_muxstudio.bat` (cũng cần Python).

## 2. Công cụ ngoài cần có

MuxStudio không tự làm việc của các công cụ này mà gọi chúng (như ba file .bat cũ):

| Công cụ | Dùng ở | Bắt buộc? | Lấy ở đâu |
|---|---|---|---|
| `mkvmerge.exe`, `mkvextract.exe` (MKVToolNix) | ③ (tạo MKV), ① ② (đọc track, trích phụ đề gốc) | **Có** cho ③ | <https://mkvtoolnix.download> (bản *portable* cũng được) |
| `alass.exe` | ② (đồng bộ phụ đề) | **Có** cho ② | <https://github.com/kaegi/alass/releases> — file `alass-windows64.zip` (có sẵn `ffmpeg.exe`, `ffprobe.exe`) |
| `ffmpeg.exe`, `ffprobe.exe` | ② (đồng bộ theo audio), ③ (bitrate, HDR, đổi tốc độ audio ghép) | Nên có | đi kèm alass như trên |

Trên máy bạn chúng đã nằm ở `Desktop\mkvtoolnix\` và `Desktop\alass-windows64\bin\alass\` — MuxStudio **tự tìm thấy**.
Kiểm tra: thẻ **Cài đặt › Chung — thư mục & công cụ › Công cụ ngoài**: mỗi dòng phải có dấu **✓** và số phiên bản.
Dòng **✗**: bấm **Chọn…** để chỉ tới đúng file `.exe`, hoặc **Tự tìm**. Lúc mở chương trình, công cụ còn thiếu cũng
được báo trong khung Nhật ký (kèm việc gì sẽ không làm được).

## 3. Cấu hình lần đầu

1. Thẻ **Cài đặt** › nút **Nhập từ .bat…** › chọn cả ba file `.bat` cũ (`SubFetch_v30.bat`, `SubPrep_v20.bat`,
   `MuxTool_v17.bat` — bản **có key**). Bảng hiện *giá trị hiện tại* và *giá trị từ .bat*; tick khoá muốn nhập (khoá có
   hai giá trị khác nhau thì chọn một) › **Nhập**.
   - Key, tài khoản, mật khẩu (SubSource, TMDB, TVDB, OpenSubtitles) được cất vào **Windows Credential Manager**,
     không nằm trong file cấu hình, không bao giờ hiện trong log.
2. Nhóm **API & tài khoản**: bấm **Kiểm tra kết nối** cho từng dịch vụ (vd *"SubSource: key hợp lệ"*,
   *"OpenSubtitles: đăng nhập VIP…, còn 987/1000 lượt tải"*).
3. Xem lại các nhóm khác nếu muốn (mỗi ô có chú thích tiếng Việt; bật **Hiện mục nâng cao** để thấy hết) › **Lưu cài đặt**.

4. **Giao diện** (Cài đặt › Chung): **Ngôn ngữ giao diện** `vi` = Tiếng Việt, `en` = English (đổi xong **mở lại**
   MuxStudio mới có hiệu lực); **Chế độ Sáng / Tối** `system` / `light` / `dark` (đổi ngay khi bấm Lưu cài đặt);
   **Nền mờ của Windows 11** `none` / `mica` / `acrylic` — nền cửa sổ mờ nhìn xuyên kiểu kính (chỉ Windows 11, mở lại
   mới có hiệu lực). Nếu bật mà chữ có viền lạ hoặc cửa sổ hiện sai: chọn lại `none`, lưu, mở lại.

Nơi lưu: cấu hình `%APPDATA%\MuxStudio\config.json` (nút **Mở thư mục cấu hình** trong Cài đặt); key trong
*Control Panel › Credential Manager › Windows Credentials*, các mục có chữ `MuxStudio`.

## 4. Quy trình làm việc

Làm lần lượt ① → ② → ③. Chọn **Thư mục phim** ở đầu cửa sổ (**Chọn…**) — dùng chung cho cả ba bước.

Mỗi thẻ có một **thanh các bước** (hàng nút to, đánh số, có mũi tên ➜): nút **xanh dương có ▶** = bước nên bấm tiếp
theo; **xanh lá có ✓** = đã xong (vẫn bấm lại được); xám mờ = chưa bấm được. Nút cuối thanh chuyển sang thẻ kế tiếp.
Các nút nhỏ màu xám (Xem lệnh, Xuất CSV…, Bỏ qua…) là việc phụ, không bắt buộc.

**Làm lô file mới:** mỗi thẻ có nút xám **↺ Làm lại từ đầu** — xoá mọi bảng, dòng tổng kết và dấu ✓ của lượt vừa làm
(thẻ ③ bỏ luôn lựa chọn TMDB / TVDB), đưa thẻ về như lúc chưa làm gì. Nút này **không xoá file nào**, không xoá khung
Nhật ký, và "Hoàn tác…" vẫn dùng được cho lượt cũ.

**① Tải phụ đề** (thay SubFetch)
1. Tick ngôn ngữ cần tải, bật/tắt nguồn (SubSource, OpenSubtitles — kho `srt_pool` đã bỏ từ bản 1.2.0; muốn dùng phụ
   đề tự có thì bỏ vào `srt_can_sync`); **Kiểm tra nguồn** để xem key /
   lượt tải còn lại.
2. **1. Quét thư mục** → **2. Tìm phụ đề**. Ngôn ngữ mà phim **đã có phụ đề** thì dòng đó ghi *"phim đã có … — không
   tìm"* và được bỏ qua; chương trình chỉ tìm ngôn ngữ còn thiếu. "Đã có" = có file `<tên phim>.<mã>.srt` nằm cạnh
   phim, hoặc trong file MKV có track phụ đề chữ của ngôn ngữ đó (tính cả SDH; không tính Forced và phụ đề dạng ảnh).
   Muốn vẫn tìm: tắt *"Phim đã có phụ đề ngôn ngữ đó thì không tìm nữa"* trong Cài đặt › ① Tải phụ đề. Dòng màu vàng *"Cần chọn"*: bấm vào dòng, bảng dưới hiện ứng viên
   (cột **Lệch** = độ lệch đo thật / `~` ước tính) → bấm đúp ứng viên (hoặc **Chọn bản này** / **Bỏ qua ngôn ngữ này**).
3. **3. Tải phụ đề** → phụ đề vào `srt_can_sync\<mã ngôn ngữ>\`. Bước này **không** sửa thời gian phụ đề.

**② Chuẩn bị phụ đề** (thay SubPrep) — trước tiên chọn **Mốc đồng bộ** (*Phụ đề gốc trong 'srt_goc'* hoặc *Audio
của file phim*) và **Ngôn ngữ**, rồi theo thanh các bước:
1. **Trích phụ đề gốc** — chỉ cần khi `srt_goc` trống và mốc là phụ đề gốc (có sẵn thì bước này đã ✓).
   Chọn mốc = *Audio của file phim* thì bước này **không hiện** (không cần), các bước sau đánh số lại từ 1; thay vào
   đó là bảng **audio track của mọi file phim** (tập nào có audio nào): chọn *Tự động* / *Theo số thứ tự* (bấm một
   dòng) / *Theo ngôn ngữ* — cột **Làm mốc** đánh dấu "◄ mốc" ở track sẽ dùng của từng tập. Dòng **vàng** = tập có
   bố cục audio khác các tập còn lại (nên kiểm tra); dòng đỏ = không đọc được audio.
2. **Quét phụ đề** (bảng xem trước đổi tên) → kiểm tra cột lý do → 3. **Đổi tên**.
4. **Lập cặp đồng bộ** → (tuỳ chọn **Xem lệnh**) → 5. **Chạy đồng bộ**. Mỗi dòng có mức sức khoẻ **TỐT** (xanh) /
   **CẦN XEM** (vàng) / **CÓ VẤN ĐỀ** (đỏ); **Chỉ hiện file cần xem**, **Xuất CSV…**.
6. **Dọn dẹp** (xoá file nguồn đã dùng — vào Thùng rác).

**③ Mux** (thay MuxTool)
1. **Đặt tên theo (bắt buộc chọn):** TMDB / TVDB / Không đổi tên — không có lựa chọn sẵn (chưa chọn thì thanh các
   bước còn khoá).
2. **1. Quét & lập kế hoạch** → bảng tổng quan. Dòng vàng *"Cần chọn"*: bấm đúp để chọn tên (danh sách kết quả,
   **Tìm lại với tên:**, *giữ tên file gốc*) hoặc chọn audio (tick track muốn xoá, **Áp dụng cho mọi file còn lại**).
3. Xem / sửa **Bảng chọn phụ đề theo tập** (ô tick xanh = giữ; ô **vàng** = khác quy tắc tự động; ô tối có "—" = tập đó
   không có loại phụ đề này; bấm tiêu đề cột / tên tập = cả cột / cả dòng), audio ghép từ `audio_nguon` (sửa độ lệch
   tay ngay trong bảng); **Xem lệnh** để xem đúng lệnh `mkvmerge` / `ffmpeg` sẽ chạy.
4. **2. Bắt đầu mux** → kết quả trong thư mục `Output\`. File gốc không bị sửa.
5. **3. Dọn file nguồn đã dùng** — mux xong chương trình **tự hỏi** (tắt được trong Cài đặt › ③ Mux): danh sách MKV gốc,
   phụ đề ngoài, file nguồn audio ghép của các file mux **thành công**, tick sẵn → bỏ tick file muốn giữ → **Đưa các
   file đã tick vào Thùng rác**. File vẫn cần cho file chưa mux / mux lỗi không có trong danh sách. Lỡ tay: mở Thùng
   rác › chuột phải › *Restore*. Bấm "Không xoá" thì vẫn mở lại được bằng nút này.
6. **Mở thư mục Output**.

## 5. Dừng, Hoàn tác, khung Nhật ký

- **Thanh tiến độ** (xanh lá, cạnh nút Dừng): số phần trăm và số file đã xong, vd `37% · 3/8`.
- Thẻ **Giới thiệu**: phiên bản, tác giả, và danh sách phần mềm / dịch vụ của bên khác mà MuxStudio dùng kèm giấy phép
  (bấm đúp một dòng để mở trang chủ; **Chép nội dung trang này** để dán vào nơi khác).

- Đang chạy: **Dừng sau file này** (làm xong file hiện tại rồi dừng) hoặc **■ Dừng ngay** (kết thúc ngay, xoá file dở dang).
- Nút **Hoàn tác…** ở đầu cửa sổ: danh sách các lần chạy của cả ba bước (mới nhất ở trên, giữ 20 lần mỗi bước) → tick →
  **Hoàn tác các lần đã tick…**. File tạo ra → **Thùng rác**; file đã đổi tên / di chuyển → trả về chỗ cũ.
  File nguồn đã đưa vào Thùng rác (dọn dẹp ở ② / ③) thì khôi phục bằng tay từ Thùng rác. Đọc được cả nhật ký cũ của
  SubFetch / SubPrep (.bat).
- **Khung Nhật ký**: kéo **tay kéo** (vạch nhỏ ở mép trên khung, con trỏ ↕) lên để khung cao hơn, xuống để thấp hơn —
  chương trình nhớ chiều cao cho lần mở sau. **▾ Nhật ký** = thu gọn / mở lại.

## 6. Báo lỗi

Gửi cho người hỗ trợ:
1. **Số phiên bản** (thanh tiêu đề, vd *MuxStudio 1.2.0*; dòng thứ hai của khung Nhật ký ghi rõ bản `.exe`, Python, Windows).
2. **Log**: khung Nhật ký › **Lưu log…** (key, mật khẩu đã được che).
3. **Ảnh chụp màn hình** chỗ bị lỗi.
4. **Không mở được chương trình**: file `%APPDATA%\MuxStudio\loi_khoi_dong.txt` (gõ `%APPDATA%\MuxStudio` vào thanh
   địa chỉ của File Explorer rồi Enter).
5. **Báo cáo tự kiểm tra**: bấm đúp `kiem_tra_MuxStudio.bat` (nằm cạnh `MuxStudio.exe`) → Notepad mở báo cáo
   (`%APPDATA%\MuxStudio\kiem_tra.txt`). Ngay sau khi build thì báo cáo là file `kiem_tra_exe.txt` cạnh `build.bat`.
   Dòng `[LỖI]` = hỏng, `[!]` = chạy được nhưng nên xem (vd chưa có alass).

## 7. Bị chặn khi chạy

- **"Windows protected your PC"** (SmartScreen, màn hình xanh): bấm **More info** (*Thông tin thêm*) › **Run anyway**
  (*Vẫn chạy*). Chỉ hỏi lần đầu.
- **Phần mềm diệt virus báo nhầm / xoá `MuxStudio.exe`** (hay gặp với chương trình đóng gói bằng PyInstaller):
  *Windows Security › Virus & threat protection › Manage settings › Exclusions › Add or remove exclusions › Add an
  exclusion › Folder* → chọn thư mục MuxStudio (và thư mục mã nguồn nếu bị chặn lúc build), rồi build / chép lại.
- **Công cụ tải từ mạng không chạy** (Cài đặt ghi *"tìm thấy nhưng KHÔNG chạy được"*): chuột phải file đó (vd
  `alass.exe`, `mkvmerge.exe`) › **Properties** › tick **Unblock** › **OK**. Nếu cả thư mục tải về dạng ZIP: Unblock file
  ZIP **trước khi** giải nén.

## 8. Việc khác (tuỳ chọn)

- **Chạy toàn bộ test** (kiểm tra chương trình trên máy bạn, cần Python): bấm đúp `run_tests.bat` → cuối cùng có dòng
  `… passed`. Trên máy bạn chạy được cả test alass thật.
- **Dò API với key thật**: bấm đúp `chay_do_api.bat` → gửi file `ket_qua_do_api.txt` (key đã được che).
- Thư mục `build\`, `dist\`, `.venv-build\` do `build.bat` tạo ra — xoá được, lần build sau tự tạo lại.
