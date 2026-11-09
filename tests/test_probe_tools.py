"""Script dò API + nhập từ .bat: chạy với API giả, KHÔNG BAO GIỜ lộ bí mật trong kết quả."""
from __future__ import annotations

import pytest

from conftest import REFERENCE
from core import config as C
from core import secrets as S
from fakehttp import FakeHTTP, FakeResponse, zip_bytes
from tools import nhap_tu_bat, probe_all, probe_common, probe_opensubs, probe_subsource, probe_tmdb, probe_tvdb

SS_KEY = "ss-demo-key-1234567890abcdef"
OS_KEY = "os-demo-key-0987654321"
TOKEN = "eyJ0b2tlbi1kYW5nLW5oYXA.fake.sig"


def reporter(tmp_path, secrets):
    return probe_common.Reporter(tmp_path / "out.txt", S.Redactor(secrets.values()))


def test_reporter_masks_everything(tmp_path):
    rep = reporter(tmp_path, {"TmdbApiKey": "0123456789abcdef0123456789abcdef"})
    rep.line("GET https://api.themoviedb.org/3/x?api_key=0123456789abcdef0123456789abcdef&q=1")
    rep.line('{"token": "abc.def.ghi", "password": "matkhau"}')
    rep.line("Authorization: Bearer eyJhbGci.payload.sig")
    rep.close()
    out = (tmp_path / "out.txt").read_text(encoding="utf-8")
    assert "0123456789abcdef0123456789abcdef" not in out
    assert "abc.def.ghi" not in out and "matkhau" not in out and "eyJhbGci" not in out


def test_probe_subsource_with_fake_api(tmp_path, monkeypatch):
    http = FakeHTTP()
    http.add("GET", r"/movies/search", lambda c: FakeResponse(200, {"data": [
        {"movieId": 5, "title": "The Matrix", "season": None}]}, headers={"X-RateLimit-Remaining": "58", "X-RateLimit-Limit": "60"}))
    http.add("GET", r"/subtitles$", FakeResponse(200, {"data": [
        {"subtitleId": 1, "language": "english", "releaseInfo": ["x"], "framerate": "", "hearingImpaired": None,
         "foreignParts": None, "files": 1, "downloads": 3, "preview": "1\n00:00:01,000 --> 00:00:02,000\nhi"}],
        "pagination": {"pages": 1}}))
    http.add("GET", r"/subtitles/1/download", FakeResponse(200, content=zip_bytes({"a.srt": "x"})))
    monkeypatch.setattr(probe_subsource, "http_session", lambda *a: http)
    monkeypatch.setattr(probe_subsource, "pause", lambda s: None)
    secrets = {"SubSourceApiKey": SS_KEY}
    rep = reporter(tmp_path, secrets)
    probe_subsource.run(rep, C.Config(), secrets)
    rep.close()
    out = (tmp_path / "out.txt").read_text(encoding="utf-8")
    assert SS_KEY not in out
    assert any("key hợp lệ" in c for c in rep.conclusions)
    assert any("đủ các trường" in c for c in rep.conclusions)
    assert any("tải: ZIP" in c for c in rep.conclusions)


def test_probe_opensubs_masks_token_and_password(tmp_path, monkeypatch):
    http = FakeHTTP()
    http.add("GET", r"/infos/formats", FakeResponse(200, {"data": {}}))
    http.add("GET", r"/infos/languages", FakeResponse(200, {"data": [{"language_code": c} for c in ("en", "vi", "cs", "zh-cn")]}))
    http.add("POST", r"/login", FakeResponse(200, {"token": TOKEN, "base_url": "vip-api.opensubtitles.com",
                                                   "user": {"vip": True, "allowed_downloads": 1000}}))
    http.add("GET", r"/infos/user", FakeResponse(200, {"data": {"remaining_downloads": 987, "allowed_downloads": 1000}}))
    http.add("GET", r"/subtitles", FakeResponse(200, {"total_count": 1, "data": [{"attributes": {
        "release": "x", "fps": 23.976, "download_count": 1, "hearing_impaired": False, "foreign_parts_only": False,
        "machine_translated": False, "ai_translated": False, "files": [{"file_id": 1}]}}]}))
    monkeypatch.setattr(probe_opensubs, "http_session", lambda *a: http)
    monkeypatch.setattr(probe_opensubs, "pause", lambda s: None)
    secrets = {"OsApiKey": OS_KEY, "OsUsername": "nguoidung-that", "OsPassword": "matkhau-that"}
    rep = reporter(tmp_path, secrets)
    probe_opensubs.run(rep, C.Config(), secrets)
    rep.close()
    out = (tmp_path / "out.txt").read_text(encoding="utf-8")
    for s in (OS_KEY, TOKEN, "nguoidung-that", "matkhau-that"):
        assert s not in out
    assert not http.calls_to(r"/download")  # mac dinh KHONG tai (ton quota)
    assert any("VIP" in c and "vip-api" in c for c in rep.conclusions)
    assert any("987/1000" in c for c in rep.conclusions)
    assert any("KHÔNG có" in c and "zh" in c for c in rep.conclusions)


def test_probes_without_keys_skip(tmp_path):
    rep = reporter(tmp_path, {})
    for mod in (probe_subsource, probe_tmdb, probe_tvdb):
        mod.run(rep, C.Config(), {})
    probe_opensubs.run(rep, C.Config(), {})
    assert len(rep.conclusions) == 4 and all("chưa có key" in c for c in rep.conclusions)


def test_probe_all_writes_file(tmp_path, monkeypatch):
    out = tmp_path / "ket_qua_do_api.txt"
    monkeypatch.setattr(probe_all, "OUT", out)
    assert probe_all.main() == 0
    txt = out.read_text(encoding="utf-8")
    assert "TÓM TẮT" in txt and "chưa có key" in txt
    assert txt.count("KHÔNG CÓ KEY NÀO") == 2  # dau file + trong tom tat: chi cach chay lai cho dung


@pytest.mark.skipif(not (REFERENCE / "SubFetch_v30.bat").exists(), reason="thiếu reference/")
def test_nhap_tu_bat_reference_folder(config_dir, capsys):
    """Thư mục reference\\ (bản ĐÃ XOÁ key): vẫn nhập cấu hình, nhưng báo rõ chưa có key và trả 2 để hỏi lại."""
    assert nhap_tu_bat.main([str(REFERENCE)]) == 2
    cfg = C.load_config()
    assert cfg["MkvToolNixDir"] == r"C:\Users\fenha\Desktop\mkvtoolnix" and cfg["Languages"] == ["vi", "en", "cs"]
    assert (config_dir / "config.json").exists()
    out = capsys.readouterr().out
    assert "(trống)" in out and "CHƯA CÓ KEY NÀO" in out and "ĐÃ XOÁ KEY" in out


def test_nhap_tu_bat_puts_secrets_in_keyring(tmp_path, memory_keyring, capsys):
    bat = tmp_path / "SubFetch_moi.bat"
    bat.write_text("<# :\n#>\n$SubSourceApiKey = '" + SS_KEY + "'\n$OsPassword = 'matkhau-that'\n", encoding="utf-8")
    assert nhap_tu_bat.main([str(tmp_path)]) == 0
    assert S.store.get("SubSourceApiKey") == SS_KEY and S.store.get("OsPassword") == "matkhau-that"
    out = capsys.readouterr().out
    assert SS_KEY not in out and "matkhau-that" not in out


def test_nhap_tu_bat_no_files(tmp_path):
    assert nhap_tu_bat.main([str(tmp_path / "khong-co")]) == 1
