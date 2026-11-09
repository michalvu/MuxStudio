"""Nhật ký + hoàn tác (cả ba bước), Thùng rác, đọc nhật ký cũ của .bat."""
from __future__ import annotations

import datetime as dt
import os

import pytest

from core import undo as U


class FakeTrash:
    def __init__(self):
        self.items: list[str] = []

    def __call__(self, path, to_recycle_bin=True):
        self.items.append(os.fspath(path))
        os.remove(path)
        return True


def write(p, text="x"):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8", newline="")
    return p


def test_runlog_csv_format_and_rotation(movie_dir):
    for i in range(23):
        log = U.RunLog(movie_dir, "_subprep_logs", "subprep", keep=20)
        log.add(U.RENAME, movie_dir / "a.srt", movie_dir / "b.srt", "S01E01")
        log.save(now=dt.datetime(2026, 1, 1, 10, 0, i))
    files = sorted(os.listdir(movie_dir / "_subprep_logs"))
    assert len(files) == 20 and files[0] == "run_20260101_100003.csv"
    raw = (movie_dir / "_subprep_logs" / files[-1]).read_bytes()
    assert raw.startswith(b"\xef\xbb\xbf")  # UTF-8 co BOM nhu Export-Csv
    assert raw.split(b"\r\n")[0] == b'\xef\xbb\xbf"Time","Action","From","To","Detail","Status"'
    assert U.RunLog(movie_dir, "_x", "mux").save() is None  # khong co gi -> khong ghi


def test_same_second_does_not_overwrite(movie_dir):
    now = dt.datetime(2026, 1, 1, 10, 0, 0)
    paths = []
    for _ in range(2):
        log = U.RunLog(movie_dir, "_muxtool_logs", "mux")
        log.add(U.CREATE, "", movie_dir / "o.mkv")
        paths.append(log.save(now=now))
    assert paths[0] != paths[1] and all(p.exists() for p in paths)


def test_undo_create_copy_move_rename_mkdir(movie_dir):
    trash = FakeTrash()
    sub = movie_dir / "srt_can_sync" / "vi"
    created = write(movie_dir / "Output" / "Phim (2020) [tmdbid-1]" / "Phim (2020) [tmdbid-1].mkv")
    copied = write(sub / "[Judas] Show - 01.vi.srt")
    moved_from = sub / "goc.srt"
    moved_to = write(sub / "moi.srt", "noi dung goc")
    ren_from = sub / "ten cu [1080p].srt"
    ren_to = write(sub / "Show.S01E02.vi.srt", "doi ten")
    log = U.RunLog(movie_dir, "_subfetch_logs", "subfetch")
    log.add(U.MKDIR, "", movie_dir / "Output" / "Phim (2020) [tmdbid-1]")
    log.add(U.CREATE, "", created)
    log.add(U.COPY, sub / "nguon.srt", copied)
    log.add(U.MOVE, moved_from, moved_to)
    log.add(U.RENAME, ren_from, ren_to)
    lp = log.save()

    runs = U.list_runs(movie_dir, "_subfetch_logs", "subfetch")
    assert len(runs) == 1 and runs[0].undoable and runs[0].counts[U.CREATE] == 1

    plan = U.plan_undo(lp)
    assert [o.kind for o in plan.ops] == ["trash", "trash", "rename_back", "move_back", "rmdir"]
    res = U.execute_undo(plan, trash_fn=trash)
    assert set(trash.items) == {os.fspath(created), os.fspath(copied)}
    assert moved_from.read_text(encoding="utf-8") == "noi dung goc" and not moved_to.exists()
    assert ren_from.read_text(encoding="utf-8") == "doi ten" and not ren_to.exists()
    assert not (movie_dir / "Output" / "Phim (2020) [tmdbid-1]").exists()
    assert not lp.exists()  # nhat ky da hoan tac -> bo
    assert not res.skipped


def test_undo_rename_conflict_is_skipped_not_overwritten(movie_dir):
    a = write(movie_dir / "a.srt", "cu")
    b = write(movie_dir / "b.srt", "moi")
    log = U.RunLog(movie_dir, "_subprep_logs", "subprep")
    log.add(U.RENAME, a, b)
    res = U.execute_undo(U.plan_undo(log.save()), trash_fn=FakeTrash())
    assert a.read_text(encoding="utf-8") == "cu" and b.exists()
    assert res.skipped


def test_missing_files_reported(movie_dir):
    log = U.RunLog(movie_dir, "_subfetch_logs", "subfetch")
    log.add(U.CREATE, "", movie_dir / "da-xoa.srt")
    plan = U.plan_undo(log.save())
    assert plan.missing == [os.fspath(movie_dir / "da-xoa.srt")] and not plan.ops


def test_reads_old_bat_logs(movie_dir):
    d = movie_dir / "_subfetch_logs"
    d.mkdir()
    made = write(movie_dir / "srt_can_sync" / "vi" / "x.vi.srt")
    (d / "actions_20250101_120000.csv").write_text(
        '"Time","Action","From","To"\r\n"10:00:00","Create","Release.Name","' + os.fspath(made) + '"\r\n', encoding="utf-8-sig", newline="")
    runs = U.list_runs(movie_dir, "_subfetch_logs", "subfetch")
    assert runs[0].undoable
    trash = FakeTrash()
    U.execute_undo(U.plan_undo(runs[0].path), trash_fn=trash)
    assert trash.items == [os.fspath(made)]

    d2 = movie_dir / "_subprep_logs"
    d2.mkdir()
    (d2 / "run_20250101_120000.csv").write_text(
        '"Time","Action","From","To","Detail"\r\n"10:00:00","Encoding","a.srt","","cp1258 -> utf8"\r\n'
        '"10:00:01","Delete","b.srt","","Thung rac"\r\n', encoding="utf-8-sig", newline="")
    plan = U.plan_undo(d2 / "run_20250101_120000.csv")
    assert not plan.ops and len(plan.notes) == 2

    d3 = movie_dir / "_muxtool_logs"
    d3.mkdir()
    (d3 / "mux_20250101_120000.csv").write_text(
        '"Time","File","Status","Output","Detail"\r\n"10:00:00","a.mkv","OK","a","x"\r\n', encoding="utf-8-sig", newline="")
    runs = U.list_runs(movie_dir, "_muxtool_logs", "mux")
    assert not runs[0].undoable and "cũ" in runs[0].note


def test_undo_many_runs_newest_first(movie_dir):
    f1 = write(movie_dir / "1.srt")
    f2 = write(movie_dir / "2.srt")
    for i, f in enumerate((f1, f2)):
        log = U.RunLog(movie_dir, "_subfetch_logs", "subfetch")
        log.add(U.CREATE, "", f)
        log.save(now=dt.datetime(2026, 1, 1, 10, 0, i))
    trash = FakeTrash()
    paths = [r.path for r in U.list_runs(movie_dir, "_subfetch_logs", "subfetch")]
    U.undo_runs(paths, trash_fn=trash)
    assert trash.items == [os.fspath(f2), os.fspath(f1)]


def test_trash_uses_send2trash(monkeypatch, tmp_path):
    called = []
    import send2trash

    monkeypatch.setattr(send2trash, "send2trash", lambda p: called.append(p))
    f = write(tmp_path / "[x] a.srt")
    assert U.trash(f) is True and called == [os.fspath(f)]
    assert f.exists()  # da "giao" cho Thung rac gia, khong tu xoa


def test_trash_failure_does_not_delete_permanently(monkeypatch, tmp_path):
    import send2trash

    def boom(p):
        raise OSError("khong co Thung rac")

    monkeypatch.setattr(send2trash, "send2trash", boom)
    f = write(tmp_path / "a.srt")
    assert U.trash(f) is False and f.exists()
    assert U.trash(f, to_recycle_bin=False) is True and not f.exists()
    assert U.trash(tmp_path / "khong-co") is False
