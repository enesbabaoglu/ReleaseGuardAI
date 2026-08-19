from __future__ import annotations

import asyncio
from typing import Any

import httpx
import pytest

from releaseguard_ai.app import create_app
from releaseguard_ai.contracts import ReleaseRiskAssessedV1, RiskExplanationContent
from releaseguard_ai.providers import ExplanationProviderError
from releaseguard_ai.settings import AiExplanationSettings


class SuccessfulProvider:
    def __init__(self) -> None:
        self.received: ReleaseRiskAssessedV1 | None = None

    async def explain(
        self,
        envelope: ReleaseRiskAssessedV1,
    ) -> RiskExplanationContent:
        self.received = envelope
        return RiskExplanationContent(
            summary="The existing deterministic assessment is low risk.",
            recommendations=["Review the recorded primary branch factor."],
        )


class FailingProvider:
    async def explain(
        self,
        _: ReleaseRiskAssessedV1,
    ) -> RiskExplanationContent:
        raise ExplanationProviderError("provider contained sensitive details")


class BlockingProvider:
    def __init__(self) -> None:
        self.cancelled = asyncio.Event()

    async def explain(
        self,
        _: ReleaseRiskAssessedV1,
    ) -> RiskExplanationContent:
        try:
            await asyncio.Event().wait()
        except asyncio.CancelledError:
            self.cancelled.set()
            raise
        raise AssertionError("unreachable")


def fake_settings(timeout_seconds: float = 1) -> AiExplanationSettings:
    return AiExplanationSettings(
        provider="fake",
        model="deterministic-v1",
        timeout_seconds=timeout_seconds,
    )


@pytest.mark.asyncio
async def test_health_is_independent_of_provider_call() -> None:
    app = create_app(fake_settings(), FailingProvider())

    async with _client(app) as client:
        response = await client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok", "service": "ai-explanation"}


@pytest.mark.asyncio
async def test_v1_endpoint_binds_response_to_request_event(
    v1_payload: dict[str, Any],
) -> None:
    provider = SuccessfulProvider()
    app = create_app(fake_settings(), provider)

    async with _client(app) as client:
        response = await client.post(
            "/v1/release-risk-explanations",
            json=v1_payload,
        )

    assert response.status_code == 200
    assert response.json() == {
        "eventId": v1_payload["eventId"],
        "summary": "The existing deterministic assessment is low risk.",
        "recommendations": ["Review the recorded primary branch factor."],
    }
    assert provider.received is not None
    assert provider.received.model_dump(mode="json") == v1_payload


@pytest.mark.asyncio
async def test_v1_endpoint_rejects_invalid_contract(
    v1_payload: dict[str, Any],
) -> None:
    payload = dict(v1_payload)
    payload["schemaVersion"] = 2
    app = create_app(fake_settings(), SuccessfulProvider())

    async with _client(app) as client:
        response = await client.post(
            "/v1/release-risk-explanations",
            json=payload,
        )

    assert response.status_code == 422


@pytest.mark.asyncio
async def test_provider_failure_is_generic_bad_gateway(
    v1_payload: dict[str, Any],
) -> None:
    app = create_app(fake_settings(), FailingProvider())

    async with _client(app) as client:
        response = await client.post(
            "/v1/release-risk-explanations",
            json=v1_payload,
        )

    assert response.status_code == 502
    assert response.json() == {"detail": "The explanation provider failed."}
    assert "sensitive" not in response.text


@pytest.mark.asyncio
async def test_provider_timeout_is_bounded_and_cancels_provider(
    v1_payload: dict[str, Any],
) -> None:
    provider = BlockingProvider()
    app = create_app(fake_settings(timeout_seconds=0.1), provider)

    async with _client(app) as client:
        response = await client.post(
            "/v1/release-risk-explanations",
            json=v1_payload,
        )

    assert response.status_code == 504
    assert response.json() == {"detail": "The explanation provider timed out."}
    assert provider.cancelled.is_set()


@pytest.mark.asyncio
async def test_request_task_cancellation_is_not_converted_to_success(
    v1_payload: dict[str, Any],
) -> None:
    provider = BlockingProvider()
    app = create_app(fake_settings(timeout_seconds=10), provider)

    async with _client(app) as client:
        task = asyncio.create_task(
            client.post("/v1/release-risk-explanations", json=v1_payload)
        )
        await asyncio.sleep(0)
        task.cancel()

        with pytest.raises(asyncio.CancelledError):
            await task

    assert provider.cancelled.is_set()


def _client(app: Any) -> httpx.AsyncClient:
    return httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://testserver",
    )
