"""Titulky.com (bản 1.3.0, TĐ-40): bộ đọc HTML chạy trên MẪU THẬT của trang (tải 2026-11-11, đã cắt gọn trong
``tests/data/titulky/``); luồng đăng nhập / tải / CAPTCHA / chờ chạy với máy chủ giả. Không gọi mạng thật."""
from __future__ import annotations

import re
from pathlib import Path

import pytest

from core import srt, titulky, tools
from core.titulky import SearchRow, TitulkyClient
from fakehttp import FakeHTTP, FakeResponse, Sleeper, zip_bytes

DATA = Path(__file__).resolve().parent / "data" / "titulky"


def page(name: str) -> str:
    return (DATA / name).read_text(encoding="utf-8")


def client(http, tmp_path, **kw):
    sl = Sleeper()
    job = tools.Job()
    c = TitulkyClient(kw.pop("username", ""), kw.pop("password", ""), 1100, http, sl, job, tmp_path / "tt", clock=sl.clock,
                      now=lambda: 1794415365, **kw)
    return c, job, sl


def countdown_page(n: int = 6, link: str = "idown.php?id=abc123") -> str:
    """Trang chờ — CHƯA xem được trang thật (cần gõ CAPTCHA); dựng theo downfnc.js thật: CountDown(n) + id=downlink."""
    return (f'<!DOCTYPE html><html><body><span id="count_down">{n}</span>'
            f'<div id="downdiv" style="display:none"><a id="downlink" href="{link}">stáhnout</a></div>'
            f"<script>CountDown({n})</script></body></html>")


# ---------------------------------------------------------------- doc HTML that

def test_parse_search_real_series_page():
    rows = titulky.parse_search(page("search_breaking_bad_s01e05.html"))
    assert len(rows) == 4
    r = rows[2]
    assert (r.id, r.title, r.series, r.year, r.lang) == (232758, "Breaking Bad S01E05", "S01E05", 2008, "CZ")
    assert r.release == "Breaking.Bad.S01E05.Gray.Matter.720p.BRRip.x264.AC3" and r.downloads > 1000
    assert r.href == "Breaking-Bad-S01E05-232758.htm" and r.url == "https://www.titulky.com/Breaking-Bad-S01E05-232758.htm"
    assert all(x.lang == "CZ" and x.series == "S01E05" for x in rows)


def test_parse_search_real_movie_page_languages_years_and_missing_cells():
    rows = titulky.parse_search(page("search_dune.html"))
    assert len(rows) == 13
    assert rows[0].title == "Duna" and rows[0].year == 1984  # ten tieng Sec cua phim
    assert {r.lang for r in rows} == {"CZ", "SK", ""}  # dong cu khong ghi ngon ngu
    assert [r.year for r in rows if r.lang == "SK"] == [2021]
    assert any(r.release == "" for r in rows) and any(r.year is None for r in rows)
    assert all(r.series == "" for r in rows)  # phim le: cot Seriál la &nbsp;
    assert len({r.id for r in rows}) == 13 and rows[1].release == rows[2].release == "Dune.2021.BRRip.x264-ION10"
    assert titulky.parse_search("<html>không có bảng</html>") == [] and titulky.parse_search("") == []
    # bang "find" cua doan ma doan mo KHONG ton tai tren trang that
    assert 'class="soupis"' in page("search_dune.html") and "table.find" not in page("search_dune.html")


def test_parse_search_logged_in_layout_has_extra_saved_column():
    """Đã đăng nhập (người dùng dò 2026-11-12): bảng có thêm cột ``Uloženo`` giữa Rok và Staž. — chèn cột đó vào mẫu
    thật rồi đọc phải ra đúng như cũ (dò theo tiêu đề cột). Đoạn mã đọc theo vị trí cứng sẽ lấy nhầm lượt tải / ngôn ngữ."""
    html = page("search_breaking_bad_s01e05.html")
    head_cells = re.findall(r'<td[^>]*class="detailh[^>]*>.*?</td>', html, re.S)
    assert len(head_cells) == 7
    html = html.replace(head_cells[3], head_cells[3] + '<td class="detailh ucase">Uloženo</td>', 1)

    def add_cell(m):
        cells = re.findall(r"<td[^>]*>.*?</td>", m.group(0), re.S)
        return m.group(0).replace(cells[3], cells[3] + "<td>12.11.2026</td>", 1)

    html = re.sub(r'<tr class="r1?">.*?</tr>', add_cell, html, flags=re.S)
    assert [titulky._text(c) for c in re.findall(r'<td[^>]*class="detailh[^>]*>(.*?)</td>', html, re.S)] == [
        "Název", "V.", "Seriál", "Rok", "Uloženo", "Staž.", "Jazyk", "Uložil"]
    rows, plain = titulky.parse_search(html), titulky.parse_search(page("search_breaking_bad_s01e05.html"))
    assert len(rows) == 4 and [(r.id, r.title, r.release, r.series, r.year, r.downloads, r.lang) for r in rows] == [
        (r.id, r.title, r.release, r.series, r.year, r.downloads, r.lang) for r in plain]
    assert all(r.lang == "CZ" and r.downloads > 500 for r in rows)


def test_parse_search_follows_header_when_columns_move():
    """Đăng nhập có thể thêm cột → dò theo TIÊU ĐỀ cột chứ không theo vị trí cứng."""
    html = ('<table class="soupis"><tr><td class="detailh ucase">Jazyk</td><td class="detailh">Nový</td>'
            '<td class="detailh ucase">Název</td><td class="detailh" title="Verze/Release">V.</td>'
            '<td class="detailh">Rok</td></tr>'
            '<tr class="r"><td><img alt="CZ" src="x.gif" /></td><td>x</td><td><a href="/Film-A-77.htm">Film &amp; A</a></td>'
            '<td><a class="listTip" title="Film.A.2020.WEB&amp;DL"><img/></a></td><td>2020</td></tr></table>')
    (r,) = titulky.parse_search(html)
    assert (r.id, r.title, r.release, r.year, r.lang, r.href) == (77, "Film & A", "Film.A.2020.WEB&DL", 2020, "CZ", "Film-A-77.htm")


def test_parse_detail_preview_download_and_login_real_pages():
    d = titulky.parse_detail(page("detail_breaking_bad_s01e05.html"))
    assert d == {"fps": "23.976", "cds": 1, "kind": "srt", "imdb": "tt0903747",
                 "release": "Breaking.Bad.S01E05.Gray.Matter.720p.BRRip.x264.AC3"}
    assert titulky.parse_detail("") == {"fps": "", "cds": 0, "kind": "", "release": "", "imdb": ""}
    # trang tai that (vuot gioi han theo IP): co CAPTCHA + form, chua co link
    p = titulky.parse_download_page(page("idown_captcha.html"))
    assert p.captcha == "./captcha/captcha.php" and p.link == "" and p.wait == 0
    assert p.form == {"downkod": "", "securedown": "2", "zip": "", "T": "", "titulky": "115664", "histstamp": ""}
    assert "denní limit" in p.message and "ShowLoader" not in p.message
    # trang cho (dung theo downfnc.js that)
    w = titulky.parse_download_page(countdown_page(6, "idown.php?id=9&amp;x=1"))
    assert (w.link, w.wait, w.captcha) == ("idown.php?id=9&x=1", 6, "")
    js = page("downfnc.js")
    assert "15e2" in js and "getElementById('downlink')" in js and titulky.COUNTDOWN_STEP == 1.5
    # form dang nhap that: ten o dung nhu client gui
    form = page("home_login_form.html")
    assert titulky.has_login_form(form) and not titulky.has_login_form("<html><a href='?logoff=1'>Odhlásit</a></html>")
    for name in ("Login", "Password", "foreverlog", "Detail2", "prihlasit"):
        assert f'name="{name}"' in form
    # xem truoc: <br /> -> xuong dong, doc duoc moc thoi gian
    prev = ('<html><body><div id="nahled_zdroj" style="x">1<br />00:00:07,432 --&gt; 00:00:09,434<br />A &amp; B<br /><br />'
            "2<br />00:02:47,862 --&gt; 00:02:49,280<br />C<br /></div></body></html>")
    txt = titulky.parse_preview(prev)
    assert "00:00:07,432 --> 00:00:09,434\nA & B" in txt and srt.preview_times(txt) == pytest.approx([7.432, 167.862])
    assert titulky.parse_preview("<html></html>") == ""


def test_search_query_and_html_sniffing():
    assert titulky.search_query("Breaking  Bad", 1, 5) == "Breaking Bad S01E05" and titulky.search_query(" Dune ") == "Dune"
    assert titulky.looks_like_html(b"\xef\xbb\xbf\n <!DOCTYPE html><html>") and titulky.looks_like_html(b"<html>")
    assert titulky.looks_like_html(b"<!--\r\n** trang that cua Titulky bat dau bang chu thich (do that 2026-11-11) -->")
    assert titulky.looks_like_html(b"\t\t<!DOCTYPE html>") and not titulky.looks_like_html(b"{1}{25}MicroDVD") and not titulky.looks_like_html(b"")
    assert not titulky.looks_like_html(b"PK\x03\x04") and not titulky.looks_like_html(b"1\r\n00:00:01,000 --> 00:00:02,000")
    assert not titulky.looks_like_html(b"[Script Info]") and not titulky.looks_like_html(b"\xef\xbb\xbf1\n00:00")


# ---------------------------------------------------------------- client voi may chu gia

def test_search_detail_preview_use_real_urls_cache_and_pause(tmp_path):
    http = FakeHTTP()
    http.add("GET", r"titulky\.com/nahled\.php", FakeResponse(200, text='<div id="nahled_zdroj">1<br />00:00:05,000 --&gt; 00:00:06,000<br />x</div>'))
    http.add("GET", r"titulky\.com/Breaking-Bad-S01E05-232758\.htm", FakeResponse(200, text=page("detail_breaking_bad_s01e05.html")))
    http.add("GET", r"titulky\.com/$", FakeResponse(200, text=page("search_breaking_bad_s01e05.html")))
    c, job, sl = client(http, tmp_path)
    rows = c.search("Breaking Bad S01E05")
    assert len(rows) == 4 and c.search("breaking bad  s01e05") is rows  # nho dem, khong goi lai
    call = http.calls[0]
    assert call.url == "https://www.titulky.com/" and call.params == {"Fulltext": "Breaking Bad S01E05"}
    assert "MuxStudio" in call.headers["User-Agent"] and "Chrome" not in call.headers["User-Agent"]
    row = next(r for r in rows if r.id == 232758)
    c.fill_detail(row)
    c.fill_detail(row)
    assert (row.fps, row.cds, row.kind, row.imdb) == ("23.976", 1, "srt", "tt0903747")
    assert srt.preview_times(c.preview(row)) == [5.0] and c.preview(row)
    assert len(http.calls) == 3  # 1 tim + 1 chi tiet + 1 xem truoc
    assert http.calls[2].params == {"Titulky": 232758, "histstamp": ""} and http.calls[2].headers["Referer"] == row.url
    assert sl.calls == [1.1, 1.1]  # nghi giua cac luot goi
    assert c.search("") == []


def test_download_with_captcha_wrong_then_right_waits_full_countdown_and_unzips(tmp_path):
    sub = "1\r\n00:00:01,000 --> 00:00:02,000\r\nPříliš žluťoučký\r\n".encode("cp1250")  # bang ma cu
    http = FakeHTTP()
    http.add("GET", r"idown\.php\?id=abc123", FakeResponse(200, content=zip_bytes({"Breaking.Bad.S01E05.srt": sub, "info.nfo": b"x"})))
    http.add("GET", r"captcha/captcha\.php", FakeResponse(200, content=b"\xff\xd8\xff\xe0JPEG"))
    http.add("POST", r"titulky\.com/idown\.php$", [FakeResponse(200, text=page("idown_captcha.html")),
                                                    FakeResponse(200, text=countdown_page(6))])
    http.add("GET", r"titulky\.com/idown\.php$", FakeResponse(200, text=page("idown_captcha.html")))
    c, job, sl = client(http, tmp_path)
    asked = []

    def captcha(img, note):
        asked.append((img, note))
        return " 7KQ2 " if len(asked) == 1 else "x9y8"

    row = SearchRow(232758, "Breaking Bad S01E05", "Breaking-Bad-S01E05-232758.htm", "Breaking.Bad.S01E05.720p")
    d = c.download(row, captcha)
    files = c.subtitle_files(d)
    assert [f.name for f in files] == ["Breaking.Bad.S01E05.srt"]
    assert files[0].read_bytes() == sub  # NGUYEN byte: buoc ① khong doi bang ma
    first = http.calls_to(r"idown\.php$", "GET")[0]
    assert first.params == {"R": 1794415365, "titulky": 232758, "zip": "z", "histstamp": ""} and first.headers["Referer"] == row.url
    posts = http.calls_to(r"idown\.php$", "POST")
    assert [p.data["downkod"] for p in posts] == ["7KQ2", "x9y8"]
    assert posts[0].data == {"downkod": "7KQ2", "securedown": "2", "zip": "", "T": "", "titulky": "115664", "histstamp": ""}
    assert len(asked) == 2 and asked[0] == (b"\xff\xd8\xff\xe0JPEG", "Breaking.Bad.S01E05.720p")
    assert "không đúng" in asked[1][1]
    # cho du: 6 nhip x 1,5 s (+0,5) = 9,5 s, chia tung 0,5 s de bam Dung la thoat
    waits = [x for x in sl.calls if x == 0.5]
    assert len(waits) == 19 and any("chờ 10 giây" in m or "chờ 9 giây" in m for _, m in job.lines)
    assert http.calls[-1].url == "https://www.titulky.com/idown.php?id=abc123" and http.calls[-1].headers["Referer"].endswith("idown.php")
    n = len(http.calls)
    assert c.download(row, captcha) == d and len(http.calls) == n  # nho dem: khong tai lai, khong hoi lai


def test_download_without_captcha_plain_file_and_vip_like_direct_zip(tmp_path):
    http = FakeHTTP()
    http.add("GET", r"idown\.php\?id=abc123", FakeResponse(200, content=b"[Script Info]\nTitle: x\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,D,,0,0,0,,a\n",
                                                           headers={"Content-Disposition": 'attachment; filename="Film.A.2020.ass"'}))
    http.add("GET", r"titulky\.com/idown\.php$", lambda call: FakeResponse(200, text=countdown_page(2)) if call.params["titulky"] == 5
             else FakeResponse(200, content=zip_bytes({"a/Film.B.srt": b"1\n00:00:01,000 --> 00:00:02,000\nx\n"})))
    c, job, sl = client(http, tmp_path)
    never = lambda img, note: pytest.fail("không được hỏi CAPTCHA")  # noqa: E731
    d5 = c.download(SearchRow(5, "Film A", "Film-A-5.htm"), never)
    assert [f.name for f in c.subtitle_files(d5)] == ["Film.A.2020.ass"]
    d6 = c.download(SearchRow(6, "Film B", "Film-B-6.htm"), never)  # may chu tra thang ZIP (khong trang cho)
    assert [f.name for f in c.subtitle_files(d6)] == ["Film.B.srt"]
    assert d5 != d6 and d5.parent == tmp_path / "tt"


def test_download_captcha_skipped_once_is_not_asked_again_and_other_failures(tmp_path):
    http = FakeHTTP()
    http.add("GET", r"captcha/captcha\.php", FakeResponse(200, content=b"\xff\xd8JPEG"))
    http.add("GET", r"titulky\.com/idown\.php$", FakeResponse(200, text=page("idown_captcha.html")))
    c, job, _ = client(http, tmp_path)
    asked = []
    skip = lambda img, note: asked.append(note) or None  # noqa: E731
    assert c.download(SearchRow(1, "A", "A-1.htm"), skip) is None and c.captcha_declined
    assert c.download(SearchRow(2, "B", "B-2.htm"), skip) is None
    assert len(asked) == 1  # ban bo qua mot lan -> khong hoi nua trong luot nay
    assert sum("bỏ qua CAPTCHA" in m for _, m in job.lines) == 1 and any("đã bỏ qua trong lượt này" in m for _, m in job.lines)
    # khong co cua so nhap ma (chay khong giao dien)
    c2, job2, _ = client(http, tmp_path)
    assert c2.download(SearchRow(3, "C", "C-3.htm"), None) is None
    assert any("không có cửa sổ để nhập" in m for _, m in job2.lines)
    # sai 3 lan lien tiep
    http.add("POST", r"titulky\.com/idown\.php$", FakeResponse(200, text=page("idown_captcha.html")))
    c3, job3, _ = client(http, tmp_path)
    assert c3.download(SearchRow(4, "D", "D-4.htm"), lambda img, note: "sai") is None
    assert len(http.calls_to(r"idown\.php$", "POST")) == 3 and any("sai 3 lần" in m for _, m in job3.lines)
    # trang khong co link, khong co CAPTCHA (vd het luot) -> bao nguyen van loi cua trang; RAR -> can 7-Zip
    h2 = FakeHTTP()
    h2.add("GET", r"titulky\.com/idown\.php$", lambda call: FakeResponse(200, text="<html><body><h3>Limit vyčerpán</h3></body></html>")
           if call.params["titulky"] == 7 else FakeResponse(200, content=b"Rar!\x1a\x07\x00" + b"0" * 40))
    c4, job4, _ = client(h2, tmp_path)
    assert c4.download(SearchRow(7, "E", "E-7.htm"), skip) is None and c4.download(SearchRow(8, "F", "F-8.htm"), skip) is None
    text = "\n".join(m for _, m in job4.lines)
    assert "Titulky không cho tải: Limit vyčerpán" in text and "RAR" in text


def login_error_page(name: str) -> str:
    """Cùng cấu trúc với trang THẬT khi sai tên / mật khẩu (xem ngày 2026-11-12): thẻ meta Refresh + script chuyển hướng
    tới ``/?ShowInfo=LoginError&BadId=&BadLogin=<tên>`` (trang thật dài 232 byte với tên 28 ký tự; người dùng nhận 192
    byte = tên 8 ký tự; khoảng trắng giữa hai thẻ ở đây không sao chép chính xác)."""
    url = f"/?ShowInfo=LoginError&BadId=&BadLogin={name}"
    return f'<meta http-equiv="Refresh" content="0.1;URL={url}">\t\t\t\t\t<script>window.location.assign("{url}")</script>'


def site_with_login(valid: tuple[str, str] | None):
    """Máy chủ giả có trạng thái: trang chủ hiện form đăng nhập cho tới khi POST đúng tên + mật khẩu."""
    form, home_in = page("home_login_form.html"), "<html><body><a href='/?logoff=1'>Odhlásit</a></body></html>"
    http = FakeHTTP()
    state = {"in": False}

    def post(call):
        if valid and (call.data["Login"], call.data["Password"]) == valid:
            state["in"] = True
            # nguyen van trang that khi dang nhap DUNG (nguoi dung do 2026-11-12, 120 byte)
            return FakeResponse(200, text='<meta http-equiv="Refresh" content="0.1;URL=/?welcome=">\t\t\t\t\t'
                                          '<script>window.location.assign("/?welcome=")</script>')
        return FakeResponse(200, text=login_error_page(call.data["Login"]))

    http.add("POST", r"titulky\.com/$", post)
    http.add("GET", r"titulky\.com/$", lambda call: FakeResponse(200, text=home_in if state["in"] else form))
    return http


def test_login_real_field_names_and_connection_check(tmp_path):
    http = site_with_login(("anh", "demo-pass"))
    c, job, _ = client(http, tmp_path, username=" anh ", password="demo-pass")
    assert c.login() and c.logged_in is True and c.login_note == ""
    post = http.calls_to(r"titulky\.com/$", "POST")[0]
    assert post.data == {"Login": "anh", "Password": "demo-pass", "foreverlog": "1", "Detail2": "", "prihlasit": "Přihlásit"}
    assert http.calls[-1].method == "GET"  # sau khi gui: xem lai trang chu de CHAC la da dang nhap
    assert c.check().ok is True and c.check().text == "Titulky: đăng nhập được"
    # sai mat khau: trang that tra ve trang chuyen huong LoginError -> bao ro la trang TU CHOI, khong goi them
    bad = site_with_login(("anh", "demo-pass"))
    c2, job2, _ = client(bad, tmp_path, username="anh", password="demo-sai")
    assert not c2.login() and c2.logged_in is False and c2.login_note == "rejected"
    assert len(bad.calls) == 1
    msg = "\n".join(m for _, m in job2.lines)
    assert "Titulky từ chối tên đăng nhập / mật khẩu (trang báo LoginError)" in msg and "kiểu khách" in msg
    st = c2.check()
    assert st.ok is False and st.text.startswith("Titulky: đăng nhập lỗi — Titulky từ chối tên đăng nhập / mật khẩu")
    assert "demo-sai" not in msg and "demo-sai" not in st.text
    # trang khong bao loi nhung trang chu van hien o dang nhap -> KHONG coi la da dang nhap
    odd = FakeHTTP()
    odd.add("POST", r"titulky\.com/$", FakeResponse(200, text="<html>ok?</html>"))
    odd.add("GET", r"titulky\.com/$", FakeResponse(200, text=page("home_login_form.html")))
    c5, job5, _ = client(odd, tmp_path, username="anh", password="demo-pass")
    assert not c5.login() and c5.login_note == "unconfirmed"
    assert any("trang chủ vẫn hiện ô đăng nhập" in m for _, m in job5.lines)
    # khong tai khoan: dung kieu khach, khong gui form dang nhap
    c3, _, _ = client(bad, tmp_path)
    n = len(bad.calls_to(r"titulky\.com/$", "POST"))
    st = c3.check()
    assert st.ok is True and "kiểu khách" in st.text and c3.logged_in is None and not c3.login()
    assert len(bad.calls_to(r"titulky\.com/$", "POST")) == n
    # may chu loi
    down = FakeHTTP()
    down.add("GET", r"titulky\.com/$", FakeResponse(500, text="x"))
    c4, _, _ = client(down, tmp_path)
    assert c4.check().ok is False and "500" in c4.check().text


def test_download_replays_the_real_run_of_2026_11_12(tmp_path):
    """Đúng chuỗi trả lời người dùng nhận được trên Windows: gõ SAI mã → trang báo "CHYBA…" + CAPTCHA mới; gõ ĐÚNG →
    CountDown(10) + link ``/idown.php?id=100196299`` → ZIP ``application/octet-stream`` chứa một file .srt cp1250."""
    srt_cp1250 = "1\r\n00:00:01,000 --> 00:00:02,000\r\nP\u0159\u00edli\u0161 \u017elu\u0165ou\u010dk\u00fd k\u016f\u0148\r\n".encode("cp1250")
    wrong = page("idown_captcha.html").replace("<h3>Zabezpečené", "<b>CHYBA - Byl špatně zadaný bezpečnostní kód</b><h3>Zabezpečené")
    ready = ('<!DOCTYPE html><html><body><div id="imgLoader"></div><div id="razenidiv"></div><span id="count_down">10</span>'
             '<div id="downdiv" style="display:none"><a id="downlink" href="/idown.php?id=100196299">STÁHNOUT TITULKY</a></div>'
             '<div id="afterdown"></div><script>CountDown(10)</script></body></html>')
    http = FakeHTTP()
    http.add("GET", r"titulky\.com/idown\.php\?id=100196299$", FakeResponse(
        200, content=zip_bytes({"Breaking Bad S01E05.srt": srt_cp1250}),
        headers={"Content-Type": "application/octet-stream",
                 "Content-Disposition": "attachment; filename=Breaking-Bad-S01E05(0000126732).zip"}))
    http.add("GET", r"captcha/captcha\.php", FakeResponse(200, content=b"\xff\xd8\xff\xe0\x00\x10JFIF", headers={"Content-Type": "image/jpeg"}))
    http.add("POST", r"titulky\.com/idown\.php$", [FakeResponse(200, text=wrong), FakeResponse(200, text=ready)])
    http.add("GET", r"titulky\.com/idown\.php$", FakeResponse(200, text=page("idown_captcha.html")))
    c, job, sl = client(http, tmp_path)
    notes = []
    row = SearchRow(126732, "Breaking Bad S01E05", "Breaking-Bad-S01E05-126732.htm", "Breaking.Bad.S01E05.DVDRip.XviD-ORPHEUS")
    d = c.download(row, lambda img, note: notes.append(note) or f"ma{len(notes)}")
    (f,) = c.subtitle_files(d)
    assert f.name == "Breaking Bad S01E05.srt" and f.read_bytes() == srt_cp1250  # nguyen byte (cp1250), buoc ② moi doi ma
    assert len(notes) == 2 and "không đúng" in notes[1]
    assert http.calls[-1].url == "https://www.titulky.com/idown.php?id=100196299"  # link dang /idown.php?id=… (tuyet doi)
    assert sum(1 for x in sl.calls if x == 0.5) == 31  # CountDown(10): 10 x 1,5 + 0,5 = 15,5 s (may that: 16 giay)
    assert any("chờ 16 giây" in m for _, m in job.lines)
    from core import encoding

    assert encoding.detect_kind_bytes(f.read_bytes()) == "legacy"


def test_stop_now_interrupts_the_countdown(tmp_path):
    http = FakeHTTP()
    http.add("GET", r"titulky\.com/idown\.php$", FakeResponse(200, text=countdown_page(40)))
    c, job, sl = client(http, tmp_path)
    orig = sl.__call__

    def sleeping(s):
        sl.calls.append(s)
        if len(sl.calls) == 5:
            job.cancel_now()

    c.sleep = sleeping
    with pytest.raises(tools.Cancelled):
        c.download(SearchRow(9, "G", "G-9.htm"), None)
    assert len(sl.calls) == 5 and orig is not None  # dung sau 5 nhip 0,5 s, khong cho het 60 s
    assert re.search(r"idown\.php$", http.calls[-1].url)  # chua goi link tai


# ---------------------------------------------------------------- script do (nguoi dung chay voi tai khoan that)

def test_probe_script_runs_the_real_client_and_never_writes_credentials(tmp_path, monkeypatch):
    import requests

    from core import config as C
    from core.secrets import Redactor
    from tools import probe_titulky as P
    from tools.probe_common import Reporter

    sub = b"1\r\n00:00:01,000 --> 00:00:02,000\r\nAhoj\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nNazdar\r\n"
    http = FakeHTTP()
    http.add("GET", r"idown\.php\?id=abc123", FakeResponse(200, content=zip_bytes({"Breaking.Bad.S01E05.srt": sub}),
                                                           headers={"Content-Type": "application/zip"}))
    http.add("GET", r"titulky\.com/idown\.php$", FakeResponse(200, text=countdown_page(2), headers={"Content-Type": "text/html"}))
    http.add("GET", r"titulky\.com/nahled\.php", FakeResponse(200, text='<div id="nahled_zdroj">1<br />00:00:05,000 --&gt; 00:00:06,000<br />x</div>'))
    http.add("GET", r"titulky\.com/Breaking-Bad-S01E05-\d+\.htm", FakeResponse(200, text=page("detail_breaking_bad_s01e05.html")))
    state = {"in": False}

    def post(call):
        state["in"] = True
        return FakeResponse(200, text='<meta http-equiv="Refresh" content="0.1;URL=/?welcome=tajny-uzivatel">')

    http.add("POST", r"titulky\.com/$", post)
    http.add("GET", r"titulky\.com/$", lambda call: FakeResponse(
        200, text=page("search_breaking_bad_s01e05.html") if call.params else
        ("<!--\r\n** --><html><title>Titulky</title>přihlášen: tajny-uzivatel <a>Odhlásit</a></html>" if state["in"]
         else "<!--\r\n** -->" + page("home_login_form.html"))))
    monkeypatch.setattr(requests, "Session", lambda: http)
    monkeypatch.setattr(P, "ROOT", tmp_path)
    monkeypatch.setattr(P, "CAPTCHA_FILE", tmp_path / "captcha_titulky.jpg")
    monkeypatch.setattr("time.sleep", lambda s: None)
    out = tmp_path / "ket_qua.txt"
    rep = Reporter(out, Redactor([]))
    P.run(rep, C.Config({"ApiPauseMs": 0}), {"TitulkyUsername": "tajny-uzivatel", "TitulkyPassword": "demo-heslo-123"},
          download=True, interactive=False)
    rep.close()
    text = out.read_text(encoding="utf-8")
    assert "tajny-uzivatel" not in text and "demo-heslo-123" not in text  # ten dang nhap / mat khau khong lot vao file
    assert "Titulky: đăng nhập được" in text and "4 bản tiếng Séc" in text
    assert "FPS = 23.976" in text and "bản xem trước có 1 mốc thời gian" in text
    assert "Tải thử: ĐƯỢC (hỏi CAPTCHA 0 lần)" in text and "Breaking.Bad.S01E05.srt" in text and "2 mốc thời gian" in text
    assert "form: ['Detail2', 'Login', 'Password', 'foreverlog', 'prihlasit']" in text  # chi ghi TEN o, khong ghi gia tri
    assert "bảng kết quả: tiêu đề cột = ['Název', 'V.', 'Seriál', 'Rok', 'Staž.', 'Jazyk', 'Uložil']" in text
    assert "CountDown = 2 | link = idown.php?id=abc123" in text
    assert not (tmp_path / "_titulky_probe_tmp").exists()  # don thu muc tam
    # file .bat de nguoi dung bam dup: ASCII + CRLF, goi dung script
    bat = (Path(__file__).resolve().parent.parent / "chay_do_titulky.bat").read_bytes()
    assert bat.isascii() and b"\n" not in bat.replace(b"\r\n", b"") and b"-m tools.probe_titulky" in bat
