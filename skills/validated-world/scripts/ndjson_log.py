"""Keep the ordinary NDJSON host live with directly flushed UTF-8 file output."""

from __future__ import annotations

import argparse
from contextlib import redirect_stdout
import os
from pathlib import Path
import runpy
import stat
import sys
import tempfile


def _project_temp_root(project_folder):
    # Locate the same bundled source as the launcher, including under -I -S.
    for parent in Path(__file__).resolve().parents:
        source = parent / "src"
        if (source / "validated_world" / "__main__.py").is_file():
            sys.path.insert(0, str(source))
            from validated_world.path_safety import project_temp_root
            return project_temp_root(project_folder)
    raise ValueError("bundled ValidatedWorld source was not found")


def _remove_log(directory, response_log, identity):
    if directory.resolve() != directory or response_log.is_symlink():
        raise ValueError("owned log path changed")
    entries = list(directory.iterdir())
    current = response_log.stat()
    if (entries != [response_log] or not stat.S_ISREG(current.st_mode)
            or (current.st_dev, current.st_ino) != identity):
        raise ValueError("owned log directory contains unexpected or replaced files")
    # Never recurse: the directory may contain a project or recovery artifact.
    response_log.unlink()
    directory.rmdir()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    location = parser.add_mutually_exclusive_group()
    location.add_argument("response_log", nargs="?", help="New absolute log path; its parent must already exist.")
    location.add_argument("--temp-root", help="Override the current working directory with an absolute shared writable root.")
    parser.add_argument("--keep-log", action="store_true", help="Retain the owned response log for requested diagnostics.")
    arguments = parser.parse_args()
    directory = None
    if arguments.response_log:
        response_log = Path(arguments.response_log)
        if not response_log.is_absolute():
            parser.error("response_log must be an absolute path")
    else:
        root = Path(arguments.temp_root) if arguments.temp_root else Path.cwd()
        if not root.is_absolute():
            parser.error("temp-root must be an absolute path")
        try:
            if not arguments.temp_root:
                root = _project_temp_root(root)
            directory = Path(tempfile.mkdtemp(prefix="vw-ndjson-", dir=root)).resolve()
        except (OSError, ValueError) as exc:
            parser.error(f"cannot create temporary response directory: {exc}")
        response_log = directory / "responses.jsonl"
    try:
        output = response_log.open("x", encoding="utf-8", newline="\n")
    except OSError as exc:
        if directory is not None:
            try:
                directory.rmdir()
            except OSError as cleanup:
                print(f"Response directory cleanup requires attention: {directory}: {cleanup}", file=sys.stderr)
        parser.error(f"cannot create response log {response_log}: {exc}")
    opened = os.fstat(output.fileno())
    identity = (opened.st_dev, opened.st_ino)
    launcher = Path(__file__).with_name("validated_world.py")
    original_arguments = sys.argv
    clean_exit = False
    try:
        with output, redirect_stdout(output):
            sys.argv = [str(launcher), "ndjson"]
            print(f"NDJSON ready; responses: {response_log}", file=sys.stderr, flush=True)
            runpy.run_path(str(launcher), run_name="__main__")
        clean_exit = True
    except SystemExit as exc:
        clean_exit = exc.code in (None, 0)
        raise
    finally:
        sys.argv = original_arguments
        if directory is not None:
            if clean_exit and not arguments.keep_log:
                try:
                    _remove_log(directory, response_log, identity)
                except (OSError, ValueError) as exc:
                    print(f"Response log cleanup requires attention: {response_log}: {exc}", file=sys.stderr, flush=True)
            else:
                print(f"Response log retained: {response_log}", file=sys.stderr, flush=True)


if __name__ == "__main__":
    main()
