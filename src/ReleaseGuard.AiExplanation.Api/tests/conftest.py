from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest


@pytest.fixture
def v1_fixture_path() -> Path:
    return (
        Path(__file__).resolve().parents[3]
        / "contracts"
        / "release-risk-assessed.v1.example.json"
    )


@pytest.fixture
def v1_payload(v1_fixture_path: Path) -> dict[str, Any]:
    return json.loads(v1_fixture_path.read_text(encoding="utf-8"))
