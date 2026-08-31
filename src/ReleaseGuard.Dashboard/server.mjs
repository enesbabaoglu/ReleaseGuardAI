import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { extname, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { OidcBoundaryError, OidcSessionBoundary } from "./auth.mjs";

const root = fileURLToPath(new URL(".", import.meta.url));
const staticRoot = resolve(root, "public");
const canonicalGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const repositoryPart = /^[A-Za-z0-9_.-]{1,100}$/;
const bearerToken = /^[A-Za-z0-9._~+/-]+={0,}$/;
const maximumUpstreamBodyBytes = 256 * 1024;

const contentTypes = new Map([
  [".css", "text/css; charset=utf-8"],
  [".html", "text/html; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
]);

function loadConfig(environment = process.env) {
  const port = parseBoundedInteger(environment.DASHBOARD_PORT ?? "3000", "DASHBOARD_PORT", 1, 65535);
  const requestTimeoutMilliseconds = parseBoundedInteger(
    environment.DASHBOARD_REQUEST_TIMEOUT_MILLISECONDS ?? "5000",
    "DASHBOARD_REQUEST_TIMEOUT_MILLISECONDS",
    100,
    30000,
  );

  const authEnabled = parseBoolean(environment.DASHBOARD_AUTH_ENABLED ?? "false", "DASHBOARD_AUTH_ENABLED");
  const externalUrl = authEnabled
    ? parseOriginUrl(environment.DASHBOARD_EXTERNAL_URL, "DASHBOARD_EXTERNAL_URL")
    : null;
  const secureCookie = authEnabled
    ? parseBoolean(
      environment.DASHBOARD_SECURE_COOKIE ?? String(externalUrl.protocol === "https:"),
      "DASHBOARD_SECURE_COOKIE",
    )
    : false;
  if (authEnabled && (secureCookie !== (externalUrl.protocol === "https:"))) {
    throw new Error("DASHBOARD_SECURE_COOKIE HTTPS external URL için true, HTTP local URL için false olmalıdır.");
  }

  return Object.freeze({
    host: environment.DASHBOARD_HOST?.trim() || "0.0.0.0",
    port,
    requestTimeoutMilliseconds,
    apiBaseUrl: parseBaseUrl(environment.RELEASEGUARD_API_BASE_URL ?? "http://127.0.0.1:8080", "RELEASEGUARD_API_BASE_URL"),
    aiBaseUrl: parseBaseUrl(environment.RELEASEGUARD_AI_BASE_URL ?? "http://127.0.0.1:8090", "RELEASEGUARD_AI_BASE_URL"),
    ollamaBaseUrl: parseBaseUrl(environment.RELEASEGUARD_OLLAMA_BASE_URL ?? "http://127.0.0.1:11434", "RELEASEGUARD_OLLAMA_BASE_URL"),
    queryCredential: parseCredential(environment.RELEASEGUARD_QUERY_CREDENTIAL, "RELEASEGUARD_QUERY_CREDENTIAL"),
    replayCredential: parseCredential(environment.RELEASEGUARD_REPLAY_CREDENTIAL, "RELEASEGUARD_REPLAY_CREDENTIAL"),
    aiProvider: parseLabel(environment.RELEASEGUARD_AI_PROVIDER ?? "ollama", "RELEASEGUARD_AI_PROVIDER"),
    aiModel: parseLabel(environment.RELEASEGUARD_AI_MODEL ?? "qwen3:1.7b", "RELEASEGUARD_AI_MODEL"),
    auth: Object.freeze({
      enabled: authEnabled,
      externalUrl,
      secureCookie,
      discoveryUrl: authEnabled
        ? parseBaseUrl(required(environment.RELEASEGUARD_OIDC_DISCOVERY_URL, "RELEASEGUARD_OIDC_DISCOVERY_URL"), "RELEASEGUARD_OIDC_DISCOVERY_URL")
        : null,
      issuer: authEnabled
        ? parseBaseUrl(required(environment.RELEASEGUARD_OIDC_ISSUER, "RELEASEGUARD_OIDC_ISSUER"), "RELEASEGUARD_OIDC_ISSUER")
        : null,
      backchannelBaseUrl: authEnabled
        ? parseOriginUrl(required(environment.RELEASEGUARD_OIDC_BACKCHANNEL_BASE_URL, "RELEASEGUARD_OIDC_BACKCHANNEL_BASE_URL"), "RELEASEGUARD_OIDC_BACKCHANNEL_BASE_URL")
        : null,
      clientId: authEnabled
        ? parseIdentifier(environment.RELEASEGUARD_OIDC_CLIENT_ID ?? "releaseguard-dashboard", "RELEASEGUARD_OIDC_CLIENT_ID")
        : null,
      viewerRole: authEnabled
        ? parseIdentifier(environment.RELEASEGUARD_OIDC_VIEWER_ROLE ?? "releaseguard-viewer", "RELEASEGUARD_OIDC_VIEWER_ROLE")
        : null,
      operatorRole: authEnabled
        ? parseIdentifier(environment.RELEASEGUARD_OIDC_OPERATOR_ROLE ?? "releaseguard-operator", "RELEASEGUARD_OIDC_OPERATOR_ROLE")
        : null,
      sessionLifetimeSeconds: authEnabled
        ? parseBoundedInteger(environment.DASHBOARD_SESSION_LIFETIME_SECONDS ?? "28800", "DASHBOARD_SESSION_LIFETIME_SECONDS", 300, 28800)
        : null,
      requestTimeoutMilliseconds,
    }),
  });
}

function required(value, name) {
  if (typeof value !== "string" || value.trim() === "") {
    throw new Error(`${name} zorunludur.`);
  }
  return value.trim();
}

function parseBoundedInteger(value, name, minimum, maximum) {
  if (!/^\d+$/.test(value)) {
    throw new Error(`${name} bir tam sayı olmalıdır.`);
  }
  const parsed = Number.parseInt(value, 10);
  if (parsed < minimum || parsed > maximum) {
    throw new Error(`${name} ${minimum}-${maximum} aralığında olmalıdır.`);
  }
  return parsed;
}

function parseBaseUrl(value, name) {
  let parsed;
  try {
    parsed = new URL(value);
  } catch {
    throw new Error(`${name} mutlak bir URL olmalıdır.`);
  }
  if (
    !["http:", "https:"].includes(parsed.protocol) ||
    parsed.username ||
    parsed.password ||
    parsed.search ||
    parsed.hash
  ) {
    throw new Error(`${name} yalnız HTTP(S), host ve isteğe bağlı temel path içermelidir.`);
  }
  parsed.pathname = parsed.pathname.replace(/\/+$/, "");
  return parsed;
}

function parseOriginUrl(value, name) {
  const parsed = parseBaseUrl(required(value, name), name);
  if (parsed.pathname !== "" && parsed.pathname !== "/") {
    throw new Error(`${name} path içermeyen bir origin olmalıdır.`);
  }
  parsed.pathname = "/";
  return parsed;
}

function parseBoolean(value, name) {
  if (value !== "true" && value !== "false") {
    throw new Error(`${name} true veya false olmalıdır.`);
  }
  return value === "true";
}

function parseIdentifier(value, name) {
  if (!/^[A-Za-z0-9._-]{1,128}$/.test(value)) {
    throw new Error(`${name} 1-128 güvenli kimlik karakteri içermelidir.`);
  }
  return value;
}

function parseCredential(value, name) {
  if (value === undefined || value === "") {
    return null;
  }
  if (value.length < 32 || value.length > 512 || !bearerToken.test(value)) {
    throw new Error(`${name} geçerli bir 32-512 karakter bearer token olmalıdır.`);
  }
  return value;
}

function parseLabel(value, name) {
  const label = value.trim();
  if (label.length < 1 || label.length > 128 || /[\u0000-\u001f\u007f]/.test(label)) {
    throw new Error(`${name} 1-128 görünür karakter içermelidir.`);
  }
  return label;
}

function createDashboardServer(config = loadConfig(), fetchImplementation = fetch, dependencies = {}) {
  const authBoundary = config.auth.enabled
    ? new OidcSessionBoundary(config.auth, fetchImplementation, dependencies)
    : null;
  return createServer(async (request, response) => {
    applySecurityHeaders(response);
    const requestUrl = new URL(request.url ?? "/", "http://dashboard.local");

    try {
      if (requestUrl.pathname === "/healthz" && request.method === "GET") {
        writeJson(response, 200, { status: "ok" });
        return;
      }

      if (requestUrl.pathname === "/login" && request.method === "GET") {
        if (!authBoundary) {
          writeRedirect(response, "/");
          return;
        }
        try {
          await authBoundary.beginLogin(response);
        } catch (error) {
          if (!(error instanceof OidcBoundaryError)) throw error;
          writeProblem(response, 503, "dashboard_identity_unavailable", "Kimlik sağlayıcı şu anda yanıt vermiyor.");
        }
        return;
      }

      if (requestUrl.pathname === "/auth/callback" && request.method === "GET" && authBoundary) {
        try {
          await authBoundary.completeLogin(request, response, requestUrl);
        } catch (error) {
          if (!(error instanceof OidcBoundaryError)) throw error;
          writeProblem(response, 400, "dashboard_login_failed", "Login yanıtı doğrulanamadı veya süresi doldu.");
        }
        return;
      }

      if (requestUrl.pathname === "/signed-out" && request.method === "GET") {
        await serveStatic(request, response, "/signed-out.html");
        return;
      }
      if (requestUrl.pathname === "/styles.css" && request.method === "GET") {
        await serveStatic(request, response, requestUrl.pathname);
        return;
      }

      if (requestUrl.pathname.startsWith("/api/")) {
        const session = authBoundary?.getSession(request) ?? null;
        if (requestUrl.pathname === "/api/session" && request.method === "GET") {
          if (authBoundary && !session) {
            writeProblem(response, 401, "dashboard_authentication_required", "Dashboard oturumu gerekli.");
            return;
          }
          writeJson(response, 200, authBoundary
            ? authBoundary.sessionView(session)
            : { authEnabled: false, user: { displayName: "Yerel geliştirme" }, roles: ["local-operator"], canView: true, canReplay: true, csrfToken: null, expiresAt: null });
          return;
        }
        if (authBoundary && !session) {
          writeProblem(response, 401, "dashboard_authentication_required", "Dashboard oturumu gerekli.");
          return;
        }
        if (authBoundary && requestUrl.pathname === "/api/logout" && request.method === "POST") {
          if (!authBoundary.verifyCsrf(session, request.headers["x-csrf-token"])) {
            writeProblem(response, 403, "dashboard_csrf_invalid", "Logout isteğinin CSRF doğrulaması başarısız.");
            return;
          }
          const redirectTo = await authBoundary.endSession(request, response);
          writeJson(response, 200, { redirectTo });
          return;
        }
        if (authBoundary && !authBoundary.hasViewerAccess(session)) {
          writeProblem(response, 403, "dashboard_role_required", "ReleaseGuard viewer veya operator rolü gerekli.");
          return;
        }
        await handleApi(request, response, requestUrl, config, fetchImplementation, authBoundary, session);
        return;
      }

      if (authBoundary) {
        const session = authBoundary.getSession(request);
        if (!session) {
          writeRedirect(response, "/login");
          return;
        }
        if (!authBoundary.hasViewerAccess(session)) {
          writeProblem(response, 403, "dashboard_role_required", "ReleaseGuard viewer veya operator rolü gerekli.");
          return;
        }
      }
      await serveStatic(request, response, requestUrl.pathname);
    } catch (error) {
      if (!response.headersSent) {
        if (error instanceof DashboardRequestError) {
          writeProblem(response, 400, "dashboard_request_invalid", error.message);
        } else {
          writeProblem(response, 500, "dashboard_internal_error", "Dashboard isteği tamamlanamadı.");
        }
      } else {
        response.destroy(error);
      }
    }
  });
}

async function handleApi(request, response, requestUrl, config, fetchImplementation, authBoundary, session) {
  if (requestUrl.pathname === "/api/status" && request.method === "GET") {
    await writeStatus(response, config, fetchImplementation);
    return;
  }

  if (requestUrl.pathname === "/api/explanations" && request.method === "GET") {
    const upstreamQuery = parseListQuery(requestUrl.searchParams);
    await proxyRequest(request, response, config, fetchImplementation, {
      method: "GET",
      path: `v1/release-risk-events/ai-explanations${upstreamQuery}`,
      credential: config.queryCredential,
    });
    return;
  }

  const eventMatch = requestUrl.pathname.match(/^\/api\/events\/([^/]+)\/explanation$/);
  if (eventMatch && request.method === "GET" && canonicalGuid.test(eventMatch[1])) {
    await proxyRequest(request, response, config, fetchImplementation, {
      method: "GET",
      path: `v1/release-risk-events/${eventMatch[1]}/ai-explanation`,
      credential: config.queryCredential,
    });
    return;
  }

  const replayMatch = requestUrl.pathname.match(/^\/api\/events\/([^/]+)\/replays$/);
  if (replayMatch && request.method === "POST" && canonicalGuid.test(replayMatch[1])) {
    if (authBoundary && !authBoundary.hasOperatorAccess(session)) {
      writeProblem(response, 403, "dashboard_operator_role_required", "Replay için ReleaseGuard operator rolü gerekli.");
      return;
    }
    if (authBoundary && !authBoundary.verifyCsrf(session, request.headers["x-csrf-token"])) {
      writeProblem(response, 403, "dashboard_csrf_invalid", "Replay isteğinin CSRF doğrulaması başarısız.");
      return;
    }
    if (request.headers["transfer-encoding"] || Number(request.headers["content-length"] ?? 0) > 0) {
      writeProblem(response, 400, "dashboard_replay_request_invalid", "Replay isteği body içeremez.");
      return;
    }
    const idempotencyKey = request.headers["idempotency-key"];
    if (typeof idempotencyKey !== "string" || !canonicalGuid.test(idempotencyKey)) {
      writeProblem(response, 400, "dashboard_replay_request_invalid", "Tek ve canonical bir Idempotency-Key gereklidir.");
      return;
    }
    await proxyRequest(request, response, config, fetchImplementation, {
      method: "POST",
      path: `v1/release-risk-events/${replayMatch[1]}/ai-explanation/replays`,
      credential: config.replayCredential,
      headers: { "Idempotency-Key": idempotencyKey },
    });
    return;
  }

  const latestMatch = requestUrl.pathname.match(/^\/api\/repositories\/([^/]+)\/([^/]+)\/changes\/([^/]+)\/latest$/);
  if (latestMatch && request.method === "GET") {
    const [, rawOwner, rawRepository, rawChangeNumber] = latestMatch;
    if (
      !repositoryPart.test(rawOwner) || rawOwner === "." || rawOwner === ".." ||
      !repositoryPart.test(rawRepository) || rawRepository === "." || rawRepository === ".." ||
      !/^[1-9]\d{0,18}$/.test(rawChangeNumber)
    ) {
      writeProblem(response, 400, "dashboard_latest_route_invalid", "Depo ve değişiklik yolu geçersiz.");
      return;
    }
    await proxyRequest(request, response, config, fetchImplementation, {
      method: "GET",
      path: `v1/repositories/${rawOwner}/${rawRepository}/changes/${rawChangeNumber}/ai-explanation/latest-accepted`,
      credential: config.queryCredential,
    });
    return;
  }

  writeProblem(response, 404, "dashboard_route_not_found", "Dashboard API yolu bulunamadı.");
}

function parseListQuery(parameters) {
  for (const key of parameters.keys()) {
    if (key !== "limit" && key !== "cursor") {
      throw new DashboardRequestError("Liste sorgusu yalnız limit ve cursor içerebilir.");
    }
  }
  if (parameters.getAll("limit").length > 1 || parameters.getAll("cursor").length > 1) {
    throw new DashboardRequestError("Liste parametreleri birden fazla gönderilemez.");
  }
  const limitText = parameters.get("limit") ?? "50";
  if (!/^\d+$/.test(limitText) || Number(limitText) < 1 || Number(limitText) > 100) {
    throw new DashboardRequestError("limit 1-100 aralığında olmalıdır.");
  }
  const cursor = parameters.get("cursor");
  if (cursor !== null && (cursor.length < 1 || cursor.length > 256 || !/^[A-Za-z0-9_-]+$/.test(cursor))) {
    throw new DashboardRequestError("cursor geçersizdir.");
  }

  const upstream = new URLSearchParams({ limit: limitText });
  if (cursor !== null) {
    upstream.set("cursor", cursor);
  }
  return `?${upstream.toString()}`;
}

class DashboardRequestError extends Error {}

async function proxyRequest(request, response, config, fetchImplementation, upstream) {
  if (upstream.credential === null) {
    writeProblem(response, 503, "dashboard_credential_not_configured", "Dashboard backend bağlantısı yapılandırılmamış.");
    return;
  }
  const headers = {
    Accept: "application/json",
    Authorization: `Bearer ${upstream.credential}`,
    ...upstream.headers,
  };

  let upstreamResponse;
  try {
    upstreamResponse = await fetchWithTimeout(
      new URL(upstream.path, withTrailingSlash(config.apiBaseUrl)),
      { method: upstream.method, headers },
      config.requestTimeoutMilliseconds,
      fetchImplementation,
      request,
    );
  } catch {
    writeProblem(response, 502, "dashboard_upstream_unavailable", "ReleaseGuard API yanıt vermedi.");
    return;
  }

  let body;
  try {
    body = await readBoundedBody(upstreamResponse);
  } catch {
    writeProblem(response, 502, "dashboard_upstream_contract_invalid", "ReleaseGuard API yanıtı güvenli sınırı aştı.");
    return;
  }
  const responseHeaders = {
    "Content-Type": safeJsonContentType(upstreamResponse.headers.get("content-type")),
    "Cache-Control": "no-store",
  };
  const retryAfter = upstreamResponse.headers.get("retry-after");
  if (retryAfter && /^\d{1,10}$/.test(retryAfter)) {
    responseHeaders["Retry-After"] = retryAfter;
  }
  response.writeHead(upstreamResponse.status, responseHeaders);
  response.end(body);
}

async function writeStatus(response, config, fetchImplementation) {
  const [api, ai, ollama] = await Promise.all([
    probe(new URL("health", withTrailingSlash(config.apiBaseUrl)), config, fetchImplementation),
    probe(new URL("health", withTrailingSlash(config.aiBaseUrl)), config, fetchImplementation),
    probeOllama(new URL("api/tags", withTrailingSlash(config.ollamaBaseUrl)), config, fetchImplementation),
  ]);
  writeJson(response, 200, {
    services: { api, ai, ollama: ollama.status },
    ai: {
      provider: config.aiProvider,
      model: config.aiModel,
      modelReady: ollama.models.includes(config.aiModel),
    },
    generatedAt: new Date().toISOString(),
  });
}

async function probe(url, config, fetchImplementation) {
  try {
    const result = await fetchWithTimeout(url, { headers: { Accept: "application/json" } }, config.requestTimeoutMilliseconds, fetchImplementation);
    return result.ok ? "online" : "degraded";
  } catch {
    return "offline";
  }
}

async function probeOllama(url, config, fetchImplementation) {
  try {
    const result = await fetchWithTimeout(url, { headers: { Accept: "application/json" } }, config.requestTimeoutMilliseconds, fetchImplementation);
    if (!result.ok) {
      return { status: "degraded", models: [] };
    }
    const payload = JSON.parse(new TextDecoder().decode(await readBoundedBody(result)));
    const models = Array.isArray(payload.models)
      ? payload.models.map((model) => model?.name).filter((name) => typeof name === "string")
      : [];
    return { status: "online", models };
  } catch {
    return { status: "offline", models: [] };
  }
}

async function fetchWithTimeout(url, options, timeoutMilliseconds, fetchImplementation, incomingRequest) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMilliseconds);
  const abort = () => controller.abort();
  incomingRequest?.once("aborted", abort);
  try {
    return await fetchImplementation(url, { ...options, signal: controller.signal });
  } finally {
    clearTimeout(timeout);
    incomingRequest?.off("aborted", abort);
  }
}

async function readBoundedBody(upstreamResponse) {
  const contentLength = upstreamResponse.headers.get("content-length");
  if (contentLength && Number(contentLength) > maximumUpstreamBodyBytes) {
    throw new Error("Upstream response is too large.");
  }
  if (upstreamResponse.body === null) {
    return new Uint8Array();
  }
  const reader = upstreamResponse.body.getReader();
  const chunks = [];
  let totalBytes = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    totalBytes += value.byteLength;
    if (totalBytes > maximumUpstreamBodyBytes) {
      await reader.cancel();
      throw new Error("Upstream response is too large.");
    }
    chunks.push(value);
  }
  const result = new Uint8Array(totalBytes);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return result;
}

async function serveStatic(request, response, rawPath) {
  if (request.method !== "GET" && request.method !== "HEAD") {
    writeProblem(response, 405, "dashboard_method_not_allowed", "Bu yöntem desteklenmiyor.");
    return;
  }
  const relativePath = rawPath === "/" ? "index.html" : normalize(rawPath).replace(/^\/+/, "");
  const filePath = resolve(staticRoot, relativePath);
  if (!filePath.startsWith(`${staticRoot}/`) && filePath !== staticRoot) {
    writeProblem(response, 404, "dashboard_asset_not_found", "Dosya bulunamadı.");
    return;
  }

  try {
    const fileStat = await stat(filePath);
    if (!fileStat.isFile()) {
      throw new Error("Not found");
    }
    response.writeHead(200, {
      "Content-Type": contentTypes.get(extname(filePath)) ?? "application/octet-stream",
      "Cache-Control": "no-store",
    });
    response.end(request.method === "HEAD" ? undefined : await readFile(filePath));
  } catch {
    writeProblem(response, 404, "dashboard_asset_not_found", "Dosya bulunamadı.");
  }
}

function withTrailingSlash(url) {
  const copy = new URL(url);
  copy.pathname = `${copy.pathname.replace(/\/+$/, "")}/`;
  return copy;
}

function safeJsonContentType(value) {
  return value?.toLowerCase().startsWith("application/problem+json")
    ? "application/problem+json; charset=utf-8"
    : "application/json; charset=utf-8";
}

function applySecurityHeaders(response) {
  response.setHeader("Content-Security-Policy", "default-src 'self'; base-uri 'none'; connect-src 'self'; form-action 'none'; frame-ancestors 'none'; img-src 'self'; object-src 'none'; script-src 'self'; style-src 'self'");
  response.setHeader("Cross-Origin-Opener-Policy", "same-origin");
  response.setHeader("Permissions-Policy", "camera=(), geolocation=(), microphone=()");
  response.setHeader("Referrer-Policy", "no-referrer");
  response.setHeader("X-Content-Type-Options", "nosniff");
  response.setHeader("X-Frame-Options", "DENY");
}

function writeRedirect(response, location) {
  response.writeHead(303, { Location: location, "Cache-Control": "no-store" });
  response.end();
}

function writeJson(response, status, body) {
  response.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  response.end(JSON.stringify(body));
}

function writeProblem(response, status, code, detail) {
  response.writeHead(status, { "Content-Type": "application/problem+json; charset=utf-8", "Cache-Control": "no-store" });
  response.end(JSON.stringify({ status, code, title: "Dashboard request failed.", detail }));
}

const runningDirectly = fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (runningDirectly) {
  let config;
  try {
    config = loadConfig();
  } catch (error) {
    console.error(`Dashboard yapılandırması geçersiz: ${error.message}`);
    process.exitCode = 1;
  }
  if (config) {
    createDashboardServer(config).listen(config.port, config.host, () => {
      console.log(`ReleaseGuard Dashboard: http://127.0.0.1:${config.port}`);
    });
  }
}

export { createDashboardServer, loadConfig };
