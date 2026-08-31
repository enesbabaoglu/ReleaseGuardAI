import assert from "node:assert/strict";
import { chmodSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";

import {
  assertProductionCompose,
  assertSecretFiles,
  loadProductionCompose,
  validateProductionEnvironment,
} from "../../../scripts/validate-production-compose.mjs";

const repositoryRoot = resolve(import.meta.dirname, "../../..");
const exampleEnvironment = resolve(repositoryRoot, "deploy/production/production.env.example");

test("checked-in production Compose keeps one TLS edge and hardened Keycloak boundaries", () => {
  const { model, realm, caddyfile } = loadProductionCompose(exampleEnvironment, { allowExample: true });

  assert.equal(Object.keys(model.services).length, 11);
  assert.equal(model.services.caddy.ports.length, 3);
  assert.equal(realm.users.length, 0);
  assert.match(caddyfile, /Strict-Transport-Security/);
});

test("production validator rejects dev identity, direct ports, and seeded users", () => {
  const { model, realm, caddyfile } = loadProductionCompose(exampleEnvironment, { allowExample: true });

  const devIdentity = structuredClone(model);
  devIdentity.services.keycloak.command = ["start-dev"];
  assert.throws(() => assertProductionCompose(devIdentity, realm, caddyfile), /start-dev/);

  const directApi = structuredClone(model);
  directApi.services["webhook-api"].ports = [{ published: "8080", target: 8080, protocol: "tcp" }];
  assert.throws(() => assertProductionCompose(directApi, realm, caddyfile), /yalnız Caddy/);

  const seededRealm = structuredClone(realm);
  seededRealm.users.push({ username: "admin", credentials: [{ value: "secret" }] });
  assert.throws(() => assertProductionCompose(model, seededRealm, caddyfile), /seed edemez/);
});

test("production environment and secret preflight fail closed", () => {
  assert.throws(
    () => validateProductionEnvironment({
      RELEASEGUARD_PUBLIC_HOST: "localhost",
      CADDY_ACME_EMAIL: "ops@example.com",
      RELEASEGUARD_KEYCLOAK_ADMIN_USERNAME: "admin-user",
    }),
    /public bir DNS/,
  );
  assert.throws(
    () => validateProductionEnvironment({
      RELEASEGUARD_PUBLIC_HOST: "releaseguard.example.com",
      CADDY_ACME_EMAIL: "ops@example.com",
      RELEASEGUARD_KEYCLOAK_ADMIN_USERNAME: "admin-user",
    }),
    /örnek domain/,
  );
  for (const invalidHost of ["127.0.0.1", "https://releaseguard.test/path"]) {
    assert.throws(
      () => validateProductionEnvironment({
        RELEASEGUARD_PUBLIC_HOST: invalidHost,
        CADDY_ACME_EMAIL: "ops@releaseguard.test",
        RELEASEGUARD_KEYCLOAK_ADMIN_USERNAME: "admin-user",
      }),
      /public bir DNS/,
    );
  }

  const { model } = loadProductionCompose(exampleEnvironment, { allowExample: true });
  const temporaryDirectory = mkdtempSync(join(tmpdir(), "releaseguard-production-secrets-"));
  try {
    let index = 0;
    for (const secret of Object.values(model.secrets)) {
      const path = join(temporaryDirectory, `secret-${index}`);
      writeFileSync(path, `${String(index + 1).padStart(2, "0")}${"a".repeat(62)}\n`, { mode: 0o600 });
      chmodSync(path, 0o600);
      secret.file = path;
      index += 1;
    }
    assert.doesNotThrow(() => assertSecretFiles(model));

    const firstSecret = Object.values(model.secrets)[0].file;
    chmodSync(firstSecret, 0o644);
    assert.throws(() => assertSecretFiles(model), /chmod 600/);
    chmodSync(firstSecret, 0o600);

    writeFileSync(firstSecret, `${"b".repeat(513)}\n`, { mode: 0o600 });
    assert.throws(() => assertSecretFiles(model), /32-512/);
    writeFileSync(firstSecret, `${"01"}${"a".repeat(62)}\n`, { mode: 0o600 });

    const secondSecret = Object.values(model.secrets)[1];
    const secondSecretFile = secondSecret.file;
    const symlink = join(temporaryDirectory, "secret-link");
    symlinkSync(firstSecret, symlink);
    secondSecret.file = symlink;
    assert.throws(() => assertSecretFiles(model), /regular file/);
    secondSecret.file = secondSecretFile;

    const duplicate = Object.values(model.secrets)[1].file;
    writeFileSync(duplicate, `${"01"}${"a".repeat(62)}\n`, { mode: 0o600 });
    assert.throws(() => assertSecretFiles(model), /aynı olamaz/);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
});
