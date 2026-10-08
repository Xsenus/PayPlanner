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

export const APP_THEME_KEYS = [
  'standard',
  'business',
  'ocean',
  'emerald',
  'warm',
  'violet',
  'graphite',
  'midnight',
];

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

export const FULL_APP_ROUTES = [
  { name: 'workspace', path: '/?ppTab=workspace' },
  { name: 'calendar', path: '/?ppTab=calendar' },
  { name: 'notifications', path: '/notifications' },
  { name: 'tasks', path: '/tasks' },
  { name: 'tasks-control', path: '/tasks/control' },
  { name: 'tasks-approvals', path: '/tasks/approvals' },
  { name: 'tasks-calendar', path: '/tasks/calendar' },
  { name: 'notes', path: '/notes' },
  { name: 'reports', path: '/?ppTab=reports' },
  { name: 'calculator', path: '/?ppTab=calculator' },
  { name: 'legal-entities', path: '/?ppTab=legalEntities' },
  { name: 'cases', path: '/cases' },
  { name: 'clients', path: '/clients' },
  { name: 'contacts', path: '/?ppTab=contacts' },
  { name: 'contacts-clients', path: '/?ppTab=contactsClients' },
  { name: 'contacts-legal', path: '/?ppTab=contactsLegal' },
  { name: 'contacts-external', path: '/?ppTab=contactsExternal' },
  { name: 'invoices', path: '/invoices' },
  { name: 'invoices-income', path: '/invoices?ppTab=accountsIncome' },
  { name: 'invoices-expense', path: '/invoices?ppTab=accountsExpense' },
  { name: 'installments', path: '/?ppTab=installments' },
  { name: 'acts', path: '/acts' },
  { name: 'acts-income', path: '/acts?ppTab=actsIncome' },
  { name: 'acts-expense', path: '/acts?ppTab=actsExpense' },
  { name: 'act-create', path: '/acts/new' },
  { name: 'payments', path: '/payments' },
  { name: 'payments-income', path: '/payments?ppTab=paymentsIncome' },
  { name: 'payments-expense', path: '/payments?ppTab=paymentsExpense' },
  { name: 'money-bank', path: '/?ppTab=moneyBank' },
  { name: 'money-cash', path: '/?ppTab=moneyCash' },
  { name: 'money-cards', path: '/?ppTab=moneyCards' },
  { name: 'money-electronic', path: '/?ppTab=moneyEMoney' },
  { name: 'money-total', path: '/?ppTab=moneyTotal' },
  { name: 'payroll', path: '/?ppTab=payroll' },
  { name: 'contracts', path: '/contracts' },
  { name: 'contract-create', path: '/?ppTab=contractCreate' },
  { name: 'company-profile', path: '/?ppTab=companyProfile' },
  { name: 'profile', path: '/?ppTab=profile' },
  { name: 'users', path: '/?ppTab=users' },
  { name: 'roles', path: '/?ppTab=roles' },
  { name: 'audit', path: '/?ppTab=audit' },
  { name: 'user-activity', path: '/?ppTab=userActivity' },
  { name: 'yandex-disk', path: '/?ppTab=yandexDisk' },
  { name: 's3-storage', path: '/?ppTab=s3Storage' },
  { name: 'dictionaries', path: '/?ppTab=dictionaries' },
  { name: 'settings', path: '/?ppTab=settings' },
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

    const argument = arg.slice(2);
    const equalsIndex = argument.indexOf('=');
    const rawKey = equalsIndex >= 0 ? argument.slice(0, equalsIndex) : argument;
    const inlineValue = equalsIndex >= 0 ? argument.slice(equalsIndex + 1) : undefined;
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

export function resolveRoutes(value, authenticated, fullApp = false) {
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

  if (!authenticated) return PUBLIC_ROUTES;
  return fullApp ? FULL_APP_ROUTES : AUTHENTICATED_ROUTES;
}

export function resolveThemeNames(value) {
  const requested = splitCsv(value);
  if (requested.length === 0) return [null];
  if (requested.includes('all')) {
    if (requested.length > 1) throw new Error('Theme "all" cannot be combined with named themes.');
    return APP_THEME_KEYS;
  }

  const unknown = requested.filter((name) => !APP_THEME_KEYS.includes(name));
  if (unknown.length > 0) {
    throw new Error(`Unknown theme(s): ${unknown.join(', ')}. Known: ${APP_THEME_KEYS.join(', ')}`);
  }
  return [...new Set(requested)];
}

export function isIgnoredConsoleMessage(message) {
  return ignoredConsolePatterns.some((pattern) => pattern.test(message));
}

export function formatPageFailure(routeName, viewportName, message) {
  return `[${viewportName}] ${routeName}: ${message}`;
}

function buildUrl(frontendUrl, routePath, toastPreview = false) {
  const resolvedUrl = /^https?:\/\//i.test(routePath)
    ? routePath
    : `${frontendUrl}${routePath.startsWith('/') ? routePath : `/${routePath}`}`;

  if (!toastPreview) return resolvedUrl;
  const url = new URL(resolvedUrl);
  url.searchParams.set('ppVisualAudit', 'true');
  return url.toString();
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

export function safeResourceUrl(value) {
  try {
    const url = new URL(value);
    const pathname = url.pathname.replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, ':id')
      .replace(/\/\d+(?=\/|$)/g, '/:id');
    return `${url.origin}${pathname}`;
  } catch {
    return '<invalid-resource-url>';
  }
}

export async function withRouteDiagnostics(page, inspect) {
  const consoleErrors = [];
  const pageErrors = [];
  const networkErrors = [];
  const expectedNavigationAborts = [];
  const observedRequests = new WeakSet();
  let navigationStartedAt;
  let navigationPending = false;
  const requestLabel = request => `${request.method()} ${safeResourceUrl(request.url())}`;
  const listeners = {
    console(message) {
      if (message.type() !== 'error' || isIgnoredConsoleMessage(message.text())) return;
      const locationUrl = message.location().url;
      consoleErrors.push(locationUrl ? `${message.text()} (${safeResourceUrl(locationUrl)})` : message.text());
    },
    pageerror(error) { pageErrors.push(error.message); },
    request(request) { observedRequests.add(request); },
    response(response) {
      if (response.status() >= 400)
        networkErrors.push(`HTTP ${response.status()}: ${requestLabel(response.request())}`);
    },
    requestfailed(request) {
      const rawReason = request.failure()?.errorText ?? '';
      const reason = /^(net::ERR_[A-Z0-9_]+|NS_ERROR_[A-Z0-9_]+)$/.test(rawReason) ? rawReason : 'unknown-transport-error';
      let startedAt;
      try { startedAt = request.timing().startTime; } catch { /* Missing timing cannot qualify an expected abort. */ }
      // Only an older request cancelled during our explicit next-document
      // navigation is an expected teardown. New/current requests still fail.
      if (navigationPending && !observedRequests.has(request) && reason === 'net::ERR_ABORTED'
        && Number.isFinite(startedAt) && startedAt > 0 && startedAt < navigationStartedAt) {
        expectedNavigationAborts.push(requestLabel(request));
        return;
      }
      networkErrors.push(`requestfailed: ${requestLabel(request)} (${reason})`);
    },
  };
  for (const [event, listener] of Object.entries(listeners)) page.on(event, listener);
  try {
    return await inspect({ consoleErrors, pageErrors, networkErrors, expectedNavigationAborts,
      beginNavigation() { navigationStartedAt = Date.now(); navigationPending = true; },
      endNavigation() { navigationPending = false; },
    });
  } catch (error) {
    console.error('Visual route diagnostics: ' + JSON.stringify({ consoleErrors, pageErrors, networkErrors,
      expectedNavigationAbortCount: expectedNavigationAborts.length }));
    throw error;
  } finally {
    for (const [event, listener] of Object.entries(listeners)) page.off(event, listener);
  }
}

async function checkPage(options) {
  return withRouteDiagnostics(options.page, diagnostics => checkObservedPage({ ...options, diagnostics }));
}

async function checkObservedPage({ page, frontendUrl, route, viewportName, themeName, timeoutMs, artifactDir, toastPreview, selectionPreview, diagnostics }) {
  const { consoleErrors, pageErrors, networkErrors, expectedNavigationAborts } = diagnostics;

  await page.evaluate(() => {
    try {
      window.localStorage.removeItem('pp.last_active_tab');
    } catch {
      // The first smoke page can still be about:blank with an opaque origin.
    }
  }).catch(() => undefined);

  diagnostics.beginNavigation();
  try {
    await page.goto(buildUrl(frontendUrl, route.path, toastPreview), { waitUntil: 'domcontentloaded', timeout: timeoutMs });
  } finally {
    diagnostics.endNavigation();
  }
  await page.waitForLoadState('load', { timeout: Math.min(timeoutMs, 3_000) }).catch(() => undefined);
  // Lazy route chunks and their API reads can start after the document load event.
  // Capture only after those requests settle, within the existing page timeout.
  await page.waitForLoadState('networkidle', { timeout: timeoutMs });
  await page.locator('body').waitFor({ state: 'visible', timeout: timeoutMs });
  await page.waitForTimeout(500);
  await page.waitForFunction(() => {
    const lines = document.body.innerText.split('\n').map((line) => line.trim()).filter(Boolean);
    return !lines.some((line) => /^(Загрузка|Загружаем).*\.\.\.$/i.test(line));
  }, undefined, { timeout: timeoutMs }).catch(() => undefined);
  await page.waitForTimeout(150);
  await page.getByRole('button', { name: /Понятно|Закрыть/i }).first().click({ timeout: 1000 }).catch(() => undefined);

  if (themeName) {
    await page.evaluate((theme) => {
      const motionGuard = document.createElement('style');
      motionGuard.dataset.payplannerVisualMotionGuard = 'true';
      motionGuard.textContent = '*, *::before, *::after { transition-duration: 0s !important; animation-duration: 0s !important; animation-delay: 0s !important; }';
      document.head.appendChild(motionGuard);
      const root = document.documentElement;
      const applyForcedTheme = () => {
        root.dataset.payplannerTheme = theme;
        root.style.colorScheme = theme === 'graphite' || theme === 'midnight' ? 'dark' : 'light';
      };
      applyForcedTheme();
      const existingObserver = window.__payplannerVisualThemeObserver;
      existingObserver?.disconnect?.();
      const observer = new MutationObserver(() => {
        if (root.dataset.payplannerTheme !== theme) applyForcedTheme();
      });
      observer.observe(root, { attributes: true, attributeFilter: ['data-payplanner-theme'] });
      window.__payplannerVisualThemeObserver = observer;
      try {
        window.localStorage.setItem('payplanner.global-theme.v1', theme);
      } catch {
        // The forced theme still remains active for this visual check.
      }
    }, themeName);
    await page.evaluate(() => new Promise((resolve) => window.requestAnimationFrame(() => resolve(undefined))));
  }

  if (toastPreview) {
    await page.evaluate(() => {
      const show = (detail) => window.dispatchEvent(new CustomEvent('payplanner:visual-toast', { detail }));
      show({
        title: 'Изменения сохранены',
        description: 'Настройки применены и уже доступны пользователям.',
        variant: 'success',
        durationMs: 0,
      });
      show({
        title: 'Проверьте обязательные поля',
        description: 'Укажите дату исполнения, чтобы продолжить сохранение.',
        variant: 'error',
        durationMs: 0,
      });
      show({
        title: 'Новая задача на согласовании',
        description: 'Документ ожидает решения. Откройте карточку или отложите уведомление.',
        variant: 'info',
        domain: 'task',
        durationMs: 0,
        actions: [
          { label: 'Открыть карточку', onClick: () => undefined },
          { label: 'Позже', variant: 'secondary', onClick: () => undefined },
        ],
      });
    });
    await page.getByTestId('toast-card').nth(2).waitFor({ state: 'visible', timeout: timeoutMs });
    await page.evaluate(() => new Promise((resolve) => window.requestAnimationFrame(() => resolve(undefined))));
  }

  if (selectionPreview) {
    await page.getByRole('dialog').getByRole('button', { name: /Понятно|Закрыть/i }).last().click({ timeout: 1_500 }).catch(() => undefined);
    await page.getByRole('checkbox', { name: /Выбрать все/i }).click({ timeout: timeoutMs });
    await page.waitForFunction(() => {
      const visibleSelectionInputs = Array.from(document.querySelectorAll('[data-system-checkbox="checkbox"] > input'))
        .filter((input) => input.getClientRects().length > 0 && input.getAttribute('aria-label')?.startsWith('Выбрать'));

      return visibleSelectionInputs.length > 1 && visibleSelectionInputs.every((input) => {
        const wrapper = input.closest('[data-system-checkbox="checkbox"]');
        const mark = wrapper?.querySelector('.pp-system-checkbox-check');
        return input.checked && mark && Number.parseFloat(getComputedStyle(mark).opacity) >= 0.99;
      });
    }, undefined, { timeout: timeoutMs });
    await page.evaluate(() => new Promise((resolve) => window.requestAnimationFrame(() => resolve(undefined))));
  }

  const metrics = await page.evaluate(() => {
    const viewportWidth = window.innerWidth;
    const documentWidth = Math.max(document.documentElement.scrollWidth, document.body.scrollWidth);
    const bodyText = document.body.innerText.trim();
    const loadingLines = bodyText
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => /^(Загрузка|Загружаем).*\.\.\.$/i.test(line))
      .slice(0, 8);
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
          text: (node.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 120),
          ancestors: ancestors.join('>'),
          position: window.getComputedStyle(node).position,
        };
      })
      .filter((item) => (item.right > viewportWidth + 2 && item.right <= documentWidth + 2) || item.left < -2)
      .sort((left, right) => right.right - left.right)
      .slice(0, 24) : [];
    const scrollWidthElements = inspectOverflow ? [document.documentElement, document.body, ...Array.from(document.querySelectorAll('body *'))]
      .map((element) => {
        const node = /** @type {HTMLElement} */ (element);
        const style = window.getComputedStyle(node);
        const rect = node.getBoundingClientRect();
        return {
          tag: node.tagName.toLowerCase(),
          testId: node.dataset?.testid ?? '',
          clientWidth: node.clientWidth,
          scrollWidth: node.scrollWidth,
          overflowX: style.overflowX,
          left: Math.round(rect.left),
          right: Math.round(rect.right),
          position: style.position,
          className: typeof node.className === 'string' ? node.className.slice(0, 100) : '',
        };
      })
      .filter((item) => item.scrollWidth > item.clientWidth + 2)
      .sort((left, right) => (right.scrollWidth - right.clientWidth) - (left.scrollWidth - left.clientWidth))
      .slice(0, 16) : [];
    const topScrollbars = inspectOverflow ? Array.from(document.querySelectorAll('[data-pp-top-scrollbar]')).map((element) => {
      const node = /** @type {HTMLElement} */ (element);
      const rect = node.getBoundingClientRect();
      return `left=${Math.round(rect.left)},right=${Math.round(rect.right)},w=${Math.round(rect.width)},cw=${node.clientWidth},sw=${node.scrollWidth},display=${window.getComputedStyle(node).display}`;
    }) : [];

    const parseColor = (value) => {
      const normalized = String(value).trim();
      if (!normalized.startsWith('rgb') && !normalized.startsWith('color(srgb')) return null;
      const values = normalized.match(/[\d.]+/g)?.map(Number) ?? [];
      if (values.length < 3) return null;
      const srgbScale = normalized.startsWith('color(srgb') ? 255 : 1;
      return {
        r: values[0] * srgbScale,
        g: values[1] * srgbScale,
        b: values[2] * srgbScale,
        a: values[3] ?? 1,
      };
    };
    const composite = (front, back) => ({
      r: (front.r * front.a) + (back.r * (1 - front.a)),
      g: (front.g * front.a) + (back.g * (1 - front.a)),
      b: (front.b * front.a) + (back.b * (1 - front.a)),
      a: 1,
    });
    const luminance = (color) => {
      const channel = (value) => {
        const normalized = value / 255;
        return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
      };
      return (0.2126 * channel(color.r)) + (0.7152 * channel(color.g)) + (0.0722 * channel(color.b));
    };
    const contrast = (first, second) => {
      const high = Math.max(luminance(first), luminance(second));
      const low = Math.min(luminance(first), luminance(second));
      return (high + 0.05) / (low + 0.05);
    };
    const effectiveBackground = (element) => {
      const chain = [];
      for (let current = element; current; current = current.parentElement) chain.push(current);
      let result = { r: 255, g: 255, b: 255, a: 1 };
      for (const current of chain.reverse()) {
        const color = parseColor(window.getComputedStyle(current).backgroundColor);
        if (color && color.a > 0) result = composite(color, result);
      }
      return result;
    };
    const lowContrast = Array.from(document.querySelectorAll('body *'))
      .filter((element) => Array.from(element.childNodes).some((node) => node.nodeType === Node.TEXT_NODE && node.textContent?.trim()))
      .map((element) => {
        const node = /** @type {HTMLElement} */ (element);
        const style = window.getComputedStyle(node);
        const rect = node.getBoundingClientRect();
        const disabledControl = node.closest(':disabled, [aria-disabled="true"]');
        if (disabledControl || style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) < 0.55 || rect.width < 2 || rect.height < 2) return null;
        const foreground = parseColor(style.color);
        if (!foreground) return null;
        const usesClippedBackgroundText = foreground.a === 0
          && style.backgroundImage !== 'none'
          && (style.backgroundClip === 'text' || style.webkitBackgroundClip === 'text');
        if (usesClippedBackgroundText) return null;
        const background = effectiveBackground(node);
        const ratio = contrast(composite(foreground, background), background);
        if (ratio >= 2.5) return null;
        return {
          ratio: ratio.toFixed(2),
          foreground: style.color,
          background: style.backgroundColor,
          tag: node.tagName.toLowerCase(),
          className: typeof node.className === 'string' ? node.className.slice(0, 120) : '',
          text: Array.from(node.childNodes)
            .filter((child) => child.nodeType === Node.TEXT_NODE)
            .map((child) => child.textContent ?? '')
            .join(' ')
            .replace(/\s+/g, ' ')
            .trim()
            .slice(0, 100),
        };
      })
      .filter(Boolean)
      .slice(0, 20);

    const primaryColor = parseColor(window.getComputedStyle(document.documentElement).getPropertyValue('--pp-primary'));
    const primaryActionTokens = new Set([
      'bg-gray-800', 'bg-gray-900',
      'bg-slate-800', 'bg-slate-900', 'bg-slate-950',
      'bg-blue-500', 'bg-blue-600', 'bg-blue-700',
      'bg-sky-500', 'bg-sky-600', 'bg-sky-700',
      'bg-cyan-500', 'bg-cyan-600', 'bg-cyan-700',
      'bg-indigo-500', 'bg-indigo-600', 'bg-indigo-700',
      'bg-violet-500', 'bg-violet-600', 'bg-violet-700',
      'bg-purple-500', 'bg-purple-600', 'bg-purple-700',
    ]);
    const colorDistance = (first, second) => Math.max(
      Math.abs(first.r - second.r),
      Math.abs(first.g - second.g),
      Math.abs(first.b - second.b),
    );
    const visibleInteractive = Array.from(document.querySelectorAll('button, a[href], [role="button"], input[type="button"], input[type="submit"]'))
      .filter((element) => {
        const node = /** @type {HTMLElement} */ (element);
        const style = window.getComputedStyle(node);
        const rect = node.getBoundingClientRect();
        return !node.matches(':disabled, [aria-disabled="true"]')
          && style.display !== 'none'
          && style.visibility !== 'hidden'
          && Number(style.opacity) >= 0.55
          && rect.width >= 2
          && rect.height >= 2;
      });
    const controlThemeOutliers = primaryColor ? visibleInteractive
      .map((element) => {
        const node = /** @type {HTMLElement} */ (element);
        const classTokens = typeof node.className === 'string' ? node.className.split(/\s+/) : [];
        const expectsSolidPrimary = classTokens.some((token) => primaryActionTokens.has(token));
        const expectsPrimaryGradient = classTokens.some((token) => token === 'from-blue-500' || token === 'from-blue-600');
        if (!expectsSolidPrimary && !expectsPrimaryGradient) return null;

        const style = window.getComputedStyle(node);
        const background = parseColor(style.backgroundColor);
        const gradientColors = (style.backgroundImage.match(/rgba?\([^)]+\)|color\(srgb[^)]+\)/g) ?? [])
          .map(parseColor)
          .filter(Boolean);
        const matchesTheme = expectsSolidPrimary
          ? Boolean(background && colorDistance(background, primaryColor) <= 3)
          : gradientColors.some((color) => colorDistance(color, primaryColor) <= 3);
        if (matchesTheme) return null;

        return {
          tag: node.tagName.toLowerCase(),
          className: classTokens.join(' ').slice(0, 160),
          text: (node.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 80),
          background: style.backgroundColor,
          backgroundImage: style.backgroundImage.slice(0, 180),
        };
      })
      .filter(Boolean)
      .slice(0, 20) : [];

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
      theme: document.documentElement.dataset.payplannerTheme ?? '',
      lowContrast,
      controlThemeOutliers,
      loadingLines,
    };
  });

  await mkdir(artifactDir, { recursive: true });
  await page.screenshot({
    path: path.join(artifactDir, `${themeName ? `${themeName}-` : ''}${viewportName}-${route.name}.png`),
    fullPage: true,
  });

  const failures = [];

  if (themeName && metrics.theme !== themeName) {
    failures.push(`requested theme ${themeName} was replaced by ${metrics.theme || 'an empty value'}`);
  }

  if (metrics.lowContrast.length > 0) {
    failures.push(`low-contrast text: ${metrics.lowContrast.map((item) => `${item.tag} ratio=${item.ratio} fg=${item.foreground} bg=${item.background} class="${item.className}" text="${item.text}"`).join('; ')}`);
  }

  if (metrics.controlThemeOutliers.length > 0) {
    failures.push(`primary controls outside the selected theme: ${metrics.controlThemeOutliers.map((item) => `${item.tag} bg=${item.background} image=${item.backgroundImage} class="${item.className}" text="${item.text}"`).join('; ')}`);
  }

  if (metrics.bodyLength < 20) {
    failures.push(`page rendered too little text (${metrics.bodyLength} chars)`);
  }

  if (metrics.loadingLines.length > 0) {
    failures.push(`page was still loading when captured: ${metrics.loadingLines.join('; ')}`);
  }

  if (metrics.documentOverflowX > 2) {
    const offenders = metrics.overflowingElements
      .filter((item) => item.right <= metrics.documentWidth + 2)
      .slice(0, 8)
      .map((item) => `${item.tag}${item.testId ? `[data-testid=${item.testId}]` : ''} right=${item.right}px width=${item.width}px pos=${item.position} class="${item.className}" text="${item.text}" ancestors=${item.ancestors}`)
      .join('; ');
    const scrollOffenders = metrics.scrollWidthElements
      .map((item) => `${item.tag}${item.testId ? `[data-testid=${item.testId}]` : ''}{left=${item.left},right=${item.right},cw=${item.clientWidth},sw=${item.scrollWidth},ox=${item.overflowX},pos=${item.position},class="${item.className}"}`)
      .join('; ');
    failures.push(`horizontal document overflow is ${metrics.documentOverflowX}px (document ${metrics.documentWidth}px, viewport ${metrics.viewportWidth}px)${offenders ? `; candidates: ${offenders}` : ''}${scrollOffenders ? `; visible-scroll candidates: ${scrollOffenders}` : ''}${metrics.topScrollbars.length ? `; top-scrollbars: ${metrics.topScrollbars.join('; ')}` : ''}`);
  }

  for (const text of consoleErrors) {
    failures.push(`console.error: ${text}`);
  }

  for (const text of pageErrors) {
    failures.push(`pageerror: ${text}`);
  }
  failures.push(...networkErrors);
  if (expectedNavigationAborts.length > 0)
    console.log(`Expected prior-page navigation aborts [${viewportName}] ${route.name}: ${expectedNavigationAborts.length}`);

  return {
    route: route.name,
    viewport: viewportName,
    url: metrics.url,
    title: metrics.title,
    theme: themeName ?? (metrics.theme || 'server-default'),
    failures,
  };
}

export function printHelp() {
  console.log(`Usage: node scripts/run-visual-smoke.mjs [options]

Options:
  --frontend-url <url>     Frontend base URL. Default: PAYPLANNER_VISUAL_FRONTEND_URL or ${DEFAULT_FRONTEND_URL}
  --routes <csv>           Routes to check. Default: public login page, or key app pages when auth is available.
  --full-app <bool>        Check every top-level CRM section when authenticated.
  --themes <csv|all>       Force visual themes without changing the setting on the server.
  --toast-preview <bool>   Render a stable matrix of real application toasts for visual auditing (development only).
  --selection-preview <bool>
                           Select all visible rows and verify their checkbox marks.
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
  const toastPreview = options.toastPreview;
  const selectionPreview = options.selectionPreview;
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

    const routes = resolveRoutes(options.routes, authenticated, options.fullApp);
    const themeNames = resolveThemeNames(options.themes);
    const viewportNames = resolveViewportNames(options.viewports);

    console.log('PayPlanner visual smoke');
    console.log(`Frontend: ${frontendUrl}`);
    console.log(`Browser: ${label}`);
    console.log(`Auth: ${authenticated ? 'enabled' : 'public-only'}`);
    console.log(`Viewports: ${viewportNames.join(', ')}`);
    console.log(`Themes: ${themeNames.map((theme) => theme ?? 'server-default').join(', ')}`);
    console.log(`Toast preview: ${toastPreview ? 'enabled' : 'disabled'}`);
    console.log(`Selection preview: ${selectionPreview ? 'enabled' : 'disabled'}`);
    console.log(`Routes: ${routes.map((route) => route.path).join(', ')}`);
    console.log(`Screenshots: ${artifactDir}`);
    console.log('');

    const failures = [];
    const rows = [];

    for (const viewportName of viewportNames) {
      const page = await context.newPage();
      await page.setViewportSize(VIEWPORTS[viewportName]);

      try {
        for (const themeName of themeNames) {
          for (const route of routes) {
            const result = await checkPage({ page, frontendUrl, route, viewportName, themeName, timeoutMs, artifactDir, toastPreview, selectionPreview });
            rows.push(result);

            if (result.failures.length > 0) {
              for (const failure of result.failures) {
                failures.push(formatPageFailure(route.name, `${viewportName}/${themeName ?? 'server-default'}`, failure));
              }
            }
          }
        }
      } finally {
        await page.close();
      }
    }

    console.log('| viewport | theme | route | url | title | status |');
    console.log('| --- | --- | --- | --- | --- | --- |');
    for (const row of rows) {
      console.log(`| ${row.viewport} | ${row.theme} | ${row.route} | ${row.url} | ${row.title.replaceAll('|', '\\|')} | ${row.failures.length === 0 ? 'ok' : 'failed'} |`);
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
    fullApp: readBoolean(args.fullApp ?? process.env.PAYPLANNER_VISUAL_FULL_APP),
    themes: args.themes ?? process.env.PAYPLANNER_VISUAL_THEMES,
    viewports: args.viewports ?? process.env.PAYPLANNER_VISUAL_VIEWPORTS,
    artifactDir: args.artifactDir ?? process.env.PAYPLANNER_VISUAL_ARTIFACT_DIR ?? DEFAULT_ARTIFACT_DIR,
    timeoutMs: readPositiveInt(args.timeoutMs ?? process.env.PAYPLANNER_VISUAL_TIMEOUT_MS, DEFAULT_TIMEOUT_MS),
    requireAuth: readBoolean(args.requireAuth ?? process.env.PAYPLANNER_VISUAL_REQUIRE_AUTH),
    toastPreview: readBoolean(args.toastPreview ?? process.env.PAYPLANNER_VISUAL_TOAST_PREVIEW),
    selectionPreview: readBoolean(args.selectionPreview ?? process.env.PAYPLANNER_VISUAL_SELECTION_PREVIEW),
    token: args.token ?? process.env.PAYPLANNER_VISUAL_TOKEN ?? '',
    email: args.email ?? process.env.PAYPLANNER_VISUAL_EMAIL ?? '',
    password: args.password ?? process.env.PAYPLANNER_VISUAL_PASSWORD ?? '',
  });
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  await main();
}
