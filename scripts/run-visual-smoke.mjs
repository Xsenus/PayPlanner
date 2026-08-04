#!/usr/bin/env node

import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { chromium } from '@playwright/test';

export const DEFAULT_FRONTEND_URL = 'http://localhost:5173';
export const DEFAULT_TIMEOUT_MS = 20_000;
export const DEFAULT_ARTIFACT_DIR = 'artifacts/visual-smoke';

export const VIEWPORTS = {
  desktop: { width: 1440, height: 1000 },
  tablet: { width: 820, height: 1180 },
  mobile: { width: 390, height: 844 },
};

export const PUBLIC_ROUTES = [
  { name: 'login', path: '/' },
];

export const AUTHENTICATED_ROUTES = [
  { name: 'calendar', path: '/?ppTab=calendar' },
  { name: 'tasks', path: '/tasks' },
  { name: 'tasks-approvals', path: '/tasks/approvals' },
  { name: 'tasks-calendar', path: '/tasks/calendar' },
  { name: 'payments', path: '/payments' },
  { name: 'invoices', path: '/invoices' },
];

const ignoredConsolePatterns = [
  /favicon/i,
  /ResizeObserver loop completed/i,
  /ResizeObserver loop limit exceeded/i,
  /Failed to start the connection: Error: The connection was stopped during negotiation/i,
];

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

export function splitCsv(value) {
  return String(value ?? '')
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean);
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

export function resolveViewportNames(value) {
  const requested = splitCsv(value);
  const names = requested.length > 0 ? requested : Object.keys(VIEWPORTS);
  const unknown = names.filter((name) => !VIEWPORTS[name]);

  if (unknown.length > 0) {
    throw new Error(`Unknown viewport(s): ${unknown.join(', ')}. Known: ${Object.keys(VIEWPORTS).join(', ')}`);
  }

  return names;
}

export function resolveRoutes(value, authenticated) {
  const requested = splitCsv(value);
  if (requested.length > 0) {
    return requested.map((route, index) => ({
      name: route
        .replace(/^https?:\/\//, '')
        .replace(/[^a-z0-9а-яё]+/gi, '-')
        .replace(/^-|-$/g, '')
        .toLowerCase() || `route-${index + 1}`,
      path: /^https?:\/\//i.test(route) || route.startsWith('/') ? route : `/${route}`,
    }));
  }

  return authenticated ? AUTHENTICATED_ROUTES : PUBLIC_ROUTES;
}

export function isIgnoredConsoleMessage(message) {
  return ignoredConsolePatterns.some((pattern) => pattern.test(message));
}

export function formatPageFailure(routeName, viewportName, message) {
  return `[${viewportName}] ${routeName}: ${message}`;
}

function buildUrl(frontendUrl, routePath) {
  if (/^https?:\/\//i.test(routePath)) return routePath;
  return `${frontendUrl}${routePath.startsWith('/') ? routePath : `/${routePath}`}`;
}

async function launchChromium() {
  const attempts = [
    process.env.PLAYWRIGHT_CHROMIUM_CHANNEL
      ? { label: process.env.PLAYWRIGHT_CHROMIUM_CHANNEL, options: { channel: process.env.PLAYWRIGHT_CHROMIUM_CHANNEL } }
      : null,
    { label: 'msedge', options: { channel: 'msedge' } },
    { label: 'chrome', options: { channel: 'chrome' } },
    { label: 'bundled chromium', options: {} },
  ].filter(Boolean);

  const failures = [];
  for (const attempt of attempts) {
    try {
      const browser = await chromium.launch({
        ...attempt.options,
        headless: true,
      });
      return { browser, label: attempt.label };
    } catch (error) {
      failures.push(`${attempt.label}: ${error.message}`);
    }
  }

  throw new Error(`Unable to launch Chromium for visual smoke.\n${failures.join('\n')}`);
}

async function loginWithCredentials(page, frontendUrl, email, password, timeoutMs) {
  await page.goto(`${frontendUrl}/`, { waitUntil: 'domcontentloaded', timeout: timeoutMs });
  await page.getByLabel('Email').fill(email, { timeout: timeoutMs });
  await page.getByLabel('Пароль').fill(password, { timeout: timeoutMs });
  await page.getByRole('button', { name: 'Войти' }).click({ timeout: timeoutMs });
  await page.waitForFunction(() => Boolean(window.localStorage.getItem('pp_token')), undefined, { timeout: timeoutMs });
}

async function prepareAuth(context, frontendUrl, token, email, password, timeoutMs) {
  if (token) {
    await context.addInitScript((authToken) => {
      window.localStorage.setItem('pp_token', authToken);
      window.localStorage.setItem('auth_token', authToken);
      window.localStorage.setItem('pp.jwt', authToken);
    }, token);
    return true;
  }

  if (email && password) {
    const page = await context.newPage();
    try {
      await loginWithCredentials(page, frontendUrl, email, password, timeoutMs);
      const resolvedToken = await page.evaluate(() => window.localStorage.getItem('pp_token'));
      assert(resolvedToken, 'Login completed without pp_token.');
      await context.addInitScript((authToken) => {
        window.localStorage.setItem('pp_token', authToken);
        window.localStorage.setItem('auth_token', authToken);
        window.localStorage.setItem('pp.jwt', authToken);
      }, resolvedToken);
      return true;
    } finally {
      await page.close();
    }
  }

  return false;
}

async function checkPage({ page, frontendUrl, route, viewportName, timeoutMs, artifactDir }) {
  const consoleErrors = [];
  const pageErrors = [];

  page.on('console', (message) => {
    if (message.type() !== 'error') return;

    const text = message.text();
    if (!isIgnoredConsoleMessage(text)) {
      const locationUrl = message.location().url;
      consoleErrors.push(locationUrl ? `${text} (${locationUrl})` : text);
    }
  });
  page.on('pageerror', (error) => {
    pageErrors.push(error.message);
  });

  await page.evaluate(() => {
    try {
      window.localStorage.removeItem('pp.last_active_tab');
    } catch {
      // The first smoke page can still be about:blank with an opaque origin.
    }
  }).catch(() => undefined);

  await page.goto(buildUrl(frontendUrl, route.path), { waitUntil: 'domcontentloaded', timeout: timeoutMs });
  await page.waitForLoadState('networkidle', { timeout: timeoutMs }).catch(() => undefined);
  await page.locator('body').waitFor({ state: 'visible', timeout: timeoutMs });
  await page.getByRole('button', { name: /Понятно|Закрыть/i }).first().click({ timeout: 1000 }).catch(() => undefined);

  const metrics = await page.evaluate(() => {
    const viewportWidth = window.innerWidth;
    const documentWidth = Math.max(document.documentElement.scrollWidth, document.body.scrollWidth);
    const bodyText = document.body.innerText.trim();
    const inspectOverflow = documentWidth > viewportWidth + 2;
    const overflowingElements = inspectOverflow ? Array.from(document.querySelectorAll('body *'))
      .map((element) => {
        const node = /** @type {HTMLElement} */ (element);
        const rect = node.getBoundingClientRect();
        const ancestors = [];
        let parent = node.parentElement;
        for (let depth = 0; parent && depth < 5; depth += 1, parent = parent.parentElement) {
          const parentRect = parent.getBoundingClientRect();
          const style = window.getComputedStyle(parent);
          ancestors.push(`${parent.tagName.toLowerCase()}${parent.dataset?.testid ? `[data-testid=${parent.dataset.testid}]` : ''}{w=${Math.round(parentRect.width)},cw=${parent.clientWidth},sw=${parent.scrollWidth},ox=${style.overflowX}}`);
        }
        return {
          right: Math.round(rect.right),
          left: Math.round(rect.left),
          width: Math.round(rect.width),
          tag: node.tagName.toLowerCase(),
          testId: node.dataset?.testid ?? '',
          className: typeof node.className === 'string' ? node.className.slice(0, 140) : '',
          ancestors: ancestors.join('>'),
          position: window.getComputedStyle(node).position,
        };
      })
      .filter((item) => item.right > viewportWidth + 2 || item.left < -2)
      .sort((left, right) => right.right - left.right)
      .slice(0, 24) : [];
    const scrollWidthElements = inspectOverflow ? [document.documentElement, document.body, ...Array.from(document.querySelectorAll('body *'))]
      .map((element) => {
        const node = /** @type {HTMLElement} */ (element);
        const style = window.getComputedStyle(node);
        return {
          tag: node.tagName.toLowerCase(),
          testId: node.dataset?.testid ?? '',
          clientWidth: node.clientWidth,
          scrollWidth: node.scrollWidth,
          overflowX: style.overflowX,
          className: typeof node.className === 'string' ? node.className.slice(0, 100) : '',
        };
      })
      .filter((item) => item.scrollWidth > item.clientWidth + 2 && item.overflowX === 'visible')
      .sort((left, right) => right.scrollWidth - left.scrollWidth)
      .slice(0, 8) : [];
    const topScrollbars = inspectOverflow ? Array.from(document.querySelectorAll('[data-pp-top-scrollbar]')).map((element) => {
      const node = /** @type {HTMLElement} */ (element);
      const rect = node.getBoundingClientRect();
      return `left=${Math.round(rect.left)},right=${Math.round(rect.right)},w=${Math.round(rect.width)},cw=${node.clientWidth},sw=${node.scrollWidth},display=${window.getComputedStyle(node).display}`;
    }) : [];

    return {
      title: document.title,
      bodyLength: bodyText.length,
      documentOverflowX: Math.max(0, documentWidth - viewportWidth),
      viewportWidth,
      documentWidth,
      overflowingElements,
      scrollWidthElements,
      topScrollbars,
      url: window.location.href,
    };
  });

  await mkdir(artifactDir, { recursive: true });
  await page.screenshot({
    path: path.join(artifactDir, `${viewportName}-${route.name}.png`),
    fullPage: true,
  });

  const failures = [];

  if (metrics.bodyLength < 20) {
    failures.push(`page rendered too little text (${metrics.bodyLength} chars)`);
  }

  if (metrics.documentOverflowX > 2) {
    const offenders = metrics.overflowingElements
      .filter((item) => item.right <= metrics.documentWidth + 2)
      .slice(0, 8)
      .map((item) => `${item.tag}${item.testId ? `[data-testid=${item.testId}]` : ''} right=${item.right}px width=${item.width}px pos=${item.position} class="${item.className}" ancestors=${item.ancestors}`)
      .join('; ');
    const scrollOffenders = metrics.scrollWidthElements
      .map((item) => `${item.tag}${item.testId ? `[data-testid=${item.testId}]` : ''}{cw=${item.clientWidth},sw=${item.scrollWidth},ox=${item.overflowX},class="${item.className}"}`)
      .join('; ');
    failures.push(`horizontal document overflow is ${metrics.documentOverflowX}px (document ${metrics.documentWidth}px, viewport ${metrics.viewportWidth}px)${offenders ? `; candidates: ${offenders}` : ''}${scrollOffenders ? `; visible-scroll candidates: ${scrollOffenders}` : ''}${metrics.topScrollbars.length ? `; top-scrollbars: ${metrics.topScrollbars.join('; ')}` : ''}`);
  }

  for (const text of consoleErrors) {
    failures.push(`console.error: ${text}`);
  }

  for (const text of pageErrors) {
    failures.push(`pageerror: ${text}`);
  }

  return {
    route: route.name,
    viewport: viewportName,
    url: metrics.url,
    title: metrics.title,
    failures,
  };
}

export function printHelp() {
  console.log(`Usage: node scripts/run-visual-smoke.mjs [options]

Options:
  --frontend-url <url>     Frontend base URL. Default: PAYPLANNER_VISUAL_FRONTEND_URL or ${DEFAULT_FRONTEND_URL}
  --routes <csv>           Routes to check. Default: public login page, or key app pages when auth is available.
  --viewports <csv>        Viewports to check: desktop,tablet,mobile. Default: all.
  --artifact-dir <path>    Screenshot output directory. Default: ${DEFAULT_ARTIFACT_DIR}
  --timeout-ms <number>    Per-page timeout. Default: ${DEFAULT_TIMEOUT_MS}
  --require-auth <bool>    Fail when token/email credentials are not provided.

Auth:
  PAYPLANNER_VISUAL_TOKEN
  PAYPLANNER_VISUAL_EMAIL + PAYPLANNER_VISUAL_PASSWORD
`);
}

export async function runVisualSmoke(options) {
  const frontendUrl = trimTrailingSlash(options.frontendUrl);
  const timeoutMs = options.timeoutMs;
  const artifactDir = options.artifactDir;
  const requireAuth = options.requireAuth;
  const { browser, label } = await launchChromium();

  try {
    const context = await browser.newContext();
    const authenticated = await prepareAuth(
      context,
      frontendUrl,
      options.token,
      options.email,
      options.password,
      timeoutMs,
    );

    if (requireAuth && !authenticated) {
      throw new Error('Visual smoke requires auth, but no token or email/password credentials were provided.');
    }

    const routes = resolveRoutes(options.routes, authenticated);
    const viewportNames = resolveViewportNames(options.viewports);

    console.log('PayPlanner visual smoke');
    console.log(`Frontend: ${frontendUrl}`);
    console.log(`Browser: ${label}`);
    console.log(`Auth: ${authenticated ? 'enabled' : 'public-only'}`);
    console.log(`Viewports: ${viewportNames.join(', ')}`);
    console.log(`Routes: ${routes.map((route) => route.path).join(', ')}`);
    console.log(`Screenshots: ${artifactDir}`);
    console.log('');

    const failures = [];
    const rows = [];

    for (const viewportName of viewportNames) {
      const page = await context.newPage();
      await page.setViewportSize(VIEWPORTS[viewportName]);

      try {
        for (const route of routes) {
          const result = await checkPage({ page, frontendUrl, route, viewportName, timeoutMs, artifactDir });
          rows.push(result);

          if (result.failures.length > 0) {
            for (const failure of result.failures) {
              failures.push(formatPageFailure(route.name, viewportName, failure));
            }
          }
        }
      } finally {
        await page.close();
      }
    }

    console.log('| viewport | route | url | title | status |');
    console.log('| --- | --- | --- | --- | --- |');
    for (const row of rows) {
      console.log(`| ${row.viewport} | ${row.route} | ${row.url} | ${row.title.replaceAll('|', '\\|')} | ${row.failures.length === 0 ? 'ok' : 'failed'} |`);
    }

    if (failures.length > 0) {
      console.log('');
      console.log('Failures:');
      for (const failure of failures) {
        console.log(`- ${failure}`);
      }
      throw new Error(`Visual smoke failed:\n${failures.join('\n')}`);
    }

    console.log('');
    console.log('Visual smoke passed.');
  } finally {
    await browser.close();
  }
}

async function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.help) {
    printHelp();
    return;
  }

  await runVisualSmoke({
    frontendUrl: args.frontendUrl ?? process.env.PAYPLANNER_VISUAL_FRONTEND_URL ?? process.env.PUBLIC_FRONTEND_URL ?? DEFAULT_FRONTEND_URL,
    routes: args.routes ?? process.env.PAYPLANNER_VISUAL_ROUTES,
    viewports: args.viewports ?? process.env.PAYPLANNER_VISUAL_VIEWPORTS,
    artifactDir: args.artifactDir ?? process.env.PAYPLANNER_VISUAL_ARTIFACT_DIR ?? DEFAULT_ARTIFACT_DIR,
    timeoutMs: readPositiveInt(args.timeoutMs ?? process.env.PAYPLANNER_VISUAL_TIMEOUT_MS, DEFAULT_TIMEOUT_MS),
    requireAuth: readBoolean(args.requireAuth ?? process.env.PAYPLANNER_VISUAL_REQUIRE_AUTH),
    token: args.token ?? process.env.PAYPLANNER_VISUAL_TOKEN ?? '',
    email: args.email ?? process.env.PAYPLANNER_VISUAL_EMAIL ?? '',
    password: args.password ?? process.env.PAYPLANNER_VISUAL_PASSWORD ?? '',
  });
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
