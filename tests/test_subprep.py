"""Bước ② (SubPrep): cắt recap, sửa khối 00:00:00, sức khoẻ, ghép tên, đổi tên, đồng bộ (alass GIẢ), dọn dẹp.

Không cần alass thật: ``fake_alass.py`` đọc tham số như alass, ghi lại mỗi lần gọi và dịch phụ đề theo cấu hình.
Test với ffmpeg / mkvmerge / alass thật nằm ở ``test_e2e_subprep.py``.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

import pytest

from core import config as C
from core import srt, subprep, tools, undo
from synth import make_ref_times, pgs_stream, srt_text
from test_subfetch import ass_text

FAKE = Path(__file__).with_name("fake_alass.py")
SHOW = ["Show.S01E01.1080p.WEB-DL", "Show.S01E02.1080p.WEB-DL", "Show.S01E03.1080p.WEB-DL"]


def blocks(times, dur=1.0):
    return [srt.Block(t, t + dur, "", f"dòng {i}") for i, t in enumerate(times)]


def write_srt(p: Path, times, dur=1.0, enc="utf-8", texts=None) -> Path:
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(srt_text(list(times), dur, texts).encode(enc))
    return p


def recap_sub(ref, n_recap=30, delay=65.0):
    """Phụ đề có ``n_recap`` dòng "tóm tắt tập trước" ở đầu; phần thật trễ ``delay`` giây so với phim."""
    return [1.0 + 2 * i for i in range(n_recap)] + [r + delay for r in ref]


def tree(root: Path) -> list[str]:
    return sorted(p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file())


class FakeAlass:
    def __init__(self, d: Path, monkeypatch):
        self.conf = d / "fake_alass.json"
        self.log = d / "fake_alass.jsonl"
        self.cmd = [sys.executable, str(FAKE)]
        monkeypatch.setenv("FAKE_ALASS", str(self.conf))
        self.set()

    def set(self, **kw):
        kw.setdefault("log", str(self.log))
        self.conf.write_text(json.dumps(kw), encoding="utf-8", newline="")

    def calls(self):
        return [json.loads(x) for x in self.log.read_text(encoding="utf-8").splitlines()] if self.log.exists() else []


@pytest.fixture
def fa(tmp_path_factory, monkeypatch):
    return FakeAlass(tmp_path_factory.mktemp("fake alass"), monkeypatch)


def sess(root, fa, cfg=None, **paths):
    tp = {"mkvmerge": None, "mkvextract": None, "ffmpeg": None, "ffprobe": None, "alass": fa.cmd}
    tp.update(paths)
    return subprep.PrepSession(root, C.Config(cfg or {}), tools.Job(), tool_paths=tp)


class Lib:
    """3 tập phim bộ (file phim rỗng) + phụ đề gốc trong srt_goc + mồi nhử ở srt_pool / audio_nguon / Output."""

    def __init__(self, root: Path):
        self.root = root
        self.ref = {s: make_ref_times(400, seed=i + 1) for i, s in enumerate(SHOW)}
        for s, t in self.ref.items():
            (root / f"{s}.mkv").write_bytes(b"")
            write_srt(root / "srt_goc" / f"{s}.srt", t)
        for d in ("srt_pool/vi", "audio_nguon", "Output/Show (2019) [tmdbid-1]/Season 01"):
            (root / d).mkdir(parents=True)
        write_srt(root / "srt_pool" / "vi" / f"{SHOW[0]}.vi.srt", self.ref[SHOW[1]])
        (root / "audio_nguon" / "Show.S01E01.720p.CZ.DUB.mkv").write_bytes(b"")
        (root / "audio_nguon" / "Show.S01E02.720p.CZ.DUB.mkv").write_bytes(b"")
        (root / "Output" / "Show (2019) [tmdbid-1]" / "Season 01" / "Show - S01E01.mkv").write_bytes(b"")

    @property
    def can(self) -> Path:
        return self.root / "srt_can_sync"


@pytest.fixture
def lib(movie_dir):
    return Lib(movie_dir)


# ==========================================================================
#  CẮT RECAP (Remove-LeadingExtra)
# ==========================================================================

REF = make_ref_times(300, start=30, seed=1)


def test_trim_recap_cuts_exact_count():
    keep, cut = subprep.remove_leading_extra(blocks(recap_sub(REF)), REF)
    assert cut == 30 and len(keep) == 300
    assert keep[0].start == pytest.approx(REF[0] + 65)


def test_trim_recap_with_fps_scale():
    scale = 25 / 23.976
    sub = [1.0 + 2 * i for i in range(30)] + [(r + 65) / scale for r in REF]
    _, cut = subprep.remove_leading_extra(blocks(sub), REF)
    assert cut == 30


def test_trim_nothing_when_no_recap():
    assert subprep.remove_leading_extra(blocks([r + 5 for r in REF]), REF) == (None, 0)


def test_trim_cancelled_when_recap_too_long():
    # recap > 40% (nam ngoai 30% dau duoc phep tim) -> huy, khong cat
    assert subprep.remove_leading_extra(blocks(recap_sub(REF, 200, delay=420)), REF) == (None, 0)


def test_trim_needs_40_lines_and_strong_relation():
    assert subprep.remove_leading_extra(blocks(recap_sub(REF[:9], 30)), REF) == (None, 0)  # 39 dong
    other = make_ref_times(300, start=30, seed=99)
    assert subprep.remove_leading_extra(blocks(recap_sub(other)), REF) == (None, 0)  # phim khac
    assert subprep.remove_leading_extra(blocks(recap_sub(REF)), None) == (None, 0)


def test_trim_then_write_keeps_hours(tmp_path):
    """TĐ-01: ghi lại sau khi cắt giữ đúng giờ (bản .bat biến 00:45:00 thành 01:45:00)."""
    ref = make_ref_times(300, start=2600, seed=3)
    keep, cut = subprep.remove_leading_extra(blocks(recap_sub(ref)), ref)
    p = tmp_path / "x.srt"
    srt.write_blocks(p, keep)
    assert srt.cue_times(p)[0] == pytest.approx(ref[0] + 65, abs=0.001)
    assert "00:44:" in p.read_text(encoding="utf-8")


# ==========================================================================
#  KHỐI 00:00:00 (Repair-CollapsedCues)
# ==========================================================================


def test_repair_collapsed_cues():
    good = blocks(REF[:100])
    keep, n = subprep.repair_collapsed_cues(blocks([0.0] * 5) + good, 2)
    assert n == 5 and len(keep) == 100
    assert subprep.repair_collapsed_cues(blocks([0.0, 0.01]) + good, 2) == (None, 0)  # <= MaxZeroStartCues
    assert subprep.repair_collapsed_cues(blocks([0.0] * 5) + good[:8], 2) == (None, 0)  # con < 10 dong


# ==========================================================================
#  SỨC KHOẺ (Test-SubHealth) — mỗi dấu hiệu một ca
# ==========================================================================

HREF = make_ref_times(400, seed=5)


def health(tmp_path, times, ref=HREF, dur=None, before=0, src=None, d=1.0, name="h.srt", raw=None):
    p = tmp_path / name
    if raw is not None:
        p.write_bytes(raw)
    else:
        write_srt(p, times, d)
    vd = HREF[-1] + 30 if dur is None else dur
    return subprep.sub_health(p, ref, vd, before, src)


def test_health_good(tmp_path):
    h = health(tmp_path, HREF, src=[t - 3 for t in HREF], before=400)
    assert (h.level, h.flags, h.text) == (0, [], "TỐT")
    assert h.shift_first == pytest.approx(3) and h.shift_last == pytest.approx(3)
    assert h.res_votes >= 90 and abs(h.res_offset) < 0.01


@pytest.mark.parametrize("case,code,level", [
    ("zero", "zero", 2), ("overlap", "overlap", 1), ("lost", "lost", 1), ("overrun", "overrun", 2),
    ("weak", "weak", 2), ("fps", "fps", 1), ("offset", "offset", 1), ("stretch", "stretch", 1),
    ("badchar", "badchar", 1), ("few", "few", 1),
])
def test_health_each_sign(tmp_path, case, code, level):
    kw: dict = {}
    times = HREF
    if case == "zero":
        times = [0.0] * 5 + HREF
    elif case == "overlap":
        kw["d"] = 6.0
    elif case == "lost":
        kw["before"] = 500
    elif case == "overrun":
        kw["dur"] = HREF[-1] - 200
    elif case == "weak":
        kw["ref"] = make_ref_times(400, seed=77)
    elif case == "fps":
        times = [r / (25 / 23.976) for r in HREF]
    elif case == "offset":
        times = [r + 2 for r in HREF]
    elif case == "stretch":
        kw["src"] = [t * 0.995 for t in HREF]
    elif case == "badchar":
        kw["raw"] = srt_text(HREF, 1.0).encode("utf-8").replace("Dòng 7".encode(), b"D\xffng 7")
    elif case == "few":
        times, kw["ref"] = HREF[:40], None
    h = health(tmp_path, times, **kw)
    assert code in h.codes, h.flags
    assert h.level == level, h.flags
    assert len(h.codes) == len(h.flags)


def test_health_messages(tmp_path):
    h = health(tmp_path, [r + 2 for r in HREF], dur=600)
    assert "vẫn lệch -2s" in h.flags
    assert any(f.startswith("dòng cuối ") and f.endswith("> phim 10p") for f in h.flags)
    h = health(tmp_path, [0.0] * 5 + HREF, ref=None)
    assert h.flags[0] == "5 dòng dính ở 00:00:00"


def test_health_reads_ass_and_vtt(tmp_path):
    """TĐ-14: ASS/VTT được đọc mốc (bản .bat báo 'CÓ VẤN ĐỀ: không đọc được nội dung')."""
    p = tmp_path / "a.ass"
    p.write_text(ass_text(HREF), encoding="utf-8", newline="")
    h = subprep.sub_health(p, HREF, HREF[-1] + 30, 400, None)
    assert (h.level, h.fmt, h.cues) == (0, "ass", 400)
    v = tmp_path / "a.vtt"
    v.write_text("WEBVTT\n\n" + "".join(f"{srt.format_stamp(t).replace(',', '.')} --> {srt.format_stamp(t + 1).replace(',', '.')}\nx\n\n"
                                       for t in HREF), encoding="utf-8", newline="")
    h = subprep.sub_health(v, HREF, HREF[-1] + 30, 400, None)
    assert (h.level, h.fmt) == (0, "vtt")
    e = tmp_path / "e.srt"
    e.write_bytes(b"")
    assert subprep.sub_health(e, HREF, 0, 0, None).codes == ["unreadable"]


# ==========================================================================
#  GHÉP PHỤ ĐỀ ↔ MỐC (Resolve-Ref)
# ==========================================================================


def index(names, kind="video", root=Path("/lib")):
    return subprep.RefIndex.build([root / n for n in names], kind, root, [".mkv", ".mp4"])


def rr(idx, name, n=5):
    return subprep.resolve_ref(idx, name, n)


def test_resolve_tiers():
    idx = index(["Dune.Part.Two.2024.2160p.mkv", "Dune.2021.1080p.mkv", "Avengers.Endgame.2019.mkv",
                 "Other.Name.S01E05.mkv", "Other.Name.S01E06.mkv"])
    assert rr(idx, "Avengers.Endgame.2019.mkv".removesuffix(".mkv")).how == "trùng tên"
    m = rr(idx, "Dune.Part.Two.2024.WEB.vi")
    assert m.ref.canon == "Dune.Part.Two.2024.2160p" and m.how.startswith("theo tên phim")
    m = rr(idx, "Dune.2021.vi")  # loc theo nam
    assert m.ref.canon == "Dune.2021.1080p"
    m = rr(idx, "Phim Bo S01E05.vi")  # ten khac han -> theo so tap
    assert (m.ref.canon, m.how) == ("Other.Name.S01E05", "theo tập S01E05")
    m = rr(idx, "Unknown.Film.Part.3.vi")
    assert m.ref is None and m.reason == "không tìm được phim tương ứng" and m.details
    assert rr(idx, "Hoan toan khac.vi").reason == "không nhận diện được số tập"


def test_resolve_title_ambiguity_reported():
    idx = index(["Movie.Night.2010.mkv", "Movie.Night.2012.mkv"])
    m = rr(idx, "Movie Night.vi")
    assert m.ref is None and m.reason.startswith("nhập nhằng theo tên phim (2 mốc")


def test_resolve_anime_absolute_numbers():
    """TĐ-31a: anime đánh số tuyệt đối — bản .bat luôn 'NHẬP NHẰNG theo tên phim'."""
    idx = index([f"[Judas] One Piece - {e:04d} [1080p].mkv" for e in (643, 644, 645)])
    m = rr(idx, "One Piece - 0644.vi")
    assert m.ref.canon == "[Judas] One Piece - 0644 [1080p]"
    assert rr(index(["One.Piece.S16E644.1080p.mkv", "One.Piece.S16E645.1080p.mkv"]), "One Piece - 0644.vi").ref.canon \
        == "One.Piece.S16E644.1080p"


def test_resolve_never_pairs_different_episode():
    """TĐ-31b: thiếu phim tập 5, chỉ có tập 6 → bản .bat ghép phụ đề tập 5 vào tập 6."""
    m = rr(index(["Show.S01E06.1080p.WEB-DL.mkv"]), "Show.S01E05.vi", n=1)
    assert m.ref is None and m.reason == "không tìm được mốc"
    assert any("số tập khác" in d for d in m.details)
    # tang 4 (1 phu de - 1 moc) cung khong ghep khi so tap khac nhau
    assert rr(index(["Foo.Bar.S01E05.mkv"]), "Completely Different - 07", n=1).ref is None


def test_resolve_tier3_ambiguity_reported():
    """TĐ-31c: tầng 3 nhiều phim cùng khớp → báo nhập nhằng (bản .bat lấy bừa file đầu)."""
    idx = index(["Frieren.Beyond.Journeys.End.S01E05.1080p.mkv", "Frieren.Beyond.Journeys.End.S02E05.1080p.mkv"])
    m = rr(idx, "Sousou no Frieren - 05.vi")
    assert m.ref is None and m.reason == "nhập nhằng tập E05 (2 file mốc khớp)"


def test_resolve_direct_one_to_one():
    m = rr(index(["Foo.Bar.2019.mkv"]), "Something E05", n=1)
    assert m.ref.canon == "Foo.Bar.2019" and m.how.startswith("ghép thẳng")
    assert rr(index(["Foo.Bar.2019.mkv"]), "Something E05", n=2).ref is None


def test_select_variant_and_by_name_prefer_plain():
    idx = index(["Movie.A.2010.en.srt", "Movie.A.2010.srt", "Movie.A.2010.vi.srt"], kind="sub")
    assert idx.by_name["movie.a.2010"].file.name == "Movie.A.2010.srt"
    assert subprep.select_ref_variant(idx.items).file.name == "Movie.A.2010.srt"
    v = index(["Movie.A.2010.en.srt", "Movie.A.2010.mkv"], kind="").items
    assert subprep.select_ref_variant(v).is_video


# ==========================================================================
#  CHỈ MỤC MỐC TRONG THƯ MỤC THẬT
# ==========================================================================


def test_index_excludes_work_folders(lib, fa):
    """TĐ-13 + §6.5: srt_pool, srt_can_sync, Output, audio_nguon không bao giờ làm mốc."""
    with sess(lib.root, fa) as s:
        for mode in ("sub", "audio"):
            idx = s.build_index(mode)
            names = [r.file.relative_to(lib.root).as_posix() for r in idx.items]
            assert not any(n.split("/")[0] in ("srt_pool", "audio_nguon", "Output", "srt_can_sync") for n in names), names
        assert len(s.build_index("audio").items) == 3
        assert "Mốc lấy từ: srt_goc (3)" in [m for _, m in s.job.lines][1]


def test_counts(lib, fa):
    write_srt(lib.can / "vi" / "a.srt", REF)
    with sess(lib.root, fa) as s:
        assert s.counts() == subprep.PrepCounts(3, 1, 3)


# ==========================================================================
#  ĐỔI TÊN
# ==========================================================================


def test_plan_rename_lang_from_folder_writes_nothing(lib, fa):
    write_srt(lib.can / "vi" / "show.s01e01.srt", REF)
    write_srt(lib.can / "cs" / "Show S01E02 [1080p].srt", REF)
    before = tree(lib.root)
    with sess(lib.root, fa) as s:
        rows = s.plan_rename("sub", "auto")
    assert tree(lib.root) == before
    got = {r.src.name: (r.new_path.name, r.ep_label) for r in rows}
    assert got == {"show.s01e01.srt": (f"{SHOW[0]}.vi.srt", "S01E01"), "Show S01E02 [1080p].srt": (f"{SHOW[1]}.cs.srt", "S01E02")}


def test_rename_reasons(lib, fa):
    write_srt(lib.can / "Show.S01E01.srt", REF)  # khong ro ngon ngu
    write_srt(lib.can / "vi" / f"{SHOW[1]}.vi.srt", REF)  # da dung ten
    write_srt(lib.can / "vi" / "Show.S01E03.a.srt", REF)
    write_srt(lib.can / "vi" / "Show.S01E03.b.srt", REF)  # trung dich voi file tren
    write_srt(lib.can / "en" / "Show.S01E02.srt", REF)
    write_srt(lib.can / "en" / f"{SHOW[1]}.en.srt", REF)  # dich da ton tai
    with sess(lib.root, fa) as s:
        rows = {r.src.name if r.src.parent.name != "en" else "en/" + r.src.name: r for r in s.plan_rename("sub", "auto")}
    assert rows["Show.S01E01.srt"].reason == "không đoán được ngôn ngữ"
    assert rows[f"{SHOW[1]}.vi.srt"].reason == "đã đúng tên"
    assert rows["Show.S01E03.a.srt"].ok and rows["Show.S01E03.b.srt"].reason == "trùng đích với 'Show.S01E03.a.srt'"
    assert rows["en/Show.S01E02.srt"].reason == "đích đã tồn tại"


def test_rename_forced_lang_warns_other_folder(lib, fa):
    write_srt(lib.can / "cs" / "Show.S01E01.srt", REF)
    with sess(lib.root, fa) as s:
        (row,) = s.plan_rename("sub", "vi")
    assert row.new_path.name == f"{SHOW[0]}.vi.srt"
    assert row.warn == "file nằm trong thư mục 'cs' nhưng bạn chọn 'vi'"


def test_cz_suffix_becomes_cs(lib, fa):
    """TĐ-15: hậu tố .cz → .cs."""
    write_srt(lib.can / "Show.S01E01.cz.srt", REF)
    with sess(lib.root, fa) as s:
        (row,) = s.plan_rename("sub", "auto")
        assert row.new_path.name == f"{SHOW[0]}.cs.srt"
        (row2,) = s.plan_rename("sub", "cz")
        assert row2.lang == "cs"


def test_execute_rename_then_undo(lib, fa):
    src = write_srt(lib.can / "vi" / "show.s01e01.srt", REF)
    with sess(lib.root, fa) as s:
        rows = s.plan_rename("sub", "auto")
        rep = s.execute_rename(rows)
    new = lib.can / "vi" / f"{SHOW[0]}.vi.srt"
    assert rep.ok == 1 and new.is_file() and not src.exists()
    log = undo.read_run(rep.log_path)
    assert [(r["Action"], Path(r["From"]).name, Path(r["To"]).name, r["Detail"]) for r in log] == \
        [("Rename", "show.s01e01.srt", new.name, "S01E01")]
    undo.execute_undo(undo.plan_undo(rep.log_path))
    assert src.is_file() and not new.exists()


def test_unticked_rows_not_renamed(lib, fa):
    write_srt(lib.can / "vi" / "show.s01e01.srt", REF)
    with sess(lib.root, fa) as s:
        rows = s.plan_rename("sub", "auto")
        rows[0].selected = False
        assert s.execute_rename(rows).ok == 0
    assert (lib.can / "vi" / "show.s01e01.srt").is_file()


# ==========================================================================
#  ĐỒNG BỘ (alass giả)
# ==========================================================================


def test_sync_sub_mode_full_pipeline(lib, fa):
    """Chuyển mã + cắt recap trên BẢN SAO (TĐ-12), alass không cờ, kết quả UTF-8 cạnh phim, sức khoẻ, nhật ký."""
    ref = lib.ref[SHOW[0]]
    src = write_srt(lib.can / "vi" / "show.s01e01.srt", recap_sub(ref), enc="cp1258",
                    texts=[f"Đây là tiên {i}" for i in range(430)])
    fa.set(offset=-65.0)
    with sess(lib.root, fa) as s:
        s.execute_rename(s.plan_rename("sub", "auto"))
        renamed = lib.can / "vi" / f"{SHOW[0]}.vi.srt"
        raw = renamed.read_bytes()
        pairs = s.plan_sync("sub")
        assert [p.out_path for p in pairs] == [lib.root / f"{SHOW[0]}.vi.srt"]
        assert pairs[0].relation.offset == pytest.approx(-65, abs=0.1)
        assert not (lib.root / f"{SHOW[0]}.vi.srt").exists()  # lap ke hoach khong ghi gi
        rep = s.execute_sync(pairs)
    assert not src.exists() and renamed.read_bytes() == raw  # file nguon KHONG bi sua (TD-12)
    (call,) = fa.calls()
    assert call["flags"] == [] and Path(call["ref"]) == lib.root / "srt_goc" / f"{SHOW[0]}.srt"
    out = lib.root / f"{SHOW[0]}.vi.srt"
    txt = out.read_bytes().decode("utf-8")  # da chuyen UTF-8
    assert "Đây là tiên 30\r\n" in txt and "Đây là tiên 29\r\n" not in txt  # 30 dong recap da cat
    assert srt.cue_times(out) == pytest.approx(ref, abs=0.002)
    r = pairs[0].result
    assert (rep.ok, rep.good, r.cut, r.health.level) == (1, 1, 30, 0)
    assert "cp1258 → UTF-8" in r.notes
    runs = list((lib.root / "_subprep_logs").glob("run_*.csv"))
    assert len(runs) == 1  # doi ten + dong bo = MOT lan chay
    acts = [(x["Action"], Path(x["To"]).name) for x in undo.read_run(runs[0])]
    assert acts == [("Rename", f"{SHOW[0]}.vi.srt"), ("Create", f"{SHOW[0]}.vi.srt")]
    hp = rep.health_path
    assert hp.name.startswith("health_") and "DichDau" in hp.read_text(encoding="utf-8-sig").splitlines()[0]


def test_sync_ass_stays_ass(lib, fa):
    """Q7 + TĐ-14: phụ đề ASS giữ ASS, không cắt recap (ghi lý do), sức khoẻ đọc được."""
    a = lib.can / "vi" / f"{SHOW[0]}.vi.ass"
    a.parent.mkdir(parents=True)
    a.write_text(ass_text([r + 3 for r in lib.ref[SHOW[0]]]), encoding="utf-8", newline="")
    fa.set(offset=-3.0)
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("sub")
        s.execute_sync(pairs)
    out = lib.root / f"{SHOW[0]}.vi.ass"
    assert out.is_file() and out.read_text(encoding="utf-8").startswith("[Script Info]")
    r = pairs[0].result
    assert r.health.level == 0 and r.health.fmt == "ass"
    assert "không cắt recap (ASS)" in r.notes


def test_sync_repairs_collapsed_block(lib, fa):
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", [r + 2 for r in lib.ref[SHOW[0]]])
    fa.set(offset=-2.0, zero_first=6)
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("sub")
        s.execute_sync(pairs)
    assert pairs[0].result.fixed == 6
    assert len(srt.read_blocks(lib.root / f"{SHOW[0]}.vi.srt")) == 394


def test_max_shift_zero_still_repairs_and_checks(lib, fa):
    """TĐ-20: MaxShiftSeconds = 0 chỉ tắt ngưỡng từ chối, vẫn sửa 00:00:00 + kiểm tra sức khoẻ."""
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", [r + 2 for r in lib.ref[SHOW[0]]])
    fa.set(offset=-2.0, zero_first=6)
    with sess(lib.root, fa, {"MaxShiftSeconds": 0}) as s:
        pairs = s.plan_sync("sub")
        s.execute_sync(pairs)
    assert pairs[0].result.fixed == 6 and pairs[0].result.health is not None


def test_max_shift_sub_mode_only_warns(lib, fa):
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", [r + 200 for r in lib.ref[SHOW[0]]])
    fa.set(offset=-200.0)
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("sub")
        rep = s.execute_sync(pairs)
    assert rep.ok == 1 and (lib.root / f"{SHOW[0]}.vi.srt").is_file()
    assert any(n.startswith("dịch 200s") for n in pairs[0].result.notes)


def test_sync_skip_existing_and_replace_into_trash(lib, fa, monkeypatch):
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", lib.ref[SHOW[0]])
    old = write_srt(lib.root / f"{SHOW[0]}.vi.srt", REF[:50])
    with sess(lib.root, fa) as s:
        (p,) = s.plan_sync("sub")
    assert p.reason == "đã có kết quả"
    trashed = []
    monkeypatch.setattr(undo, "trash", lambda path, rb=True: (trashed.append(Path(path)), os.remove(path))[0] is None)
    with sess(lib.root, fa, {"SkipExistingOut": False}) as s:
        pairs = s.plan_sync("sub")
        rep = s.execute_sync(pairs)
    assert rep.ok == 1 and trashed == [old]  # file cu vao Thung rac, khong ghi de mat
    acts = [r["Action"] for r in undo.read_run(rep.log_path)]
    assert acts == ["Delete", "Create"]


def test_sync_duplicate_target_and_untagged_lang(lib, fa):
    """TĐ-32: phụ đề chưa đổi tên (không hậu tố) trong thư mục vi\\ → kết quả vẫn có .vi; hai phụ đề cùng đích → bỏ cái sau."""
    write_srt(lib.can / "vi" / "Show.S01E01.a.srt", lib.ref[SHOW[0]])
    write_srt(lib.can / "vi" / "Show.S01E01.b.srt", lib.ref[SHOW[0]])
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("sub")
    assert pairs[0].out_path.name == f"{SHOW[0]}.vi.srt"
    assert pairs[1].reason == "trùng đích với 'Show.S01E01.a.srt'"


def test_alass_failure_and_retry_without_flags(lib, fa):
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", lib.ref[SHOW[0]])
    fa.set(fail=[{"ref": "sub", "flags": True}])
    with sess(lib.root, fa, {"AlassArgsSubMode": ["--no-split"]}) as s:
        pairs = s.plan_sync("sub")
        rep = s.execute_sync(pairs)
    assert [c["flags"] for c in fa.calls()] == [["--no-split"], []]
    assert rep.ok == 1 and pairs[0].result.attempt == 1
    fa.log.unlink()
    fa.set(fail=[{"ref": "any", "flags": False}, {"ref": "any", "flags": True}])
    (lib.root / f"{SHOW[0]}.vi.srt").unlink()
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("sub")
        rep = s.execute_sync(pairs)
    assert rep.errors == 1 and not (lib.root / f"{SHOW[0]}.vi.srt").exists()
    assert pairs[0].status == "thất bại (mã 1)" and "codec_long_name" in pairs[0].result.output_tail[-1]


def test_audio_mode_without_ffmpeg_tries_video_then_no_flags(lib, fa):
    """Thiếu ffmpeg: đưa thẳng file phim (có cảnh báo); lỗi kèm cờ → thử lại không cờ."""
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", lib.ref[SHOW[0]])
    fa.set(fail=[{"ref": "video", "flags": True}])
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("audio")
        rep = s.execute_sync(pairs)
        assert any("missing field codec_long_name" in m for _, m in s.job.lines)
    calls = fa.calls()
    assert [(Path(c["ref"]).name, c["flags"]) for c in calls] == \
        [(f"{SHOW[0]}.mkv", ["--no-split", "--disable-fps-guessing"]), (f"{SHOW[0]}.mkv", [])]
    assert rep.ok == 1


def test_audio_mode_fps_mismatch_from_srt_goc_drops_flag(lib, fa):
    """TĐ-18: mốc audio + có phụ đề gốc trong srt_goc → đo được lệch FPS → bỏ --disable-fps-guessing."""
    pal = [r / (25 / 23.976) for r in lib.ref[SHOW[0]]]
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", pal)
    write_srt(lib.can / "vi" / f"{SHOW[1]}.vi.srt", [r + 1 for r in lib.ref[SHOW[1]]])
    with sess(lib.root, fa) as s:
        p1, p2 = s.plan_sync("audio")
    assert p1.relation.fps_mismatch and p1.flags == ["--no-split"]
    assert p1.relation_src == f"srt_goc\\{SHOW[0]}.srt"
    assert not p2.relation.fps_mismatch and p2.flags == ["--no-split", "--disable-fps-guessing"]


def test_audio_mode_rejects_big_shift(lib, fa):
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", lib.ref[SHOW[0]])
    fa.set(offset=150.0)
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("audio")
        rep = s.execute_sync(pairs)
    assert rep.rejected == 1 and rep.ok == 0 and not (lib.root / f"{SHOW[0]}.vi.srt").exists()
    assert pairs[0].status == "từ chối (dịch 150s)"


def test_unsupported_alass_flags(lib, fa):
    fl = tools.alass_supported_flags(["x"], ["--no-split", "--split-penalty", "7", "--disable-fps-guessing"],
                                     help_text="USAGE\n --no-split\n --Disable-FPS-Guessing")
    assert fl.ok == ["--no-split", "--disable-fps-guessing"] and fl.unsupported == ["--split-penalty"]
    assert tools.alass_supported_flags(["x"], ["--a", "1"], help_text="").ok == ["--a", "1"]  # khong doc duoc --help
    fa.set(help="USAGE: alass --no-split")
    with sess(lib.root, fa) as s:
        assert s.alass_flags(["--no-split", "--disable-fps-guessing"]) == ["--no-split"]
        assert any("KHÔNG hỗ trợ: --disable-fps-guessing" in m for _, m in s.job.lines)


def test_loose_sup_in_srt_goc_used_as_ref(lib, fa):
    """``.sup`` bạn tự bỏ vào srt_goc → đọc mốc (lập kế hoạch không ghi gì); lúc chạy mới ghi ``.srt`` cạnh nó."""
    (lib.root / "srt_goc" / f"{SHOW[2]}.srt").unlink()
    ref = lib.ref[SHOW[2]]
    ev = []
    for t in ref:
        ev += [(t, "show"), (t + 1.0, "hide")]
    sup = lib.root / "srt_goc" / f"{SHOW[2]}.sup"
    sup.write_bytes(pgs_stream(ev))
    write_srt(lib.can / "vi" / f"{SHOW[2]}.vi.srt", [r + 4 for r in ref])
    fa.set(offset=-4.0)
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("sub")
        assert pairs[0].ref.file == sup and not sup.with_suffix(".srt").exists()
        s.execute_sync(pairs)
    made = sup.with_suffix(".srt")
    assert made.is_file() and len(srt.cue_times(made)) == 400
    rows = undo.read_run(next((lib.root / "_subprep_logs").glob("run_*.csv")))
    assert ("Create", made.name) in [(r["Action"], Path(r["To"]).name) for r in rows]
    assert pairs[0].result.health.level == 0


def test_stop_after_current(lib, fa):
    for s_ in SHOW[:2]:
        write_srt(lib.can / "vi" / f"{s_}.vi.srt", lib.ref[s_])
    with sess(lib.root, fa) as s:
        pairs = s.plan_sync("sub")
        s.job.stop_after_current.set()
        rep = s.execute_sync(pairs)
    assert rep.stopped and rep.ok == 0 and fa.calls() == []


def test_preview_commands_sub_mode(lib, fa):
    write_srt(lib.can / "vi" / f"{SHOW[0]}.vi.srt", lib.ref[SHOW[0]])
    with sess(lib.root, fa, {"AlassArgsSubMode": ["--no-split"]}) as s:
        (p,) = s.plan_sync("sub")
        lines = s.preview_commands(p)
    assert "--no-split" in lines[0] and f"{SHOW[0]}.srt" in lines[0]
    assert lines[1].startswith("# thử lại không kèm tham số")
    assert lines[-1].startswith("# rồi chép")


# ==========================================================================
#  DỌN DẸP + HOÀN TÁC
# ==========================================================================


def test_cleanup_and_undo_whole_run(lib, fa, monkeypatch):
    write_srt(lib.can / "vi" / "show.s01e01.srt", [r + 3 for r in lib.ref[SHOW[0]]])
    write_srt(lib.can / "vi" / "show.s01e02.srt", [r + 3 for r in lib.ref[SHOW[1]]])
    fa.set(offset=-3.0, fail=[])
    trashed = []
    monkeypatch.setattr(undo, "trash", lambda path, rb=True: trashed.append(Path(path)) is None)
    with sess(lib.root, fa) as s:
        s.execute_rename(s.plan_rename("sub", "auto"))
        pairs = s.plan_sync("sub")
        s.execute_sync(pairs)
        plan = s.cleanup_targets()
        assert plan.done == 2 and plan.where == "Thùng rác (khôi phục được)"
        assert [p.name for p in plan.targets("subs")] == [f"{SHOW[0]}.vi.srt", f"{SHOW[1]}.vi.srt"]
        assert [p.name for p in plan.targets("refs")] == [f"{SHOW[0]}.srt", f"{SHOW[1]}.srt"]
        assert plan.targets("none") == []
        rep = s.execute_cleanup(plan, "refs")
    assert rep.deleted == 2 and [p.parent.name for p in trashed] == ["srt_goc", "srt_goc"]
    log = next((lib.root / "_subprep_logs").glob("run_*.csv"))
    acts = [r["Action"] for r in undo.read_run(log)]
    assert acts == ["Rename", "Rename", "Create", "Create", "Delete", "Delete"]
    # Hoan tac ca luot: ket qua vao Thung rac, ten cu tra lai
    up = undo.plan_undo(log)
    kinds = sorted(o.kind for o in up.ops)
    assert kinds == ["rename_back", "rename_back", "trash", "trash"]
    assert any("Thùng rác" in n for n in up.notes)


def test_cleanup_none_when_nothing_synced(lib, fa):
    with sess(lib.root, fa) as s:
        assert s.cleanup_targets() is None


def test_extract_needs_mkvtoolnix(lib, fa):
    with sess(lib.root, fa) as s:
        assert s.plan_extract_refs() == []
        assert any("mkvmerge" in m for _, m in s.job.lines)
