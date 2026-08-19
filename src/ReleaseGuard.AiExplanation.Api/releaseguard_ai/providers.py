from __future__ import annotations

from typing import Protocol

import httpx
from pydantic import ValidationError

from .contracts import ReleaseRiskAssessedV1, RiskExplanationContent
from .settings import AiExplanationSettings


class ExplanationProvider(Protocol):
    async def explain(
        self,
        envelope: ReleaseRiskAssessedV1,
    ) -> RiskExplanationContent:
        """Explain an existing deterministic risk snapshot without changing it."""


class ExplanationProviderError(RuntimeError):
    """Raised when a provider call cannot produce a valid explanation."""


class ExplanationProviderTimeoutError(ExplanationProviderError):
    """Raised when the provider exceeds its configured network timeout."""


class DeterministicFakeExplanationProvider:
    async def explain(
        self,
        envelope: ReleaseRiskAssessedV1,
    ) -> RiskExplanationContent:
        assessment = envelope.riskAssessment
        factors = assessment.factors
        if factors:
            factor_summary = "; ".join(
                f"{factor.reason} (+{factor.points})" for factor in factors
            )
            recommendations = [
                f"Review {factor.code}: {factor.reason}" for factor in factors
            ]
        else:
            factor_summary = "No scored risk factors were recorded."
            recommendations = [
                "No scored factors were recorded; follow the repository's standard "
                "review and test process."
            ]

        return RiskExplanationContent(
            summary=(
                f"The recorded risk for {envelope.eventId} is "
                f"{assessment.level} ({assessment.score}/100). {factor_summary}"
            ),
            recommendations=recommendations,
        )


class HttpJsonExplanationProvider:
    def __init__(
        self,
        settings: AiExplanationSettings,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        if settings.provider != "http-json":
            raise ValueError("HttpJsonExplanationProvider requires http-json settings.")
        if settings.provider_endpoint is None or settings.provider_api_key is None:
            raise ValueError("Validated http-json endpoint and API key are required.")
        self._settings = settings
        self._transport = transport

    async def explain(
        self,
        envelope: ReleaseRiskAssessedV1,
    ) -> RiskExplanationContent:
        headers = {
            "Authorization": f"Bearer {self._settings.provider_api_key}",
            "Content-Type": "application/json",
        }
        request = {
            "model": self._settings.model,
            "envelope": envelope.model_dump(mode="json"),
        }
        try:
            async with httpx.AsyncClient(
                timeout=self._settings.timeout_seconds,
                transport=self._transport,
            ) as client:
                response = await client.post(
                    self._settings.provider_endpoint,
                    headers=headers,
                    json=request,
                )
                response.raise_for_status()
        except httpx.TimeoutException as error:
            raise ExplanationProviderTimeoutError(
                "The explanation provider timed out."
            ) from error
        except (httpx.HTTPStatusError, httpx.RequestError) as error:
            raise ExplanationProviderError(
                "The explanation provider request failed."
            ) from error

        try:
            return RiskExplanationContent.model_validate(response.json())
        except (ValueError, ValidationError) as error:
            raise ExplanationProviderError(
                "The explanation provider returned an invalid response."
            ) from error


def create_provider(settings: AiExplanationSettings) -> ExplanationProvider:
    if settings.provider == "fake":
        return DeterministicFakeExplanationProvider()
    if settings.provider == "http-json":
        return HttpJsonExplanationProvider(settings)
    raise ValueError(f"Unsupported validated provider: {settings.provider}")
