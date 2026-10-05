"""Publish a complete sibling file without replacing an existing destination."""

import os


def publish_new_file(stage, destination):
    # Windows rename rejects an existing destination in the operation itself.
    # It needs no hard-link support or permission. POSIX rename can overwrite,
    # so retain link publication there. The caller cleans a remaining stage.
    if os.name == 'nt':
        os.rename(stage, destination)
    else:
        os.link(stage, destination)
