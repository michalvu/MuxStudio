"""Nhật ký thao tác + hoàn tác nhiều lần chạy, chung cho ba bước.

Thay ``Add-Action``/``Save-ActionLog``/``Invoke-UndoRun`` (SubFetch), ``Add-RunLog``/
``Save-RunLog``/``Invoke-Undo``/``Remove-FileSafe`` (SubPrep), ``Add-MuxLog``/``Save-MuxLog`` (MuxTool).

* File: ``_subfetch_logs\\actions_*.csv``, ``_subprep_logs\\run_*.csv``, ``_muxtool_logs\\mux_*.csv``.
* Cột: ``Time, Action, From, To, Detail, Status`` — đọc được cả nhật ký cũ của .bat
  (SubFetch: ``Time, Action, From, To``; SubPrep: ``… Detail``; MuxTool cũ: ``Time, File, Status,
  Output, Detail`` — loại này không có thông tin để hoàn tác).
* Giữ ``keep`` (20) lần gần nhất mỗi bước. Hoàn tác: file tạo ra → Thùng rác; file đã di
  chuyển/đổi tên → trả về chỗ cũ, theo thứ tự ngược. Khác .bat: không bao giờ xoá hẳn (TĐ-16).
"""
from __future__ import annotations

import csv
import datetime as _dt
import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Iterable

STEP_PREFIX = {"subfetch": "actions_", "subprep": "run_", "mux": "mux_"}
COLUMNS = ("Time", "Action", "From", "To", "Detail", "Status")

# Hanh dong ghi duoc va cach hoan tac
CREATE = "Create"  # tao file moi (To)            -> Thung rac
COPY = "Copy"  # chep file (From -> To)            -> Thung rac file To
MOVE = "Move"  # di chuyen (From -> To)            -> tra To ve From
RENAME = "Rename"  # doi ten (From -> To)          -> doi To ve ten cu
MKDIR = "Mkdir"  # tao thu muc moi (To)            -> xoa neu rong
DELETE = "Delete"  # da xoa vao Thung rac (From)   -> khong tu khoi phuc duoc
ENCODING = "Encoding"  # nhat ky cu cua SubPrep      -> khong hoan tac duoc


# --------------------------------------------------------------------------
#  Xoá an toàn
# --------------------------------------------------------------------------


def trash(path: str | os.PathLike[str], to_recycle_bin: bool = True) -> bool:
    """Xoá một file/thư mục: vào Thùng rác (``send2trash``) hoặc xoá hẳn khi tắt ``DeleteToRecycleBin``.

    Khác ``Remove-FileSafe``: Thùng rác lỗi thì KHÔNG tự chuyển sang xoá hẳn — trả ``False``.
    """
    p = os.fspath(path)
    if not os.path.lexists(p):
        return False
    if to_recycle_bin:
        try:
            from send2trash import send2trash  # noqa: PLC0415

            send2trash(p)
            return True
        except Exception:
            return False
    try:
        if os.path.isdir(p) and not os.path.islink(p):
            os.rmdir(p)
        else:
            os.remove(p)
        return True
    except OSError:
        return False


# --------------------------------------------------------------------------
#  Ghi nhật ký
# --------------------------------------------------------------------------


@dataclass
class LogRow:
    Time: str
    Action: str
    From: str = ""
    To: str = ""
    Detail: str = ""
    Status: str = ""


class RunLog:
    """Nhật ký của MỘT lần chạy. ``save()`` ghi CSV (UTF-8 có BOM, mọi ô có ngoặc kép — như ``Export-Csv``)."""

    def __init__(self, root: str | os.PathLike[str], log_dir_name: str, step: str, keep: int = 20):
        if step not in STEP_PREFIX:
            raise ValueError(step)
        self.root = Path(root)
        self.log_dir = self.root / log_dir_name
        self.step = step
        self.keep = keep
        self.rows: list[LogRow] = []

    def add(self, action: str, from_: str | os.PathLike[str] = "", to: str | os.PathLike[str] = "",
            detail: str = "", status: str = "") -> None:
        self.rows.append(LogRow(_dt.datetime.now().strftime("%H:%M:%S"), action,
                                os.fspath(from_), os.fspath(to), detail, status))

    def save(self, now: _dt.datetime | None = None) -> Path | None:
        if not self.rows:
            return None
        self.log_dir.mkdir(parents=True, exist_ok=True)
        stamp = (now or _dt.datetime.now()).strftime("%Y%m%d_%H%M%S")
        prefix = STEP_PREFIX[self.step]
        path = self.log_dir / f"{prefix}{stamp}.csv"
        n = 2
        while path.exists():
            path = self.log_dir / f"{prefix}{stamp}_{n}.csv"
            n += 1
        with open(path, "w", encoding="utf-8-sig", newline="") as fh:
            w = csv.writer(fh, quoting=csv.QUOTE_ALL, lineterminator="\r\n")
            w.writerow(COLUMNS)
            for r in self.rows:
                w.writerow([r.Time, r.Action, r.From, r.To, r.Detail, r.Status])
        rotate_logs(self.log_dir, prefix, self.keep)
        return path


def rotate_logs(log_dir: Path, prefix: str, keep: int) -> None:
    """Chỉ giữ ``keep`` nhật ký mới nhất (theo tên, giảm dần)."""
    if keep <= 0 or not log_dir.is_dir():
        return
    files = sorted((e.name for e in os.scandir(log_dir)
                    if e.is_file() and e.name.startswith(prefix) and e.name.lower().endswith(".csv")), reverse=True)
    for name in files[keep:]:
        try:
            os.remove(log_dir / name)
        except OSError:
            pass


# --------------------------------------------------------------------------
#  Đọc nhật ký + hoàn tác
# --------------------------------------------------------------------------


def read_run(path: str | os.PathLike[str]) -> list[dict[str, str]]:
    with open(path, encoding="utf-8-sig", newline="") as fh:
        return [dict(r) for r in csv.DictReader(fh)]


@dataclass
class RunInfo:
    path: Path
    name: str
    counts: dict[str, int]
    undoable: bool
    note: str = ""


def list_runs(root: str | os.PathLike[str], log_dir_name: str, step: str) -> list[RunInfo]:
    """Các lần chạy (mới nhất trước) cho màn hình Hoàn tác."""
    d = Path(root) / log_dir_name
    prefix = STEP_PREFIX[step]
    if not d.is_dir():
        return []
    out: list[RunInfo] = []
    names = sorted((e.name for e in os.scandir(d)
                    if e.is_file() and e.name.startswith(prefix) and e.name.lower().endswith(".csv")), reverse=True)
    for name in names:
        p = d / name
        try:
            rows = read_run(p)
        except (OSError, csv.Error, UnicodeDecodeError):
            out.append(RunInfo(p, p.stem, {}, False, "không đọc được nhật ký"))
            continue
        counts: dict[str, int] = {}
        for r in rows:
            a = r.get("Action") or ""
            if a:
                counts[a] = counts.get(a, 0) + 1
        undoable = any(a in (CREATE, COPY, MOVE, RENAME, MKDIR) for a in counts)
        note = "" if undoable else ("nhật ký cũ của .bat — không có thông tin để hoàn tác"
                                    if rows and "Action" not in rows[0] else "không có thao tác hoàn tác được")
        out.append(RunInfo(p, p.stem, counts, undoable, note))
    return out


@dataclass
class UndoOp:
    kind: str  # trash | move_back | rename_back | rmdir
    path: str  # file/thu muc se tac dong
    target: str = ""  # cho move_back/rename_back: duong dan tra ve
    row: int = 0


@dataclass
class UndoPlan:
    log_path: Path
    ops: list[UndoOp] = field(default_factory=list)
    missing: list[str] = field(default_factory=list)  # file khong con -> bo qua
    notes: list[str] = field(default_factory=list)


def plan_undo(log_path: str | os.PathLike[str]) -> UndoPlan:
    """Lập kế hoạch hoàn tác một lần chạy (chưa đụng tới file nào) — để hiện hộp thoại xác nhận."""
    lp = Path(log_path)
    rows = read_run(lp)
    plan = UndoPlan(lp)
    n_delete = n_enc = 0
    # 1. File tao ra / chep ra -> Thung rac
    for i, r in enumerate(rows):
        a = r.get("Action", "")
        if a in (CREATE, COPY):
            to = r.get("To", "")
            if to and os.path.isfile(to):
                plan.ops.append(UndoOp("trash", to, row=i))
            elif to:
                plan.missing.append(to)
        elif a == DELETE:
            n_delete += 1
        elif a == ENCODING:
            n_enc += 1
    # 2. Di chuyen / doi ten -> tra ve, theo thu tu NGUOC
    for i in range(len(rows) - 1, -1, -1):
        r = rows[i]
        a = r.get("Action", "")
        src, to = r.get("From", ""), r.get("To", "")
        if a == MOVE:
            if os.path.isfile(to):
                plan.ops.append(UndoOp("move_back", to, src, i))
            else:
                plan.missing.append(to)
        elif a == RENAME:
            if os.path.isfile(to):
                back = os.path.join(os.path.dirname(to), os.path.basename(src))
                plan.ops.append(UndoOp("rename_back", to, back, i))
            else:
                plan.missing.append(to)
    # 3. Thu muc moi tao -> xoa neu rong (sau cung, thu muc sau truoc)
    dirs = [r.get("To", "") for r in rows if r.get("Action") == MKDIR and r.get("To")]
    for d in sorted(set(dirs), key=lambda x: x.count(os.sep), reverse=True):
        plan.ops.append(UndoOp("rmdir", d))
    if n_delete:
        plan.notes.append(f"{n_delete} file đã được xoá vào Thùng rác — muốn lấy lại thì khôi phục trong Thùng rác.")
    if n_enc:
        plan.notes.append(f"{n_enc} lần chuyển mã của .bat cũ đã ghi đè file nguồn — không hoàn tác được.")
    return plan


@dataclass
class UndoResult:
    done: list[str] = field(default_factory=list)
    skipped: list[tuple[str, str]] = field(default_factory=list)  # (duong dan, ly do)


def execute_undo(plan: UndoPlan, to_recycle_bin: bool = True,
                 trash_fn: Callable[[str, bool], bool] = trash, remove_log: bool = True) -> UndoResult:
    """Thực hiện kế hoạch hoàn tác. Không bao giờ xoá hẳn file của bạn khi ``to_recycle_bin``."""
    res = UndoResult()
    for op in plan.ops:
        try:
            if op.kind == "trash":
                if trash_fn(op.path, to_recycle_bin):
                    res.done.append(op.path)
                else:
                    res.skipped.append((op.path, "không xoá được"))
            elif op.kind in ("move_back", "rename_back"):
                if not os.path.isfile(op.path):
                    res.skipped.append((op.path, "không còn file"))
                    continue
                if os.path.lexists(op.target):
                    if op.kind == "rename_back":
                        res.skipped.append((op.path, f"đã có file trùng tên cũ: {os.path.basename(op.target)}"))
                        continue
                    # Move (SubFetch): cho cu da co file moi -> dua file do vao Thung rac roi tra ve
                    if not trash_fn(op.target, to_recycle_bin):
                        res.skipped.append((op.path, "không dọn được file đang nằm ở chỗ cũ"))
                        continue
                os.makedirs(os.path.dirname(op.target) or ".", exist_ok=True)
                os.rename(op.path, op.target)
                res.done.append(op.target)
            elif op.kind == "rmdir":
                if os.path.isdir(op.path) and not os.listdir(op.path):
                    os.rmdir(op.path)
                    res.done.append(op.path)
        except OSError as e:
            res.skipped.append((op.path, str(e)))
    if remove_log:
        try:
            os.remove(plan.log_path)
        except OSError:
            pass
    return res


def undo_runs(log_paths: Iterable[str | os.PathLike[str]], to_recycle_bin: bool = True,
              trash_fn: Callable[[str, bool], bool] = trash) -> UndoResult:
    """Hoàn tác nhiều lần chạy, mới nhất trước."""
    total = UndoResult()
    for p in sorted((Path(x) for x in log_paths), key=lambda x: x.name, reverse=True):
        r = execute_undo(plan_undo(p), to_recycle_bin, trash_fn)
        total.done += r.done
        total.skipped += r.skipped
    return total
