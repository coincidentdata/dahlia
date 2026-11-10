"""OAuth2 authorization-code flow for an Onshape application registered in the Developer
Portal: a one-time browser sign-in through a localhost redirect, then stored tokens that
refresh themselves. Tokens live outside the repository in ~/.dahlia/onshape-oauth.json."""
import json
import secrets
import time
import webbrowser
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import Request, urlopen

AUTHORIZE_URL = "https://oauth.onshape.com/oauth/authorize"
TOKEN_URL = "https://oauth.onshape.com/oauth/token"
DEFAULT_SCOPES = ("OAuth2Read", "OAuth2Write")
TOKEN_FILE = Path.home() / ".dahlia" / "onshape-oauth.json"


class OAuthTokens:
    def __init__(self, client_id: str, client_secret: str, *, path: Path | None = None, opener=urlopen):
        self.client_id = client_id
        self.client_secret = client_secret
        self.path = Path(path or TOKEN_FILE).expanduser()
        self._open = opener
        self._tokens = json.loads(self.path.read_text(encoding="utf-8")) if self.path.exists() else None

    @property
    def authorized(self) -> bool:
        return bool(self._tokens and self._tokens.get("refresh_token"))

    def access_token(self) -> str:
        """A valid bearer token, refreshed when within a minute of expiry."""
        if not self.authorized:
            raise RuntimeError("Not authorized; run OAuthTokens.authorize() once in a browser session")
        if time.time() > self._tokens.get("expires_at", 0) - 60:
            self._store(self._exchange({"grant_type": "refresh_token", "refresh_token": self._tokens["refresh_token"]}))
        return self._tokens["access_token"]

    def authorize(self, *, scopes=DEFAULT_SCOPES, port: int = 8765, open_browser=webbrowser.open, timeout: float = 300) -> None:
        """Send the user to Onshape's consent page and exchange the returned code for tokens.
        The application must list http://localhost:<port> as a redirect URL."""
        state = secrets.token_urlsafe(16)
        redirect = f"http://localhost:{port}"
        url = AUTHORIZE_URL + "?" + urlencode({"response_type": "code", "client_id": self.client_id,
                                              "redirect_uri": redirect, "scope": " ".join(scopes), "state": state})
        received = {}

        class Callback(BaseHTTPRequestHandler):
            def do_GET(self):
                received.update({k: v[0] for k, v in parse_qs(urlsplit(self.path).query).items()})
                self.send_response(200)
                self.send_header("Content-Type", "text/plain")
                self.end_headers()
                self.wfile.write(b"Dahlia is authorized; you can close this tab.")

            def log_message(self, *args):
                pass

        with HTTPServer(("localhost", port), Callback) as server:
            server.timeout = timeout
            open_browser(url)
            server.handle_request()
        if received.get("state") != state or "code" not in received:
            raise RuntimeError(f"OAuth redirect did not carry a matching code: {received.get('error', received)}")
        self._store(self._exchange({"grant_type": "authorization_code", "code": received["code"], "redirect_uri": redirect}))

    def _exchange(self, fields: dict) -> dict:
        body = urlencode({**fields, "client_id": self.client_id, "client_secret": self.client_secret}).encode()
        request = Request(TOKEN_URL, data=body, method="POST",
                          headers={"Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json"})
        with self._open(request, timeout=60) as response:
            payload = json.loads(response.read())
        if "access_token" not in payload:
            raise RuntimeError(f"Onshape token endpoint returned no access token: {payload}")
        return payload

    def _store(self, payload: dict) -> None:
        self._tokens = {"access_token": payload["access_token"],
                        "refresh_token": payload.get("refresh_token", (self._tokens or {}).get("refresh_token")),
                        "expires_at": time.time() + float(payload.get("expires_in", 3600))}
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(self._tokens), encoding="utf-8")
