from __future__ import annotations

import json
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


class OllamaExplanationProvider:
    def __init__(
        self,
        settings: AiExplanationSettings,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        if settings.provider != "ollama":
            raise ValueError("OllamaExplanationProvider requires ollama settings.")
        if settings.provider_endpoint is None:
            raise ValueError("A validated Ollama endpoint is required.")
        self._settings = settings
        self._transport = transport

    async def explain(
        self,
        envelope: ReleaseRiskAssessedV1,
    ) -> RiskExplanationContent:
        if self._settings.output_language == "tr":
            system_prompt = (
                "Sen ReleaseGuard risk açıklama asistanısın. Yalnız verilen "
                "değişmez risk değerlendirmesini açıkla. Repository, değişiklik "
                "numarası, skor, seviye veya faktörleri değiştirme ve yeni faktör "
                "uydurma. Özette kayıtlı skorun n/100 değerini ve risk seviyesini "
                "açıkça belirt. Faktör yoksa bunu söyle ve standart inceleme/test "
                "adımlarını öner. Önerileri yalnız kayıtlı faktörlere dayanan, kısa "
                "ve uygulanabilir maddeler olarak yaz. Karar verme veya dağıtımı "
                "onaylama. Çıktının tamamı Türkçe ve markdown içermeden yazılmalıdır."
            )
            user_prompt = (
                "Bu release-risk snapshot'ını zorunlu JSON şemasına göre açıkla. "
                f"summary alanında kayıtlı {envelope.riskAssessment.score}/100 "
                f"skorunu ve '{envelope.riskAssessment.level}' seviyesini açıkça "
                "yaz; recommendations alanında yalnız kayıtlı faktörlerden veya "
                "standart inceleme/test adımlarından yararlan:"
            )
        else:
            system_prompt = (
                "You are ReleaseGuard's release-risk explainer. Use only the "
                "supplied immutable assessment. Never change the repository, change "
                "number, score, level, or factors, and never invent a factor. State "
                "the recorded n/100 score and risk level explicitly in the summary. "
                "When no factors exist, say so and recommend standard review and "
                "testing. Keep recommendations concise, actionable, and grounded in "
                "recorded factors. Do not approve or reject a deployment. Write all "
                "output in English without markdown."
            )
            user_prompt = (
                "Explain this release-risk snapshot using the required JSON schema. "
                f"In summary, explicitly state the recorded "
                f"{envelope.riskAssessment.score}/100 score and "
                f"'{envelope.riskAssessment.level}' level. In recommendations, use "
                "only recorded factors or standard review and testing steps:"
            )
        envelope_json = json.dumps(
            envelope.model_dump(mode="json"),
            ensure_ascii=False,
            separators=(",", ":"),
        )
        request = {
            "model": self._settings.model,
            "messages": [
                {
                    "role": "system",
                    "content": system_prompt,
                },
                {
                    "role": "user",
                    "content": f"{user_prompt}\n{envelope_json}",
                },
            ],
            "stream": False,
            "think": False,
            "format": RiskExplanationContent.model_json_schema(),
            "keep_alive": "5m",
            "options": {
                "temperature": 0.2,
                "num_predict": 512,
            },
        }
        try:
            async with httpx.AsyncClient(
                timeout=self._settings.timeout_seconds,
                transport=self._transport,
            ) as client:
                response = await client.post(
                    self._settings.provider_endpoint,
                    headers={"Content-Type": "application/json"},
                    json=request,
                )
                response.raise_for_status()
        except httpx.TimeoutException as error:
            raise ExplanationProviderTimeoutError(
                "The Ollama explanation provider timed out."
            ) from error
        except (httpx.HTTPStatusError, httpx.RequestError) as error:
            raise ExplanationProviderError(
                "The Ollama explanation provider request failed."
            ) from error

        try:
            response_json = response.json()
            content = response_json["message"]["content"]
            if not isinstance(content, str):
                raise ValueError("Ollama message content must be a JSON string.")
            return RiskExplanationContent.model_validate(json.loads(content))
        except (KeyError, TypeError, ValueError, ValidationError) as error:
            raise ExplanationProviderError(
                "The Ollama explanation provider returned an invalid response."
            ) from error


def create_provider(settings: AiExplanationSettings) -> ExplanationProvider:
    if settings.provider == "fake":
        return DeterministicFakeExplanationProvider()
    if settings.provider == "http-json":
        return HttpJsonExplanationProvider(settings)
    if settings.provider == "ollama":
        return OllamaExplanationProvider(settings)
    raise ValueError(f"Unsupported validated provider: {settings.provider}")
