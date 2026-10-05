"""Check this project's extracted skills-only plugin before human submission.

This is local release validation, not the public catalog's eligibility verdict.
Only standard-library Python is needed.
"""

from __future__ import annotations

import json
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit
import xml.etree.ElementTree as ET


def _text(value: object, label: str, limit: int, *, multiline: bool = False) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > limit:
        raise ValueError(f"{label} must be nonempty text of at most {limit} characters")
    if any(ord(char) < 32 and not (multiline and char == "\n") for char in value):
        raise ValueError(f"{label} contains unsupported control characters")
    return value


def _file(root: Path, value: object) -> Path:
    if not isinstance(value, str) or not value.startswith("./") or "\\" in value:
        raise ValueError("Packaged paths must start with ./ and use forward slashes")
    if ".." in value.split("/"):
        raise ValueError("Packaged paths cannot traverse parent directories")
    result = (root / value).resolve()
    if not result.is_relative_to(root.resolve()) or not result.is_file():
        raise ValueError(f"Missing or escaping packaged resource: {value}")
    return result


def _svg(root: Path, value: object) -> None:
    path = _file(root, value)
    if path.suffix != ".svg" or path.stat().st_size > 5 * 1024 * 1024:
        raise ValueError("This release uses bundled SVG branding assets of at most 5 MiB")
    source = path.read_text(encoding="utf-8")
    if "<!DOCTYPE" in source.upper() or "<!ENTITY" in source.upper():
        raise ValueError("Branding SVG must not use document types or entities")
    svg = ET.fromstring(source)
    if svg.tag != "{http://www.w3.org/2000/svg}svg":
        raise ValueError("Branding asset is not an SVG document")
    width, height = float(svg.attrib["width"]), float(svg.attrib["height"])
    if not (width == height and 48 <= width <= 4096):
        raise ValueError("Branding SVG must be square and between 48 and 4096 pixels")
    allowed = {"svg", "title", "rect", "path", "circle"}
    for element in svg.iter():
        if element.tag.rsplit("}", 1)[-1] not in allowed:
            raise ValueError("Unexpected active or unsupported branding SVG element")
        for key, value in element.attrib.items():
            if key.rsplit("}", 1)[-1].lower().startswith("on") or "url(" in value.lower():
                raise ValueError("Branding SVG must not reference active or external resources")


def verify(root: Path) -> None:
    portable = json.loads((root / "plugin.json").read_text(encoding="utf-8"))
    overlay = json.loads((root / ".codex-plugin/plugin.json").read_text(encoding="utf-8"))
    extension = portable["extensions"]["com.openai"]
    interface = extension["interface"]
    if interface != overlay["interface"]:
        raise ValueError("Portable and compatibility listing metadata disagree")
    if extension["onboardingSkill"] != overlay["extensions"]["com.openai"]["onboardingSkill"]:
        raise ValueError("Portable and compatibility onboarding paths disagree")
    _file(root, extension["onboardingSkill"])
    for field, limit in (("displayName", 30), ("shortDescription", 30),
                         ("longDescription", 4000), ("developerName", 80), ("category", 80)):
        _text(interface[field], field, limit, multiline=field == "longDescription")
    capabilities = interface["capabilities"]
    if not isinstance(capabilities, list) or not 1 <= len(capabilities) <= 20:
        raise ValueError("Expected between one and twenty capability labels")
    for capability in capabilities:
        _text(capability, "capability", 120)
    prompts = interface["defaultPrompt"]
    if not isinstance(prompts, list) or not 1 <= len(prompts) <= 3:
        raise ValueError("Expected between one and three starter prompts")
    if len(set(prompts)) != len(prompts):
        raise ValueError("Starter prompts must be unique")
    for prompt in prompts:
        _text(prompt, "starter prompt", 128)
    for field in ("websiteURL", "supportURL", "privacyPolicyURL", "termsOfServiceURL"):
        url = urlsplit(_text(interface[field], field, 1024))
        if url.scheme != "https" or not url.hostname or url.username or url.password:
            raise ValueError(f"{field} must be HTTPS without embedded credentials")
    for field in ("brandColor", "brandColorDark"):
        if not re.fullmatch(r"#[0-9A-Fa-f]{6}", interface[field]):
            raise ValueError(f"{field} must use #RRGGBB")
    for field in ("logo", "logoDark", "composerIcon", "composerIconDark"):
        _svg(root, interface[field])
    if "screenshots" in interface or any((root / name).exists() for name in ("mcp.json", ".mcp.json", "hooks", "apps")):
        raise ValueError("Unexpected component in this skills-only release")
    for directory in (root, root / "skills/validated-world"):
        for guide in ("privacy", "terms", "support"):
            policy = directory / "docs" / f"{guide}.md"
            if not policy.is_file():
                raise ValueError(f"Missing bundled policy: {policy}")


if __name__ == "__main__":
    verify(Path(sys.argv[1]))
    print("Plugin listing metadata, branding, onboarding and bundled policies passed.")
