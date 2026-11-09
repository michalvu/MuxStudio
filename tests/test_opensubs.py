"""OpenSubtitles (giả lập) — User-Agent, VIP base_url + token, 401 đăng nhập lại, quota."""
from __future__ import annotations

import pytest

from core import opensubs, tools
from fakehttp import FakeHTTP, FakeResponse, Sleeper

LOGIN_VIP = {"token": "tok-1", "base_url": "vip-api.opensubtitles.com",
             "user": {"allowed_downloads": 1000, "vip": True, "level": "VIP Member"}}


def client(http, **kw):
    sl = Sleeper()
    job = tools.Job()
    c = opensubs.OpenSubtitlesClient("demo-os-key", kw.get("user", "nguoidung"), kw.get("pw", "matkhau"),
                                     "MyApp v1.0.0", 1100, 50, 5, http, sl, job, kw.get("tmp"))
    return c, sl, job


def base_http():
    http = FakeHTTP()
    http.add("POST", r"api\.opensubtitles\.com/api/v1/login$", FakeResponse(200, LOGIN_VIP))
    http.add("GET", r"vip-api\.opensubtitles\.com/api/v1/infos/user$",
             FakeResponse(200, {"data": {"remaining_downloads": 987, "allowed_downloads": 1000}}))
    return http


def test_login_vip_switches_host_and_sends_token_everywhere():
    http = base_http()
    http.add("GET", r"vip-api\.opensubtitles\.com/api/v1/subtitles", FakeResponse(200, {"data": [{"id": "1"}]}))
    c, _, job = client(http)
    assert c.login()
    assert c.host == "vip-api.opensubtitles.com" and c.vip and c.remaining == 987
    c.search("tt0133093", None, None, "vi")
    for call in http.calls[1:]:
        assert call.url.startswith("https://vip-api.opensubtitles.com/") and call.headers["Authorization"] == "Bearer tok-1"
    for call in http.calls:
        assert call.headers["Api-Key"] == "demo-os-key" and call.headers["User-Agent"] == "MyApp v1.0.0"
    assert http.calls_to("/subtitles")[0].params["imdb_id"] == "0133093"  # bo 'tt'
    assert any("VIP" in m for _, m in job.lines)


def test_check_text():
    c, _, _ = client(base_http())
    st = c.check()
    assert st.ok and st.text == "OpenSubtitles: đăng nhập VIP, máy chủ vip-api, còn 987/1000 lượt tải"


def test_401_relogin_at_most_twice_then_disable():
    http = base_http()
    http.add("GET", r"/subtitles", FakeResponse(401, text="invalid token"))
    c, _, job = client(http)
    c.login(quiet=True)
    assert c.search("tt1", None, None, "en") == []
    assert len(http.calls_to("/login", "POST")) == 3  # 1 + 2 lan dang nhap lai
    assert c.disabled and c.relogins == 2


def test_401_relogin_succeeds_and_retries():
    http = base_http()
    http.add("GET", r"/subtitles", [FakeResponse(401), FakeResponse(200, {"data": [{"id": "x"}]})])
    c, _, _ = client(http)
    c.login(quiet=True)
    assert c.search("tt1", None, None, "en") == [{"id": "x"}]
    assert not c.disabled and c.relogins == 1


def test_403_user_agent_block_disables_source():
    http = FakeHTTP().add("POST", r"/login", FakeResponse(403, text='{"message":"kong-user-agent-block: User-Agent not allowed"}'))
    c, _, job = client(http)
    assert not c.login()
    assert c.disabled and any("User-Agent" in m for _, m in job.lines)


def test_429_waits_5s():
    http = base_http()
    http.add("GET", r"/subtitles", [FakeResponse(429), FakeResponse(200, {"data": [{"id": "y"}]})])
    c, sl, _ = client(http)
    c.login(quiet=True)
    assert c.search("tt1", None, None, "en") == [{"id": "y"}] and 5 in sl.calls


def test_no_account_can_search_not_download():
    http = FakeHTTP().add("GET", r"/subtitles", FakeResponse(200, {"data": [{"id": "1"}]}))
    c, _, job = client(http, user="", pw="")
    assert not c.login()
    assert not c.disabled and c.search("tt1", None, None, "en")
    assert c.download({"attributes": {"files": [{"file_id": 1}]}}) is None
    assert any("không tải được" in m.lower() for _, m in job.lines)


def test_search_three_attempts_and_params():
    http = base_http()
    seen = []

    def h(call):
        seen.append(dict(call.params))
        if "query" in call.params:
            return FakeResponse(200, {"data": [{"id": "theo-ten"}]})
        return FakeResponse(200, {"data": []})

    http.add("GET", r"/subtitles", h)
    c, _, _ = client(http)
    c.login(quiet=True)
    assert c.search("tt1234", 2, 5, "vi", "Show", 2019) == [{"id": "theo-ten"}]
    assert seen[0]["order_by"] == "download_count" and seen[0]["season_number"] == "2" and seen[0]["episode_number"] == "5"
    assert "order_by" not in seen[1] and seen[1]["imdb_id"] == "1234"
    assert "imdb_id" not in seen[2] and seen[2]["query"] == "Show" and seen[2]["year"] == "2019"


@pytest.mark.parametrize("remaining,ok,level", [(100, True, "ok"), (50, True, "warn"), (6, True, "warn"),
                                                (5, False, "stop"), (0, False, "stop"), (-1, True, "unknown")])
def test_quota_thresholds(remaining, ok, level):
    c, _, job = client(FakeHTTP())
    c.remaining, c.allowed = remaining, 1000
    assert c.quota_level == level
    assert c.quota_ok() is ok
    if level == "warn":
        assert any("Chú ý" in m for _, m in job.lines)
    if level == "stop":
        assert c.disabled


def test_download_flow_updates_quota(tmp_path):
    http = base_http()
    http.add("POST", r"vip-api.*/download$", FakeResponse(200, {"link": "https://dl.opensubtitles.com/f/abc", "remaining": 49,
                                                                "reset_time": "23 hours"}))
    http.add("GET", r"dl\.opensubtitles\.com/f/abc", FakeResponse(200, content=b"1\r\n00:00:01,000 --> 00:00:02,000\r\nHi\r\n" * 5))
    c, _, job = client(http, tmp=tmp_path)
    c.login()
    p = c.download({"attributes": {"files": [{"file_id": 777}]}})
    assert p is not None and p.read_bytes().startswith(b"1\r\n")
    assert http.calls_to("/download", "POST")[0].json == {"file_id": 777}
    assert c.remaining == 49 and "23 hours" in c.reset_msg
    assert any("Chú ý" in m for _, m in job.lines)  # <= 50 -> canh bao


def test_download_rejects_empty_file(tmp_path):
    http = base_http()
    http.add("POST", r"/download$", FakeResponse(200, {"link": "https://dl.x/f", "remaining": 900}))
    http.add("GET", r"dl\.x/f", FakeResponse(200, content=b"tiny"))
    c, _, _ = client(http, tmp=tmp_path)
    c.login(quiet=True)
    assert c.download({"attributes": {"files": [{"file_id": 1}]}}) is None


def test_download_stops_when_quota_low(tmp_path):
    c, _, _ = client(base_http(), tmp=tmp_path)
    c.login(quiet=True)
    c.remaining = 4
    assert c.download({"attributes": {"files": [{"file_id": 1}]}}) is None and c.disabled


@pytest.mark.parametrize("attrs,kw,ok", [
    ({"files": [1]}, {}, True),
    ({"files": []}, {}, False),
    ({"files": [1], "hearing_impaired": True}, {}, False),
    ({"files": [1], "hearing_impaired": True}, {"exclude_sdh": False}, True),
    ({"files": [1], "foreign_parts_only": True}, {}, False),
    ({"files": [1], "machine_translated": True}, {}, False),
    ({"files": [1], "ai_translated": True}, {"exclude_machine": False}, True),
])
def test_usable(attrs, kw, ok):
    assert opensubs.usable({"attributes": attrs}, **kw) is ok


# ---------------------------------------------------------------- ket qua do API that (2026-11-09, SPEC 7.E)
def test_language_codes_match_real_server():
    """``/infos/languages`` thật KHÔNG có ``zh``, ``pt`` (có ``zh-cn``, ``zh-tw``, ``pt-pt``, ``pt-br``)."""
    assert opensubs.os_languages("zh") == "zh-cn,zh-tw" and opensubs.os_languages("pt") == "pt-br,pt-pt"
    assert opensubs.os_languages("vi") == "vi" and opensubs.os_languages("CS") == "cs"
    http = base_http()
    http.add("GET", r"/subtitles", FakeResponse(200, {"data": [{"id": "1"}]}))
    c, _, _ = client(http)
    c.login(quiet=True)
    c.search("tt0133093", None, None, "zh")
    call = http.calls_to("/subtitles")[0]
    assert call.params["languages"] == "zh-cn,zh-tw"
    assert list(call.params) == sorted(call.params)  # theo chu cai: may chu khong chuyen huong 301


def test_absolute_episode_without_season():
    """Dò thật: ``query=One Piece&episode_number=643`` (không season) → 0 kết quả ⇒ không hỏi theo tên; có IMDb thì
    vẫn thử IMDb (2 lượt) nhưng bỏ lượt tìm theo tên."""
    http = base_http()
    http.add("GET", r"/subtitles", FakeResponse(200, {"data": []}))
    c, _, job = client(http)
    c.login(quiet=True)
    assert c.search(None, None, 643, "en", "One Piece") == []
    assert http.calls_to("/subtitles") == [] and any("không rõ season" in m for _, m in job.lines)
    assert c.search("tt0388629", None, 643, "en", "One Piece") == []
    calls = http.calls_to("/subtitles")
    assert len(calls) == 2 and all("query" not in x.params and x.params["episode_number"] == "643" for x in calls)
    # co season (TMDB doi so tuyet doi -> S16E01): du 3 luot nhu .bat
    c.search("tt0388629", 16, 1, "en", "One Piece")
    assert len(http.calls_to("/subtitles")) == 5 and "query" in http.calls_to("/subtitles")[-1].params
