"""Keep the NDJSON host live through one hidden project-local temporary file."""
from __future__ import annotations

import argparse
from contextlib import redirect_stdout
from hashlib import sha256
import json
from pathlib import Path
import runpy
import sys


def request_frames(request, chunk_size=600):
    """Encode one request as short ASCII lines; size is a host transmission choice."""
    if isinstance(chunk_size, bool) or not isinstance(chunk_size, int) or chunk_size < 1:
        raise ValueError('chunk-size must be a positive integer')
    wire = json.dumps(request, ensure_ascii=True, separators=(',', ':'), allow_nan=False)
    yield '@vw-begin\n'
    for offset in range(0, len(wire), chunk_size):
        yield '@vw-part ' + wire[offset:offset + chunk_size] + '\n'
    yield '@vw-end ' + sha256(wire.encode('ascii')).hexdigest() + '\n'


class FramedInput:
    """Adapt helper-only input frames to the unchanged strict NDJSON protocol."""
    def __init__(self, source, output):
        self.source = source
        self.output = output

    def __iter__(self):
        parts = None

        def error(message):
            self.output.write(json.dumps({'version': 1, 'command': 'input.frame', 'status': 'error',
                                         'payload': {'code': 'input-framing', 'message': message}}) + '\n')
            self.output.flush()

        for line in self.source:
            frame = line.removesuffix('\n').removesuffix('\r')
            if frame.startswith('@vw-'):
                if not line.endswith('\n'):
                    error('Incomplete input frame; resend the complete request from @vw-begin.')
                    parts = None
                elif frame == '@vw-cancel':
                    parts = None
                elif frame == '@vw-begin' and parts is None:
                    parts = []
                elif frame.startswith('@vw-part ') and parts is not None:
                    parts.append(frame[len('@vw-part '):])
                elif frame.startswith('@vw-end ') and parts is not None:
                    wire = ''.join(parts)
                    parts = None
                    if not wire or sha256(wire.encode('utf-8')).hexdigest() != frame[len('@vw-end '):]:
                        error('Input checksum mismatch; nothing dispatched. Resend from @vw-begin.')
                    else:
                        yield wire + '\n'
                else:
                    error('Unexpected input frame; nothing dispatched. Resend from @vw-begin.')
                    parts = None
            elif parts is not None:
                error('Unframed input inside a request; nothing dispatched. Resend from @vw-begin.')
                parts = None
            else:
                yield line
        if parts is not None:
            error('Incomplete framed request at EOF; nothing dispatched.')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    location = parser.add_mutually_exclusive_group()
    location.add_argument('response_log', nargs='?', help='New caller-owned absolute log path.')
    location.add_argument('--temp-root', help='Absolute authorized shared folder instead of the working directory.')
    parser.add_argument('--document', help='Selected HTML document (default: docs-vw.html in the working folder).')
    parser.add_argument('--keep-log', action='store_true', help='Retain temporary responses for requested diagnostics.')
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--read', help='Read complete responses from the announced hidden temporary file and exit.')
    modes.add_argument('--frame-input', action='store_true', help='Encode stdin NDJSON as short integrity-checked input frames; no session or files.')
    parser.add_argument('--chunk-size', type=int, help='Characters per encoded fragment with --frame-input (default: 600).')
    parser.add_argument('--offset', type=int, default=0, help='Byte offset returned by the previous --read.')
    arguments = parser.parse_args()
    launcher = Path(__file__).with_name('validated_world.py')
    runpy.run_path(str(launcher))
    if arguments.frame_input:
        from validated_world.protocol import json_loads_strict
        try:
            size = 600 if arguments.chunk_size is None else arguments.chunk_size
            if size < 1: raise ValueError('chunk-size must be a positive integer')
            for line in sys.stdin:
                if line.strip():
                    for frame in request_frames(json_loads_strict(line), size):
                        sys.stdout.write(frame)
            sys.stdout.flush()
        except ValueError as exc: parser.error(str(exc))
        return
    if arguments.chunk_size is not None: parser.error('--chunk-size requires --frame-input')
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
    original_input = sys.stdin
    clean_exit = False
    try:
        with redirect_stdout(output):
            sys.argv = [str(launcher), 'ndjson']
            sys.stdin = FramedInput(original_input, output)
            print(f'NDJSON ready; responses: {response_log}', file=sys.stderr, flush=True)
            runpy.run_path(str(launcher), run_name='__main__')
        clean_exit = True
    except SystemExit as exc:
        clean_exit = exc.code in (None, 0)
        raise
    finally:
        sys.argv = original_arguments
        sys.stdin = original_input
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
