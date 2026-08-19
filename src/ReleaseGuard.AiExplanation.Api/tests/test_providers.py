from __future__ import annotations

import asyncio
from typing import Any

import httpx
import pytest

from releaseguard_ai.contracts import ReleaseRiskAssessedV1
from releaseguard_ai.providers import (
    DeterministicFakeExplanationProvider,
    ExplanationProviderError,
    ExplanationProviderTimeoutError,
    HttpJsonExplanationProvider,
)
from releaseguard_ai.settings import AiExplanationSettings


def http_settings(timeout_seconds: float = 1) -> AiExplanationSettings:
    return AiExplanationSettings(
        provider="http-json",
        model="configured-model",
        timeout_seconds=timeout_seconds,
        provider_endpoint="https://models.example/v1/explain",
        provider_api_key="environment-secret",
    )


@pytest.mark.asyncio
async def test_fake_provider_uses_only_existing_assessment(
    v1_payload: dict[str, Any],
) -> None:
    envelope = ReleaseRiskAssessedV1.model_validate(v1_payload)

    explanation = await DeterministicFakeExplanationProvider().explain(envelope)

    assert str(envelope.eventId) in explanation.summary
    assert "low (20/100)" in explanation.summary
    assert "primary_target_branch" in explanation.recommendations[0]
    assert "The change targets" in explanation.recommendations[0]


@pytest.mark.asyncio
async def test_http_provider_sends_model_auth_and_exact_envelope(
    v1_payload: dict[str, Any],
) -> None:
    captured: dict[str, Any] = {}

    async def handler(request: httpx.Request) -> httpx.Response:
        captured["authorization"] = request.headers["Authorization"]
        captured["body"] = request.read()
        return httpx.Response(
            200,
            json={
                "summary": "Existing factors indicate low risk.",
                "recommendations": ["Review the recorded primary branch factor."],
            },
        )

    provider = HttpJsonExplanationProvider(
        http_settings(),
        httpx.MockTransport(handler),
    )
    envelope = ReleaseRiskAssessedV1.model_validate(v1_payload)

    explanation = await provider.explain(envelope)

    request_json = httpx.Response(200, content=captured["body"]).json()
    assert captured["authorization"] == "Bearer environment-secret"
    assert request_json == {
        "model": "configured-model",
        "envelope": v1_payload,
    }
    assert explanation.summary == "Existing factors indicate low risk."


@pytest.mark.asyncio
@pytest.mark.parametrize("status_code", [400, 401, 429, 500])
async def test_http_provider_surfaces_non_success_status(status_code: int) -> None:
    async def handler(_: httpx.Request) -> httpx.Response:
        return httpx.Response(status_code, text="sensitive-provider-body")

    provider = HttpJsonExplanationProvider(
        http_settings(),
        httpx.MockTransport(handler),
    )

    with pytest.raises(ExplanationProviderError) as error:
        await provider.explain(_minimal_envelope())

    assert "sensitive-provider-body" not in str(error.value)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "response",
    [
        httpx.Response(200, text="not-json"),
        httpx.Response(200, json={"summary": "Missing recommendations"}),
        httpx.Response(
            200,
            json={
                "summary": "Summary",
                "recommendations": ["Recommendation"],
                "score": 99,
            },
        ),
    ],
)
async def test_http_provider_rejects_invalid_response(response: httpx.Response) -> None:
    async def handler(_: httpx.Request) -> httpx.Response:
        return response

    provider = HttpJsonExplanationProvider(
        http_settings(),
        httpx.MockTransport(handler),
    )

    with pytest.raises(ExplanationProviderError):
        await provider.explain(_minimal_envelope())


@pytest.mark.asyncio
async def test_http_provider_surfaces_transport_timeout() -> None:
    async def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ReadTimeout("provider timeout", request=request)

    provider = HttpJsonExplanationProvider(
        http_settings(),
        httpx.MockTransport(handler),
    )

    with pytest.raises(ExplanationProviderTimeoutError):
        await provider.explain(_minimal_envelope())


@pytest.mark.asyncio
async def test_http_provider_does_not_swallow_cancellation() -> None:
    started = asyncio.Event()

    async def handler(_: httpx.Request) -> httpx.Response:
        started.set()
        await asyncio.Event().wait()
        raise AssertionError("unreachable")

    provider = HttpJsonExplanationProvider(
        http_settings(),
        httpx.MockTransport(handler),
    )
    task = asyncio.create_task(provider.explain(_minimal_envelope()))
    await started.wait()

    task.cancel()

    with pytest.raises(asyncio.CancelledError):
        await task


def _minimal_envelope() -> ReleaseRiskAssessedV1:
    return ReleaseRiskAssessedV1.model_validate(
        {
            "eventId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
            "eventType": "releaseguard.release-risk-assessed",
            "schemaVersion": 1,
            "sourceProvider": "github",
            "kind": "change_opened",
            "riskInput": {
                "sourceDeliveryId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
                "sourceProvider": "github",
                "kind": "change_opened",
                "repository": "acme/ReleaseGuard",
                "changeNumber": 42,
                "title": "Protect production releases",
                "author": "octocat",
                "baseBranch": "main",
                "headBranch": "feature/release-guard",
                "isDraft": False,
                "changedFiles": 4,
                "additions": 120,
                "deletions": 15,
            },
            "riskAssessment": {
                "score": 20,
                "level": "low",
                "factors": [],
            },
        }
    )
