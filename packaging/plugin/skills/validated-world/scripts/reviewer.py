"""Read-only bounded subagent evidence from the one companion HTML."""
from pathlib import Path
import sys
sys.dont_write_bytecode = True
for parent in Path(__file__).resolve().parents:
    candidate = parent / 'src'
    if (candidate / 'validated_world/__main__.py').is_file():
        sys.path.insert(0,str(candidate)); break
else:
    raise RuntimeError('bundled engine source was not found')
from validated_world.review_workspace import ReviewReader
