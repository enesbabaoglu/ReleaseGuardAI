#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { lstatSync, readFileSync } from "node:fs";
import { isIP } from "node:net";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..");
const composePath = resolve(repositoryRoot, "compose.production.yml");
const exampleHost = "releaseguard.example.com";
const requiredSecrets = Object.freeze([
  "releaseguard_postgres_password",
  "keycloak_postgres_password",
  "keycloak_admin_password",
  "github_webhook_secret",
  "query_credential",
  "replay_credential",
]);

export function parseEnvironmentFile(contents) {
  const values = {};
  for (const [index, rawLine] of contents.split(/\r?\n/).entries()) {
    const line = rawLine.trim();
    if (line === "" || line.startsWith("#")) continue;
    const separator = line.indexOf("=");
    if (separator < 1) {
      throw new Error(`Environment satırı ${index + 1} KEY=VALUE biçiminde değil.`);
    }
    const key = line.slice(0, separator).trim();
    let value = line.slice(separator + 1).trim();
    if (!/^[A-Z][A-Z0-9_]*$/.test(key)) {
      throw new Error(`Environment satırı ${index + 1} geçersiz bir anahtar içeriyor.`);
    }
    if (Object.hasOwn(values, key)) {
      throw new Error(`${key} environment dosyasında birden çok kez tanımlanmış.`);
    }
    if (
      value.length >= 2 &&
      ((value.startsWith('"') && value.endsWith('"')) ||
        (value.startsWith("'") && value.endsWith("'")))
    ) {
      value = value.slice(1, -1);
    }
    values[key] = value;
  }
  return Object.freeze(values);
}

export function validateProductionEnvironment(values, { allowExample = false } = {}) {
  const host = required(values.RELEASEGUARD_PUBLIC_HOST, "RELEASEGUARD_PUBLIC_HOST");
  if (
    host !== host.toLowerCase() ||
    host.length > 253 ||
    host.includes(":") ||
    host.includes("/") ||
    isIP(host) !== 0 ||
    !/^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/.test(host) ||
    host === "localhost" ||
    host.endsWith(".localhost") ||
    host.endsWith(".local") ||
    host.endsWith(".internal")
  ) {
    throw new Error("RELEASEGUARD_PUBLIC_HOST port/path içermeyen public bir DNS adı olmalıdır.");
  }
  if (!allowExample && (host === "example.com" || host.endsWith(".example.com"))) {
    throw new Error("RELEASEGUARD_PUBLIC_HOST örnek domain olarak bırakılamaz.");
  }

  const email = required(values.CADDY_ACME_EMAIL, "CADDY_ACME_EMAIL");
  if (email.length > 254 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
    throw new Error("CADDY_ACME_EMAIL geçerli bir operasyon e-posta adresi olmalıdır.");
  }

  const adminUsername = required(
    values.RELEASEGUARD_KEYCLOAK_ADMIN_USERNAME,
    "RELEASEGUARD_KEYCLOAK_ADMIN_USERNAME",
  );
  if (!/^[A-Za-z0-9._-]{3,128}$/.test(adminUsername)) {
    throw new Error("RELEASEGUARD_KEYCLOAK_ADMIN_USERNAME 3-128 güvenli kimlik karakteri içermelidir.");
  }

  return Object.freeze({ host, email, adminUsername });
}

export function loadProductionCompose(envFile, { allowExample = false } = {}) {
  const absoluteEnvFile = resolve(envFile);
  const values = parseEnvironmentFile(readFileSync(absoluteEnvFile, "utf8"));
  validateProductionEnvironment(values, { allowExample });
  const output = execFileSync(
    "docker",
    [
      "compose",
      "--env-file",
      absoluteEnvFile,
      "-f",
      composePath,
      "config",
      "--format",
      "json",
    ],
    { cwd: repositoryRoot, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
  );
  const model = JSON.parse(output);
  const realm = JSON.parse(
    readFileSync(resolve(repositoryRoot, "deploy/production/keycloak/releaseguard-realm.json"), "utf8"),
  );
  const caddyfile = readFileSync(resolve(repositoryRoot, "deploy/production/Caddyfile"), "utf8");
  assertProductionCompose(model, realm, caddyfile);
  return Object.freeze({ model, realm, caddyfile, values });
}

export function assertProductionCompose(model, realm, caddyfile) {
  assert(model?.name === "releaseguard-production", "Production Compose project adı sabit değil.");
  assert(model.networks?.app?.internal === true, "Uygulama ağı external erişime kapalı olmalıdır.");
  assert(model.networks?.edge?.internal !== true, "Caddy edge ağı public çıkış yapabilmelidir.");
  assert(model.networks?.egress?.internal !== true, "Model init ağı model indirebilmelidir.");

  const publishedServices = Object.entries(model.services)
    .filter(([, service]) => Array.isArray(service.ports) && service.ports.length > 0)
    .map(([name]) => name);
  assert(
    publishedServices.length === 1 && publishedServices[0] === "caddy",
    "Host portlarını yalnız Caddy yayımlamalıdır.",
  );
  const publishedPorts = model.services.caddy.ports
    .map((port) => `${port.published}/${port.protocol}`)
    .sort();
  assert(
    JSON.stringify(publishedPorts) === JSON.stringify(["443/tcp", "443/udp", "80/tcp"]),
    "Caddy yalnız 80/tcp ve 443/tcp+udp yayımlamalıdır.",
  );
  assert(
    Object.keys(model.services.caddy.networks).length === 1 && model.services.caddy.networks.edge === null,
    "Caddy veri ağına doğrudan bağlanmamalıdır.",
  );

  const serialized = JSON.stringify(model);
  assert(!serialized.includes("start-dev"), "Production Keycloak start-dev kullanamaz.");
  assert(!serialized.includes("dev-container"), "Production Redpanda dev-container kullanamaz.");
  assert(!serialized.includes("unsafe-bypass-fsync"), "Production Redpanda fsync bypass kullanamaz.");
  assert(arrayText(model.services.redpanda.command).includes("rpk redpanda mode production"), "Redpanda production mode zorunludur.");
  for (const [name, service] of Object.entries(model.services)) {
    if (service.image) {
      assert(!service.image.endsWith(":latest"), `${name} latest image etiketi kullanamaz.`);
    }
    if (!["topic-init", "ollama-model-init"].includes(name)) {
      assert(service.restart === "unless-stopped", `${name} restart policy içermelidir.`);
      assert(service.healthcheck, `${name} healthcheck içermelidir.`);
      assert(service.mem_limit, `${name} bellek sınırı içermelidir.`);
    }
  }

  const keycloak = model.services.keycloak;
  const keycloakCommand = arrayText(keycloak.command);
  assert(keycloakCommand.includes("start --optimized --import-realm"), "Keycloak optimized başlamalıdır.");
  assert(keycloak.environment.KC_HTTP_ENABLED === "true", "Keycloak edge TLS arkasında internal HTTP açmalıdır.");
  assert(keycloak.environment.KC_PROXY_HEADERS === "xforwarded", "Keycloak proxy header sınırı eksik.");
  assert(keycloak.environment.KC_DB === "postgres", "Keycloak kalıcı PostgreSQL kullanmalıdır.");
  assert(keycloak.environment.KC_HOSTNAME.startsWith("https://"), "Keycloak hostname HTTPS olmalıdır.");
  assert(arrayText(keycloak.healthcheck.test).includes("/identity/health/ready"), "Keycloak health relative path'i izlemelidir.");
  assert(!keycloak.ports, "Keycloak doğrudan host portu yayımlayamaz.");

  const dashboard = model.services.dashboard;
  assert(dashboard.environment.DASHBOARD_AUTH_ENABLED === "true", "Dashboard auth production'da açık olmalıdır.");
  assert(dashboard.environment.DASHBOARD_SECURE_COOKIE === "true", "Dashboard cookie Secure olmalıdır.");
  assert(dashboard.environment.DASHBOARD_EXTERNAL_URL.startsWith("https://"), "Dashboard external URL HTTPS olmalıdır.");

  assert(model.services["webhook-api"].environment.ASPNETCORE_ENVIRONMENT === "Production", ".NET production environment kullanmalıdır.");
  assert(model.services["webhook-api"].environment.AiExplanationMetricsExport__Enabled === "false", "Hedefsiz OTLP export açık bırakılamaz.");
  const topicInitCommand = arrayText(model.services["topic-init"].command);
  assert(topicInitCommand.includes("write_caching_default disabled"), "Redpanda write cache cluster seviyesinde kapanmalıdır.");
  assert(topicInitCommand.includes("auto_create_topics_enabled false"), "Redpanda topic auto-create kapalı olmalıdır.");

  for (const secret of requiredSecrets) {
    assert(model.secrets?.[secret]?.file, `${secret} file-backed Compose secret olmalıdır.`);
  }
  assertServiceSecrets(model, "postgres", ["releaseguard_postgres_password"]);
  assertServiceSecrets(model, "keycloak-postgres", ["keycloak_postgres_password"]);
  assertServiceSecrets(model, "keycloak", ["keycloak_admin_password", "keycloak_postgres_password"]);
  assertServiceSecrets(model, "webhook-api", [
    "github_webhook_secret",
    "query_credential",
    "releaseguard_postgres_password",
    "replay_credential",
  ]);
  assertServiceSecrets(model, "dashboard", ["query_credential", "replay_credential"]);

  assert(realm.sslRequired === "external", "Production realm external TLS istemelidir.");
  assert(Array.isArray(realm.users) && realm.users.length === 0, "Production realm kullanıcı/parola seed edemez.");
  const client = realm.clients?.find((candidate) => candidate.clientId === "releaseguard-dashboard");
  assert(client?.publicClient === true, "Dashboard OIDC client public olmalıdır.");
  assert(client.standardFlowEnabled === true, "Authorization Code flow açık olmalıdır.");
  assert(client.directAccessGrantsEnabled === false, "Direct grant kapalı olmalıdır.");
  assert(client.implicitFlowEnabled === false, "Implicit flow kapalı olmalıdır.");
  assert(client.attributes?.["pkce.code.challenge.method"] === "S256", "PKCE S256 zorunlu olmalıdır.");

  for (const fragment of [
    "admin off",
    "{$RELEASEGUARD_PUBLIC_HOST}",
    "/identity/admin*",
    "/identity/realms/master*",
    "handle /webhooks/github",
    "reverse_proxy webhook-api:8080",
    "reverse_proxy dashboard:3000",
  ]) {
    assert(caddyfile.includes(fragment), `Caddyfile '${fragment}' sınırını içermiyor.`);
  }
}

export function assertSecretFiles(model) {
  const values = new Map();
  for (const name of requiredSecrets) {
    const path = model.secrets?.[name]?.file;
    assert(typeof path === "string", `${name} secret dosya yolu çözülemedi.`);
    const metadata = lstatSync(path);
    assert(metadata.isFile() && !metadata.isSymbolicLink(), `${name} regular file olmalıdır; symlink kabul edilmez.`);
    assert((metadata.mode & 0o077) === 0, `${name} yalnız dosya sahibi tarafından okunabilir olmalıdır (chmod 600).`);
    const raw = readFileSync(path, "utf8");
    const value = raw.endsWith("\n") ? raw.slice(0, -1) : raw;
    assert(value === value.trim(), `${name} başta/sonda whitespace içeremez.`);
    assert(/^(?=.{32,512}$)[A-Za-z0-9._~+/-]+={0,2}$/.test(value), `${name} 32-512 güvenli secret karakteri içermelidir.`);
    assert(!values.has(value), `${name} başka bir production secret ile aynı olamaz.`);
    values.set(value, name);
  }
}

function assertServiceSecrets(model, serviceName, expected) {
  const actual = (model.services[serviceName].secrets ?? []).map((secret) => secret.source).sort();
  assert(
    JSON.stringify(actual) === JSON.stringify([...expected].sort()),
    `${serviceName} yalnız gerekli secret dosyalarını almalıdır.`,
  );
}

function arrayText(value) {
  return Array.isArray(value) ? value.join("\n") : String(value ?? "");
}

function required(value, name) {
  if (typeof value !== "string" || value.trim() === "") {
    throw new Error(`${name} zorunludur.`);
  }
  return value.trim();
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

async function main(arguments_) {
  let envFile = resolve(repositoryRoot, "deploy/production/production.env");
  let checkSecrets = false;
  for (let index = 0; index < arguments_.length; index += 1) {
    const argument = arguments_[index];
    if (argument === "--env-file" && arguments_[index + 1]) {
      envFile = resolve(arguments_[index + 1]);
      index += 1;
    } else if (argument === "--check-secrets") {
      checkSecrets = true;
    } else {
      throw new Error(`Bilinmeyen argüman: ${argument}`);
    }
  }
  const { model } = loadProductionCompose(envFile);
  if (checkSecrets) assertSecretFiles(model);
  process.stdout.write(`Production Compose doğrulandı${checkSecrets ? "; secret dosyaları güvenli" : ""}.\n`);
}

const runningDirectly = process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url;
if (runningDirectly) {
  main(process.argv.slice(2)).catch((error) => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
