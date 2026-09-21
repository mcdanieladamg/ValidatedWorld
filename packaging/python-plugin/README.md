# ValidatedWorld Python plugin

This skills-only Codex plugin bundles the ValidatedWorld Agent Skill and its
standard-library Python engine. Build a release package from the repository
with `eng/Build-PythonPackage.ps1`; that command copies the tracked Python
source package into a clean staging directory and validates the archive
contents.

The host must have Python 3.12 or newer or an explicitly managed `uv` runtime.
The plugin does not install an operating-system runtime automatically.
