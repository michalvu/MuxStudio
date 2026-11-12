"""TMDB (giả lập): key 32 ký tự / token eyJ, 401 tắt cả lượt, 404 im lặng, One Piece trùng tên, tập 643."""
from __future__ import annotations

import re

from core import tmdb, tools
from fakehttp import FakeHTTP, FakeResponse, Sleeper

KEY = "0123456789abcdef0123456789abcdef"
TOKEN = "eyJhbGciOiJIUzI1NiJ9.ZmFrZQ.ZmFrZQ"

# One Piece: anime 1999 (nhieu season) va live-action 2023 (1 season, 8 tap)
ANIME = {"id": 37854, "name": "One Piece", "original_name": "ワンピース", "first_air_date": "1999-10-20"}
LIVE = {"id": 111110, "name": "One Piece", "original_name": "One Piece", "first_air_date": "2023-08-31"}
ANIME_SEASONS = [{"season_number": 0, "episode_count": 30}] + [
    {"season_number": i + 1, "episode_count": n} for i, n in enumerate(
        [61, 16, 14, 39, 13, 52, 33, 35, 73, 45, 26, 14, 101, 56, 100, 121])]  # tong 16 season dau = 799


def client(http, **kw):
    job = tools.Job()
    return tmdb.TmdbClient(kw.get("key", KEY), http=http, sleep=Sleeper(), job=job), job


def test_api_key_in_query_and_bearer_header():
    http = FakeHTTP().add("GET", r"/configuration$", FakeResponse(200, {"images": {}}))
    c, _ = client(http)
    assert c.check().ok
    assert http.calls[0].params["api_key"] == KEY and "Authorization" not in http.calls[0].headers
    http2 = FakeHTTP().add("GET", r"/configuration$", FakeResponse(200, {"images": {}}))
    c2, _ = client(http2, key=TOKEN)
    st = c2.check()
    assert st.ok and "Bearer" in st.text
    assert http2.calls[0].headers["Authorization"] == f"Bearer {TOKEN}" and "api_key" not in http2.calls[0].params


def test_401_disables_tmdb_for_whole_run_and_reports_once():
    http = FakeHTTP().add("GET", r".", FakeResponse(401, {"status_message": "Invalid API key"}))
    c, job = client(http)
    assert c.get("/search/movie", {"query": "x"}) is None
    assert c.get("/search/movie", {"query": "y"}) is None
    assert len(http.calls) == 1  # khong goi lai sau 401
    assert sum("401" in m for _, m in job.lines) == 1
    assert c.check().ok is False


def test_404_quiet_when_probing_episode():
    http = FakeHTTP().add("GET", r"/tv/1/season/16/episode/643", FakeResponse(404, {"status_code": 34}))
    c, job = client(http)
    assert c.get("/tv/1/season/16/episode/643", quiet404=True) is None
    assert not job.lines
    c.get("/tv/1/season/16/episode/643")
    assert any("404" in m for _, m in job.lines)


def test_network_error_retries_with_backoff():
    import requests

    def boom(call):
        raise requests.ConnectionError("mat mang")

    http = FakeHTTP().add("GET", r".", boom)
    sl = Sleeper()
    c = tmdb.TmdbClient(KEY, retry=3, http=http, sleep=sl)
    assert c.get("/configuration") is None
    assert sl.calls == [2, 4] and len(http.calls) == 3


def test_search_retries_without_year():
    def h(call):
        if call.params.get("year"):
            return FakeResponse(200, {"results": []})
        return FakeResponse(200, {"results": [{"id": 1, "title": "X"}]})

    http = FakeHTTP().add("GET", r"/search/movie", h)
    c, _ = client(http)
    assert c.search("movie", "X", 2001) == [{"id": 1, "title": "X"}]
    assert [bool(x.params.get("year")) for x in http.calls] == [True, False]


def onepiece_http():
    http = FakeHTTP()
    http.add("GET", r"/search/tv", FakeResponse(200, {"results": [LIVE, ANIME]}))
    http.add("GET", r"/tv/111110$", FakeResponse(200, {"id": 111110, "seasons": [{"season_number": 1, "episode_count": 8}]}))
    http.add("GET", r"/tv/37854$", FakeResponse(200, {"id": 37854, "seasons": ANIME_SEASONS}))
    http.add("GET", r"/tv/(\d+)/external_ids", lambda call: FakeResponse(
        200, {"imdb_id": {"37854": "tt0388629", "111110": "tt11737520"}[re.search(r"/tv/(\d+)/", call.url).group(1)]}))
    return http


def test_one_piece_duplicate_names_pick_series_that_has_episode():
    http = onepiece_http()
    c, job = client(http)
    show = c.find_show("One Piece", None, 16, 643)
    assert show["id"] == 37854
    assert c.imdb_id("One Piece", None, True, 16, 643) == "tt0388629"
    # live-action co S01E05 -> ca hai co season 1; anime co nhieu tap hon nhung ca hai deu >= 5 -> diem bang -> giu cai dau
    assert c.find_show("One Piece", None, 1, 5)["id"] == 111110
    assert any("cùng tên" in m for _, m in job.lines)


def test_absolute_episode_643_to_season():
    http = onepiece_http()
    c, _ = client(http)
    # cong don: 61+16+14+39+13+52+33+35+73+45+26+14+101+56 = 578 -> tap 643 = S15E65 (season 15 co 100 tap)
    assert c.absolute_to_season(37854, 643) == (15, 65)
    assert c.absolute_to_season(37854, 1) == (1, 1)
    assert c.absolute_to_season(37854, 99999) is None


def test_imdb_movie_exact_title_and_year():
    http = FakeHTTP()
    http.add("GET", r"/search/movie", FakeResponse(200, {"results": [
        {"id": 1, "title": "Dune", "original_title": "Dune", "release_date": "1984-12-14"},
        {"id": 2, "title": "Dune", "original_title": "Dune", "release_date": "2021-09-15"}]}))
    http.add("GET", r"/movie/2/external_ids", FakeResponse(200, {"imdb_id": "tt1160419"}))
    c, _ = client(http)
    assert c.imdb_id("Dune", 2021, False) == "tt1160419"
    n = len(http.calls)
    assert c.imdb_id("Dune", 2021, False) == "tt1160419" and len(http.calls) == n  # bo nho dem trong luot


def test_official_title_keeps_punctuation_and_reuses_cached_search():
    """Tên rút từ tên file mất dấu hai chấm; TMDB trả tên chính thức có đủ dấu câu (để hỏi Titulky — so khớp nguyên văn)."""
    http = FakeHTTP()
    http.add("GET", r"/search/movie", FakeResponse(200, {"results": [
        {"id": 122, "title": "The Lord of the Rings: The Return of the King", "original_title": "The Lord of the Rings: The Return of the King",
         "release_date": "2003-12-17"},
        {"id": 123, "title": "The Lord of the Rings", "original_title": "The Lord of the Rings", "release_date": "1978-11-15"}]}))
    http.add("GET", r"/movie/122/external_ids", FakeResponse(200, {"imdb_id": "tt0167260"}))
    http.add("GET", r"/search/tv", FakeResponse(200, {"results": [
        {"id": 7, "name": "Marvel's Agents of S.H.I.E.L.D.", "original_name": "Marvel's Agents of S.H.I.E.L.D.", "first_air_date": "2013-09-24"}]}))
    http.add("GET", r"/tv/7$", FakeResponse(200, {"seasons": [{"season_number": 1, "episode_count": 22}]}))
    c, _ = client(http)
    assert c.imdb_id("The Lord of the Rings The Return of the King", 2003, False) == "tt0167260"
    n = len(http.calls)
    assert c.official_title("The Lord of the Rings The Return of the King", 2003, False) == "The Lord of the Rings: The Return of the King"
    assert len(http.calls) == n  # dung lai ket qua tim da nho dem: khong ton them luot goi
    assert c.official_title("Marvels Agents of S H I E L D", 2013, True, 1, 5) == "Marvel's Agents of S.H.I.E.L.D."
    assert c.official_title("", 2003, False) is None
    empty = FakeHTTP()
    empty.add("GET", r"/search/movie", FakeResponse(200, {"results": []}))
    c2, _ = client(empty)
    assert c2.official_title("Phim Khong Co", None, False) is None


def test_no_key():
    c = tmdb.TmdbClient("", http=FakeHTTP())
    assert c.check().ok is None and c.imdb_id("x", None, False) is None and c.official_title("x", None, False) is None
