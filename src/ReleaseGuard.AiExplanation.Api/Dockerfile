FROM python:3.12.11-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1
WORKDIR /app

COPY src/ReleaseGuard.AiExplanation.Api/pyproject.toml ./
COPY src/ReleaseGuard.AiExplanation.Api/releaseguard_ai/ ./releaseguard_ai/
RUN python -m pip install --no-cache-dir .

RUN useradd --create-home --uid 10001 releaseguard \
    && chown -R releaseguard:releaseguard /app
USER releaseguard
EXPOSE 8080
CMD ["python", "-m", "uvicorn", "releaseguard_ai.main:app", "--host", "0.0.0.0", "--port", "8080"]
