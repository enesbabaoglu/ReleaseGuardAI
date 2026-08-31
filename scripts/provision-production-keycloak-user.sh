#!/usr/bin/env bash

set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "${script_directory}/.." && pwd)"
environment_file="${1:-${repository_root}/deploy/production/production.env}"
role="${2:-releaseguard-operator}"

if [[ ! -f "${environment_file}" ]]; then
  echo "Production environment dosyası bulunamadı: ${environment_file}" >&2
  exit 1
fi

if [[ "${role}" != "releaseguard-viewer" && "${role}" != "releaseguard-operator" ]]; then
  echo "Rol releaseguard-viewer veya releaseguard-operator olmalıdır." >&2
  exit 1
fi

if [[ ! -t 0 ]]; then
  echo "Kullanıcı bilgileri ve geçici parola interaktif terminalden alınmalıdır." >&2
  exit 1
fi

read -r -p "Keycloak kullanıcı adı: " username
read -r -p "Doğrulanmış e-posta: " email
read -r -s -p "En az 14 karakter geçici parola: " password
echo
read -r -s -p "Geçici parolayı tekrar girin: " password_confirmation
echo

if [[ ! "${username}" =~ ^[A-Za-z0-9._-]{3,128}$ ]]; then
  echo "Kullanıcı adı 3-128 güvenli kimlik karakteri içermelidir." >&2
  exit 1
fi

if [[ ! "${email}" =~ ^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$ ]]; then
  echo "Geçerli bir e-posta adresi girilmelidir." >&2
  exit 1
fi

if (( ${#password} < 14 )); then
  echo "Geçici parola en az 14 karakter olmalıdır." >&2
  exit 1
fi

if [[ "${password}" != "${password_confirmation}" ]]; then
  echo "Geçici parola doğrulaması eşleşmedi." >&2
  exit 1
fi

printf '%s\n%s\n%s\n%s\n' "${username}" "${email}" "${password}" "${role}" |
  docker compose \
    --env-file "${environment_file}" \
    -f "${repository_root}/compose.production.yml" \
    exec -T keycloak /bin/bash -ec '
      set -euo pipefail
      IFS= read -r username
      IFS= read -r email
      IFS= read -r password
      IFS= read -r role
      cli=/opt/keycloak/bin/kcadm.sh
      cli_config=/tmp/releaseguard-kcadm.config
      trap '\''rm -f "${cli_config}"'\'' EXIT

      "${cli}" config credentials \
        --config "${cli_config}" \
        --server http://127.0.0.1:8080/identity \
        --realm master \
        --user "${KC_BOOTSTRAP_ADMIN_USERNAME}" \
        --password "$(</run/secrets/keycloak_admin_password)" >/dev/null

      existing="$("${cli}" get users \
        --config "${cli_config}" \
        -r releaseguard \
        -q "username=${username}" \
        -q exact=true \
        --fields id \
        --format csv \
        --noquotes)"
      if [[ -n "${existing}" ]]; then
        echo "Kullanıcı zaten var; hiçbir şey değiştirilmedi." >&2
        exit 1
      fi

      "${cli}" create users \
        --config "${cli_config}" \
        -r releaseguard \
        -s "username=${username}" \
        -s "email=${email}" \
        -s enabled=true \
        -s emailVerified=true >/dev/null
      "${cli}" set-password \
        --config "${cli_config}" \
        -r releaseguard \
        --username "${username}" \
        --new-password "${password}" \
        --temporary >/dev/null
      "${cli}" add-roles \
        --config "${cli_config}" \
        -r releaseguard \
        --uusername "${username}" \
        --cclientid releaseguard-dashboard \
        --rolename "${role}" >/dev/null
      echo "${username} kullanıcısı ${role} rolüyle oluşturuldu; parola ilk login sırasında değiştirilmelidir."
    '

unset password password_confirmation
