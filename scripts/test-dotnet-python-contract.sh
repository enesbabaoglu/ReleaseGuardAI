#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "${script_directory}/.." && pwd)"
python_project="${repository_root}/src/ReleaseGuard.AiExplanation.Api"
python_executable="${RELEASEGUARD_AI_PYTHON:-${python_project}/.venv/bin/python}"

if [[ ! -x "${python_executable}" ]]; then
  echo "Python test environment not found at ${python_executable}. Follow the README Python setup first." >&2
  exit 1
fi

export RELEASEGUARD_AI_PYTHON="${python_executable}"

dotnet test \
  "${repository_root}/tests/ReleaseGuard.WebhookIngestion.Api.Tests/ReleaseGuard.WebhookIngestion.Api.Tests.csproj" \
  --filter 'FullyQualifiedName~PythonAiExplanationContractIntegrationTests' \
  --disable-build-servers \
  -m:1
