#!/usr/bin/env node

import { randomBytes } from 'node:crypto';
import net from 'node:net';
import tls from 'node:tls';
import { pathToFileURL } from 'node:url';

export const DEFAULT_API_URL = 'https://api.elitpravo.com';
export const DEFAULT_FRONTEND_URL = 'https://crm.elitpravo.com';
export const DEFAULT_TIMEOUT_MS = 15_000;
export const HUB_PATHS = ['/hubs/notifications', '/hubs/notes'];
export const PUSH_WORKER_PATH = '/payplanner-push-worker.js';

export function parseArgs(argv) {
  const args = {};

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];

    if (arg === '--help' || arg === '-h') {
      args.help = true;
      continue;
    }

    if (!arg.startsWith('--')) {
      throw new Error(`Unexpected argument: ${arg}`);
    }

    const [rawKey, inlineValue] = arg.slice(2).split('=', 2);
    const key = rawKey.replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
    const value = inlineValue ?? argv[index + 1];

    if (inlineValue === undefined) {
      if (value === undefined || value.startsWith('--')) {
        throw new Error(`Missing value for --${rawKey}`);
      }
      index += 1;
    }

    args[key] = value;
  }

  return args;
}

export function readBoolean(value) {
  if (value === undefined || value === null || value === '') return false;
  return ['1', 'true', 'yes', 'on'].includes(String(value).trim().toLowerCase());
}

export function readPositiveInt(value, fallback) {
  if (value === undefined || value === null || value === '') return fallback;

  const number = Number(value);
  if (!Number.isInteger(number) || number <= 0) {
    throw new Error(`Expected a positive integer, got: ${value}`);
  }

  return number;
}

export function trimTrailingSlash(value) {
  return String(value).replace(/\/+$/, '');
}

export function normalizeBaseUrl(value, name, allowHttp = false) {
  const normalized = trimTrailingSlash(value);
  const url = new URL(normalized);

  if (!['http:', 'https:'].includes(url.protocol)) {
    throw new Error(`${name} must be an HTTP(S) URL.`);
  }

  if (!allowHttp && url.protocol !== 'https:') {
    throw new Error(`${name} must use HTTPS for production readiness checks. Pass --allow-http=true for local dry runs.`);
  }

  return normalized;
}

export function buildConfig(rawArgs = {}) {
  const allowHttp = readBoolean(rawArgs.allowHttp);
  const apiUrl = normalizeBaseUrl(rawArgs.apiUrl ?? DEFAULT_API_URL, 'api-url', allowHttp);
  const frontendUrl = normalizeBaseUrl(rawArgs.frontendUrl ?? DEFAULT_FRONTEND_URL, 'frontend-url', allowHttp);

  return {
    apiUrl,
    frontendUrl,
    timeoutMs: readPositiveInt(rawArgs.timeoutMs, DEFAULT_TIMEOUT_MS),
    token: String(rawArgs.token ?? process.env.PAYPLANNER_PRODUCTION_SMOKE_TOKEN ?? '').trim(),
    requireWebSocket: readBoolean(rawArgs.requireWebSocket),
    allowHttp,
  };
}

export function buildUrl(baseUrl, path) {
  return `${trimTrailingSlash(baseUrl)}${path.startsWith('/') ? path : `/${path}`}`;
}

export function buildHubUrl(apiUrl, hubPath, token) {
  const url = new URL(buildUrl(apiUrl, hubPath));
  if (token) {
    url.searchParams.set('access_token', token);
  }

  return url;
}

export function parseHttpStatusLine(value) {
  const match = /^HTTP\/\d(?:\.\d)?\s+(\d{3})\s*(.*)$/i.exec(String(value).trim());
  if (!match) {
    throw new Error(`Invalid HTTP status line: ${value}`);
  }

  return {
    statusCode: Number(match[1]),
    statusText: match[2].trim(),
  };
}

export function buildWebSocketUpgradeRequest(url, frontendUrl) {
  const key = randomBytes(16).toString('base64');
  const path = `${url.pathname}${url.search}`;

  return [
    `GET ${path} HTTP/1.1`,
    `Host: ${url.host}`,
    'Connection: Upgrade',
    'Upgrade: websocket',
    'Sec-WebSocket-Version: 13',
    `Sec-WebSocket-Key: ${key}`,
    `Origin: ${frontendUrl}`,
    'User-Agent: PayPlanner production readiness smoke',
    '',
    '',
  ].join('\r\n');
}

async function fetchWithTimeout(url, timeoutMs) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);

  try {
    return await fetch(url, {
      signal: controller.signal,
      headers: {
        'User-Agent': 'PayPlanner production readiness smoke',
      },
    });
  } finally {
    clearTimeout(timeout);
  }
}

async function checkApiHealth(config) {
  const url = buildUrl(config.apiUrl, '/api/health');
  const response = await fetchWithTimeout(url, config.timeoutMs);
  const body = await response.text();

  if (response.status !== 200) {
    throw new Error(`API health returned ${response.status}: ${body.slice(0, 200)}`);
  }

  if (!/"status"\s*:\s*"ok"/i.test(body)) {
    throw new Error(`API health response does not contain status ok: ${body.slice(0, 200)}`);
  }

  return `API health ok (${response.status})`;
}

async function checkPushWorker(config) {
  const url = buildUrl(config.frontendUrl, PUSH_WORKER_PATH);
  const response = await fetchWithTimeout(url, config.timeoutMs);
  const body = await response.text();

  if (response.status !== 200) {
    throw new Error(`Service worker returned ${response.status}: ${body.slice(0, 200)}`);
  }

  if (!/addEventListener\(['"]push['"]/i.test(body)) {
    throw new Error('Service worker does not contain a push event listener.');
  }

  if (!/addEventListener\(['"]notificationclick['"]/i.test(body)) {
    throw new Error('Service worker does not contain notification click routing.');
  }

  return `Service worker ok (${response.status})`;
}

export async function checkWebSocketUpgrade(config, hubPath) {
  if (!config.token) {
    if (config.requireWebSocket) {
      throw new Error(`WebSocket check for ${hubPath} requires --token or PAYPLANNER_PRODUCTION_SMOKE_TOKEN.`);
    }

    return `WebSocket ${hubPath} skipped: token was not provided`;
  }

  const url = buildHubUrl(config.apiUrl, hubPath, config.token);
  const socketFactory = url.protocol === 'https:' || url.protocol === 'wss:' ? tls.connect : net.connect;
  const port = Number(url.port || (url.protocol === 'https:' || url.protocol === 'wss:' ? 443 : 80));

  const statusLine = await new Promise((resolve, reject) => {
    const socket = socketFactory({
      host: url.hostname,
      port,
      servername: url.hostname,
    });
    let settled = false;
    let buffer = '';

    const timeout = setTimeout(() => {
      if (!settled) {
        settled = true;
        socket.destroy();
        reject(new Error(`WebSocket check for ${hubPath} timed out after ${config.timeoutMs} ms.`));
      }
    }, config.timeoutMs);

    socket.once('connect', () => {
      socket.write(buildWebSocketUpgradeRequest(url, config.frontendUrl));
    });

    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8');
      const lineEnd = buffer.indexOf('\r\n');
      if (lineEnd >= 0 && !settled) {
        settled = true;
        clearTimeout(timeout);
        socket.destroy();
        resolve(buffer.slice(0, lineEnd));
      }
    });

    socket.once('error', (error) => {
      if (!settled) {
        settled = true;
        clearTimeout(timeout);
        reject(error);
      }
    });

    socket.once('end', () => {
      if (!settled) {
        settled = true;
        clearTimeout(timeout);
        reject(new Error(`WebSocket check for ${hubPath} ended without an HTTP status line.`));
      }
    });
  });

  const status = parseHttpStatusLine(statusLine);
  if (status.statusCode !== 101) {
    throw new Error(`WebSocket ${hubPath} expected 101 Switching Protocols, got ${status.statusCode} ${status.statusText}.`);
  }

  return `WebSocket ${hubPath} ok (${status.statusCode} ${status.statusText})`;
}

export async function runReadinessChecks(config) {
  const results = [];
  results.push(await checkApiHealth(config));
  results.push(await checkPushWorker(config));

  for (const hubPath of HUB_PATHS) {
    results.push(await checkWebSocketUpgrade(config, hubPath));
  }

  return results;
}

function printUsage() {
  console.log(`Usage: node scripts/run-notification-production-readiness.mjs [options]

Options:
  --api-url <url>             Public API URL. Default: ${DEFAULT_API_URL}
  --frontend-url <url>        Public CRM URL. Default: ${DEFAULT_FRONTEND_URL}
  --token <jwt>               JWT for WebSocket checks. Can also use PAYPLANNER_PRODUCTION_SMOKE_TOKEN.
  --require-websocket <bool>  Fail when token is missing. Default: false
  --timeout-ms <number>       Per-check timeout. Default: ${DEFAULT_TIMEOUT_MS}
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

  const config = buildConfig(args);
  const results = await runReadinessChecks(config);

  console.log('PayPlanner notification production readiness');
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
