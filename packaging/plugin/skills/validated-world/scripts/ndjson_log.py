"""Keep the NDJSON host live through one hidden project-local temporary file."""
from __future__ import annotations

import argparse
from contextlib import redirect_stdout
from pathlib import Path
import runpy
import sys


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    location = parser.add_mutually_exclusive_group()
    location.add_argument('response_log', nargs='?', help='New caller-owned absolute log path.')
    location.add_argument('--temp-root', help='Absolute authorized shared folder instead of the working directory.')
    parser.add_argument('--document', help='Selected HTML document (default: docs-vw.html in the working folder).')
    parser.add_argument('--keep-log', action='store_true', help='Retain temporary responses for requested diagnostics.')
    parser.add_argument('--read', help='Read complete responses from the announced hidden temporary file and exit.')
    parser.add_argument('--offset', type=int, default=0, help='Byte offset returned by the previous --read.')
    arguments = parser.parse_args()
    launcher = Path(__file__).with_name('validated_world.py')
    runpy.run_path(str(launcher))
    from validated_world.scratch import ResponseTransport, read_responses
    if arguments.read:
        import json
        try: print(json.dumps(read_responses(arguments.read, arguments.offset), ensure_ascii=True))
        except (OSError, ValueError) as exc: parser.error(str(exc))
        return

    owned = None
    if arguments.response_log:
        response_log = Path(arguments.response_log)
        if not response_log.is_absolute(): parser.error('response_log must be an absolute path')
        if arguments.document: parser.error('--document cannot be combined with a caller-owned response log')
        try:
            output = response_log.open('x', encoding='utf-8', newline='\n')
        except OSError as exc:
            parser.error(f'cannot create response log {response_log}: {exc}')
    else:
        root = Path(arguments.temp_root) if arguments.temp_root else Path.cwd()
        if not root.is_absolute(): parser.error('temp-root must be an absolute path')
        document = Path(arguments.document) if arguments.document else root / 'docs-vw.html'
        if not document.is_absolute(): document = root / document
        if not document.parent.is_dir(): parser.error(f'cannot create temporary response file in {document.parent}')
        try:
            owned = output = ResponseTransport(document)
        except (OSError, ValueError, RuntimeError) as exc:
            parser.error(f'cannot create temporary response file: {exc}')
        response_log = owned.path

    original_arguments = sys.argv
    clean_exit = False
    try:
        with redirect_stdout(output):
            sys.argv = [str(launcher), 'ndjson']
            print(f'NDJSON ready; responses: {response_log}', file=sys.stderr, flush=True)
            runpy.run_path(str(launcher), run_name='__main__')
        clean_exit = True
    except SystemExit as exc:
        clean_exit = exc.code in (None, 0)
        raise
    finally:
        sys.argv = original_arguments
        if owned:
            retained = not clean_exit or arguments.keep_log or owned.recovery
            try:
                owned.close(clean_exit, arguments.keep_log)
            except (OSError, ValueError) as exc:
                print(f'Response log cleanup requires attention: {response_log}: {exc}', file=sys.stderr, flush=True)
            else:
                if retained: print(f'Response log retained: {response_log}', file=sys.stderr, flush=True)
        else:
            output.close()


if __name__ == '__main__': main()
