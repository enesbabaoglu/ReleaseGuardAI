import {
  createHash,
  createPublicKey,
  randomBytes,
  timingSafeEqual,
  verify,
} from "node:crypto";

const maximumOidcBodyBytes = 128 * 1024;
const maximumTransactions = 1_000;
const maximumSessions = 1_000;
const discoveryCacheMilliseconds = 5 * 60 * 1_000;
const jwksCacheMilliseconds = 5 * 60 * 1_000;
const transactionLifetimeSeconds = 5 * 60;
const clockSkewSeconds = 60;
const sessionCookieName = "releaseguard_session";
const transactionCookieName = "releaseguard_oidc_tx";

class OidcBoundaryError extends Error {}

class OidcSessionBoundary {
  constructor(config, fetchImplementation = fetch, dependencies = {}) {
    this.config = config;
    this.fetchImplementation = fetchImplementation;
    this.now = dependencies.now ?? Date.now;
    this.randomBytes = dependencies.randomBytes ?? randomBytes;
    this.transactions = new Map();
    this.sessions = new Map();
    this.discoveryCache = null;
    this.jwksCache = null;
  }

  async beginLogin(response) {
    this.cleanup();
    if (this.transactions.size >= maximumTransactions) {
      throw new OidcBoundaryError("Çok fazla eşzamanlı login işlemi var.");
    }

    const discovery = await this.getDiscovery();
    const state = this.randomToken(32);
    const nonce = this.randomToken(32);
    const verifier = this.randomToken(64);
    const codeChallenge = createHash("sha256").update(verifier).digest("base64url");
    this.transactions.set(state, {
      nonce,
      verifier,
      expiresAt: this.now() + transactionLifetimeSeconds * 1_000,
    });

    const authorizationUrl = new URL(discovery.authorization_endpoint);
    authorizationUrl.search = new URLSearchParams({
      client_id: this.config.clientId,
      redirect_uri: this.callbackUrl.href,
      response_type: "code",
      scope: "openid profile email",
      state,
      nonce,
      code_challenge: codeChallenge,
      code_challenge_method: "S256",
    }).toString();

    response.setHeader("Set-Cookie", this.cookie(transactionCookieName, state, transactionLifetimeSeconds));
    response.writeHead(303, { Location: authorizationUrl.href, "Cache-Control": "no-store" });
    response.end();
  }

  async completeLogin(request, response, requestUrl) {
    const stateValues = requestUrl.searchParams.getAll("state");
    const codeValues = requestUrl.searchParams.getAll("code");
    if (stateValues.length !== 1 || codeValues.length !== 1 || requestUrl.searchParams.has("error")) {
      throw new OidcBoundaryError("OIDC callback parametreleri geçersiz.");
    }

    const state = stateValues[0];
    const transactionCookie = readSingleCookie(request, transactionCookieName);
    const transaction = this.transactions.get(state);
    this.transactions.delete(state);
    response.setHeader("Set-Cookie", this.expiredCookie(transactionCookieName));
    if (
      !transaction ||
      transaction.expiresAt <= this.now() ||
      !transactionCookie ||
      !safeEqual(transactionCookie, state)
    ) {
      throw new OidcBoundaryError("OIDC login işlemi bulunamadı veya süresi doldu.");
    }

    const discovery = await this.getDiscovery();
    const tokenResponse = await this.fetchProvider(this.toBackchannelUrl(discovery.token_endpoint), {
      method: "POST",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: new URLSearchParams({
        grant_type: "authorization_code",
        client_id: this.config.clientId,
        redirect_uri: this.callbackUrl.href,
        code: codeValues[0],
        code_verifier: transaction.verifier,
      }),
    });
    if (!tokenResponse.ok) {
      throw new OidcBoundaryError("OIDC token değişimi reddedildi.");
    }
    const tokenPayload = await readBoundedJson(tokenResponse);
    if (typeof tokenPayload.id_token !== "string") {
      throw new OidcBoundaryError("OIDC token yanıtı id_token içermiyor.");
    }

    const claims = await this.verifyIdToken(tokenPayload.id_token, transaction.nonce);
    const roles = extractClientRoles(claims, this.config.clientId);
    this.cleanup();
    if (this.sessions.size >= maximumSessions) {
      throw new OidcBoundaryError("Çok fazla etkin dashboard oturumu var.");
    }

    const maximumExpiry = this.now() + this.config.sessionLifetimeSeconds * 1_000;
    const expiresAt = Math.min(maximumExpiry, claims.exp * 1_000);
    const sessionId = this.randomToken(32);
    const session = Object.freeze({
      id: sessionId,
      subject: claims.sub,
      displayName: firstVisibleString(claims.preferred_username, claims.name, claims.email, claims.sub),
      roles,
      csrfToken: this.randomToken(32),
      expiresAt,
    });
    this.sessions.set(sessionId, session);
    const maxAge = Math.max(1, Math.floor((expiresAt - this.now()) / 1_000));
    response.appendHeader("Set-Cookie", this.cookie(sessionCookieName, sessionId, maxAge));
    response.writeHead(303, { Location: this.config.externalUrl.href, "Cache-Control": "no-store" });
    response.end();
  }

  getSession(request) {
    const sessionId = readSingleCookie(request, sessionCookieName);
    if (!sessionId) {
      return null;
    }
    const session = this.sessions.get(sessionId);
    if (!session || session.expiresAt <= this.now()) {
      this.sessions.delete(sessionId);
      return null;
    }
    return session;
  }

  hasViewerAccess(session) {
    return session.roles.includes(this.config.viewerRole) || session.roles.includes(this.config.operatorRole);
  }

  hasOperatorAccess(session) {
    return session.roles.includes(this.config.operatorRole);
  }

  verifyCsrf(session, value) {
    return typeof value === "string" && safeEqual(value, session.csrfToken);
  }

  sessionView(session) {
    return {
      authEnabled: true,
      user: { displayName: session.displayName },
      roles: session.roles,
      canView: this.hasViewerAccess(session),
      canReplay: this.hasOperatorAccess(session),
      csrfToken: session.csrfToken,
      expiresAt: new Date(session.expiresAt).toISOString(),
    };
  }

  async endSession(request, response) {
    const sessionId = readSingleCookie(request, sessionCookieName);
    if (sessionId) {
      this.sessions.delete(sessionId);
    }
    response.setHeader("Set-Cookie", this.expiredCookie(sessionCookieName));

    let redirectTo = this.signedOutUrl.href;
    try {
      const discovery = await this.getDiscovery();
      if (typeof discovery.end_session_endpoint === "string") {
        const logoutUrl = new URL(discovery.end_session_endpoint);
        logoutUrl.search = new URLSearchParams({
          client_id: this.config.clientId,
          post_logout_redirect_uri: this.signedOutUrl.href,
        }).toString();
        redirectTo = logoutUrl.href;
      }
    } catch {
      // The local session is still closed if the identity provider is unavailable.
    }
    return redirectTo;
  }

  async getDiscovery() {
    if (this.discoveryCache?.expiresAt > this.now()) {
      return this.discoveryCache.value;
    }
    const response = await this.fetchProvider(this.config.discoveryUrl, {
      headers: { Accept: "application/json" },
    });
    if (!response.ok) {
      throw new OidcBoundaryError("OIDC discovery belgesi alınamadı.");
    }
    const discovery = await readBoundedJson(response);
    if (discovery.issuer !== this.config.issuer.href.replace(/\/$/, "")) {
      throw new OidcBoundaryError("OIDC issuer beklenen değerle eşleşmiyor.");
    }
    for (const field of ["authorization_endpoint", "token_endpoint", "jwks_uri"]) {
      validateProviderEndpoint(discovery[field], this.config.issuer, field);
    }
    if (discovery.end_session_endpoint !== undefined) {
      validateProviderEndpoint(discovery.end_session_endpoint, this.config.issuer, "end_session_endpoint");
    }
    this.discoveryCache = { value: Object.freeze(discovery), expiresAt: this.now() + discoveryCacheMilliseconds };
    return this.discoveryCache.value;
  }

  async verifyIdToken(token, expectedNonce) {
    const [encodedHeader, encodedPayload, encodedSignature, extra] = token.split(".");
    if (!encodedHeader || !encodedPayload || !encodedSignature || extra !== undefined) {
      throw new OidcBoundaryError("OIDC id_token biçimi geçersiz.");
    }
    let header;
    let claims;
    try {
      header = JSON.parse(Buffer.from(encodedHeader, "base64url").toString("utf8"));
      claims = JSON.parse(Buffer.from(encodedPayload, "base64url").toString("utf8"));
    } catch {
      throw new OidcBoundaryError("OIDC id_token JSON biçimi geçersiz.");
    }
    if (header.alg !== "RS256" || typeof header.kid !== "string" || header.kid.length > 256) {
      throw new OidcBoundaryError("OIDC id_token algoritması desteklenmiyor.");
    }
    const key = await this.getSigningKey(header.kid);
    const signatureValid = verify(
      "RSA-SHA256",
      Buffer.from(`${encodedHeader}.${encodedPayload}`),
      key,
      Buffer.from(encodedSignature, "base64url"),
    );
    if (!signatureValid) {
      throw new OidcBoundaryError("OIDC id_token imzası geçersiz.");
    }

    const nowSeconds = Math.floor(this.now() / 1_000);
    const audiences = typeof claims.aud === "string" ? [claims.aud] : claims.aud;
    if (
      claims.iss !== this.config.issuer.href.replace(/\/$/, "") ||
      !Array.isArray(audiences) ||
      !audiences.includes(this.config.clientId) ||
      (audiences.length > 1 && claims.azp !== this.config.clientId) ||
      !Number.isSafeInteger(claims.exp) ||
      claims.exp <= nowSeconds ||
      (Number.isSafeInteger(claims.nbf) && claims.nbf > nowSeconds + clockSkewSeconds) ||
      (Number.isSafeInteger(claims.iat) && claims.iat > nowSeconds + clockSkewSeconds) ||
      typeof claims.sub !== "string" ||
      claims.sub.length < 1 ||
      claims.sub.length > 512 ||
      typeof claims.nonce !== "string" ||
      !safeEqual(claims.nonce, expectedNonce)
    ) {
      throw new OidcBoundaryError("OIDC id_token claim doğrulaması başarısız.");
    }
    return claims;
  }

  async getSigningKey(kid) {
    if (this.jwksCache?.expiresAt > this.now() && this.jwksCache.keys.has(kid)) {
      return this.jwksCache.keys.get(kid);
    }
    const discovery = await this.getDiscovery();
    const response = await this.fetchProvider(this.toBackchannelUrl(discovery.jwks_uri), {
      headers: { Accept: "application/json" },
    });
    if (!response.ok) {
      throw new OidcBoundaryError("OIDC imza anahtarları alınamadı.");
    }
    const jwks = await readBoundedJson(response);
    if (!Array.isArray(jwks.keys) || jwks.keys.length > 10) {
      throw new OidcBoundaryError("OIDC JWKS sözleşmesi geçersiz.");
    }
    const keys = new Map();
    for (const jwk of jwks.keys) {
      if (
        jwk?.kty === "RSA" &&
        typeof jwk.kid === "string" &&
        (!jwk.use || jwk.use === "sig") &&
        (!jwk.alg || jwk.alg === "RS256") &&
        (!jwk.key_ops || (Array.isArray(jwk.key_ops) && jwk.key_ops.includes("verify")))
      ) {
        try {
          keys.set(jwk.kid, createPublicKey({ key: jwk, format: "jwk" }));
        } catch {
          // Ignore malformed or unsupported keys and keep looking for a valid key.
        }
      }
    }
    this.jwksCache = { keys, expiresAt: this.now() + jwksCacheMilliseconds };
    const key = keys.get(kid);
    if (!key) {
      throw new OidcBoundaryError("OIDC id_token imza anahtarı bulunamadı.");
    }
    return key;
  }

  toBackchannelUrl(publicEndpoint) {
    const endpoint = new URL(publicEndpoint);
    const backchannel = new URL(this.config.backchannelBaseUrl);
    backchannel.pathname = endpoint.pathname;
    backchannel.search = endpoint.search;
    return backchannel;
  }

  async fetchProvider(url, options) {
    try {
      return await this.fetchImplementation(url, {
        ...options,
        redirect: "error",
        signal: AbortSignal.timeout(this.config.requestTimeoutMilliseconds),
      });
    } catch {
      throw new OidcBoundaryError("OIDC sağlayıcı çağrısı tamamlanamadı.");
    }
  }

  cleanup() {
    const now = this.now();
    for (const [key, value] of this.transactions) {
      if (value.expiresAt <= now) this.transactions.delete(key);
    }
    for (const [key, value] of this.sessions) {
      if (value.expiresAt <= now) this.sessions.delete(key);
    }
  }

  randomToken(bytes) {
    return this.randomBytes(bytes).toString("base64url");
  }

  cookie(name, value, maxAge) {
    return `${name}=${value}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${maxAge}${this.config.secureCookie ? "; Secure" : ""}`;
  }

  expiredCookie(name) {
    return `${name}=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0${this.config.secureCookie ? "; Secure" : ""}`;
  }

  get callbackUrl() {
    return new URL("auth/callback", withTrailingSlash(this.config.externalUrl));
  }

  get signedOutUrl() {
    return new URL("signed-out", withTrailingSlash(this.config.externalUrl));
  }
}

function validateProviderEndpoint(value, issuer, name) {
  let endpoint;
  try {
    endpoint = new URL(value);
  } catch {
    throw new OidcBoundaryError(`${name} mutlak bir URL değil.`);
  }
  const issuerPath = issuer.pathname.replace(/\/$/, "");
  if (
    endpoint.origin !== issuer.origin ||
    (endpoint.pathname !== issuerPath && !endpoint.pathname.startsWith(`${issuerPath}/`)) ||
    endpoint.username ||
    endpoint.password ||
    endpoint.hash
  ) {
    throw new OidcBoundaryError(`${name} beklenen issuer sınırının dışında.`);
  }
}

function extractClientRoles(claims, clientId) {
  const values = claims?.resource_access?.[clientId]?.roles;
  if (!Array.isArray(values)) {
    return Object.freeze([]);
  }
  return Object.freeze([...new Set(values.filter((value) => typeof value === "string" && value.length <= 128))].sort());
}

function firstVisibleString(...values) {
  for (const value of values) {
    if (typeof value !== "string") continue;
    const normalized = value.trim();
    if (normalized.length >= 1 && normalized.length <= 256 && !/[\u0000-\u001f\u007f]/.test(normalized)) {
      return normalized;
    }
  }
  return "ReleaseGuard kullanıcısı";
}

function readSingleCookie(request, name) {
  const header = request.headers.cookie;
  if (typeof header !== "string") {
    return null;
  }
  const values = header
    .split(";")
    .map((part) => part.trim())
    .filter((part) => part.startsWith(`${name}=`))
    .map((part) => part.slice(name.length + 1));
  return values.length === 1 && /^[A-Za-z0-9_-]{1,512}$/.test(values[0]) ? values[0] : null;
}

function safeEqual(left, right) {
  const leftBuffer = Buffer.from(left);
  const rightBuffer = Buffer.from(right);
  return leftBuffer.length === rightBuffer.length && timingSafeEqual(leftBuffer, rightBuffer);
}

async function readBoundedJson(response) {
  const declaredLength = response.headers.get("content-length");
  if (declaredLength && Number(declaredLength) > maximumOidcBodyBytes) {
    throw new OidcBoundaryError("OIDC yanıtı güvenli sınırı aştı.");
  }
  const reader = response.body?.getReader();
  if (!reader) {
    throw new OidcBoundaryError("OIDC yanıtı boş.");
  }
  const chunks = [];
  let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maximumOidcBodyBytes) {
      await reader.cancel();
      throw new OidcBoundaryError("OIDC yanıtı güvenli sınırı aştı.");
    }
    chunks.push(value);
  }
  const body = Buffer.concat(chunks.map((chunk) => Buffer.from(chunk)), total).toString("utf8");
  try {
    return JSON.parse(body);
  } catch {
    throw new OidcBoundaryError("OIDC yanıtı geçerli JSON değil.");
  }
}

function withTrailingSlash(url) {
  const copy = new URL(url);
  copy.pathname = `${copy.pathname.replace(/\/+$/, "")}/`;
  return copy;
}

export { OidcBoundaryError, OidcSessionBoundary };
