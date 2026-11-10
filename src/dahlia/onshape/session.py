import os
from pathlib import Path

from .budget import RequestBudget
from .file import PartStudio
from .oauth import OAuthTokens
from .transport import Transport


class Session:
    def __init__(self, transport: Transport):
        self.transport = transport

    def open_file(self, url: str) -> PartStudio:
        return PartStudio.from_url(self.transport, url)

    def create_file(self, name: str, *, public: bool = False) -> PartStudio:
        if not name.strip():
            raise ValueError("A document name is required")
        document = self.transport.request("POST", "/api/v9/documents", body={"name": name, "isPublic": public})
        did, wid = document["id"], document["defaultWorkspace"]["id"]
        elements = self.transport.request("GET", f"/api/v9/documents/d/{did}/w/{wid}/elements")
        studios = [element for element in elements if element["elementType"] == "PARTSTUDIO"]
        if len(studios) != 1:
            raise RuntimeError(f"Created document {did}, but expected one default Part Studio, found {len(studios)}")
        return PartStudio(self.transport, did, wid, studios[0]["id"])


def connect(*, budget: RequestBudget | None = None, access_key: str | None = None,
            secret_key: str | None = None, base_url: str = "https://cad.onshape.com",
            oauth: OAuthTokens | None = None) -> Session:
    """Authenticates with `oauth`, else with a Developer Portal application named by
    ONSHAPE_CLIENT_ID / ONSHAPE_CLIENT_SECRET, else with the API key pair from the
    arguments or ONSHAPE_ACCESS_KEY / ONSHAPE_SECRET_KEY."""
    if budget is None:
        budget = RequestBudget(Path.home() / ".dahlia" / "onshape-requests.sqlite3",
                               limit=int(os.environ.get("ONSHAPE_REQUEST_LIMIT", 1000)))
    if oauth is None and not (access_key or secret_key):
        client_id, client_secret = os.environ.get("ONSHAPE_CLIENT_ID"), os.environ.get("ONSHAPE_CLIENT_SECRET")
        if client_id and client_secret:
            oauth = OAuthTokens(client_id, client_secret)
    return Session(Transport(budget, access_key=access_key, secret_key=secret_key, base_url=base_url, oauth=oauth))
