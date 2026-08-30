from __future__ import annotations

import pytest

from releaseguard_ai.settings import (
    AiExplanationConfigurationError,
    AiExplanationSettings,
)


def test_fake_provider_configuration_is_explicit() -> None:
    settings = AiExplanationSettings.from_environment(
        {
            "RELEASEGUARD_AI_PROVIDER": "fake",
            "RELEASEGUARD_AI_MODEL": "deterministic-v1",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "2.5",
        }
    )

    assert settings == AiExplanationSettings(
        provider="fake",
        model="deterministic-v1",
        timeout_seconds=2.5,
    )


def test_http_json_provider_configuration_accepts_https() -> None:
    settings = AiExplanationSettings.from_environment(
        {
            "RELEASEGUARD_AI_PROVIDER": "http-json",
            "RELEASEGUARD_AI_MODEL": "configured-model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "10",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "https://models.example/v1/explain",
            "RELEASEGUARD_AI_PROVIDER_API_KEY": "secret-from-environment",
        }
    )

    assert settings.provider_endpoint == "https://models.example/v1/explain"
    assert settings.provider_api_key == "secret-from-environment"


def test_http_json_provider_allows_loopback_http_for_local_testing() -> None:
    settings = AiExplanationSettings.from_environment(
        {
            "RELEASEGUARD_AI_PROVIDER": "http-json",
            "RELEASEGUARD_AI_MODEL": "configured-model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "10",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "http://127.0.0.1:8090/explain",
            "RELEASEGUARD_AI_PROVIDER_API_KEY": "local-only",
        }
    )

    assert settings.provider_endpoint == "http://127.0.0.1:8090/explain"


def test_ollama_provider_accepts_local_compose_endpoint_without_api_key() -> None:
    settings = AiExplanationSettings.from_environment(
        {
            "RELEASEGUARD_AI_PROVIDER": "ollama",
            "RELEASEGUARD_AI_MODEL": "qwen3:1.7b",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "30",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "http://ollama:11434/api/chat",
            "RELEASEGUARD_AI_OUTPUT_LANGUAGE": "tr",
        }
    )

    assert settings.provider == "ollama"
    assert settings.provider_endpoint == "http://ollama:11434/api/chat"
    assert settings.provider_api_key is None
    assert settings.output_language == "tr"


@pytest.mark.parametrize(
    "environment",
    [
        {},
        {
            "RELEASEGUARD_AI_PROVIDER": "fake",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "1",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "unknown",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "1",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "fake",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "forever",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "fake",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "0.01",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "fake",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "61",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "http-json",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "1",
            "RELEASEGUARD_AI_PROVIDER_API_KEY": "secret",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "http-json",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "1",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "https://models.example/explain",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "http-json",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "1",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "http://models.example/explain",
            "RELEASEGUARD_AI_PROVIDER_API_KEY": "secret",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "http-json",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "1",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "https://user:pass@models.example/x",
            "RELEASEGUARD_AI_PROVIDER_API_KEY": "secret",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "ollama",
            "RELEASEGUARD_AI_MODEL": "qwen3:1.7b",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "30",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "ollama",
            "RELEASEGUARD_AI_MODEL": "qwen3:1.7b",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "30",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "http://remote.example/api/chat",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "ollama",
            "RELEASEGUARD_AI_MODEL": "qwen3:1.7b",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "30",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "http://ollama:11434/api/generate",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "ollama",
            "RELEASEGUARD_AI_MODEL": "qwen3:1.7b",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "30",
            "RELEASEGUARD_AI_PROVIDER_ENDPOINT": "http://ollama:11434/api/chat",
            "RELEASEGUARD_AI_PROVIDER_API_KEY": "must-not-be-used",
        },
        {
            "RELEASEGUARD_AI_PROVIDER": "fake",
            "RELEASEGUARD_AI_MODEL": "model",
            "RELEASEGUARD_AI_TIMEOUT_SECONDS": "1",
            "RELEASEGUARD_AI_OUTPUT_LANGUAGE": "de",
        },
    ],
)
def test_missing_or_invalid_configuration_fails_explicitly(
    environment: dict[str, str],
) -> None:
    with pytest.raises(AiExplanationConfigurationError):
        AiExplanationSettings.from_environment(environment)
