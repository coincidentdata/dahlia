import sqlite3
import time
from contextlib import contextmanager
from datetime import timezone, datetime
from pathlib import Path


class RequestLimitError(RuntimeError):
    pass


class RequestBudget:
    """Counts HTTP attempts per UTC day in a shared sqlite ledger; failures count and
    nothing is retried automatically. The limit only stops runaway loops; Onshape's own
    allowance is yearly (see docs/onshape.md)."""

    def __init__(self, path: str | Path, *, limit: int = 1000):
        if not isinstance(limit, int) or isinstance(limit, bool) or limit < 1:
            raise ValueError("request limit must be a positive integer")
        self.path = Path(path).expanduser().resolve()
        self.limit = limit
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as db:
            db.execute("CREATE TABLE IF NOT EXISTS requests (day TEXT PRIMARY KEY, attempts INTEGER NOT NULL)")
            db.execute("CREATE TABLE IF NOT EXISTS throttle (id INTEGER PRIMARY KEY CHECK (id = 1), blocked_until REAL NOT NULL)")
            db.execute("INSERT OR IGNORE INTO throttle VALUES (1, 0)")
            legacy = db.execute("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'usage'").fetchone()
            if legacy:
                attempts, blocked_until = db.execute("SELECT attempts, blocked_until FROM usage WHERE id = 1").fetchone()
                db.execute("INSERT OR IGNORE INTO requests VALUES (?, ?)", (self._today(), attempts))
                db.execute("UPDATE throttle SET blocked_until = MAX(blocked_until, ?) WHERE id = 1", (blocked_until,))
                db.execute("DROP TABLE usage")

    @staticmethod
    def _today() -> str:
        return datetime.now(timezone.utc).date().isoformat()

    @contextmanager
    def _connect(self):
        db = sqlite3.connect(self.path, timeout=5)
        try:
            with db:
                yield db
        finally:
            db.close()

    @property
    def used(self) -> int:
        with self._connect() as db:
            row = db.execute("SELECT attempts FROM requests WHERE day = ?", (self._today(),)).fetchone()
        return row[0] if row else 0

    def reserve(self) -> None:
        today = self._today()
        with self._connect() as db:
            db.execute("BEGIN IMMEDIATE")
            blocked_until = db.execute("SELECT blocked_until FROM throttle WHERE id = 1").fetchone()[0]
            if blocked_until > time.time():
                raise RequestLimitError(f"Onshape throttled requests until {blocked_until}; no request sent")
            db.execute("INSERT OR IGNORE INTO requests VALUES (?, 0)", (today,))
            used = db.execute("SELECT attempts FROM requests WHERE day = ?", (today,)).fetchone()[0]
            if used >= self.limit:
                raise RequestLimitError(f"Onshape request budget exhausted for {today} ({used}/{self.limit}); no request sent")
            db.execute("UPDATE requests SET attempts = attempts + 1 WHERE day = ?", (today,))

    def block_until(self, deadline: float) -> None:
        with self._connect() as db:
            db.execute("UPDATE throttle SET blocked_until = MAX(blocked_until, ?) WHERE id = 1", (deadline,))

    def release(self) -> None:
        """Lift a server hold early, after a request outside this ledger confirmed the server accepts again."""
        with self._connect() as db:
            db.execute("UPDATE throttle SET blocked_until = 0 WHERE id = 1")
