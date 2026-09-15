"""Batch-export SolidWorks parts to STEP / STL via the live plugin.

    dahlia-export <input ...> [--formats step,stl] [--out-dir DIR] [--overwrite]

Each ``<input>`` is a ``.SLDPRT`` file or a directory (scanned recursively for
``*.SLDPRT``). By default each part is exported next to its source; ``--out-dir``
writes into DIR, mirroring the path relative to the scanned input directory (so
``archive/1/01/01.SLDPRT`` -> ``DIR/1/01/01.step``).

Export is just ``File.save(path)`` — ``SaveAs3`` picks the format from the
extension. Requires a SolidWorks install with the Dahlia add-in registered;
``connect()`` attaches to a running instance or cold-starts one.
"""
from __future__ import annotations

import argparse
import sys
import traceback
from dataclasses import dataclass
from pathlib import Path

from . import close_all_files, connect, open_file

VALID_FORMATS = ("step", "stl")


@dataclass(slots=True)
class Job:
    src: Path                 # absolute .SLDPRT path
    out_dir: Path             # where outputs for this part go
    stem: str                 # output filename stem


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="dahlia-export", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("inputs", nargs="+", help=".SLDPRT files or directories to scan")
    p.add_argument("--formats", default="step",
                   help="comma list of: step, stl (default: step)")
    p.add_argument("--out-dir", type=Path, default=None,
                   help="write here (mirroring dir structure); default = beside each source")
    p.add_argument("--overwrite", action="store_true",
                   help="re-export even if the output already exists")

    args = p.parse_args(argv)

    formats = [f.strip().lower() for f in args.formats.split(",") if f.strip()]
    bad = [f for f in formats if f not in VALID_FORMATS]
    if bad:
        print(f"unknown format(s): {bad}; valid: {VALID_FORMATS}", file=sys.stderr)
        return 2

    jobs = _collect_jobs(args.inputs, args.out_dir)
    if not jobs:
        print("no .SLDPRT files found in the given inputs", file=sys.stderr)
        return 1

    print(f"connecting to SolidWorks... ({len(jobs)} part(s) to export -> {formats})")
    connect()

    n_ok = n_fail = n_skip = 0
    for job in jobs:
        job.out_dir.mkdir(parents=True, exist_ok=True)
        targets = {fmt: job.out_dir / f"{job.stem}.{fmt}" for fmt in formats}
        todo = {fmt: t for fmt, t in targets.items()
                if args.overwrite or not t.exists()}
        if not todo:
            n_skip += 1
            print(f"  skip  {job.src.name}  (all outputs exist)")
            continue

        try:
            f = open_file(str(job.src))
            try:
                for target in todo.values():
                    f.save(str(target))   # SaveAs3 infers STEP/STL from the extension
            finally:
                f.close()
            n_ok += 1
            print(f"  ok    {job.src.name}  ->  {', '.join(t.name for t in todo.values())}")
        except Exception as e:  # noqa: BLE001 — record + continue; one bad part shouldn't abort the batch
            n_fail += 1
            print(f"  FAIL  {job.src.name}: {type(e).__name__}: {e}", file=sys.stderr)
            traceback.print_exc(file=sys.stderr)

    # Leave SW running; just release the documents we opened.
    try:
        close_all_files()
    except Exception:
        pass

    print(f"\ndone: {n_ok} exported, {n_skip} skipped, {n_fail} failed")
    return 1 if n_fail else 0


def _collect_jobs(inputs: list[str], out_dir: Path | None) -> list[Job]:
    """Resolve inputs (files or dirs) into per-part export jobs."""
    jobs: list[Job] = []
    seen: set[Path] = set()
    for raw in inputs:
        ip = Path(raw).resolve()
        if ip.is_file() and ip.suffix.lower() == ".sldprt":
            target_dir = out_dir if out_dir is not None else ip.parent
            _add(jobs, seen, Job(ip, target_dir, ip.stem))
        elif ip.is_dir():
            for src in sorted(ip.rglob("*")):
                if (src.is_file() and src.suffix.lower() == ".sldprt"
                        and not src.name.startswith("~$")):
                    if out_dir is not None:
                        target_dir = out_dir / src.relative_to(ip).parent
                    else:
                        target_dir = src.parent
                    _add(jobs, seen, Job(src.resolve(), target_dir, src.stem))
        else:
            print(f"skipping {raw!r}: not a .SLDPRT file or directory", file=sys.stderr)
    return jobs


def _add(jobs: list[Job], seen: set[Path], job: Job) -> None:
    if job.src not in seen:
        seen.add(job.src)
        jobs.append(job)


if __name__ == "__main__":
    raise SystemExit(main())
