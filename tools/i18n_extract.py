"""Rút mọi câu chữ tiếng Việt HIỂN THỊ ra khỏi mã nguồn (``core/``, ``gui/``) để dịch (bản 1.2.0).

Mỗi câu thành một MẪU: phần chữ cố định giữ nguyên, mỗi chỗ chèn giá trị (``{…}`` của f-string, ``.format``) thành
``{}``. Bỏ docstring và chuỗi không có chữ tiếng Việt có dấu. In ra danh sách mẫu (mỗi mẫu một dòng JSON) kèm nơi gặp.

    python tools/i18n_extract.py            # liệt kê mọi mẫu
    python tools/i18n_extract.py --missing  # chỉ mẫu CHƯA có trong core/i18n_en.py
"""
from __future__ import annotations

import ast
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VI = re.compile("[àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ"
                "ÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬĐÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴ]")
FMT = re.compile(r"\{[^{}]*\}")


def _docstrings(tree: ast.AST) -> set[int]:
    out = set()
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            body = getattr(node, "body", [])
            if body and isinstance(body[0], ast.Expr) and isinstance(body[0].value, ast.Constant) \
                    and isinstance(body[0].value.value, str):
                out.add(id(body[0].value))
    return out


def template_of(node: ast.AST) -> str | None:
    """Mẫu của một nút chuỗi: hằng chuỗi (``{0}`` / ``{tên}`` của ``.format`` → ``{}``) hoặc f-string."""
    if isinstance(node, ast.Constant) and isinstance(node.value, str):
        return FMT.sub("{}", node.value)
    if isinstance(node, ast.JoinedStr):
        parts = []
        for v in node.values:
            if isinstance(v, ast.Constant):
                parts.append(str(v.value))
            else:
                parts.append("{}")
        return "".join(parts)
    return None


def extract_file(path: str) -> list[tuple[str, int]]:
    with open(path, encoding="utf-8") as fh:
        tree = ast.parse(fh.read(), path)
    skip = _docstrings(tree)
    inner: set[int] = set()  # hang chuoi nam TRONG f-string: da tinh theo f-string
    for node in ast.walk(tree):
        if isinstance(node, ast.JoinedStr):
            for v in node.values:
                if isinstance(v, ast.Constant):
                    inner.add(id(v))
    out = []
    for node in ast.walk(tree):
        if id(node) in skip or id(node) in inner:
            continue
        t = template_of(node)
        if t and VI.search(t):
            out.append((t, getattr(node, "lineno", 0)))
    return out


def extract_all() -> dict[str, list[str]]:
    """``{mẫu: [nơi gặp]}`` theo thứ tự file / dòng."""
    found: dict[str, list[str]] = {}
    for pkg in ("core", "gui"):
        d = os.path.join(ROOT, pkg)
        for name in sorted(os.listdir(d)):
            if not name.endswith(".py") or name.startswith("i18n"):
                continue
            for t, line in sorted(extract_file(os.path.join(d, name)), key=lambda x: x[1]):
                found.setdefault(t, []).append(f"{pkg}/{name}:{line}")
    return found


def main() -> None:
    found = extract_all()
    known: dict[str, str] = {}
    if "--missing" in sys.argv:
        sys.path.insert(0, ROOT)
        from core import i18n_en  # noqa: PLC0415

        known = i18n_en.EN
    n = 0
    for t, where in found.items():
        if t in known:
            continue
        n += 1
        print(json.dumps(t, ensure_ascii=False) + "  # " + where[0])
    print(f"# {n} mẫu" + (" chưa dịch" if known else ""), file=sys.stderr)


if __name__ == "__main__":
    main()
