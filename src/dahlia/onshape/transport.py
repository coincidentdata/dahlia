import base64
import hashlib
import hmac
import json
import os
import secrets
import time
from email.utils import formatdate, parsedate_to_datetime
from urllib.error import HTTPError
from urllib.parse import urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener

from .budget import RequestBudget


class OnshapeError(RuntimeError):
    def __init__(self, message: str, *, status: int | None = None):
        super().__init__(message)
        self.status = status


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Transport:
    def __init__(self, budget: RequestBudget, *, access_key: str | None = None,
                 secret_key: str | None = None, base_url: str = "https://cad.onshape.com", oauth=None):
        self.budget = budget
        self.base_url = base_url.rstrip("/")
        origin = urlsplit(self.base_url)
        if (origin.scheme != "https" or not origin.hostname
                or not origin.hostname.endswith(".onshape.com")
                or origin.username or origin.password or origin.path or origin.query or origin.fragment):
            raise ValueError("base_url must be an HTTPS Onshape origin")
        self._oauth = oauth
        self._access_key = access_key or os.environ.get("ONSHAPE_ACCESS_KEY")
        self._secret_key = secret_key or os.environ.get("ONSHAPE_SECRET_KEY")
        if oauth is None and not (self._access_key and self._secret_key):
            raise ValueError("Set ONSHAPE_ACCESS_KEY and ONSHAPE_SECRET_KEY, or pass OAuth tokens")
        self._opener = build_opener(_NoRedirect())
        self.rate_limit_remaining: int | None = None
        self.rate_limit: dict = {}

    def request(self, method: str, path: str, *, body=None, params=None):
        data = self._send(method, path, body=body, params=params, accept="application/json")
        return json.loads(data) if data else None

    def download(self, path: str) -> bytes:
        return self._send("GET", path, accept="application/octet-stream")

    def _send(self, method: str, path: str, *, body=None, params=None, accept: str) -> bytes:
        if not path.startswith("/api/") or urlsplit(path).query or urlsplit(path).fragment:
            raise ValueError("request path must start with /api/ and have no query or fragment")
        method = method.upper()
        query = urlencode({key: str(value).lower() if isinstance(value, bool) else value
                           for key, value in (params or {}).items()})
        data = None if body is None else json.dumps(body, allow_nan=False).encode("utf-8")
        content_type = "application/json"
        headers = {"Accept": accept, "Content-Type": content_type}
        if self._oauth is not None:
            headers["Authorization"] = f"Bearer {self._oauth.access_token()}"
        else:
            nonce = secrets.token_hex(16)
            date = formatdate(usegmt=True)
            signed = "\n".join((method, nonce, date, content_type, path, query, "")).lower()
            signature = base64.b64encode(hmac.new(
                self._secret_key.encode(), signed.encode(), hashlib.sha256).digest()).decode()
            headers.update({"Date": date, "On-Nonce": nonce, "Authorization": f"On {self._access_key}:HmacSHA256:{signature}"})
        request = Request(self.base_url + path + ("?" + query if query else ""), data=data, method=method, headers=headers)
        self.budget.reserve()
        try:
            with self._opener.open(request, timeout=60) as response:
                self._read_limits(response.headers)
                return response.read()
        except HTTPError as error:
            self._read_limits(error.headers)
            if error.code == 429:
                # Retry-After has been observed to outlive the throttle by hours; a 429 costs no
                # allowance, so hold at most ten minutes before probing again.
                retry = error.headers.get("Retry-After")
                if retry is None:
                    deadline = time.time() + 60
                else:
                    try:
                        deadline = time.time() + max(0, float(retry))
                    except ValueError:
                        deadline = parsedate_to_datetime(retry).timestamp()
                self.budget.block_until(min(deadline, time.time() + 600))
            detail = error.read().decode("utf-8", errors="replace")
            for credential in (self._access_key, self._secret_key):
                if credential:
                    detail = detail.replace(credential, "[redacted]")
            raise OnshapeError(f"Onshape HTTP {error.code}: {detail[:2000]}", status=error.code) from None

    def _read_limits(self, headers):
        remaining = headers.get("X-Rate-Limit-Remaining")
        self.rate_limit_remaining = int(remaining) if remaining is not None else None
        self.rate_limit = {key: value for key, value in headers.items()
                           if key.lower().startswith("x-rate-limit") or key.lower() == "retry-after"}
