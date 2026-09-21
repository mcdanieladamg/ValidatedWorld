"""Launch the Python engine bundled beside this skill distribution."""

from pathlib import Path
import sys


def _engine_root() -> Path:
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "src-python"
        if (candidate / "validated_world" / "__main__.py").is_file():
            return candidate
    raise SystemExit("error: bundled src-python/validated_world engine was not found")


sys.path.insert(0, str(_engine_root()))

from validated_world.cli import main


if __name__ == "__main__":
    raise SystemExit(main())
