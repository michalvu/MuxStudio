"""§6.1 tiến trình con + đường dẫn; nhận diện track MKV; chọn track phụ đề gốc."""
from __future__ import annotations

import os
import subprocess
import sys
import threading
import time

import pytest

from core import config as C
from core import probe, tools


def test_run_uses_argument_list_no_shell(monkeypatch):
    seen = {}
    real = subprocess.Popen

    def spy(args, **kw):
        seen["args"], seen["kw"] = args, kw
        return real(args, **kw)

    monkeypatch.setattr(subprocess, "Popen", spy)
    r = tools.run([sys.executable, "-c", "print('xin chào [YTS.MX]')"])
    assert r.code == 0 and r.stdout.strip() == "xin chào [YTS.MX]"
    assert isinstance(seen["args"], list) and "shell" not in seen["kw"]
    if os.name == "nt":
        assert seen["kw"]["creationflags"] & subprocess.CREATE_NO_WINDOW


def test_run_decodes_utf8_with_replace():
    r = tools.run([sys.executable, "-c", "import sys; sys.stdout.buffer.write(b'Chyba: soubor \\xc5\\xa1 \\xff')"])
    assert "š" in r.stdout and "�" in r.stdout


def test_run_missing_program_and_timeout():
    assert tools.run(["khong-co-chuong-trinh-nay-123"]).code == 127
    r = tools.run([sys.executable, "-c", "import time; time.sleep(5)"], timeout=0.5)
    assert r.code == -9


def test_cancel_now_kills_child_process():
    job = tools.Job()
    res = {}

    def worker():
        try:
            tools.run([sys.executable, "-c", "import time; time.sleep(30)"], job)
            res["x"] = "xong"
        except tools.Cancelled:
            res["x"] = "dung"

    t = threading.Thread(target=worker)
    t.start()
    time.sleep(0.5)
    t0 = time.monotonic()
    job.cancel_now()
    t.join(10)
    assert res["x"] == "dung" and time.monotonic() - t0 < 5


def test_job_log_and_redact():
    out = []
    job = tools.Job(lambda lvl, m: out.append((lvl, m)), redact=lambda s: s.replace("BIMAT", "•••"))
    job.warn("key=BIMAT")
    assert out == [("warn", "key=•••")] and job.lines == out


def test_walk_files_brackets_and_skip(tmp_path):
    root = tmp_path / "[YTS.MX] Phim (2020)"
    (root / "Output").mkdir(parents=True)
    (root / "Season [01]").mkdir()
    (root / "a [1080p].mkv").write_bytes(b"")
    (root / "Season [01]" / "b.MKV").write_bytes(b"")
    (root / "Output" / "c.mkv").write_bytes(b"")
    (root / "d.txt").write_bytes(b"")
    got = tools.walk_files(root, True, [".mkv"], [root / "Output"])
    assert [p.name for p in got] == ["a [1080p].mkv", "b.MKV"]
    assert [p.name for p in tools.walk_files(root, False, [".mkv"])] == ["a [1080p].mkv"]
    assert tools.is_under(root / "Output" / "x", root / "Output") and not tools.is_under(root / "Outputs", root / "Output")


def test_copy_file_brackets(tmp_path):
    src = tmp_path / "[Judas] a.srt"
    src.write_text("x", encoding="utf-8")
    dst = tmp_path / "srt_can_sync" / "vi" / "[Judas] b [1080p].vi.srt"
    tools.copy_file(src, dst)
    assert dst.read_text(encoding="utf-8") == "x"


def test_find_tool_order(tmp_path, monkeypatch):
    cfg = C.Config()
    mkv = tmp_path / "mkvtoolnix"
    mkv.mkdir()
    exe = mkv / tools.exe_name("mkvmerge")
    exe.write_bytes(b"")
    cfg["MkvToolNixDir"] = str(mkv)
    assert tools.find_tool("mkvmerge", cfg) == str(exe)
    explicit = tmp_path / "khac" / tools.exe_name("mkvmerge")
    explicit.parent.mkdir()
    explicit.write_bytes(b"")
    cfg["MkvmergePath"] = str(explicit)
    assert tools.find_tool("mkvmerge", cfg) == str(explicit)
    # ffmpeg/ffprobe tim canh alass truoc (Find-Near)
    near = tmp_path / "alass"
    near.mkdir()
    (near / tools.exe_name("ffprobe")).write_bytes(b"")
    assert tools.find_tool("ffprobe", C.Config({"AlassDir": "", "MkvToolNixDir": "", "ExtraToolDirs": []}),
                           near=str(near)) == str(near / tools.exe_name("ffprobe"))


def test_mkvmerge_flag_names():
    assert tools.mkvmerge_flag_names(74) == ("--default-track-flag", "--forced-display-flag")
    assert tools.mkvmerge_flag_names(73) == ("--default-track", "--forced-track")


# ---------------------------------------------------------------- probe
def mkvjson(subs=(), fps_dd=41708333, dur=5_400_000_000_000):
    tr = [{"id": 0, "type": "video", "properties": {"default_duration": fps_dd, "pixel_dimensions": "1920x1080"}}]
    for i, (codec, lang, ietf, name, extra) in enumerate(subs, 1):
        p = {"codec_id": codec, "language": lang, "language_ietf": ietf, "track_name": name}
        p.update(extra)
        tr.append({"id": i, "type": "subtitles", "properties": p})
    return {"tracks": tr, "container": {"properties": {"duration": dur}}}


def test_video_fps_and_duration():
    j = mkvjson()
    assert probe.video_fps(j) == 23.976
    assert probe.video_fps_exact(j) == pytest.approx(23.976, abs=1e-3)
    assert probe.duration(j) == 5400.0
    assert probe.video_fps({"tracks": []}) is None and probe.duration(None) == 0.0


def test_pick_ref_track_language_priority_and_text_first():
    j = mkvjson([
        ("S_HDMV/PGS", "eng", "en", "", {}),
        ("S_TEXT/UTF8", "cze", "cs", "", {}),
        ("S_TEXT/UTF8", "eng", "en", "English SDH", {}),
        ("S_TEXT/UTF8", "eng", "en", "Forced", {}),
        ("S_VOBSUB", "eng", "en", "", {}),
    ])
    t = probe.pick_ref_sub_track(j, ["en", "vi", "cs"])
    assert (t.id, t.sdh) == (3, True)  # en SDH (10*0+5) thang cs (10*2) — van ban truoc PGS
    t2 = probe.pick_ref_sub_track(j, ["cs", "en"])
    assert t2.id == 2  # TD-04: 'cze cs' nay khop 'cs'


def test_pick_ref_track_pgs_only_when_nothing_else():
    j = mkvjson([("S_HDMV/PGS", "eng", "en", "", {}), ("S_TEXT/UTF8", "eng", "en", "", {"forced_track": True})])
    t = probe.pick_ref_sub_track(j, ["en"])
    assert t.is_pgs and t.ext == ".sup"
    assert probe.pick_ref_sub_track(j, ["en"], allow_pgs=False) is None


def test_track_flags():
    assert probe.is_forced({"track_name": "Forced"}) and probe.is_forced({"flag_forced": True})
    assert probe.is_sdh({"track_name": "English (SDH)"}) and probe.is_sdh({"flag_hearing_impaired": True})
    assert not probe.is_sdh({"track_name": "Sdhx"})
    assert probe.is_commentary({"track_name": "Director's Commentary"}) and probe.is_commentary({"flag_commentary": True})
    assert probe.is_image_sub("S_HDMV/PGS") and not probe.is_image_sub("S_TEXT/UTF8")
    assert probe.sub_ext_by_codec("S_TEXT/WEBVTT") == ".vtt" and probe.sub_ext_by_codec("S_VOBSUB") is None
