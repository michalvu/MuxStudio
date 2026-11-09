"""Bước ② đầu–cuối với công cụ THẬT: ffmpeg / ffprobe / mkvmerge / mkvextract (+ alass nếu tìm thấy).

Thiếu công cụ nào thì test cần nó tự bỏ qua. Trên Windows, công cụ được tìm như nút "Tự tìm"
(Desktop\\mkvtoolnix, Desktop\\alass-windows64\\bin\\alass…) nên ``run_tests.bat`` chạy được cả test alass thật.
"""
from __future__ import annotations

import shutil
import subprocess

import pytest

from core import config as C
from core import srt, subprep, tools
from synth import make_ref_times, pgs_stream
from test_subprep import FakeAlass, write_srt


def _find(name):
    return shutil.which(name) or tools.find_tool(name, C.Config({}))


T = {n: _find(n) for n in ("mkvmerge", "mkvextract", "ffmpeg", "ffprobe", "alass")}
need_mkv = pytest.mark.skipif(not (T["mkvmerge"] and T["mkvextract"] and T["ffmpeg"]), reason="thiếu mkvmerge/mkvextract/ffmpeg")
need_ff = pytest.mark.skipif(not (T["mkvmerge"] and T["ffmpeg"] and T["ffprobe"]), reason="thiếu ffmpeg/ffprobe/mkvmerge")
need_alass = pytest.mark.skipif(not (T["alass"] and T["mkvmerge"] and T["ffmpeg"] and T["ffprobe"]),
                                reason="không tìm thấy alass thật (tải bản cho máy này, hoặc chạy trên Windows)")
STEM = "[Judas] Show - S01E01 [1080p]"


@pytest.fixture
def fa(tmp_path_factory, monkeypatch):
    return FakeAlass(tmp_path_factory.mktemp("fake alass"), monkeypatch)


def make_mkv(path, subs=(), dur=3, audio=True):
    """subs: [(file, language, track_name, forced)]; audio: eng (không Default) + cze (Default)."""
    base = path.with_name("base.mkv")
    cmd = [T["ffmpeg"], "-v", "error", "-y", "-f", "lavfi", "-i", f"color=c=black:s=64x36:r=24000/1001:d={dur}"]
    if audio:
        cmd += ["-f", "lavfi", "-i", f"sine=frequency=440:duration={dur}", "-f", "lavfi", "-i", f"sine=frequency=880:duration={dur}",
                "-map", "0", "-map", "1", "-map", "2", "-c:a", "aac",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=cze",
                "-disposition:a:0", "0", "-disposition:a:1", "default"]
    cmd += ["-c:v", "mpeg4", str(base)]
    subprocess.run(cmd, check=True)
    mk = [T["mkvmerge"], "-q", "-o", str(path), str(base)]
    for f, lang, name, forced in subs:
        mk += ["--language", f"0:{lang}"]
        if name:
            mk += ["--track-name", f"0:{name}"]
        if forced:
            mk += ["--forced-display-flag" if _mkv_major() >= 74 else "--forced-track", "0:1"]
        mk.append(str(f))
    r = subprocess.run(mk, capture_output=True)
    assert r.returncode in (0, 1), r.stdout
    base.unlink()


def _mkv_major():
    return tools.mkvmerge_version(T["mkvmerge"]).major


def sess(root, fa=None, cfg=None):
    tp = dict(T)
    if fa is not None:
        tp["alass"] = fa.cmd
    return subprep.PrepSession(root, C.Config(cfg or {}), tools.Job(), tool_paths=tp)


@need_mkv
def test_extract_refs_prefers_english_non_sdh(movie_dir):
    t = {k: make_ref_times(200, seed=i) for i, k in enumerate(("en", "sdh", "cs", "forced"))}
    files = {k: write_srt(movie_dir / f"_{k}.srt", v) for k, v in t.items()}
    make_mkv(movie_dir / f"{STEM}.mkv", [(files["forced"], "eng", "Forced", True), (files["cs"], "cze", "", False),
                                          (files["sdh"], "eng", "English SDH", False), (files["en"], "eng", "", False)])
    for f in files.values():
        f.unlink()
    with sess(movie_dir) as s:
        rows = s.plan_extract_refs()
        assert not (movie_dir / "srt_goc").exists()  # lap ke hoach khong ghi gi
        (row,) = rows
        assert row.ok and row.final.name == f"{STEM}.srt" and not row.track.sdh
        rep = s.extract_refs(rows)
    got = movie_dir / "srt_goc" / f"{STEM}.srt"
    assert rep.ok == 1 and srt.cue_times(got) == pytest.approx(t["en"], abs=0.0011)
    with sess(movie_dir, cfg={"RefSubLangs": ["cs", "en"]}) as s:
        (row,) = s.plan_extract_refs()
        assert row.reason == "đã có mốc"  # SkipExistingRef


@need_mkv
def test_extract_refs_czech_priority_and_pgs_only(movie_dir):
    """TĐ-04: track 'cze' khớp 'cs'. MKV chỉ có PGS → đọc mốc thời gian, không cần OCR."""
    t = make_ref_times(150, seed=3)
    cs = write_srt(movie_dir / "_cs.srt", t)
    en = write_srt(movie_dir / "_en.srt", make_ref_times(150, seed=4))
    make_mkv(movie_dir / "A.mkv", [(en, "eng", "", False), (cs, "cze", "", False)], audio=False)
    ev = []
    for x in t:
        ev += [(x, "show"), (x + 1.0, "hide")]
    sup = movie_dir / "_p.sup"
    sup.write_bytes(pgs_stream(ev))
    make_mkv(movie_dir / "B.mkv", [(sup, "eng", "", False)], audio=False)
    for f in (cs, en, sup):
        f.unlink()
    with sess(movie_dir, cfg={"RefSubLangs": ["cs", "en"]}) as s:
        rows = {r.video.name: r for r in s.plan_extract_refs()}
        assert rows["B.mkv"].track.is_pgs and rows["B.mkv"].final.name == "B.srt"
        s.extract_refs(list(rows.values()))
    assert srt.cue_times(movie_dir / "srt_goc" / "A.srt") == pytest.approx(t, abs=0.0011)
    assert srt.cue_times(movie_dir / "srt_goc" / "B.srt") == pytest.approx(t, abs=0.0011)


@need_ff
def test_audio_mode_always_extracts_wav_and_retry_order(movie_dir, fa):
    """§6.5 + TĐ-19: mốc audio → LUÔN trích WAV (mono 16 kHz PCM 16 bit, cả chế độ tự động);
    thứ tự thử: WAV+cờ → WAV không cờ → MKV+cờ → MKV không cờ."""
    make_mkv(movie_dir / f"{STEM}.mkv")
    write_srt(movie_dir / "srt_can_sync" / "vi" / f"{STEM}.vi.srt", make_ref_times(60, seed=1))
    fa.set(fail=[{"ref": "any", "flags": True}, {"ref": "any", "flags": False}])
    with sess(movie_dir, fa) as s:
        (p,) = s.plan_sync("audio", subprep.AudioChoice("auto"))
        assert p.audio_index == 1 and "(Default)" in p.audio_desc  # track Default (cze), khong phai track dau
        cmds = s.preview_commands(p)
        assert "-map 0:a:1 -vn -ac 1 -ar 16000 -c:a pcm_s16le" in cmds[0]
        rep = s.execute_sync([p])
    calls = fa.calls()
    kinds = [("wav" if c["ref"].endswith(".wav") else "mkv", bool(c["flags"])) for c in calls]
    assert kinds == [("wav", True), ("wav", False), ("mkv", True), ("mkv", False)]
    assert calls[0]["wav"] == {"channels": 1, "rate": 16000, "bits": 16}
    assert calls[0]["flags"] == ["--no-split", "--disable-fps-guessing"]
    assert rep.errors == 1


@need_ff
def test_audio_choice_index_and_lang(movie_dir, fa):
    make_mkv(movie_dir / f"{STEM}.mkv")
    write_srt(movie_dir / "srt_can_sync" / "vi" / f"{STEM}.vi.srt", make_ref_times(60, seed=1))
    with sess(movie_dir, fa) as s:
        assert [a.lang for a in s.sample_audio_streams()[1]] == ["eng", "cze"]
        assert s.plan_sync("audio", subprep.AudioChoice("lang", "en"))[0].audio_index == 0
    with sess(movie_dir, fa) as s:
        assert s.plan_sync("audio", subprep.AudioChoice("index", 0))[0].audio_index == 0
    with sess(movie_dir, fa) as s:  # so thu tu khong co -> audio Default, van trich WAV
        p = s.plan_sync("audio", subprep.AudioChoice("index", 5))[0]
        assert p.audio_index == 1 and p.audio_desc.startswith("không có #5")
        s.execute_sync([p])
    assert fa.calls()[0]["ref"].endswith(".wav")


@need_alass
def test_real_alass_sub_mode_recovers_offset(movie_dir):
    ref = make_ref_times(500, seed=11, min_gap=1.5, max_gap=8)
    # phim phai DAI bang phu de (~40 phut): phim 3 s -> suc khoe bao dung "dong cuoi vuot thoi luong phim" (muc 2)
    make_mkv(movie_dir / f"{STEM}.mkv", audio=False, dur=int(ref[-1]) + 30)
    write_srt(movie_dir / "srt_goc" / f"{STEM}.srt", ref)
    write_srt(movie_dir / "srt_can_sync" / "vi" / f"{STEM}.vi.srt", [r + 7.5 for r in ref])
    with sess(movie_dir) as s:
        pairs = s.plan_sync("sub")
        rep = s.execute_sync(pairs)
    assert rep.ok == 1, [m for _, m in s.job.lines][-10:]
    out = srt.cue_times(movie_dir / f"{STEM}.vi.srt")
    assert out == pytest.approx(ref, abs=0.1)
    assert pairs[0].result.health.level == 0


@need_alass
def test_real_alass_audio_mode_runs_on_wav(movie_dir):
    """alass thật nhận WAV do ffmpeg trích (không lỗi 'codec_long_name'); chỉ kiểm tra chạy được."""
    make_mkv(movie_dir / f"{STEM}.mkv", dur=20)
    write_srt(movie_dir / "srt_can_sync" / "vi" / f"{STEM}.vi.srt", [1.0 + i for i in range(15)])
    with sess(movie_dir, cfg={"MaxShiftSeconds": 0}) as s:
        (p,) = s.plan_sync("audio")
        s.execute_sync([p])
    assert p.result.code == 0 and p.result.attempt == 0, p.result.output_tail
