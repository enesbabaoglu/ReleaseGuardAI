import assert from "node:assert/strict";
import { generateKeyPairSync, sign } from "node:crypto";
import { once } from "node:events";
import { test } from "node:test";

import { createDashboardServer, loadConfig } from "../server.mjs";

const queryCredential = "query-credential-12345678901234567890";
const replayCredential = "replay-credential-1234567890123456789";

function testConfig(overrides = {}) {
  return loadConfig({
    DASHBOARD_PORT: "3000",
    DASHBOARD_REQUEST_TIMEOUT_MILLISECONDS: "1000",
    RELEASEGUARD_API_BASE_URL: "http://releaseguard.test:8080",
    RELEASEGUARD_AI_BASE_URL: "http://ai.test:8080",
    RELEASEGUARD_OLLAMA_BASE_URL: "http://ollama.test:11434",
    RELEASEGUARD_QUERY_CREDENTIAL: queryCredential,
    RELEASEGUARD_REPLAY_CREDENTIAL: replayCredential,
    RELEASEGUARD_AI_PROVIDER: "ollama",
    RELEASEGUARD_AI_MODEL: "qwen3:1.7b",
    ...overrides,
  });
}

async function withServer(fetchImplementation, action, config = testConfig(), dependencies = {}) {
  const server = createDashboardServer(config, fetchImplementation, dependencies);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  try {
    await action(`http://127.0.0.1:${address.port}`);
  } finally {
    server.close();
    await once(server, "close");
  }
}

function authenticatedConfig(overrides = {}) {
  return testConfig({
    DASHBOARD_AUTH_ENABLED: "true",
    DASHBOARD_EXTERNAL_URL: "http://dashboard.test:3000",
    DASHBOARD_SECURE_COOKIE: "false",
    DASHBOARD_SESSION_LIFETIME_SECONDS: "3600",
    RELEASEGUARD_OIDC_DISCOVERY_URL: "http://identity.internal:8080/realms/releaseguard/.well-known/openid-configuration",
    RELEASEGUARD_OIDC_ISSUER: "http://identity.test:8180/realms/releaseguard",
    RELEASEGUARD_OIDC_BACKCHANNEL_BASE_URL: "http://identity.internal:8080",
    RELEASEGUARD_OIDC_CLIENT_ID: "releaseguard-dashboard",
    RELEASEGUARD_OIDC_VIEWER_ROLE: "releaseguard-viewer",
    RELEASEGUARD_OIDC_OPERATOR_ROLE: "releaseguard-operator",
    ...overrides,
  });
}

function createOidcHarness(roles, { expired = false, invalidSignature = false } = {}) {
  const { privateKey, publicKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
  const signingKey = invalidSignature
    ? generateKeyPairSync("rsa", { modulusLength: 2048 }).privateKey
    : privateKey;
  const key = { ...publicKey.export({ format: "jwk" }), kid: "test-key", use: "sig", alg: "RS256" };
  const issuer = "http://identity.test:8180/realms/releaseguard";
  let nonce;
  let challenge;
  const calls = [];
  const discovery = {
    issuer,
    authorization_endpoint: `${issuer}/protocol/openid-connect/auth`,
    token_endpoint: `${issuer}/protocol/openid-connect/token`,
    jwks_uri: `${issuer}/protocol/openid-connect/certs`,
    end_session_endpoint: `${issuer}/protocol/openid-connect/logout`,
  };

  const fakeFetch = async (url, options = {}) => {
    const value = String(url);
    calls.push({ url: value, options });
    if (value.endsWith("/.well-known/openid-configuration")) {
      return jsonResponse(discovery);
    }
    if (value.endsWith("/protocol/openid-connect/certs")) {
      return jsonResponse({ keys: [key] });
    }
    if (value.endsWith("/protocol/openid-connect/token")) {
      const parameters = new URLSearchParams(options.body);
      assert.equal(parameters.get("client_secret"), null);
      assert.equal(parameters.get("client_id"), "releaseguard-dashboard");
      assert.equal(parameters.get("redirect_uri"), "http://dashboard.test:3000/auth/callback");
      assert.equal(parameters.get("grant_type"), "authorization_code");
      assert.equal(
        Buffer.from(await crypto.subtle.digest("SHA-256", Buffer.from(parameters.get("code_verifier")))).toString("base64url"),
        challenge,
      );
      const now = Math.floor(Date.now() / 1000);
      return jsonResponse({
        token_type: "Bearer",
        id_token: createIdToken(signingKey, {
          iss: issuer,
          aud: "releaseguard-dashboard",
          sub: "user-123",
          exp: expired ? now - 1 : now + 3600,
          iat: now,
          nonce,
          preferred_username: "release.operator",
          resource_access: { "releaseguard-dashboard": { roles } },
        }),
      });
    }
    return jsonResponse({ items: [], nextCursor: null });
  };

  return {
    calls,
    fakeFetch,
    captureLogin(location) {
      const url = new URL(location);
      nonce = url.searchParams.get("nonce");
      challenge = url.searchParams.get("code_challenge");
      return url;
    },
  };
}

function createIdToken(privateKey, claims) {
  const header = Buffer.from(JSON.stringify({ alg: "RS256", typ: "JWT", kid: "test-key" })).toString("base64url");
  const payload = Buffer.from(JSON.stringify(claims)).toString("base64url");
  const signature = sign("RSA-SHA256", Buffer.from(`${header}.${payload}`), privateKey).toString("base64url");
  return `${header}.${payload}.${signature}`;
}

function jsonResponse(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function cookieValue(response, name) {
  const values = typeof response.headers.getSetCookie === "function"
    ? response.headers.getSetCookie()
    : [response.headers.get("set-cookie")];
  const match = values.filter(Boolean).map((value) => value.split(";", 1)[0]).find((value) => value.startsWith(`${name}=`));
  assert.ok(match, `${name} cookie bulunamadı`);
  return match;
}

async function login(baseUrl, harness) {
  const start = await fetch(`${baseUrl}/login`, { redirect: "manual" });
  assert.equal(start.status, 303);
  const authorizationUrl = harness.captureLogin(start.headers.get("location"));
  const state = authorizationUrl.searchParams.get("state");
  const transactionCookie = cookieValue(start, "releaseguard_oidc_tx");
  const callback = await fetch(`${baseUrl}/auth/callback?state=${encodeURIComponent(state)}&code=test-code`, {
    redirect: "manual",
    headers: { Cookie: transactionCookie },
  });
  return { authorizationUrl, callback, sessionCookie: callback.status === 303 ? cookieValue(callback, "releaseguard_session") : null };
}

test("configuration validates bounded values, URLs, and optional local credentials", () => {
  const config = loadConfig({
    DASHBOARD_PORT: "4173",
    DASHBOARD_REQUEST_TIMEOUT_MILLISECONDS: "500",
  });

  assert.equal(config.port, 4173);
  assert.equal(config.queryCredential, null);
  assert.equal(config.replayCredential, null);
  assert.equal(config.aiModel, "qwen3:1.7b");
  assert.throws(() => testConfig({ DASHBOARD_PORT: "0" }), /DASHBOARD_PORT/);
  assert.throws(() => testConfig({ RELEASEGUARD_API_BASE_URL: "file:///tmp/api" }), /RELEASEGUARD_API_BASE_URL/);
  assert.throws(() => testConfig({ RELEASEGUARD_QUERY_CREDENTIAL: "too-short" }), /RELEASEGUARD_QUERY_CREDENTIAL/);
  assert.throws(() => testConfig({ DASHBOARD_AUTH_ENABLED: "yes" }), /DASHBOARD_AUTH_ENABLED/);
  assert.throws(() => testConfig({ DASHBOARD_AUTH_ENABLED: "true" }), /DASHBOARD_EXTERNAL_URL/);
  assert.throws(() => authenticatedConfig({ DASHBOARD_SESSION_LIFETIME_SECONDS: "299" }), /DASHBOARD_SESSION_LIFETIME_SECONDS/);
  assert.throws(() => authenticatedConfig({ DASHBOARD_EXTERNAL_URL: "https://dashboard.test", DASHBOARD_SECURE_COOKIE: "false" }), /DASHBOARD_SECURE_COOKIE/);
});

test("authentication redirects the UI, protects APIs, and keeps health public", async () => {
  const harness = createOidcHarness(["releaseguard-viewer"]);
  await withServer(harness.fakeFetch, async (baseUrl) => {
    const root = await fetch(`${baseUrl}/`, { redirect: "manual" });
    assert.equal(root.status, 303);
    assert.equal(root.headers.get("location"), "/login");

    const api = await fetch(`${baseUrl}/api/explanations?limit=1`);
    assert.equal(api.status, 401);
    assert.equal((await api.json()).code, "dashboard_authentication_required");

    const health = await fetch(`${baseUrl}/healthz`);
    assert.equal(health.status, 200);
    assert.deepEqual(await health.json(), { status: "ok" });
  }, authenticatedConfig());
});

test("OIDC login uses authorization code with PKCE S256 and a bounded transaction cookie", async () => {
  const harness = createOidcHarness(["releaseguard-viewer"]);
  await withServer(harness.fakeFetch, async (baseUrl) => {
    const start = await fetch(`${baseUrl}/login`, { redirect: "manual" });
    const authorizationUrl = harness.captureLogin(start.headers.get("location"));
    assert.equal(start.status, 303);
    assert.equal(authorizationUrl.searchParams.get("client_id"), "releaseguard-dashboard");
    assert.equal(authorizationUrl.searchParams.get("response_type"), "code");
    assert.equal(authorizationUrl.searchParams.get("redirect_uri"), "http://dashboard.test:3000/auth/callback");
    assert.equal(authorizationUrl.searchParams.get("code_challenge_method"), "S256");
    assert.match(authorizationUrl.searchParams.get("code_challenge"), /^[A-Za-z0-9_-]{43}$/);
    assert.ok(authorizationUrl.searchParams.get("state"));
    assert.ok(authorizationUrl.searchParams.get("nonce"));
    assert.equal(harness.calls[0].options.redirect, "error");
    assert.match(start.headers.get("set-cookie"), /HttpOnly; SameSite=Lax; Max-Age=300/);
    assert.ok(!start.headers.get("location").includes(queryCredential));
    assert.ok(!start.headers.get("location").includes(replayCredential));
  }, authenticatedConfig());
});

test("signed ID token creates an opaque viewer session but viewer cannot replay", async () => {
  const harness = createOidcHarness(["releaseguard-viewer"]);
  await withServer(harness.fakeFetch, async (baseUrl) => {
    const { callback, sessionCookie } = await login(baseUrl, harness);
    assert.equal(callback.status, 303);
    assert.equal(callback.headers.get("location"), "http://dashboard.test:3000/");
    const setCookie = callback.headers.get("set-cookie");
    assert.match(setCookie, /releaseguard_session=/);
    assert.match(setCookie, /HttpOnly/);
    assert.match(setCookie, /SameSite=Lax/);
    assert.ok(!setCookie.includes("eyJ"), "JWT cookie içine yazılmamalı");

    const session = await fetch(`${baseUrl}/api/session`, { headers: { Cookie: sessionCookie } });
    const sessionBody = await session.json();
    assert.equal(session.status, 200);
    assert.equal(sessionBody.user.displayName, "release.operator");
    assert.equal(sessionBody.canView, true);
    assert.equal(sessionBody.canReplay, false);
    assert.ok(typeof sessionBody.csrfToken === "string");

    const list = await fetch(`${baseUrl}/api/explanations?limit=1`, { headers: { Cookie: sessionCookie } });
    assert.equal(list.status, 200);
    const replay = await fetch(`${baseUrl}/api/events/8b0ea7e1-c4ab-4ea5-90a5-42832ae812fa/replays`, {
      method: "POST",
      headers: { Cookie: sessionCookie, "Idempotency-Key": "b535fcbc-1608-49e4-846f-7af40da10f37", "X-CSRF-Token": sessionBody.csrfToken },
    });
    assert.equal(replay.status, 403);
    assert.equal((await replay.json()).code, "dashboard_operator_role_required");
    assert.ok(!harness.calls.some((call) => call.url.includes("/replays")));
  }, authenticatedConfig());
});

test("operator replay requires CSRF and preserves the server-side replay credential", async () => {
  const harness = createOidcHarness(["releaseguard-operator"]);
  await withServer(harness.fakeFetch, async (baseUrl) => {
    const { sessionCookie } = await login(baseUrl, harness);
    const session = await (await fetch(`${baseUrl}/api/session`, { headers: { Cookie: sessionCookie } })).json();
    const eventId = "8b0ea7e1-c4ab-4ea5-90a5-42832ae812fa";
    const replayId = "b535fcbc-1608-49e4-846f-7af40da10f37";
    const withoutCsrf = await fetch(`${baseUrl}/api/events/${eventId}/replays`, {
      method: "POST",
      headers: { Cookie: sessionCookie, "Idempotency-Key": replayId },
    });
    assert.equal(withoutCsrf.status, 403);
    assert.equal((await withoutCsrf.json()).code, "dashboard_csrf_invalid");

    const accepted = await fetch(`${baseUrl}/api/events/${eventId}/replays`, {
      method: "POST",
      headers: { Cookie: sessionCookie, "Idempotency-Key": replayId, "X-CSRF-Token": session.csrfToken },
    });
    assert.equal(accepted.status, 200);
    const replayCall = harness.calls.find((call) => call.url.endsWith(`/v1/release-risk-events/${eventId}/ai-explanation/replays`));
    assert.equal(replayCall.options.headers.Authorization, `Bearer ${replayCredential}`);
    assert.ok(!JSON.stringify(session).includes(replayCredential));
  }, authenticatedConfig());
});

test("authenticated users without a dashboard role cannot load protected UI or API data", async () => {
  const harness = createOidcHarness([]);
  await withServer(harness.fakeFetch, async (baseUrl) => {
    const { sessionCookie } = await login(baseUrl, harness);
    const root = await fetch(`${baseUrl}/`, { headers: { Cookie: sessionCookie } });
    assert.equal(root.status, 403);
    assert.equal((await root.json()).code, "dashboard_role_required");
    const list = await fetch(`${baseUrl}/api/explanations?limit=1`, { headers: { Cookie: sessionCookie } });
    assert.equal(list.status, 403);
    assert.equal((await list.json()).code, "dashboard_role_required");
    assert.ok(!harness.calls.some((call) => call.url.includes("/ai-explanations")));
  }, authenticatedConfig());
});

test("callback rejects mismatched state and expired signed tokens", async () => {
  const stateHarness = createOidcHarness(["releaseguard-viewer"]);
  await withServer(stateHarness.fakeFetch, async (baseUrl) => {
    const start = await fetch(`${baseUrl}/login`, { redirect: "manual" });
    stateHarness.captureLogin(start.headers.get("location"));
    const callback = await fetch(`${baseUrl}/auth/callback?state=wrong-state&code=test-code`, {
      headers: { Cookie: cookieValue(start, "releaseguard_oidc_tx") },
    });
    assert.equal(callback.status, 400);
    assert.equal((await callback.json()).code, "dashboard_login_failed");
  }, authenticatedConfig());

  const expiredHarness = createOidcHarness(["releaseguard-viewer"], { expired: true });
  await withServer(expiredHarness.fakeFetch, async (baseUrl) => {
    const { callback } = await login(baseUrl, expiredHarness);
    assert.equal(callback.status, 400);
    assert.equal((await callback.json()).code, "dashboard_login_failed");
  }, authenticatedConfig());

  const invalidSignatureHarness = createOidcHarness(["releaseguard-viewer"], { invalidSignature: true });
  await withServer(invalidSignatureHarness.fakeFetch, async (baseUrl) => {
    const { callback } = await login(baseUrl, invalidSignatureHarness);
    assert.equal(callback.status, 400);
    assert.equal((await callback.json()).code, "dashboard_login_failed");
  }, authenticatedConfig());
});

test("operator logout requires CSRF, clears local session, and returns the Keycloak end-session URL", async () => {
  const harness = createOidcHarness(["releaseguard-operator"]);
  await withServer(harness.fakeFetch, async (baseUrl) => {
    const { sessionCookie } = await login(baseUrl, harness);
    const session = await (await fetch(`${baseUrl}/api/session`, { headers: { Cookie: sessionCookie } })).json();
    const logout = await fetch(`${baseUrl}/api/logout`, {
      method: "POST",
      headers: { Cookie: sessionCookie, "X-CSRF-Token": session.csrfToken },
    });
    const body = await logout.json();
    assert.equal(logout.status, 200);
    assert.match(logout.headers.get("set-cookie"), /releaseguard_session=;.*Max-Age=0/);
    const redirect = new URL(body.redirectTo);
    assert.equal(redirect.pathname, "/realms/releaseguard/protocol/openid-connect/logout");
    assert.equal(redirect.searchParams.get("client_id"), "releaseguard-dashboard");
    assert.equal(redirect.searchParams.get("post_logout_redirect_uri"), "http://dashboard.test:3000/signed-out");
    assert.equal(redirect.searchParams.get("id_token_hint"), null);

    const after = await fetch(`${baseUrl}/api/session`, { headers: { Cookie: sessionCookie } });
    assert.equal(after.status, 401);
  }, authenticatedConfig());
});

test("list proxy allows only bounded query fields and keeps credential server-side", async () => {
  const calls = [];
  const upstreamBody = { items: [], nextCursor: "opaque_cursor" };
  const fakeFetch = async (url, options) => {
    calls.push({ url: String(url), options });
    return new Response(JSON.stringify(upstreamBody), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  };

  await withServer(fakeFetch, async (baseUrl) => {
    const response = await fetch(`${baseUrl}/api/explanations?limit=25&cursor=abc_123`);
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), upstreamBody);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, "http://releaseguard.test:8080/v1/release-risk-events/ai-explanations?limit=25&cursor=abc_123");
    assert.equal(calls[0].options.headers.Authorization, `Bearer ${queryCredential}`);

    const invalid = await fetch(`${baseUrl}/api/explanations?owner=secret`);
    assert.equal(invalid.status, 400);
    assert.equal(calls.length, 1);
    assert.ok(!JSON.stringify(await invalid.json()).includes(queryCredential));
  });
});

test("replay proxy uses separate replay credential and validated idempotency key", async () => {
  const calls = [];
  const eventId = "8b0ea7e1-c4ab-4ea5-90a5-42832ae812fa";
  const replayId = "b535fcbc-1608-49e4-846f-7af40da10f37";
  const fakeFetch = async (url, options) => {
    calls.push({ url: String(url), options });
    return new Response(JSON.stringify({ replayId, eventId, generation: 2, status: "pending" }), {
      status: 202,
      headers: { "Content-Type": "application/json" },
    });
  };

  await withServer(fakeFetch, async (baseUrl) => {
    const response = await fetch(`${baseUrl}/api/events/${eventId}/replays`, {
      method: "POST",
      headers: { "Idempotency-Key": replayId },
    });
    assert.equal(response.status, 202);
    assert.equal(calls[0].options.headers.Authorization, `Bearer ${replayCredential}`);
    assert.equal(calls[0].options.headers["Idempotency-Key"], replayId);

    const invalid = await fetch(`${baseUrl}/api/events/${eventId}/replays`, { method: "POST" });
    assert.equal(invalid.status, 400);
    assert.equal(calls.length, 1);

    const bodyRequest = await fetch(`${baseUrl}/api/events/${eventId}/replays`, {
      method: "POST",
      headers: { "Idempotency-Key": replayId, "Content-Type": "application/json" },
      body: "{}",
    });
    assert.equal(bodyRequest.status, 400);
    assert.equal(calls.length, 1);
  });
});

test("proxy rejects an upstream body beyond the bounded response budget", async () => {
  const fakeFetch = async () => new Response("x".repeat(256 * 1024 + 1), { status: 200 });

  await withServer(fakeFetch, async (baseUrl) => {
    const response = await fetch(`${baseUrl}/api/explanations?limit=1`);
    const body = await response.json();
    assert.equal(response.status, 502);
    assert.equal(body.code, "dashboard_upstream_contract_invalid");
  });
});

test("status aggregates only non-secret health and configured model readiness", async () => {
  const fakeFetch = async (url) => {
    if (String(url).endsWith("/api/tags")) {
      return new Response(JSON.stringify({ models: [{ name: "qwen3:1.7b" }] }), { status: 200 });
    }
    return new Response(JSON.stringify({ status: "ok" }), { status: 200 });
  };

  await withServer(fakeFetch, async (baseUrl) => {
    const response = await fetch(`${baseUrl}/api/status`);
    const text = await response.text();
    const body = JSON.parse(text);
    assert.equal(body.services.api, "online");
    assert.equal(body.services.ai, "online");
    assert.equal(body.services.ollama, "online");
    assert.equal(body.ai.modelReady, true);
    assert.ok(!text.includes(queryCredential));
    assert.ok(!text.includes(replayCredential));
    assert.ok(!text.includes("releaseguard.test"));
  });
});

test("static dashboard sends restrictive browser security headers", async () => {
  await withServer(async () => new Response(null, { status: 500 }), async (baseUrl) => {
    const response = await fetch(`${baseUrl}/`);
    assert.equal(response.status, 200);
    assert.match(response.headers.get("content-security-policy"), /default-src 'self'/);
    assert.equal(response.headers.get("x-frame-options"), "DENY");
    assert.match(await response.text(), /ReleaseGuard/);
  });
});
