from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any

import pytest
from pydantic import ValidationError

from releaseguard_ai.contracts import ReleaseRiskAssessedV1


def test_dotnet_v1_fixture_is_accepted_without_changing_snapshot(
    v1_fixture_path: Path,
) -> None:
    fixture_text = v1_fixture_path.read_text(encoding="utf-8").strip()
    original = json.loads(fixture_text)

    envelope = ReleaseRiskAssessedV1.model_validate_json(fixture_text)

    assert envelope.model_dump(mode="json") == original
    assert envelope.riskAssessment.score == 20
    assert envelope.riskAssessment.level == "low"
    assert [factor.model_dump() for factor in envelope.riskAssessment.factors] == [
        {
            "code": "primary_target_branch",
            "points": 20,
            "reason": "The change targets the conventional primary branch 'main'.",
        }
    ]


@pytest.mark.parametrize(
    ("path", "value"),
    [
        (("eventType",), "releaseguard.release-risk-changed"),
        (("schemaVersion",), 2),
        (("schemaVersion",), "1"),
        (("sourceProvider",), "gitlab"),
        (("kind",), "change_closed"),
        (("eventId",), "1b989ba4-242f-11e5-81e1-c7b6966d2516"),
        (("riskInput", "sourceProvider"), "gitlab"),
        (("riskInput", "kind"), "change_updated"),
    ],
)
def test_invalid_v1_contract_is_rejected(
    v1_payload: dict[str, Any],
    path: tuple[str, ...],
    value: Any,
) -> None:
    payload = copy.deepcopy(v1_payload)
    target = payload
    for segment in path[:-1]:
        target = target[segment]
    target[path[-1]] = value

    with pytest.raises(ValidationError):
        ReleaseRiskAssessedV1.model_validate(payload)


def test_unknown_fields_are_rejected(v1_payload: dict[str, Any]) -> None:
    payload = copy.deepcopy(v1_payload)
    payload["newRiskScore"] = 99

    with pytest.raises(ValidationError):
        ReleaseRiskAssessedV1.model_validate(payload)


def test_existing_risk_snapshot_is_not_recomputed(
    v1_payload: dict[str, Any],
) -> None:
    payload = copy.deepcopy(v1_payload)
    payload["riskAssessment"]["score"] = 99
    payload["riskAssessment"]["level"] = "low"

    envelope = ReleaseRiskAssessedV1.model_validate(payload)

    assert envelope.riskAssessment.score == 99
    assert envelope.riskAssessment.level == "low"
    assert envelope.riskAssessment.factors[0].points == 20


def test_missing_fields_are_rejected(v1_payload: dict[str, Any]) -> None:
    payload = copy.deepcopy(v1_payload)
    del payload["riskAssessment"]["factors"]

    with pytest.raises(ValidationError):
        ReleaseRiskAssessedV1.model_validate(payload)
