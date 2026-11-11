"""Máy chủ HTTP giả cho test — thay ``requests.Session``. Test KHÔNG BAO GIỜ gọi mạng thật."""
from __future__ import annotations

import io
import json as _json
import re
import zipfile
from dataclasses import dataclass, field
from typing import Any, Callable


class FakeResponse:
    def __init__(self, status: int = 200, json_data: Any = None, content: bytes | None = None,
                 headers: dict | None = None, text: str | None = None):
        self.status_code = status
        self._json = json_data
        if content is None:
            content = _json.dumps(json_data).encode("utf-8") if json_data is not None else (text or "").encode("utf-8")
        self.content = content
        self.text = text if text is not None else content.decode("utf-8", "replace")
        self.headers = headers or {}

    def json(self) -> Any:
        if self._json is None:
            return _json.loads(self.text)
        return self._json


@dataclass
class Call:
    method: str
    url: str
    params: dict
    headers: dict
    json: Any
    data: Any = None  # than form (POST) - Titulky


@dataclass
class Route:
    method: str
    pattern: re.Pattern
    handler: Callable[[Call], FakeResponse] | FakeResponse | list
    hits: int = 0


@dataclass
class FakeHTTP:
    routes: list[Route] = field(default_factory=list)
    calls: list[Call] = field(default_factory=list)

    def add(self, method: str, url_regex: str, handler: Any) -> "FakeHTTP":
        """``handler``: FakeResponse | danh sách FakeResponse (lần lượt) | hàm(Call) → FakeResponse."""
        self.routes.insert(0, Route(method.upper(), re.compile(url_regex), handler))
        return self

    def request(self, method: str, url: str, params: dict | None = None, headers: dict | None = None,
                json: Any = None, timeout: Any = None, data: Any = None, **_: Any) -> FakeResponse:
        call = Call(method.upper(), url, dict(params or {}), dict(headers or {}), json, data)
        self.calls.append(call)
        for r in self.routes:
            if r.method == call.method and r.pattern.search(url):
                r.hits += 1
                h = r.handler
                if isinstance(h, list):
                    return h[min(r.hits - 1, len(h) - 1)]
                if callable(h):
                    return h(call)
                return h
        return FakeResponse(404, text="khong co route")

    def get(self, url: str, **kw: Any) -> FakeResponse:
        return self.request("GET", url, **kw)

    def post(self, url: str, **kw: Any) -> FakeResponse:
        return self.request("POST", url, **kw)

    def calls_to(self, url_regex: str, method: str | None = None) -> list[Call]:
        rx = re.compile(url_regex)
        return [c for c in self.calls if rx.search(c.url) and (method is None or c.method == method.upper())]


def zip_bytes(files: dict[str, str | bytes]) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, data in files.items():
            z.writestr(name, data if isinstance(data, bytes) else data.encode("utf-8"))
    return buf.getvalue()


class Sleeper:
    """Thay ``time.sleep``: ghi lại, không chờ thật."""

    def __init__(self):
        self.calls: list[float] = []
        self.now = 0.0

    def __call__(self, s: float) -> None:
        self.calls.append(s)
        self.now += s

    def clock(self) -> float:
        return self.now
