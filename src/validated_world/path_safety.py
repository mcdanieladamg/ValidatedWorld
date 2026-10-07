"""Filesystem link checks shared by document and review-evidence adapters."""

from pathlib import Path


def is_junction(path: Path) -> bool:
    """Detect Windows junctions without requiring Python 3.12's Path helper."""
    try:
        metadata = path.lstat()
    except (FileNotFoundError, NotADirectoryError):
        return False
    # IO_REPARSE_TAG_MOUNT_POINT. Windows lstat exposes the tag since Python 3.8;
    # other platforms have no junction tag. Do not follow the junction target.
    return getattr(metadata, "st_reparse_tag", None) == 0xA0000003
