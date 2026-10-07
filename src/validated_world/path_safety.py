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


def project_temp_root(project_folder: Path) -> Path:
    """Prepare a shared parent; callers own only their unique children."""
    root = Path(project_folder).absolute()
    for ancestor in (root, *root.parents):
        if ancestor.is_symlink() or is_junction(ancestor):
            raise ValueError(f"linked temporary workspace path is unsupported: {ancestor}")
    if not root.is_dir():
        raise NotADirectoryError(f"temporary workspace project folder does not exist: {root}")
    for name in ("tmp", "validated-world"):
        root = root / name
        if root.is_symlink() or is_junction(root):
            raise ValueError(f"linked temporary workspace path is unsupported: {root}")
        root.mkdir(exist_ok=True)
        if root.is_symlink() or is_junction(root):
            raise ValueError(f"linked temporary workspace path is unsupported: {root}")
    return root.resolve()
