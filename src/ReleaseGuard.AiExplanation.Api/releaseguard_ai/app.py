from __future__ import annotations

import asyncio

from fastapi import FastAPI, HTTPException

from .contracts import ReleaseRiskAssessedV1, ReleaseRiskExplanationV1
from .providers import (
    ExplanationProvider,
    ExplanationProviderError,
    ExplanationProviderTimeoutError,
    create_provider,
)
from .settings import AiExplanationSettings


def create_app(
    settings: AiExplanationSettings,
    provider: ExplanationProvider | None = None,
) -> FastAPI:
    explanation_provider = provider or create_provider(settings)
    app = FastAPI(title="ReleaseGuard AI Explanation API", version="1.0.0")

    @app.get("/health")
    async def health() -> dict[str, str]:
        return {"status": "ok", "service": "ai-explanation"}

    @app.post(
        "/v1/release-risk-explanations",
        response_model=ReleaseRiskExplanationV1,
    )
    async def explain_risk(
        envelope: ReleaseRiskAssessedV1,
    ) -> ReleaseRiskExplanationV1:
        try:
            explanation = await asyncio.wait_for(
                explanation_provider.explain(envelope),
                timeout=settings.timeout_seconds,
            )
        except (asyncio.TimeoutError, ExplanationProviderTimeoutError) as error:
            raise HTTPException(
                status_code=504,
                detail="The explanation provider timed out.",
            ) from error
        except ExplanationProviderError as error:
            raise HTTPException(
                status_code=502,
                detail="The explanation provider failed.",
            ) from error

        return ReleaseRiskExplanationV1(
            eventId=envelope.eventId,
            summary=explanation.summary,
            recommendations=explanation.recommendations,
        )

    return app
