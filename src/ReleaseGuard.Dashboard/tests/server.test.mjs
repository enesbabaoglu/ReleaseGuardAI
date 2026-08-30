import assert from "node:assert/strict";
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

async function withServer(fetchImplementation, action) {
  const server = createDashboardServer(testConfig(), fetchImplementation);
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
