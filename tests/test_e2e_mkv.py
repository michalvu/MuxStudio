"""Đầu–cuối với MKV THẬT tạo bằng ffmpeg + mkvmerge (bỏ qua nếu máy thiếu công cụ)."""
from __future__ import annotations

import shutil
import subprocess

import pytest

from core import config as C
from core import probe, srt, subfetch, tools
from fakehttp import FakeHTTP, Sleeper
from synth import make_ref_times, srt_text
from test_subfetch import ass_text

MKVMERGE = shutil.which("mkvmerge")
MKVEXTRACT = shutil.which("mkvextract")
FFMPEG = shutil.which("ffmpeg")
pytestmark = pytest.mark.skipif(not (MKVMERGE and MKVEXTRACT and FFMPEG), reason="thiếu mkvmerge/mkvextract/ffmpeg")


def make_mkv(path, subs):
    """subs: [(file, language, track_name)]"""
    vid = path.with_name("v.mkv")
    subprocess.run([FFMPEG, "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x36:r=24000/1001", "-t", "2",
                    "-c:v", "mpeg4", str(vid)], check=True)
    cmd = [MKVMERGE, "-q", "-o", str(path), str(vid)]
    for f, lang, name in subs:
        cmd += ["--language", f"0:{lang}"]
        if name:
            cmd += ["--track-name", f"0:{name}"]
        cmd.append(str(f))
    r = subprocess.run(cmd, capture_output=True)
    assert r.returncode in (0, 1), r.stdout
    vid.unlink()


@pytest.fixture
def mkv_lib(movie_dir):
    times = {"en": make_ref_times(300, seed=1), "sdh": make_ref_times(300, seed=2), "cs": make_ref_times(300, seed=3)}
    files = {}
    for k, t in times.items():
        files[k] = movie_dir / f"{k}.srt"
        files[k].write_text(srt_text(t), encoding="utf-8")
    stem = "[YTS.MX] Phim Thử (2020) [1080p]"
    make_mkv(movie_dir / f"{stem}.mkv", [(files["cs"], "cze", ""), (files["sdh"], "eng", "English SDH"), (files["en"], "eng", "")])
    for f in files.values():
        f.unlink()
    return movie_dir, stem, times


def session(root, cfg):
    return subfetch.FetchSession(root, cfg, {}, tools.Job(), FakeHTTP(), Sleeper(),
                                 tool_paths={"mkvmerge": MKVMERGE, "mkvextract": MKVEXTRACT})


def test_mkvmerge_json_fps_and_ietf(mkv_lib):
    root, stem, _ = mkv_lib
    j = probe.mkv_json(root / f"{stem}.mkv", MKVMERGE)
    assert probe.video_fps(j) == 23.976
    langs = [(t["properties"].get("language"), t["properties"].get("language_ietf")) for t in probe.tracks(j, "subtitles")]
    assert ("cze", "cs") in langs


def test_extract_ref_prefers_english_non_sdh_and_keeps_in_srt_goc(mkv_lib):
    root, stem, times = mkv_lib
    s = session(root, C.Config({"Languages": ["vi"], "UsePool": False}))
    s.scan()
    v = s.videos[0]
    assert v.ref and v.ref.times == pytest.approx(times["en"], abs=0.0011)
    assert "trích mới từ MKV" in v.ref.source
    goc = root / "srt_goc" / f"{stem}.srt"
    assert not goc.exists()  # lap ke hoach KHONG ghi gi vao thu muc phim
    s.execute()
    assert goc.is_file() and srt.cue_times(goc) == pytest.approx(times["en"], abs=0.0011)
    # lan sau: dung lai srt_goc, khong trich nua
    s2 = session(root, C.Config({"Languages": ["vi"], "UsePool": False}))
    s2.scan()
    assert "dùng lại srt_goc" in s2.videos[0].ref.source
    s.close()
    s2.close()


def test_extract_ref_czech_priority_matches_ietf(mkv_lib):
    """TĐ-04: track 'cze' + language_ietf 'cs' giờ khớp 'cs' trong RefSubLangs."""
    root, stem, times = mkv_lib
    s = session(root, C.Config({"Languages": ["vi"], "UsePool": False, "RefSubLangs": ["cs", "en"]}))
    s.scan()
    assert s.videos[0].ref.times == pytest.approx(times["cs"], abs=0.0011)
    s.close()


def test_ass_track_kept_as_ass(movie_dir):
    """Q7: phụ đề gốc dạng ASS giữ là .ass trong srt_goc (bản .bat đổi đuôi thành .srt)."""
    t = make_ref_times(200, seed=5)
    a = movie_dir / "a.ass"
    a.write_text(ass_text(t), encoding="utf-8")
    stem = "[Judas] Show - 01 [1080p]"
    make_mkv(movie_dir / f"{stem}.mkv", [(a, "eng", "")])
    a.unlink()
    s = session(movie_dir, C.Config({"Languages": ["vi"], "UsePool": False}))
    s.scan()
    s.execute()
    kept = movie_dir / "srt_goc" / f"{stem}.ass"
    assert kept.is_file() and not (movie_dir / "srt_goc" / f"{stem}.srt").exists()
    assert len(srt.cue_times(kept)) == 200
    s.close()
