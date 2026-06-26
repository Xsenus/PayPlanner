#!/usr/bin/env node

const DEFAULT_API_URL = 'http://localhost:5080/api';
const DEFAULT_ADMIN_EMAIL = 'admin@payplanner.local';
const DEFAULT_ADMIN_PASSWORD = 'ChangeMeAdmin#12345';
const DEFAULT_TIMEOUT_MS = 15_000;

const args = parseArgs(process.argv.slice(2));

if (args.help) {
  printHelp();
  process.exit(0);
}

const apiUrl = trimTrailingSlash(args.apiUrl ?? process.env.PAYPLANNER_ROLE_SMOKE_API_URL ?? DEFAULT_API_URL);
const adminEmail = args.adminEmail ?? process.env.PAYPLANNER_ROLE_SMOKE_ADMIN_EMAIL ?? DEFAULT_ADMIN_EMAIL;
const adminPassword = args.adminPassword ?? process.env.PAYPLANNER_ROLE_SMOKE_ADMIN_PASSWORD ?? DEFAULT_ADMIN_PASSWORD;
const providedAdminToken = args.adminToken ?? process.env.PAYPLANNER_ROLE_SMOKE_ADMIN_TOKEN ?? '';
const timeoutMs = readPositiveInt(args.timeoutMs ?? process.env.PAYPLANNER_ROLE_SMOKE_TIMEOUT_MS, DEFAULT_TIMEOUT_MS);
const suffix = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
const roleName = args.roleName ?? `Юрист smoke codex ${suffix}`;
const userEmail = (args.userEmail ?? `lawyer-smoke-${suffix}@payplanner.local`).toLowerCase();
const userPassword = args.userPassword ?? `LawyerSmoke#${suffix}`;

let adminToken = '';
let lawyerToken = '';
let createdRoleId = null;
let createdUserId = null;
const rows = [];

try {
  adminToken = providedAdminToken || await login(adminEmail, adminPassword, 'admin login');
  rows.push(okRow('admin auth', providedAdminToken ? 'provided token' : adminEmail));

  const role = await createRole();
  createdRoleId = role.id;
  rows.push(okRow('create lawyer role', `roleId=${createdRoleId}`));

  const permissions = await requestJson(`/role-permissions/${createdRoleId}`, {
    token: adminToken,
    label: 'created lawyer role permissions',
  });
  assertLawyerTemplate(permissions);
  rows.push(okRow('verify lawyer template', 'operational sections only'));

  const user = await createUser(createdRoleId);
  createdUserId = user.id;
  rows.push(okRow('create lawyer user', `userId=${createdUserId}`));

  lawyerToken = await login(userEmail, userPassword, 'lawyer login');
  rows.push(okRow('login lawyer user', userEmail));

  await checkAllowedEndpoints();
  await checkForbiddenEndpoints();

  printRows(rows);
} catch (error) {
  rows.push(failRow('role permission smoke', error instanceof Error ? error.message : String(error)));
  printRows(rows);
  process.exitCode = 1;
} finally {
  await cleanup();
}

async function createRole() {
  return requestJson('/roles', {
    method: 'POST',
    token: adminToken,
    label: 'create lawyer role',
    body: {
      name: roleName,
      description: 'Временная роль для smoke-проверки прав. Удаляется автоматически.',
    },
  });
}

async function createUser(roleId) {
  return requestJson('/users', {
    method: 'POST',
    token: adminToken,
    label: 'create lawyer user',
    body: {
      email: userEmail,
      password: userPassword,
      fullName: 'Smoke Юрист Codex',
      roleId,
      isActive: true,
      isEmployee: true,
      isExternalContractor: false,
    },
  });
}

async function checkAllowedEndpoints() {
  const allowed = [
    ['/auth/me', 'profile'],
    ['/tasks?page=1&pageSize=1', 'tasks list'],
    ['/tasks/settings', 'task settings'],
  ];

  for (const [path, label] of allowed) {
    const response = await request(path, { token: lawyerToken, label });
    if (!response.ok) {
      const body = await response.text().catch(() => '');
      throw new Error(`${label} expected 2xx for safe lawyer role, got HTTP ${response.status}${body ? `: ${body.slice(0, 200)}` : ''}`);
    }
    rows.push(okRow(label, `HTTP ${response.status}`));
  }
}

async function checkForbiddenEndpoints() {
  const forbidden = [
    ['/v2/clients?page=1&pageSize=1', 'clients list'],
    ['/v2/cases?page=1&pageSize=1&scope=active&activity=all&inactiveDays=14&sortBy=lastViewedAt&sortDir=desc', 'cases list'],
    ['/v2/payments?page=1&pageSize=1', 'payments list'],
    ['/invoices?page=1&pageSize=1', 'accounts list'],
    ['/acts?page=1&pageSize=1', 'acts list'],
    ['/contracts?page=1&pageSize=1', 'contracts list'],
    ['/reports/legal-work?from=2026-01-01&to=2026-01-31', 'reports legal work'],
    ['/roles', 'roles list'],
    ['/role-permissions/audit', 'role permission audit'],
    ['/yandex-disk/settings', 'yandex disk settings'],
    ['/s3-storages', 's3 storages'],
  ];

  for (const [path, label] of forbidden) {
    const response = await request(path, { token: lawyerToken, label, allowError: true });
    if (response.status !== 403) {
      const body = await response.text().catch(() => '');
      throw new Error(`${label} expected HTTP 403 for safe lawyer role, got HTTP ${response.status}${body ? `: ${body.slice(0, 200)}` : ''}`);
    }
    rows.push(okRow(label, 'HTTP 403'));
  }
}

function assertLawyerTemplate(permissions) {
  assertSectionOpen(permissions.tasks, 'tasks', 'own');
  assertSectionOpen(permissions.calendar, 'calendar', 'own');
  assertSectionOpen(permissions.notifications, 'notifications');

  for (const [section, key] of [
    ['legalEntities', 'legalEntities'],
    ['clients', 'clients'],
    ['cases', 'cases'],
    ['accounts', 'accounts'],
    ['acts', 'acts'],
    ['payments', 'payments'],
    ['contracts', 'contracts'],
    ['reports', 'reports'],
    ['roles', 'roles'],
    ['audit', 'audit'],
    ['integrations', 'integrations'],
    ['yandexDisk', 'yandexDisk'],
    ['s3Storage', 's3Storage'],
  ]) {
    assertSectionClosed(permissions[key], section);
  }
}

function assertSectionOpen(section, label, expectedScope = null) {
  assertObject(section, `${label} permissions`);
  if (section.canView !== true) {
    throw new Error(`${label} must be visible in safe lawyer template`);
  }
  if (expectedScope && section.viewScope !== expectedScope) {
    throw new Error(`${label} must use ${expectedScope} scope, got ${section.viewScope}`);
  }
}

function assertSectionClosed(section, label) {
  assertObject(section, `${label} permissions`);
  for (const key of ['canView', 'canCreate', 'canEdit', 'canDelete', 'canExport']) {
    if (section[key] !== false) {
      throw new Error(`${label}.${key} must be false in safe lawyer template`);
    }
  }
}

async function login(email, password, label) {
  const payload = await requestJson('/auth/login', {
    method: 'POST',
    label,
    auth: false,
    body: { email, password },
  });
  assertObject(payload, `${label} response`);
  assertNonEmptyString(payload.token, `${label}.token`);
  return payload.token;
}

async function cleanup() {
  if (!adminToken) {
    return;
  }

  if (createdUserId !== null) {
    try {
      await request(`/users/${createdUserId}`, {
        method: 'DELETE',
        token: adminToken,
        label: 'cleanup lawyer user',
        allowError: true,
      });
    } catch (error) {
      console.warn(`Cleanup warning: could not delete user ${createdUserId}: ${error instanceof Error ? error.message : error}`);
    }
  }

  if (createdRoleId !== null) {
    try {
      await request(`/roles/${createdRoleId}`, {
        method: 'DELETE',
        token: adminToken,
        label: 'cleanup lawyer role',
        allowError: true,
      });
    } catch (error) {
      console.warn(`Cleanup warning: could not delete role ${createdRoleId}: ${error instanceof Error ? error.message : error}`);
    }
  }
}

async function requestJson(path, options = {}) {
  const response = await request(path, options);
  const text = await response.text();
  if (!text) {
    return null;
  }

  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.toLowerCase().includes('json')) {
    throw new Error(`${options.label ?? path} returned non-json content type: ${contentType || 'empty'}`);
  }

  return JSON.parse(text);
}

async function request(path, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const headers = {
      Accept: 'application/json',
      ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(options.auth === false ? {} : { Authorization: `Bearer ${options.token}` }),
    };

    const response = await fetch(`${apiUrl}${path}`, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: controller.signal,
    });

    if (!options.allowError && !response.ok) {
      const body = await response.text().catch(() => '');
      throw new Error(`${options.label ?? path} returned HTTP ${response.status}${body ? `: ${body.slice(0, 240)}` : ''}`);
    }

    return response;
  } finally {
    clearTimeout(timeout);
  }
}

function parseArgs(values) {
  const parsed = {};
  for (const value of values) {
    if (value === '--help' || value === '-h') {
      parsed.help = true;
      continue;
    }

    if (!value.startsWith('--')) {
      continue;
    }

    const [key, ...rest] = value.slice(2).split('=');
    parsed[toCamelCase(key)] = rest.length > 0 ? rest.join('=') : 'true';
  }

  return parsed;
}

function printHelp() {
  console.log(`Usage: node scripts/run-role-permission-smoke.mjs [options]

Options:
  --api-url=http://localhost:5080/api
  --admin-email=admin@payplanner.local
  --admin-password=ChangeMeAdmin#12345
  --admin-token=<jwt>
  --timeout-ms=15000
  --role-name="Юрист smoke codex ..."
  --user-email=lawyer-smoke@example.local
  --user-password=LawyerSmoke#...

Environment:
  PAYPLANNER_ROLE_SMOKE_API_URL
  PAYPLANNER_ROLE_SMOKE_ADMIN_EMAIL
  PAYPLANNER_ROLE_SMOKE_ADMIN_PASSWORD
  PAYPLANNER_ROLE_SMOKE_ADMIN_TOKEN
  PAYPLANNER_ROLE_SMOKE_TIMEOUT_MS
`);
}

function printRows(items) {
  console.log('| check | status | details |');
  console.log('| --- | --- | --- |');
  for (const item of items) {
    console.log(`| ${item.name} | ${item.status} | ${escapeTableCell(item.details)} |`);
  }
}

function okRow(name, details = '') {
  return { name, status: 'ok', details };
}

function failRow(name, details = '') {
  return { name, status: 'failed', details };
}

function assertObject(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} must be an object`);
  }
}

function assertNonEmptyString(value, label) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`${label} must be a non-empty string`);
  }
}

function readPositiveInt(value, fallback) {
  const parsed = Number.parseInt(`${value ?? ''}`, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function trimTrailingSlash(value) {
  return value.replace(/\/+$/, '');
}

function toCamelCase(value) {
  return value.replace(/-([a-z])/g, (_, char) => char.toUpperCase());
}

function escapeTableCell(value) {
  return `${value ?? ''}`.replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}
