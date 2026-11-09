"""Đặt tên chuẩn Jellyfin theo TMDB / TVDB (§6.10) với API giả: phim lẻ, phim bộ, "Cần chọn", One Piece trùng tên,
số tập tuyệt đối 643, TMDB 401 tắt cả lượt, TVDB đăng nhập lại khi 401, phân trang, tên tập theo ngôn ngữ."""
from __future__ import annotations

import os

from core import config as C
from core import naming, tmdb, tools, tvdb
from fakehttp import FakeHTTP, FakeResponse, Sleeper
from test_tmdb import ANIME, ANIME_SEASONS, KEY, LIVE

CFG = C.Config({})
DUNE = {"id": 693134, "title": "Dune: Part Two", "original_title": "Dune: Part Two", "release_date": "2024-02-27"}
DUNE21 = {"id": 438631, "title": "Dune", "original_title": "Dune", "release_date": "2021-09-15"}
SHOW = {"id": 1399, "name": "Game of Thrones", "original_name": "Game of Thrones", "first_air_date": "2011-04-17"}


def tm(http, cfg_lang="en-US"):
    job = tools.Job()
    return tmdb.TmdbClient(KEY, cfg_lang, 3, http, Sleeper(), job), job


def tmdb_http():
    h = FakeHTTP()
    h.add("GET", r"/search/movie$", lambda c: FakeResponse(200, {"results": [DUNE, DUNE21] if "Dune" in c.params["query"] else []}))
    h.add("GET", r"/search/tv$", lambda c: FakeResponse(200, {"results": [ANIME, LIVE] if c.params["query"] == "One Piece" else
                                                              [SHOW] if "Thrones" in c.params["query"] else []}))
    h.add("GET", r"/tv/37854$", FakeResponse(200, {**ANIME, "seasons": ANIME_SEASONS}))
    h.add("GET", r"/tv/111110$", FakeResponse(200, {**LIVE, "seasons": [{"season_number": 1, "episode_count": 8}]}))
    h.add("GET", r"/tv/1399$", FakeResponse(200, {**SHOW, "seasons": [{"season_number": 1, "episode_count": 10}]}))
    h.add("GET", r"/tv/1399/season/1/episode/5$", FakeResponse(200, {"name": "The Wolf and the Lion"}))
    # One Piece tap 643 = season 15 tap 65 (tong 14 season dau = 578)
    h.add("GET", r"/tv/37854/season/15/episode/65$", FakeResponse(200, {"name": "Một tập: Hody/Jones?"}))
    return h


def test_movie_exact_tmdb_name_and_folder():
    c, _ = tm(tmdb_http())
    o = naming.resolve_output_name("Dune.Part.Two.2024.2160p.WEB-DL", "tmdb", {"tmdb": c}, CFG, {})
    assert (o.status, o.base, o.rel_dir, o.db) == ("ok", "Dune - Part Two (2024) [tmdbid-693134]", "Dune - Part Two (2024) [tmdbid-693134]", "tmdb")
    cfg = C.Config({"TmdbCreateFolder": False})
    assert naming.resolve_output_name("Dune.Part.Two.2024.2160p", "tmdb", {"tmdb": c}, cfg, {}).rel_dir == ""


def test_movie_not_exact_needs_choice_then_choose():
    c, _ = tm(tmdb_http())
    cache: dict = {}
    o = naming.resolve_output_name("Dune.Part.2.2024.1080p", "tmdb", {"tmdb": c}, CFG, cache)
    assert o.status == "need_choice" and [x.id for x in o.candidates] == [693134, 438631]
    assert o.candidates[0].describe("tmdb") == "Dune: Part Two (2024)  tmdbid-693134"
    o2 = naming.choose_candidate(c, "Dune.Part.2.2024.1080p", 693134, o, CFG, cache)
    assert o2.status == "ok" and o2.base == "Dune - Part Two (2024) [tmdbid-693134]"
    # TmdbConfirm tat -> lay ket qua dau
    o3 = naming.resolve_output_name("Dune.Part.2.2024.1080p", "tmdb", {"tmdb": c}, C.Config({"TmdbConfirm": False}), {})
    assert o3.status == "ok" and "693134" in o3.base


def test_movie_not_found_keeps_name():
    c, _ = tm(tmdb_http())
    o = naming.resolve_output_name("Khong.Co.Phim.Nay.2020", "tmdb", {"tmdb": c}, CFG, {})
    assert (o.status, o.base) == ("keep", "Khong.Co.Phim.Nay.2020") and "không tìm thấy" in o.note


def test_tv_episode_name_and_season_folder():
    c, _ = tm(tmdb_http())
    o = naming.resolve_output_name("Game.of.Thrones.S01E05.1080p", "tmdb", {"tmdb": c}, CFG, {})
    assert o.status == "ok" and o.kind == "tv"
    assert o.base == "Game of Thrones (2011) - S01E05 - The Wolf and the Lion"
    assert o.rel_dir == os.path.join("Game of Thrones (2011) [tmdbid-1399]", "Season 01")


def test_one_piece_duplicate_names_and_absolute_643():
    """Hai series 'One Piece' (anime 1999 / live-action 2023): chọn series CÓ tập đó; số tuyệt đối 643 → S16E65."""
    c, job = tm(tmdb_http())
    o = naming.resolve_output_name("[Judas] One Piece - 0643 [1080p]", "tmdb", {"tmdb": c}, CFG, {})
    assert o.status == "ok"
    assert o.base == "One Piece (1999) - S15E65 - Một tập - HodyJones"
    assert o.rel_dir.endswith(os.path.join("One Piece (1999) [tmdbid-37854]", "Season 15"))
    assert o.episode_note.startswith("Số tập tuyệt đối: tap #643 → TMDB S15E65")
    o2 = naming.resolve_output_name("One.Piece.S16E643.1080p", "tmdb", {"tmdb": c}, CFG, {})
    assert o2.base.startswith("One Piece (1999) - S15E65")
    o3 = naming.resolve_output_name("One Piece - 0643", "tmdb", {"tmdb": c}, C.Config({"TvAbsoluteToSeason": False}), {})
    assert " - S01E643 - " in o3.base and "Season 01" in o3.rel_dir  # khong bao gio Season 00


def test_episode_404_is_quiet_and_keeps_code():
    c, job = tm(tmdb_http())
    o = naming.resolve_output_name("Game.of.Thrones.S01E07", "tmdb", {"tmdb": c}, CFG, {})
    assert o.base == "Game of Thrones (2011) - S01E07" and "không có S01E07" in o.episode_note
    assert not any("404" in m for _, m in job.lines)


def test_tmdb_401_disables_for_run_and_keeps_names():
    h = FakeHTTP().add("GET", r".", FakeResponse(401, {"status_message": "bad"}))
    c, job = tm(h)
    cache: dict = {}
    for n in ("Dune.2021", "Avatar.2009", "Show.S01E01"):
        o = naming.resolve_output_name(n, "tmdb", {"tmdb": c}, CFG, cache)
        assert o.status == "keep" and o.base == n
    assert len(h.calls) == 1 and sum("401" in m for _, m in job.lines) == 1


def test_series_choice_applies_to_all_episodes():
    h = tmdb_http()
    h.add("GET", r"/search/tv$", FakeResponse(200, {"results": [SHOW, {**SHOW, "id": 9, "name": "Thrones"}]}))
    c, _ = tm(h)
    cache: dict = {}
    o1 = naming.resolve_output_name("GoT.S01E05", "tmdb", {"tmdb": c}, CFG, cache)
    assert o1.status == "need_choice"
    naming.choose_candidate(c, "GoT.S01E05", 1399, o1, CFG, cache)
    o2 = naming.resolve_output_name("GoT.S01E06", "tmdb", {"tmdb": c}, CFG, cache)
    assert o2.status == "ok" and o2.base.startswith("Game of Thrones (2011) - S01E06")


# ---------------------------------------------------------------- TVDB
def tvdb_http(token_expire_once=False):
    h = FakeHTTP()
    state = {"logins": 0, "expired": token_expire_once}

    def login(call):
        state["logins"] += 1
        assert call.json.get("apikey") == "tvdb-demo-key"
        return FakeResponse(200, {"data": {"token": f"tok{state['logins']}"}})

    def search(call):
        if state["expired"]:
            state["expired"] = False
            return FakeResponse(401, {"status": "failure"})
        if "type=series" in call.url:
            return FakeResponse(200, {"data": [
                {"tvdb_id": "81797", "name": "One Piece", "year": "1999", "translations": {"eng": "One Piece", "vie": "Đảo Hải Tặc"}},
                {"tvdb_id": "392276", "name": "One Piece", "year": "2023"}]})
        return FakeResponse(200, {"data": [{"tvdb_id": "1234", "name": "Dune: Part Two", "year": "2024"}]})

    def eps(call):
        sid = call.url.split("/series/")[1].split("/")[0]
        page = int(call.url.split("page=")[1])
        if sid == "392276":
            return FakeResponse(200, {"data": {"episodes": [{"id": 1, "seasonNumber": 1, "number": i, "absoluteNumber": i} for i in range(1, 9)]},
                                      "links": {"next": None}})
        # anime: 2 trang, tap 643 o trang 2
        lst = [{"id": 10000 + n, "seasonNumber": 15 if n > 578 else 1, "number": n - 578 if n > 578 else n,
                "absoluteNumber": n, "nameTranslations": ["eng", "jpn"]} for n in range(1 + 500 * page, 501 + 500 * page)]
        return FakeResponse(200, {"data": {"episodes": lst}, "links": {"next": "x" if page == 0 else None}})

    h.add("POST", r"/login$", login)
    h.add("GET", r"/search\?", search)
    h.add("GET", r"/series/\d+/episodes/default\?page=\d+$", eps)
    h.add("GET", r"/episodes/10643/translations/eng$", FakeResponse(200, {"data": {"name": "Tên tập 643"}}))
    return h, state


def tv(h, lang="eng"):
    job = tools.Job()
    return tvdb.TvdbClient("tvdb-demo-key", "", lang, "default", 3, h, Sleeper(), job), job


def test_tvdb_absolute_number_pagination_and_episode_name():
    h, state = tvdb_http()
    c, _ = tv(h)
    o = naming.resolve_output_name("[Judas] One Piece - 0643 [1080p]", "tvdb", {"tvdb": c}, CFG, {})
    assert o.status == "ok" and o.base == "One Piece (1999) - S15E65 - Tên tập 643"
    assert os.path.join("One Piece (1999) [tvdbid-81797]", "Season 15") == o.rel_dir
    assert state["logins"] == 1
    assert len(h.calls_to(r"/series/81797/episodes")) == 2  # theo links.next
    assert h.calls_to(r"/episodes/10643/translations/vie") == []  # 'vie' khong co trong nameTranslations -> bo qua


def test_tvdb_local_name_and_relogin_once_on_401():
    h, state = tvdb_http(token_expire_once=True)
    c, _ = tv(h, lang="vie")
    res = c.search_series("One Piece", None)
    assert res[0]["name"] == "Đảo Hải Tặc" and res[0]["original_name"] == "One Piece"
    assert state["logins"] == 2  # dang nhap lai MOT lan sau 401
    assert c.check().ok


def test_tvdb_login_failure_status():
    h = FakeHTTP().add("POST", r"/login$", FakeResponse(401, {"status": "failure"}))
    c, job = tv(h)
    st = c.check()
    assert st.ok is False and st.text == "TVDB: đăng nhập lỗi" and not c.usable


def test_name_fallback_other_only_when_enabled():
    h401 = FakeHTTP().add("GET", r".", FakeResponse(401, {}))
    tmdb_c, _ = tm(h401)
    h, _ = tvdb_http()
    tvdb_c, _ = tv(h)
    dbs = {"tmdb": tmdb_c, "tvdb": tvdb_c}
    o = naming.resolve_output_name("Dune.Part.Two.2024", "tmdb", dbs, CFG, {})
    assert o.status == "keep"
    o2 = naming.resolve_output_name("Dune.Part.Two.2024", "tmdb", dbs, C.Config({"NameFallbackOther": True}), {})
    assert o2.status == "ok" and o2.base == "Dune - Part Two (2024) [tvdbid-1234]" and "thử TVDB" in o2.note


def test_none_keeps_original_name():
    o = naming.resolve_output_name("Bat.Ky.2020", "none", {}, CFG)
    assert (o.status, o.base, o.rel_dir) == ("keep", "Bat.Ky.2020", "")


# ---------------------------------------------------------------- "Tim lai voi ten..." (SPEC muc 6 #20, GD5)
def test_search_candidates_movie_then_choose():
    c, _ = tm(tmdb_http())
    cache: dict = {}
    o = naming.resolve_output_name("Kho.Doan.2021.1080p", "tmdb", {"tmdb": c}, CFG, cache)
    assert o.status == "keep" and not o.candidates
    s = naming.search_candidates(c, "Kho.Doan.2021.1080p", o, "Dune", 2021)
    assert s.status == "need_choice" and s.base == "Kho.Doan.2021.1080p" and s.query_key == o.query_key
    assert [x.id for x in s.candidates] == [693134, 438631] and s.note == "TMDB: kết quả tìm 'Dune' (2021) — chọn một kết quả."
    o2 = naming.choose_candidate(c, "Kho.Doan.2021.1080p", 438631, s, CFG, cache)
    assert o2.status == "ok" and o2.base == "Dune (2021) [tmdbid-438631]"
    # file khac cung khoa dung luon lua chon
    assert naming.resolve_output_name("Kho.Doan.2021.720p", "tmdb", {"tmdb": c}, CFG, cache).base == "Dune (2021) [tmdbid-438631]"
    none = naming.search_candidates(c, "x", o, "Không Có Gì", None)
    assert none.status == "need_choice" and not none.candidates and "không tìm thấy 'Không Có Gì'" in none.note


def test_mux_search_name_tv_applies_to_every_episode(movie_dir, monkeypatch):
    """Series viết tắt 'GoT' không tìm thấy → "Tìm lại với tên…" 'Game of Thrones' → chọn → mọi tập của series đó
    đều đặt tên theo lựa chọn này."""
    from core import mux
    from test_mux import A, J, Lib, V

    lib = Lib(movie_dir, monkeypatch, secrets={"TmdbApiKey": KEY}, http=tmdb_http())
    e5 = lib.add("GoT.S01E05.1080p", J(V(), A(1)))
    e6 = lib.add("GoT.S01E06.1080p", J(V(), A(1)))
    p5 = lib.plan(e5, "tmdb")
    assert p5.name.status == "keep" and "không tìm thấy series 'GoT'" in p5.name.note
    assert lib.s.search_name(p5, "Game of Thrones") is not None
    p5 = lib.s.plan_file(e5)
    assert p5.status == "need_choice" and [x.id for x in p5.name.candidates] == [1399]
    lib.s.choose_name(p5, 1399)
    p5, p6 = lib.s.plan_file(e5), lib.s.plan_file(e6)
    assert p5.status == "ok" and p5.out_base == "Game of Thrones (2011) - S01E05 - The Wolf and the Lion"
    assert p6.status == "ok" and p6.out_base == "Game of Thrones (2011) - S01E06"
    assert p5.out_path.parent == p6.out_path.parent
    # "Khong doi ten" -> khong tim duoc
    lib.s.db = "none"
    assert lib.s.search_name(p5, "Game of Thrones") is None
    assert isinstance(lib.s, mux.MuxSession)
