"""Ghép audio từ file phim khác (§6.9): tìm file nguồn, đo độ lệch bằng phụ đề chung, độ lệch tay, bộ lọc đổi tốc độ,
lệnh mkvmerge (``--sync``, thứ tự track). Test tiếng bíp với ffmpeg thật ở test_e2e_mux.py."""
from __future__ import annotations

import os
from pathlib import Path

import pytest

from core import config as C
from core import donor as D
from core import probe
from synth import make_ref_times
from test_mux import A, J, Lib, S, V

PAL = 25 / 23.976


def test_find_donor_file_episode_and_movie():
    pool = [Path(f"/n/One.Piece.S16E{n}.CZ.mkv") for n in (642, 643, 644)] + [Path("/n/Dune.2021.CZ.mkv")]
    assert D.find_donor_file("[Judas] One Piece - 0643 [1080p]", pool).name == "One.Piece.S16E643.CZ.mkv"
    assert D.find_donor_file("One Piece - 0645", pool) is None
    assert D.find_donor_file("Dune.2021.2160p", pool).name == "Dune.2021.CZ.mkv"
    assert D.find_donor_file("Dune.1984.1080p", pool) is None  # khac nam
    assert D.find_donor_file("Phim.Gi.Do.2020", [Path("/n/Ban.Sec.mkv")]).name == "Ban.Sec.mkv"  # 1 file nguon phim le
    # S02E05 khong ghep voi "- 05" (so nho, season khac 1)
    assert D.find_donor_file("Show - 05", [Path("/n/Show.S02E05.mkv")]) is None


def test_manual_offset_keys():
    offs = {"S01E03": 1200, "tap #643": -500, "Phim.Le.2020": 300, "77": 40}
    assert D.manual_offset("Show.S01E03.1080p", offs) == 1200
    assert D.manual_offset("[Judas] One Piece - 0643", offs) == -500
    assert D.manual_offset("phim.le.2020", offs) == 300
    assert D.manual_offset("Anime - 077", offs) == 40
    assert D.manual_offset("Show.S01E04", offs) is None


@pytest.mark.parametrize("sr", [48000, 44100])
@pytest.mark.parametrize("scale", [PAL, 1 / PAL, 25 / 24, 24 / 25, 24 / 23.976, 25 * 1001 / 24000, 1.0123456789])
def test_stretch_filter_math_exact(sr, scale):
    """TĐ-34: hệ số thời lượng của chuỗi lọc = đúng ``scale``; ``asetrate`` luôn là số nguyên; số thập phân dấu CHẤM."""
    flt = D.stretch_filter(sr, scale)
    rates = [p.split("=")[1] for p in flt.split(",") if p.startswith("asetrate=")]
    assert rates and all(r.isdigit() for r in rates)
    assert D.stretch_factor(flt, sr) == pytest.approx(scale, rel=1e-9)
    if "atempo=" in flt:
        assert "," not in flt.split("atempo=")[1] and "." in flt.split("atempo=")[1]
    if scale != 1.0123456789:
        assert "atempo" not in flt  # ty le FPS that -> khong can atempo (atempo lam som ~21 ms)


def test_stretch_filter_pal_exact_numbers():
    assert D.stretch_filter(48000, PAL) == "aresample=46875,asetrate=44955,aresample=48000"
    assert D.stretch_filter(48000, 1 / PAL) == "aresample=47952,asetrate=50000,aresample=48000"


def times_fn(table):
    return lambda f, trk: table.get((f.name, trk.id))


def test_measure_timing_by_common_subtitle_pal():
    ref = make_ref_times(400, seed=3)
    donor_t = [(r + 1.5 * PAL) / PAL for r in ref]  # phim_cua_ban = nguon * PAL - 1.5*PAL... (lech +1,5 s ben nguon)
    mine_j = J(V(), A(1), S(2, "eng"))
    donor_j = J(V(fps=25), A(1, "cze"), S(3, "eng"))
    tm = D.measure_timing(Path("/m/Mine.mkv"), mine_j, Path("/m/Donor.mkv"), donor_j,
                          times_fn({("Mine.mkv", 2): ref, ("Donor.mkv", 3): donor_t}), ext_refs=[])
    assert tm.sure and tm.scale == pytest.approx(PAL) and tm.votes >= 20
    assert tm.offset == pytest.approx(-1.5 * PAL, abs=0.05) and "sub English trong MKV" in tm.how


def test_measure_timing_uses_external_en_srt_and_falls_back_to_duration(tmp_path):
    ref = make_ref_times(400, seed=4)
    ext = tmp_path / "Mine.en.srt"
    from synth import srt_text

    ext.write_text(srt_text(ref), encoding="utf-8", newline="")
    mine_j = J(V(), A(1), dur=3000)
    donor_j = J(V(), A(1, "cze"), S(3, "eng"), dur=3000.4)
    tm = D.measure_timing(tmp_path / "Mine.mkv", mine_j, Path("/d/Donor.mkv"), donor_j,
                          times_fn({("Donor.mkv", 3): [r - 2 for r in ref]}), ext_refs=[("en", ext)])
    assert tm.sure and tm.offset == pytest.approx(2.0, abs=0.05) and "sub English ngoài" in tm.how
    # khong co phu de chung -> chi "khop" khi thoi luong chenh <= 1 s
    tm2 = D.measure_timing(tmp_path / "Mine.mkv", mine_j, Path("/d/Donor.mkv"), J(V(), A(1, "cze"), dur=3000.4),
                           times_fn({}), ext_refs=[])
    assert not tm2.sure and tm2.close and tm2.how.startswith("ƯỚC TÍNH")
    tm3 = D.measure_timing(tmp_path / "Mine.mkv", mine_j, Path("/d/Donor.mkv"), J(V(), A(1, "cze"), dur=2990),
                           times_fn({}), ext_refs=[])
    assert not tm3.close and tm3.how.startswith("không đo được")


def donor_lib(movie_dir, monkeypatch, cfg=None, ffmpeg=True):
    lib = Lib(movie_dir, monkeypatch, cfg={"UseDonorAudio": True, **(cfg or {})})
    lib.s.ffmpeg = "ffmpeg" if ffmpeg else None
    return lib


def add_donor(lib, stem, j):
    p = lib.root / "audio_nguon" / f"{stem}.mkv"
    p.parent.mkdir(exist_ok=True)
    p.write_bytes(b"")
    lib.s.json_cache[os.path.normcase(os.path.abspath(p))] = j
    lib.s.donor_pool = D.donor_files(lib.root, "audio_nguon")
    return p


def test_plan_donor_same_fps_sync_and_args(movie_dir, monkeypatch):
    lib = donor_lib(movie_dir, monkeypatch)
    ref = make_ref_times(400, seed=5)
    mine = lib.add("Show.S01E03.1080p", J(V(), A(1, "eng"), S(2, "eng")))
    dn = add_donor(lib, "Show.S01E03.CZ", J(V(), A(1, "cze", ch=2, bps=192000), A(2, "cze", ch=6, bps=448000),
                                             A(3, "cze", name="Komentář režiséra Commentary"), S(4, "eng")))
    lib.s.track_times = times_fn({(mine.name, 2): ref, (dn.name, 4): [r + 0.75 for r in ref]})
    plan = lib.plan(mine)
    (dt,) = plan.donor.tracks
    assert (dt.tid, dt.code, dt.offset_ms, dt.stretch, dt.name) == (2, "cs", -750, False, "Czech E-AC-3 5.1")
    a = lib.s.build_args(plan)
    i = a.index("--no-video")
    assert a[i:] == ["--no-video", "--no-subtitles", "--no-chapters", "--no-attachments", "--no-global-tags",
                     "--audio-tracks", "2", "--language", "2:cs", "--track-name", "2:Czech E-AC-3 5.1",
                     "--default-track-flag", "2:0", "--forced-display-flag", "2:0", "--sync", "2:-750", os.fspath(dn)]
    assert a[a.index("--track-order") + 1] == "0:0,0:1,1:2,0:2"


def test_donor_set_default_and_skip_if_present(movie_dir, monkeypatch):
    lib = donor_lib(movie_dir, monkeypatch, cfg={"DonorSetDefault": True})
    ref = make_ref_times(400, seed=6)
    mine = lib.add("Phim.A.2020", J(V(), A(1, "eng"), S(2, "eng")))
    dn = add_donor(lib, "Phim.A.2020.CZ", J(V(), A(1, "cze"), S(2, "eng")))
    lib.s.track_times = times_fn({(mine.name, 2): ref, (dn.name, 2): ref})
    plan = lib.plan(mine)
    a = lib.s.build_args(plan)
    assert a[a.index("--track-order") + 1] == "0:0,1:1,0:1,0:2"
    assert "1:0" in a[a.index("--audio-tracks"):]  # audio cua ban khong con Default
    assert a[a.index("--default-track-flag") + 1] == "1:0"
    # phim da co tieng Sec -> khong lay them
    mine2 = lib.add("Phim.B.2020", J(V(), A(1, "eng"), A(2, "cze")))
    p2 = lib.plan(mine2)
    assert p2.donor.tracks == [] and "đã có Czech" in p2.donor.note


def test_donor_pal_needs_stretch_and_ffmpeg(movie_dir, monkeypatch):
    lib = donor_lib(movie_dir, monkeypatch)
    ref = make_ref_times(400, seed=7)
    mine = lib.add("Phim.C.2020", J(V(), A(1, "eng"), S(2, "eng")))
    dn = add_donor(lib, "Phim.C.2020.CZ", J(V(fps=25), A(1, "cze", ch=8), S(2, "eng")))
    lib.s.track_times = times_fn({(mine.name, 2): ref, (dn.name, 2): [r / PAL for r in ref]})
    plan = lib.plan(mine)
    (dt,) = plan.donor.tracks
    assert dt.stretch and dt.out_channels == 6 and dt.bitrate == "640k" and dt.name == "Czech E-AC-3 5.1"
    assert dt.filter == "aresample=46875,asetrate=44955,aresample=48000"
    cmds = lib.s.preview_commands(plan)
    assert cmds[0].startswith(f'ffmpeg -y -v error -i "{os.fspath(dn)}" -map 0:a:0') and "-c:a eac3 -b:a 640k" in cmds[0]
    assert "a_0.mka" in cmds[1] and "--audio-tracks 0 " in cmds[1]  # mkvmerge doc .mka tam, track 0
    lib2 = donor_lib(movie_dir, monkeypatch, ffmpeg=False)
    lib2.s.json_cache, lib2.s.track_times = lib.s.json_cache, lib.s.track_times
    assert lib2.plan(mine).donor.tracks == []


def test_donor_not_measured_is_skipped_unless_manual(movie_dir, monkeypatch):
    lib = donor_lib(movie_dir, monkeypatch)
    mine = lib.add("Phim.D.2020", J(V(), A(1, "eng"), dur=6000))
    add_donor(lib, "Phim.D.2020.CZ", J(V(), A(1, "cze"), dur=6100))
    lib.s.track_times = times_fn({})
    plan = lib.plan(mine)
    assert plan.donor.tracks == [] and "KHÔNG ghép" in plan.donor.note
    lib.s.set_donor_offset(plan, 1500)  # sua tay trong bang
    plan = lib.plan(mine)
    assert plan.donor.tracks[0].offset_ms == 1500 and plan.donor.timing.how == "chỉnh tay (trong bảng)"


def test_probe_ref_sub_tracks_ranking():
    j = J(V(), S(1, "eng", codec_id="S_HDMV/PGS"), S(2, "eng", name="SDH"), S(3, "eng"), S(4, "eng", forced=True))
    assert [(t.id, t.rank) for t in probe.ref_sub_tracks(j)] == [(3, 0), (2, 1), (1, 2)]


def test_config_default_donor_settings():
    c = C.Config({})
    assert (c["DonorLangs"], c["DonorMinVotes"], c["DonorMaxDurationDiff"]) == (["cs"], 20, 1.0)
