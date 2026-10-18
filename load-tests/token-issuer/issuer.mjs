// Local OpenID Connect token issuer for load testing.
//
// Why it exists: every customer endpoint validates an Asgardeo JWT, and the
// Inventory hold endpoint limits each user to 15 attempts per 10 seconds and
// a few tickets per show. A load test of 1,000 virtual users therefore needs
// 1,000 distinct identities, which Asgardeo test accounts can't provide.
//
// The services already discover signing keys from whatever `Jwt:Authority`
// points at (/.well-known/openid-configuration -> jwks_uri). Pointing that
// setting at this issuer gives every virtual user its own valid token without
// changing any service code. Use it only for local and CI test stacks.
//
// Zero dependencies: Node 18+ built-ins only.
//
// Endpoints:
//   GET  /.well-known/openid-configuration   discovery document
//   GET  /jwks                               public signing key
//   GET  /token?sub=<id>[&ttl=<seconds>]     one token (also POST)
//   GET  /tokens?count=<n>[&prefix=<p>][&ttl=<seconds>]
//                                            n tokens for subjects <p>1..<p>n
//   GET  /health

import { createServer } from 'node:http';
import { createPrivateKey, createPublicKey, generateKeyPairSync, sign } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';

const PORT = Number(process.env.ISSUER_PORT ?? 9400);
// Must match the services' Jwt__Authority exactly (no trailing slash): it is
// both the discovery base address and the `iss` value they validate.
const ISSUER = (process.env.ISSUER_URL ?? `http://localhost:${PORT}`).replace(/\/$/, '');
const AUDIENCE = process.env.ISSUER_AUDIENCE ?? 'tickethive-load-test';
const KEY_FILE = process.env.ISSUER_KEY_FILE ?? new URL('../.secrets/issuer-key.pem', import.meta.url).pathname;
const MAX_BULK = 20000;
const DEFAULT_TTL_SECONDS = 2 * 60 * 60;

// The key is persisted so restarting the issuer doesn't invalidate keys the
// services have already cached from /jwks.
function loadOrCreateKey() {
  if (existsSync(KEY_FILE)) {
    return createPrivateKey(readFileSync(KEY_FILE));
  }
  const { privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  mkdirSync(dirname(KEY_FILE), { recursive: true });
  writeFileSync(KEY_FILE, privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
  return privateKey;
}

const privateKey = loadOrCreateKey();
const publicJwk = createPublicKey(privateKey).export({ format: 'jwk' });
const KID = 'load-test-key-1';

const base64url = (value) => Buffer.from(value).toString('base64url');

export function issueToken(sub, ttlSeconds = DEFAULT_TTL_SECONDS) {
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: 'RS256', typ: 'JWT', kid: KID };
  const payload = {
    iss: ISSUER,
    sub,
    aud: AUDIENCE,
    iat: now,
    nbf: now - 5,
    exp: now + ttlSeconds,
    groups: ['customer'],
    email: `${sub}@load.test`,
  };
  const signingInput = `${base64url(JSON.stringify(header))}.${base64url(JSON.stringify(payload))}`;
  const signature = sign('RSA-SHA256', Buffer.from(signingInput), privateKey).toString('base64url');
  return `${signingInput}.${signature}`;
}

function parseTtl(params) {
  const ttl = Number(params.get('ttl') ?? DEFAULT_TTL_SECONDS);
  return Number.isInteger(ttl) && ttl > 0 && ttl <= 24 * 60 * 60 ? ttl : null;
}

function sendJson(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(body));
}

const routes = {
  '/.well-known/openid-configuration': (_params, res) =>
    sendJson(res, 200, {
      issuer: ISSUER,
      jwks_uri: `${ISSUER}/jwks`,
      token_endpoint: `${ISSUER}/token`,
      id_token_signing_alg_values_supported: ['RS256'],
      response_types_supported: ['token'],
      subject_types_supported: ['public'],
    }),

  '/jwks': (_params, res) =>
    sendJson(res, 200, { keys: [{ ...publicJwk, kid: KID, use: 'sig', alg: 'RS256' }] }),

  '/token': (params, res) => {
    const sub = params.get('sub');
    const ttl = parseTtl(params);
    if (!sub || ttl === null) {
      return sendJson(res, 400, { error: 'sub is required; ttl must be 1..86400 seconds' });
    }
    sendJson(res, 200, { access_token: issueToken(sub, ttl), token_type: 'Bearer', expires_in: ttl });
  },

  '/tokens': (params, res) => {
    const count = Number(params.get('count'));
    const prefix = params.get('prefix') ?? 'load-user-';
    const ttl = parseTtl(params);
    if (!Number.isInteger(count) || count < 1 || count > MAX_BULK || ttl === null) {
      return sendJson(res, 400, { error: `count must be 1..${MAX_BULK}; ttl must be 1..86400 seconds` });
    }
    const tokens = Array.from({ length: count }, (_, i) => ({
      sub: `${prefix}${i + 1}`,
      token: issueToken(`${prefix}${i + 1}`, ttl),
    }));
    sendJson(res, 200, { issuer: ISSUER, expires_in: ttl, tokens });
  },

  '/health': (_params, res) => sendJson(res, 200, { status: 'ok', issuer: ISSUER }),
};

const server = createServer((req, res) => {
  const url = new URL(req.url, ISSUER);
  const handler = routes[url.pathname];
  if (!handler || !['GET', 'POST'].includes(req.method)) {
    return sendJson(res, 404, { error: 'not found' });
  }
  handler(url.searchParams, res);
});

// Only start listening when run directly, so the mock server used to test
// the k6 scripts can import issueToken without opening a port.
if (import.meta.url === `file://${process.argv[1]}`) {
  server.listen(PORT, () => {
    console.log(`Load-test token issuer listening on ${ISSUER} (audience ${AUDIENCE})`);
  });
}
