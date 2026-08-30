from __future__ import annotations

import math
import os
from collections.abc import Mapping
from dataclasses import dataclass
from urllib.parse import urlparse


class AiExplanationConfigurationError(ValueError):
    """Raised when the service configuration is missing or unsafe."""


@dataclass(frozen=True)
class AiExplanationSettings:
    provider: str
    model: str
    timeout_seconds: float
    provider_endpoint: str | None = None
    provider_api_key: str | None = None
    output_language: str = "tr"

    @classmethod
    def from_environment(
        cls,
        environment: Mapping[str, str] | None = None,
    ) -> AiExplanationSettings:
        values = os.environ if environment is None else environment
        provider = _required(values, "RELEASEGUARD_AI_PROVIDER").lower()
        model = _required(values, "RELEASEGUARD_AI_MODEL")

        if provider not in {"fake", "http-json", "ollama"}:
            raise AiExplanationConfigurationError(
                "RELEASEGUARD_AI_PROVIDER must be 'fake', 'http-json', or 'ollama'."
            )

        timeout_text = _required(values, "RELEASEGUARD_AI_TIMEOUT_SECONDS")
        try:
            timeout_seconds = float(timeout_text)
        except ValueError as error:
            raise AiExplanationConfigurationError(
                "RELEASEGUARD_AI_TIMEOUT_SECONDS must be a number."
            ) from error

        if not math.isfinite(timeout_seconds) or not 0.1 <= timeout_seconds <= 60:
            raise AiExplanationConfigurationError(
                "RELEASEGUARD_AI_TIMEOUT_SECONDS must be between 0.1 and 60."
            )

        endpoint = _optional(values, "RELEASEGUARD_AI_PROVIDER_ENDPOINT")
        api_key = _optional(values, "RELEASEGUARD_AI_PROVIDER_API_KEY")
        output_language = (
            _optional(values, "RELEASEGUARD_AI_OUTPUT_LANGUAGE") or "tr"
        ).lower()
        if output_language not in {"tr", "en"}:
            raise AiExplanationConfigurationError(
                "RELEASEGUARD_AI_OUTPUT_LANGUAGE must be 'tr' or 'en'."
            )

        if provider == "http-json":
            if endpoint is None:
                raise AiExplanationConfigurationError(
                    "RELEASEGUARD_AI_PROVIDER_ENDPOINT is required for http-json."
                )
            if api_key is None:
                raise AiExplanationConfigurationError(
                    "RELEASEGUARD_AI_PROVIDER_API_KEY is required for http-json."
                )
            _validate_endpoint(endpoint)
        elif provider == "ollama":
            if endpoint is None:
                raise AiExplanationConfigurationError(
                    "RELEASEGUARD_AI_PROVIDER_ENDPOINT is required for ollama."
                )
            if api_key is not None:
                raise AiExplanationConfigurationError(
                    "RELEASEGUARD_AI_PROVIDER_API_KEY must be absent for local ollama."
                )
            _validate_ollama_endpoint(endpoint)

        return cls(
            provider=provider,
            model=model,
            timeout_seconds=timeout_seconds,
            provider_endpoint=endpoint,
            provider_api_key=api_key,
            output_language=output_language,
        )


def _required(environment: Mapping[str, str], name: str) -> str:
    value = _optional(environment, name)
    if value is None:
        raise AiExplanationConfigurationError(f"{name} is required.")
    return value


def _optional(environment: Mapping[str, str], name: str) -> str | None:
    value = environment.get(name)
    if value is None or not value.strip():
        return None
    return value.strip()


def _validate_endpoint(endpoint: str) -> None:
    parsed = urlparse(endpoint)
    is_local_http = parsed.scheme == "http" and parsed.hostname in {
        "localhost",
        "127.0.0.1",
        "::1",
    }
    if parsed.scheme != "https" and not is_local_http:
        raise AiExplanationConfigurationError(
            "Provider endpoint must use HTTPS, except for loopback local testing."
        )
    if not parsed.netloc or parsed.username or parsed.password or parsed.fragment:
        raise AiExplanationConfigurationError(
            "Provider endpoint must be an absolute URL without credentials "
            "or a fragment."
        )


def _validate_ollama_endpoint(endpoint: str) -> None:
    parsed = urlparse(endpoint)
    is_local_http = parsed.scheme == "http" and parsed.hostname in {
        "localhost",
        "127.0.0.1",
        "::1",
        "ollama",
    }
    if parsed.scheme != "https" and not is_local_http:
        raise AiExplanationConfigurationError(
            "Ollama endpoint must use HTTPS, except for loopback or the local "
            "Compose service named 'ollama'."
        )
    if (
        not parsed.netloc
        or parsed.username
        or parsed.password
        or parsed.query
        or parsed.fragment
        or parsed.path.rstrip("/") != "/api/chat"
    ):
        raise AiExplanationConfigurationError(
            "Ollama endpoint must be an absolute /api/chat URL without "
            "credentials or query/fragment components."
        )
