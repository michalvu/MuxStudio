"""Dò trang Titulky.com với TÀI KHOẢN THẬT của bạn (bản 1.3.0, TĐ-40) — xác nhận ba điều mà lúc viết chưa xem được:

1. đăng nhập thành công trông ra sao (trang còn ô đăng nhập hay không);
2. bảng kết quả khi đã đăng nhập có thêm / đổi cột không;
3. trang tải sau khi qua CAPTCHA (hoặc khi không cần CAPTCHA): thời gian chờ, link tải, file trả về là gì.

Script chạy ĐÚNG phần mã mà MuxStudio dùng (``core/titulky.py``) và ghi lại từng lượt gọi: địa chỉ, mã trả lời, loại nội
dung, các dấu hiệu trong trang. Ghi ra ``ket_qua_do_titulky.txt`` — tên đăng nhập, mật khẩu, cookie KHÔNG được ghi.

    python -m tools.probe_titulky              # hỏi tài khoản (Enter = dò kiểu khách), tải thử MỘT phụ đề
    python -m tools.probe_titulky --no-download
    python -m tools.probe_titulky --no-input    # không hỏi gì (dò kiểu khách; gặp CAPTCHA thì bỏ qua)

Tải thử một phụ đề tốn 1 lượt tải trong ngày của bạn. Gặp CAPTCHA: ảnh được lưu thành ``captcha_titulky.jpg`` và mở ra,
bạn gõ mã vào cửa sổ đen (Enter = bỏ qua).
"""
from __future__ import annotations

import datetime as dt
import getpass
import os
import re
import sys
import time
from typing import Any

from tools.probe_common import ROOT, Reporter, load_everything

OUT = ROOT / "ket_qua_do_titulky.txt"
CAPTCHA_FILE = ROOT / "captcha_titulky.jpg"
QUERY_TITLE, QUERY_SEASON, QUERY_EPISODE = "Breaking Bad", 1, 5


class Spy:
    """Bọc ``requests.Session``: gọi thật, và ghi tóm tắt từng trả lời (không ghi cookie, không ghi thân form)."""

    def __init__(self, http: Any, rep: Reporter, hide: list[str]):
        self.http, self.rep, self.hide = http, rep, [h for h in hide if h and len(h) >= 2]
        self.n = 0

    def _clean(self, text: str) -> str:
        for h in self.hide:
            text = text.replace(h, "•••")
        return text

    def request(self, method: str, url: str, **kw: Any) -> Any:
        from core import titulky as T  # noqa: PLC0415

        self.n += 1
        t0 = time.monotonic()
        r = self.http.request(method, url, **kw)
        rep = self.rep
        q = kw.get("params") or {}
        rep.line(f"[{self.n}] {method} {url}" + (f"  tham số: {dict(q)}" if q else "")
                 + (f"  form: {sorted((kw.get('data') or {}).keys())}" if kw.get("data") else ""))
        ctype = (r.headers or {}).get("Content-Type", "")
        final = str(getattr(r, "url", url))
        rep.line(f"     → {r.status_code} | {ctype} | {len(r.content or b'')} byte | {time.monotonic() - t0:.1f} s"
                 + (f" | chuyển hướng tới: {self._clean(final)}" if final.split("?")[0] != url else ""))
        cd = (r.headers or {}).get("Content-Disposition")
        if cd:
            rep.line(f"     Content-Disposition: {cd}")
        cookies = sorted(c.name for c in getattr(self.http, "cookies", []))
        if cookies:
            rep.line(f"     tên cookie đang giữ (không ghi giá trị): {', '.join(cookies)}")
        data = r.content or b""
        if not T.looks_like_html(data):
            rep.line(f"     KHÔNG phải trang web → file: 8 byte đầu {data[:8]!r}")
            return r
        page = r.text or ""
        if len(data) < 600:  # trang nho (vd trang chuyen huong sau khi dang nhap): ghi nguyen van, da che ten / mat khau
            rep.line(f"     nguyên văn trang nhỏ: {self._clean(' '.join(page.split()))}")
        title = re.search(r"<title>([^<]*)", page, re.I)
        rep.line(f"     tiêu đề trang: {self._clean(title.group(1).strip()) if title else '(không có)'}")
        rep.line(f"     còn ô đăng nhập (id=log_login / name=Login): {T.has_login_form(page)} | có chữ 'Odhlásit': "
                 f"{'Odhlásit' in page or 'odhlasit' in page.lower()} | có 'BadLogin': {'BadLogin' in page}")
        head = re.search(r'<table[^>]*class="[^"]*soupis[^"]*"[^>]*>\s*<tr[^>]*>(.*?)</tr>', page, re.S | re.I)
        if head:
            cells = [T._text(c) for c in re.findall(r"<td[^>]*>(.*?)</td>", head.group(1), re.S | re.I)]
            rep.line(f"     bảng kết quả: tiêu đề cột = {cells} | số dòng đọc được = {len(T.parse_search(page))}")
        if "idown.php" in url:
            d = T.parse_download_page(page)
            ids = sorted(set(re.findall(r'id="([A-Za-z_0-9]+)"', page)))
            rep.line(f"     trang tải: ảnh CAPTCHA = {d.captcha or '(không)'} | CountDown = {d.wait} | link = "
                     f"{d.link or '(không)'}")
            rep.line(f"     các id trong trang: {ids}")
            rep.line(f"     ô của form: {sorted(d.form)}")
            rep.line(f"     chữ trên trang: {self._clean(d.message[:400])}")
        return r


def run(rep: Reporter, cfg: Any, secrets: dict[str, str], download: bool = True, interactive: bool = True) -> None:
    import requests  # noqa: PLC0415

    from core import titulky as T  # noqa: PLC0415
    from core import tools  # noqa: PLC0415

    rep.section("TITULKY.COM")
    user, pw = secrets.get("TitulkyUsername", ""), secrets.get("TitulkyPassword", "")

    def ask_account(first: bool) -> tuple[str, str]:
        """Hỏi tài khoản ở cửa sổ đen. Số ký tự đã nhận chỉ in ra màn hình (KHÔNG ghi vào file) — để bạn biết dán mật
        khẩu có vào không: gõ / dán mật khẩu thì màn hình không hiện gì."""
        try:
            u = input("Tên đăng nhập Titulky (Enter = dò kiểu khách, không đăng nhập): " if first else
                      "Gõ lại tên đăng nhập Titulky (Enter = thôi, dò tiếp kiểu khách): ").strip()
            p = getpass.getpass("Mật khẩu Titulky (KHÔNG hiện khi gõ — cứ gõ rồi Enter): ").strip("\r\n") if u else ""
        except EOFError:
            return "", ""
        if u:
            print(f"  (đã nhận: tên đăng nhập {len(u)} ký tự, mật khẩu {len(p)} ký tự — hai con số này không ghi vào file)")
        return u, p

    if not user and interactive:
        rep.line("Chưa có tài khoản Titulky trong Windows Credential Manager (Cài đặt › API & tài khoản của MuxStudio).")
        user, pw = ask_account(True)
    job = tools.Job(log=lambda lv, m: rep.line(f"     [{lv}] {m}"))
    hide: list[str] = []
    spy = Spy(requests.Session(), rep, hide)

    rep.step("1. Kết nối + đăng nhập")
    client = None
    for attempt in range(1, 4):
        rep.add_secret(user)
        rep.add_secret(pw)
        hide[:] = [h for h in (user, pw) if h and len(h) >= 2]
        spy.hide = hide
        client = T.TitulkyClient(user, pw, int(cfg["ApiPauseMs"]), spy, job=job, temp_dir=ROOT / "_titulky_probe_tmp")
        st = client.check()
        rep.conclude(f"Lần {attempt}: {st.text}" + ("" if client.has_account else "  (không có tài khoản: bỏ qua phần đăng nhập)"))
        if client.has_account:
            rep.line(f"  ghi chú đăng nhập: {client.login_note or 'được'}")
        if st.ok or not client.has_account or not interactive or attempt == 3:
            break
        print("  Trang không nhận tên đăng nhập / mật khẩu vừa gõ. Kiểm tra: đăng nhập thử trên https://www.titulky.com bằng")
        print("  trình duyệt; tên đăng nhập là TÊN TÀI KHOẢN (không phải e-mail); chú ý chữ hoa / chữ thường, bàn phím đang")
        print("  ở tiếng Anh hay tiếng Séc.")
        user, pw = ask_account(False)
        if not user:
            break
    assert client is not None

    rep.step("2. Tìm một tập phim bộ")
    rows = client.search(T.search_query(QUERY_TITLE, QUERY_SEASON, QUERY_EPISODE))
    cz = [r for r in rows if r.lang == "CZ"]
    rep.conclude(f"Tìm '{QUERY_TITLE} S{QUERY_SEASON:02d}E{QUERY_EPISODE:02d}': {len(rows)} dòng, {len(cz)} bản tiếng Séc")
    if not cz:
        rep.conclude("Không có dòng tiếng Séc nào → KHÔNG dò tiếp được (gửi file này để xem cấu trúc trang đã đổi chưa)")
        return
    row = max(cz, key=lambda r: r.downloads)
    rep.line(f"  chọn bản: #{row.id} {row.title} | {row.release} | {row.downloads} lượt tải")

    rep.step("3. Trang chi tiết + bản xem trước")
    client.fill_detail(row)
    from core import srt  # noqa: PLC0415

    pv = srt.preview_times(client.preview(row))
    rep.conclude(f"Chi tiết: FPS = {row.fps or '?'} | số CD = {row.cds} | loại = {row.kind or '?'} | IMDb = {row.imdb or '?'}; "
                 f"bản xem trước có {len(pv)} mốc thời gian")

    if not download:
        rep.conclude("Bỏ qua phần tải thử (--no-download)")
        return
    rep.step("4. Tải thử MỘT phụ đề (tốn 1 lượt tải trong ngày)")
    asked: list[int] = []

    def captcha(img: bytes, note: str) -> str | None:
        asked.append(len(img))
        CAPTCHA_FILE.write_bytes(img)
        rep.line(f"     CAPTCHA: ảnh {len(img)} byte, 4 byte đầu {img[:4]!r} → đã lưu {CAPTCHA_FILE.name}")
        if not interactive:
            return None
        try:
            if sys.platform.startswith("win"):
                os.startfile(CAPTCHA_FILE)  # type: ignore[attr-defined]  # noqa: S606
        except OSError:
            pass
        try:
            return input(f"Mở file {CAPTCHA_FILE.name} (cạnh file .bat), gõ mã trong ảnh rồi Enter (Enter trống = bỏ qua): ").strip()
        except EOFError:
            return None

    d = client.download(row, captcha)
    files = client.subtitle_files(d)
    if d is None:
        rep.conclude(f"Tải thử: KHÔNG được (đã hỏi CAPTCHA {len(asked)} lần) — xem các dòng [warn] ở trên")
    else:
        every = sorted(p.name for p in d.rglob("*") if p.is_file())
        rep.conclude(f"Tải thử: ĐƯỢC (hỏi CAPTCHA {len(asked)} lần). File nhận được: {every}; file phụ đề dùng được: "
                     f"{[f.name for f in files]}")
        for f in files[:1]:
            raw = f.read_bytes()
            from core import encoding  # noqa: PLC0415

            rep.line(f"  {f.name}: {len(raw)} byte, bảng mã nhận diện = {encoding.detect_kind_bytes(raw)}, "
                     f"{len(srt.cue_times(f) or [])} mốc thời gian")
    try:
        import shutil  # noqa: PLC0415

        shutil.rmtree(ROOT / "_titulky_probe_tmp", ignore_errors=True)
        CAPTCHA_FILE.unlink(missing_ok=True)
    except OSError:
        pass


def main() -> int:
    from core.secrets import Redactor  # noqa: PLC0415

    cfg, secrets = load_everything()
    rep = Reporter(OUT, Redactor(secrets.values()))
    rep.line(f"MuxStudio — kết quả dò Titulky.com, {dt.datetime.now():%Y-%m-%d %H:%M}")
    try:
        run(rep, cfg, secrets, download="--no-download" not in sys.argv, interactive="--no-input" not in sys.argv)
    except Exception:  # noqa: BLE001
        import traceback  # noqa: PLC0415

        rep.line("[LỖI khi dò Titulky]")
        for ln in traceback.format_exc().splitlines():
            rep.line("  " + ln)
    rep.section("TÓM TẮT")
    for c in rep.conclusions:
        rep.line(f"  • {c}")
    rep.line()
    rep.line(f"Đã ghi: {OUT}")
    rep.line("Gửi file này cho Claude (tên đăng nhập, mật khẩu, cookie không có trong file).")
    rep.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
