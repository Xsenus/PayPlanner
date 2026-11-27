import assert from 'node:assert/strict';
import { appendFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { fileHash } from '../prepare-deployment-plan.mjs';

const sha256 = /^[a-f0-9]{64}$/;
const revision = /^[a-f0-9]{40}$/;
const positiveInteger = /^[1-9][0-9]{0,19}$/;

function prefix(kind, env) {
  assert.ok(kind === 'runtime' || kind === 'backend', 'Unknown hosted cache payload');
  assert.match(env.GITHUB_RUN_ID ?? '', positiveInteger, 'Expected the current Actions run');
  assert.match(env.GITHUB_SHA ?? '', revision, 'Expected the current revision');
  return `payplanner-release-v1-${kind}-${env.GITHUB_RUN_ID}-${env.GITHUB_SHA}-`;
}

export function transferKey(kind, digest, env = process.env) {
  assert.match(digest, sha256);
  assert.match(env.GITHUB_RUN_ATTEMPT ?? '', positiveInteger);
  return prefix(kind, env) + env.GITHUB_RUN_ATTEMPT + '-' + digest;
}

export function verifyTransferKey(kind, digest, key, env = process.env) {
  assert.match(digest ?? '', sha256);
  assert.match(env.GITHUB_RUN_ATTEMPT ?? '', positiveInteger);
  assert.ok(key?.startsWith(prefix(kind, env)), 'Expected a producer from this Actions run and revision');
  const suffix = key.slice(prefix(kind, env).length);
  const [attempt, cachedDigest, ...extra] = suffix.split('-');
  assert.match(attempt ?? '', positiveInteger);
  assert.ok(BigInt(attempt) <= BigInt(env.GITHUB_RUN_ATTEMPT) && extra.length === 0, 'Invalid producer attempt');
  assert.equal(cachedDigest, digest, 'Cache key and producer digest differ');
}

export function verifyFile(path, digest) {
  assert.match(digest ?? '', sha256, 'Expected a producer SHA-256');
  assert.equal(fileHash(path), digest, 'Release file differs from the qualified producer');
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const [action, path, value] = process.argv.slice(2);
  if (action === 'verify-key') {
    verifyTransferKey(path, process.env.EXPECTED_SHA256, value);
  } else if (action === 'verify') {
    verifyFile(path, value);
  } else {
    assert.equal(action, 'seal');
    const digest = fileHash(path);
    const key = value === undefined ? '' : `key=${transferKey(value, digest)}\n`;
    appendFileSync(process.env.GITHUB_OUTPUT, `sha256=${digest}\n${key}`);
  }
}
