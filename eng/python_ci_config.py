"""Resolve deliberately skippable OS jobs without exposing secrets.

The workflow passes only the three skip values to this script.  A missing,
empty, or false value runs that OS; true skips it. Any other non-empty value is
configuration failure. This file is intentionally dependency-free so the
workflow can validate its own configuration before installing the package.
"""

from __future__ import annotations

import argparse
import os
import sys


def should_run(value: str | None) -> bool:
    if value is None or value.strip() == "":
        return True
    normalized = value.strip().lower()
    if normalized == "false":
        return True
    if normalized == "true":
        return False
    raise ValueError("skip flags must be true or false")


def resolve(environ: dict[str, str] | None = None) -> dict[str, bool]:
    values = environ if environ is not None else os.environ
    return {
        "windows": should_run(values.get("VW_CI_SKIP_WINDOWS")),
        "linux": should_run(values.get("VW_CI_SKIP_LINUX")),
        "macos": should_run(values.get("VW_CI_SKIP_MACOS")),
    }


def aggregate(environ: dict[str, str] | None = None) -> tuple[bool, str]:
    values = environ if environ is not None else os.environ
    if values.get("CONFIGURE_RESULT") != "success":
        return False, "platform policy configuration did not succeed"
    enabled = {name: values.get("RUN_" + name.upper()) == "true" for name in ("windows", "linux", "macos")}
    if values.get("ALL_SKIPPED") == "true":
        if any(enabled.values()):
            return False, "all_skipped conflicts with an enabled platform"
        return True, "All platform tests were deliberately excluded by VW_CI_SKIP_*; no platform test passed is claimed."
    failed = [name for name, is_enabled in enabled.items() if is_enabled and values.get(name.upper() + "_RESULT") != "success"]
    if failed:
        return False, "enabled platform job did not succeed: " + ", ".join(failed)
    if not any(enabled.values()):
        return False, "no platform was enabled but all_skipped was not true"
    return True, "Every enabled platform job succeeded."


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--github-output", default=None)
    parser.add_argument("--check-aggregate", action="store_true")
    args = parser.parse_args()
    if args.check_aggregate:
        ok, message = aggregate()
        print(message, file=sys.stdout if ok else sys.stderr)
        return 0 if ok else 2
    try:
        result = resolve()
    except ValueError as error:
        print(f"configuration error: {error}", file=sys.stderr)
        return 2
    lines = [f"run_{key}={'true' if value else 'false'}" for key, value in result.items()]
    lines.append("all_skipped=" + ("true" if not any(result.values()) else "false"))
    for line in lines:
        print(line)
    if args.github_output:
        with open(args.github_output, "a", encoding="utf-8", newline="\n") as output:
            output.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
