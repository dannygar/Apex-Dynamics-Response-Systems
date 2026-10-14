# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
"""Configuration loading for bundled profiles, rubrics, and case references."""

from __future__ import annotations

import re
from functools import cache
from importlib import resources
from pathlib import Path
from typing import Any

import yaml


class ConfigError(Exception):
    """Raised when a configuration file is missing or malformed."""


def load_yaml(path: Path) -> dict[str, Any]:
    """Load a YAML mapping from ``path``.

    Raises:
        ConfigError: If the file is missing or does not contain a mapping.
    """
    if not path.is_file():
        raise ConfigError(f"Config file not found: {path}")
    try:
        data = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as exc:
        raise ConfigError(f"Invalid YAML in {path}: {exc}") from exc
    if not isinstance(data, dict):
        raise ConfigError(f"Expected a mapping at the top of {path}")
    return data


def _bundled(name: str) -> dict[str, Any]:
    text = resources.files("agent_eval.data").joinpath(name).read_text(encoding="utf-8")
    data = yaml.safe_load(text)
    if not isinstance(data, dict):
        raise ConfigError(f"Bundled {name} is not a mapping")
    return data


@cache
def profiles() -> dict[str, Any]:
    """Return the bundled structural profiles."""
    return _bundled("profiles.yaml")


def rubrics(path: Path | None = None) -> dict[str, Any]:
    """Return judge rubrics from ``path`` or the bundled default."""
    return load_yaml(path) if path else _bundled("rubrics.yaml")


def compile_patterns(patterns: list[str]) -> list[re.Pattern[str]]:
    """Compile case-insensitive regexes, raising ConfigError on bad patterns."""
    compiled: list[re.Pattern[str]] = []
    for pattern in patterns:
        try:
            compiled.append(re.compile(pattern, re.I))
        except re.error as exc:
            raise ConfigError(f"Invalid regex {pattern!r}: {exc}") from exc
    return compiled


def first_match(text: str, patterns: list[re.Pattern[str]]) -> str | None:
    """Return the first matched substring for any pattern, else None."""
    for pattern in patterns:
        if m := pattern.search(text):
            return m.group(0)
    return None
