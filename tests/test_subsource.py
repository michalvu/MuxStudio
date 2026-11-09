"""SubSource (giả lập) — §6.11."""
from __future__ import annotations

import os

import pytest

from core import episodes, naming, subsource, tools
from fakehttp import FakeHTTP, FakeResponse, Sleeper, zip_bytes

BASE = "https://api.subsource.net/api/v1"


def client(http, **kw):
    sl = Sleeper()
    job = tools.Job()
    c = subsource.SubSourceClient("demo-ss-key", BASE, kw.get("pause_ms", 1100), kw.get("max_pages", 10), http,
                                  sleep=sl, clock=sl.clock, job=job, cache_dir=kw.get("cache_dir"))
    return c, sl, job


def test_header_and_pacing_between_calls():
    http = FakeHTTP().add("GET", r"/movies/search", FakeResponse(200, {"data": []}))
    c, sl, _ = client(http)
    c.get("/movies/search", {"searchType": "text", "q": "A", "empty": ""})
    c.get("/movies/search", {"searchType": "text", "q": "B"})
    assert http.calls[0].headers["X-API-Key"] == "demo-ss-key"
    assert "empty" not in http.calls[0].params  # bo tham so rong nhu .bat
    assert sl.calls == [pytest.approx(1.1)]  # nghi ~1,1 s giua 2 luot, luot dau khong nghi


def test_429_waits_20s_then_retries_and_4xx_stops():
    http = FakeHTTP().add("GET", r"/x", [FakeResponse(429), FakeResponse(200, {"data": [1]})])
    c, sl, _ = client(http)
    assert c.get("/x") == {"data": [1]} and 20 in sl.calls
    http2 = FakeHTTP().add("GET", r"/y", FakeResponse(403, text="forbidden"))
    c2, _, job = client(http2)
    assert c2.get("/y") is None and len(http2.calls) == 1
    assert any("403" in m for _, m in job.lines)


def test_find_entry_imdb_first_then_text_and_season_filter():
    def search(call):
        if call.params["searchType"] == "imdb":
            return FakeResponse(200, {"data": []})
        return FakeResponse(200, {"data": [
            {"movieId": 11, "title": "Show", "season": 1}, {"movieId": 12, "title": "Show", "season": 2}]})

    http = FakeHTTP().add("GET", r"/movies/search", search)
    c, _, job = client(http)
    e = c.find_entry("Show", None, 2, "tt123")
    assert e["movieId"] == 12
    assert [x.params["searchType"] for x in http.calls] == ["imdb", "text"]
    assert c.find_entry("Show", None, 5, "tt123") is None
    assert any("Không có season 5" in m and "1, 2" in m for _, m in job.lines)


def test_find_entry_movie_year_and_title():
    data = [{"movieId": 1, "title": "Dune", "releaseYear": 1984}, {"movieId": 2, "title": "Dune", "releaseYear": 2021},
            {"movieId": 3, "title": "Dune Part Two", "releaseYear": 2024}]
    http = FakeHTTP().add("GET", r"/movies/search", FakeResponse(200, {"data": data}))
    c, _, _ = client(http)
    assert c.find_entry("Dune", 2021, None)["movieId"] == 2
    assert c.find_entry("Dune Part Two", None, None)["movieId"] == 3


def test_list_all_no_server_side_filter_limit_fallback_and_pagination():
    def subs(call):
        p = call.params
        if p["limit"] == "100":
            return FakeResponse(400, text="limit too large")
        page = int(p["page"])
        return FakeResponse(200, {"data": [{"subtitleId": page * 10 + i, "language": "vietnamese"} for i in range(2)],
                                  "pagination": {"pages": 3}})

    http = FakeHTTP().add("GET", r"/subtitles$", subs)
    c, _, _ = client(http)
    all_ = c.list_all(99)
    assert [s["subtitleId"] for s in all_] == [10, 11, 20, 21, 30, 31]
    for call in http.calls:
        assert set(call.params) <= {"movieId", "page", "limit"}  # KHONG loc phia may chu
    assert [call.params["limit"] for call in http.calls] == ["100", "20", "20", "20"]
    n = len(http.calls)
    c.list_all(99)
    assert len(http.calls) == n  # bo nho dem


def test_list_all_respects_max_pages():
    http = FakeHTTP().add("GET", r"/subtitles$", lambda call: FakeResponse(
        200, {"data": [{"subtitleId": int(call.params["page"])}], "pagination": {"pages": 50}}))
    c, _, _ = client(http, max_pages=4)
    assert len(c.list_all(1)) == 4


@pytest.mark.parametrize("lang,code,ok", [
    ("vietnamese", "vi", True), ("Vietnamese ", "vi", True), ("viet", "vi", True), ("vi-vn", "vi", True), ("vi_VN", "vi", True),
    ("czech", "cs", True), ("cze", "cs", True), ("cs-cz", "cs", True), ("english", "en", True), ("en-us", "en", True),
    ("chinese_simplified", "zh", True), ("indonesian", "id", True), ("", "vi", False), (None, "vi", False),
    ("vietnamese", "en", False), ("english", "cs", False)])
def test_lang_matches(lang, code, ok):
    assert subsource.lang_matches({"language": lang}, code) is ok


def test_lang_summary():
    s = [{"language": "english"}] * 3 + [{"language": "vietnamese"}] + [{"language": ""}] * 2
    assert subsource.lang_summary(s) == ["english(3)", "(trống)(2)", "vietnamese(1)"]


def test_download_pack_zip_cache_and_zip_slip(tmp_path):
    z = zip_bytes({"Show.S01E01.srt": "a", "sub/Show.S01E02.srt": "b", "../../evil.srt": "x", "..\\evil2.srt": "y"})
    http = FakeHTTP().add("GET", r"/subtitles/5/download", FakeResponse(200, content=z))
    c, _, _ = client(http, cache_dir=tmp_path / "packs")
    d = c.download_pack(5)
    names = sorted(os.path.relpath(p, d) for p in subsource.pack_files(d))
    assert names == sorted(["Show.S01E01.srt", os.path.join("sub", "Show.S01E02.srt"), "evil.srt", "evil2.srt"])
    assert not (tmp_path / "evil.srt").exists()  # khong thoat ra ngoai
    assert c.download_pack(5) == d and len(http.calls) == 1  # tai MOT lan
    assert c.is_downloaded(5)


def test_download_pack_rejects_rar(tmp_path):
    http = FakeHTTP().add("GET", r"/download", FakeResponse(200, content=b"Rar!\x1a\x07\x00rest"))
    c, _, job = client(http, cache_dir=tmp_path)
    assert c.download_pack(6) is None
    assert any("RAR" in m for _, m in job.lines)


def make_pack(tmp_path, names):
    d = tmp_path / "[pack] goi"
    d.mkdir()
    for n in names:
        (d / n).write_text("1\n00:00:01,000 --> 00:00:02,000\nx\n", encoding="utf-8", newline="")
    return d


def test_select_from_pack_right_episode_and_exclusions(tmp_path):
    d = make_pack(tmp_path, ["Show.S01E01.srt", "Show.S01E02.srt", "Show.S01E02.forced.srt", "Show.S01E02.SDH.srt",
                             "Show.S01E02.HI.srt", "Show.S01E03.ass"])
    vt = naming.title_tokens("Show.S01E02.1080p")
    assert subsource.select_from_pack(d, episodes.parse_episode("Show.S01E02"), vt).name == "Show.S01E02.srt"
    assert subsource.select_from_pack(d, episodes.parse_episode("Show.S01E03"), vt).name == "Show.S01E03.ass"
    assert subsource.select_from_pack(d, episodes.parse_episode("Show.S01E09"), vt) is None  # goi khong co tap nay


def test_select_from_pack_anime_absolute_numbers(tmp_path):
    """TĐ-02: bản .bat không nhận số tập anime → lấy file giống tên nhất (có thể sai tập)."""
    d = make_pack(tmp_path, [f"[SubGroup] One Piece - {i:04d}.srt" for i in range(641, 646)])
    vt = naming.title_tokens("[Judas] One Piece - 0643 [1080p]")
    f = subsource.select_from_pack(d, episodes.parse_episode("[Judas] One Piece - 0643 [1080p]"), vt)
    assert f.name == "[SubGroup] One Piece - 0643.srt"
    f2 = subsource.select_from_pack(d, episodes.parse_episode("One.Piece.S16E643"), vt)
    assert f2.name == "[SubGroup] One Piece - 0643.srt"


def test_select_from_pack_only_forced_returns_none_and_movie_by_name(tmp_path):
    d = make_pack(tmp_path, ["Movie.forced.srt", "Movie.SDH.srt"])
    assert subsource.select_from_pack(d, None, ["movie"]) is None
    d2 = tmp_path / "m"
    d2.mkdir()
    for n in ("Other.Film.srt", "Dune.Part.Two.2024.srt"):
        (d2 / n).write_text("x", encoding="utf-8", newline="")
    assert subsource.select_from_pack(d2, None, naming.title_tokens("Dune.Part.Two.2024.2160p")).name == "Dune.Part.Two.2024.srt"


def test_check_reports_rate_headers():
    http = FakeHTTP().add("GET", r"/movies/search", FakeResponse(200, {"data": []}, headers={
        "X-RateLimit-Limit": "60", "X-RateLimit-Remaining": "58", "Content-Type": "application/json"}))
    c, _, _ = client(http)
    st = c.check()
    assert st.ok and "58/60" in st.text
    http2 = FakeHTTP().add("GET", r".", FakeResponse(401, text="bad key"))
    c2, _, _ = client(http2)
    assert c2.check().ok is False
    assert subsource.SubSourceClient("").check().ok is None


# ---------------------------------------------------------------- ket qua do API that (2026-11-09, SPEC 7.E)
def test_rate_text_real_headers():
    from core.subsource import _rate_text

    h = {"Content-Type": "application/json", "X-RateLimit-Limit": "60", "X-RateLimit-Remaining": "58",
         "X-RateLimit-Reset": "2026-11-09T13:04:07.358Z", "X-RateLimit-Window": "60", "X-RateLimit-Limit-Hour": "1800",
         "X-RateLimit-Remaining-Hour": "1794", "X-RateLimit-Limit-Day": "7200", "X-RateLimit-Remaining-Day": "7194"}
    assert _rate_text(h) == "còn 58/60 lượt/phút, 1794/1800 lượt/giờ, 7194/7200 lượt/ngày"
    assert _rate_text({"RateLimit-Remaining": "4", "RateLimit-Limit": "5"}) == "còn 4/5 lượt"  # ten khac: du phong
    assert _rate_text({}) == ""


def test_real_language_names():
    """Tên ``language`` thật của SubSource: tiếng Trung = ``chinese_bg_code`` / ``big_5_code``, Bồ Đào Nha có cả
    ``brazilian_portuguese``."""
    from core.subsource import lang_matches

    for v in ("chinese_bg_code", "big_5_code", "chinese"):
        assert lang_matches({"language": v}, "zh"), v
    for v in ("portuguese", "brazilian_portuguese"):
        assert lang_matches({"language": v}, "pt"), v
    assert not lang_matches({"language": "chinese_bg_code"}, "ja")
    assert not lang_matches({"language": "farsi_persian"}, "pt")
    for code, v in (("vi", "vietnamese"), ("cs", "czech"), ("en", "english"), ("ko", "korean"), ("th", "thai"),
                    ("id", "indonesian"), ("ar", "arabic"), ("tr", "turkish"), ("nl", "dutch"), ("ru", "russian"),
                    ("it", "italian"), ("pl", "polish"), ("fr", "french"), ("de", "german"), ("es", "spanish"),
                    ("ja", "japanese")):
        assert lang_matches({"language": v}, code), (code, v)  # mọi tên trong kết quả dò khớp bảng ngôn ngữ
