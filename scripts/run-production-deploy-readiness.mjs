#!/usr/bin/env node

import { pathToFileURL } from 'node:url';

import {
  DEFAULT_API_URL,
  DEFAULT_FRONTEND_URL,
  buildConfig as buildNotificationConfig,
  buildUrl,
  parseArgs,
  readBoolean,
  runReadinessChecks,
} from './run-notification-production-readiness.mjs';

export const DEFAULT_REVISION_PATH = '/deploy-revision.txt';

export function normalizeSha(value) {
  const normalized = String(value ?? '').trim().toLowerCase();
  if (!normalized) return '';

  if (!/^[0-9a-f]{7,40}$/.test(normalized)) {
    throw new Error(`Expected a git SHA with 7 to 40 hex characters, got: ${value}`);
  }

  return normalized;
}

export function compareSha(expectedSha, actualSha) {
  const expected = normalizeSha(expectedSha);
  const actual = normalizeSha(actualSha);

  if (!expected || !actual) {
    return false;
  }

  return expected.length <= actual.length
    ? actual.startsWith(expected)
    : expected.startsWith(actual);
}

export function buildDeployConfig(rawArgs = {}) {
  const notificationConfig = buildNotificationConfig(rawArgs);
  const expectedSha = normalizeSha(rawArgs.expectedSha ?? process.env.GITHUB_SHA ?? '');
  const requireRevision = rawArgs.requireRevision === undefined
    ? Boolean(expectedSha)
    : readBoolean(rawArgs.requireRevision);
  const revisionPath = String(rawArgs.revisionPath ?? DEFAULT_REVISION_PATH).trim() || DEFAULT_REVISION_PATH;

  return {
    ...notificationConfig,
    expectedSha,
    requireRevision,
    revisionPath: revisionPath.startsWith('/') ? revisionPath : `/${revisionPath}`,
  };
}

async function fetchTextWithTimeout(url, timeoutMs) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);

  try {
    const response = await fetch(url, {
      signal: controller.signal,
      headers: {
        'User-Agent': 'PayPlanner production deploy readiness smoke',
      },
    });
    const body = await response.text();
    return { response, body };
  } finally {
    clearTimeout(timeout);
  }
}

export async function checkFrontend(config) {
  const { response, body } = await fetchTextWithTimeout(config.frontendUrl, config.timeoutMs);

  if (response.status !== 200) {
    throw new Error(`Frontend returned ${response.status}: ${body.slice(0, 200)}`);
  }

  return `Frontend ok (${response.status})`;
}

export async function checkDeployRevision(config) {
  if (!config.expectedSha && !config.requireRevision) {
    return 'Deploy revision skipped: expected SHA was not provided';
  }

  const url = buildUrl(config.frontendUrl, config.revisionPath);
  const { response, body } = await fetchTextWithTimeout(url, config.timeoutMs);

  if (response.status !== 200) {
    const message = `Deploy revision marker returned ${response.status}: ${body.slice(0, 200)}`;
    if (config.requireRevision) {
      throw new Error(message);
    }
    return `Deploy revision skipped: ${message}`;
  }

  const actualSha = body.trim();
  let normalizedActualSha = '';
  try {
    normalizedActualSha = normalizeSha(actualSha);
  } catch (error) {
    if (config.requireRevision) {
      throw error;
    }

    return `Deploy revision skipped: marker did not contain a git SHA (${actualSha.slice(0, 80) || '<empty>'})`;
  }

  if (!config.expectedSha) {
    return `Deploy revision present (${normalizedActualSha})`;
  }

  if (!compareSha(config.expectedSha, normalizedActualSha)) {
    throw new Error(`Deploy revision mismatch: expected ${config.expectedSha}, got ${actualSha || '<empty>'}`);
  }

  return `Deploy revision ok (${normalizedActualSha})`;
}

export async function runDeployReadinessChecks(config) {
  const results = [];
  results.push(await checkFrontend(config));
  results.push(await checkDeployRevision(config));
  results.push(...await runReadinessChecks(config));
  return results;
}

function printUsage() {
  console.log(`Usage: node scripts/run-production-deploy-readiness.mjs [options]

Options:
  --api-url <url>             Public API URL. Default: ${DEFAULT_API_URL}
  --frontend-url <url>        Public CRM URL. Default: ${DEFAULT_FRONTEND_URL}
  --expected-sha <sha>        Expected deployed git SHA. Defaults to GITHUB_SHA.
  --revision-path <path>      Public frontend revision marker. Default: ${DEFAULT_REVISION_PATH}
  --require-revision <bool>   Fail when the revision marker is missing. Default: true when expected SHA is set.
  --token <jwt>               JWT for WebSocket checks. Can also use PAYPLANNER_PRODUCTION_SMOKE_TOKEN.
  --require-websocket <bool>  Fail when token is missing. Default: false
  --allow-websocket-auth-challenge <bool>
                              Without a token, accept 401/403 from protected hubs instead of skipping.
  --timeout-ms <number>       Per-check timeout.
  --allow-http <bool>         Allow http:// URLs for local dry runs. Default: false
  --help                      Show this help.
`);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    printUsage();
    return;
  }

  const config = buildDeployConfig(args);
  const results = await runDeployReadinessChecks(config);

  console.log('PayPlanner production deploy readiness');
  for (const result of results) {
    console.log(`ok - ${result}`);
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
  });
}
