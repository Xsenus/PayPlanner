#!/usr/bin/env node

const DEFAULT_API_URL = 'http://localhost:5080/api';
const DEFAULT_FRONTEND_URL = 'http://localhost:5173';
const DEFAULT_TIMEOUT_MS = 15_000;

const args = parseArgs(process.argv.slice(2));

if (args.help) {
  printHelp();
  process.exit(0);
}

const apiUrl = trimTrailingSlash(args.apiUrl ?? process.env.PAYPLANNER_E2E_API_URL ?? DEFAULT_API_URL);
const frontendUrl = trimTrailingSlash(args.frontendUrl ?? process.env.PAYPLANNER_E2E_FRONTEND_URL ?? DEFAULT_FRONTEND_URL);
const email = args.email ?? process.env.PAYPLANNER_E2E_EMAIL ?? '';
const password = args.password ?? process.env.PAYPLANNER_E2E_PASSWORD ?? '';
const timeoutMs = readPositiveInt(args.timeoutMs ?? process.env.PAYPLANNER_E2E_TIMEOUT_MS, DEFAULT_TIMEOUT_MS);
const publicOnly = readBoolean(args.publicOnly ?? process.env.PAYPLANNER_E2E_PUBLIC_ONLY);
const selectedScenarioNames = new Set(splitCsv(args.scenarios ?? process.env.PAYPLANNER_E2E_SCENARIOS));

let token = args.token ?? process.env.PAYPLANNER_E2E_TOKEN ?? process.env.PAYPLANNER_LOAD_TOKEN ?? '';

const today = new Date();
const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1)));
const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0)));

const scenarios = [
  {
    name: 'frontend-shell',
    auth: false,
    run: checkFrontendShell,
  },
  {
    name: 'features',
    auth: false,
    run: () => checkJsonEndpoint('/features', 'feature flags', (payload) => {
      assertObject(payload, 'feature flags response');
      assertObject(payload.legal, 'feature flags legal section');
    }),
  },
  {
    name: 'legal-integrations-diagnostics',
    auth: true,
    run: () => checkJsonEndpoint('/features/legal-integrations/diagnostics', 'legal integrations diagnostics', (payload) => {
      assertObject(payload, 'legal integrations diagnostics response');
      assertArray(payload.items, 'legal integrations diagnostics items');
      assertBoolean(payload.hasEnabledExternalLegalFlags, 'legal integrations diagnostics enabled flag');
      for (const item of payload.items) {
        assertObject(item, 'legal integration diagnostic item');
        assertNonEmptyString(item.key, 'legal integration diagnostic item key');
        assertNonEmptyString(item.status, 'legal integration diagnostic item status');
        assertBoolean(item.enabled, 'legal integration diagnostic item enabled');
        assertBoolean(item.safeToRun, 'legal integration diagnostic item safeToRun');
      }
    }),
  },
  {
    name: 'profile',
    auth: true,
    run: () => checkJsonEndpoint('/auth/me', 'current user profile', (payload) => {
      assertObject(payload, 'profile response');
      assertNumber(payload.id, 'profile.id');
      assertNonEmptyString(payload.email, 'profile.email');
      assertObject(payload.role, 'profile.role');
    }),
  },
  {
    name: 'role-permissions-audit',
    auth: true,
    run: () => checkJsonEndpoint('/role-permissions/audit', 'role permission audit', (payload) => {
      assertObject(payload, 'role permission audit response');
      assertNumber(payload.totalRoles, 'audit.totalRoles');
      assertNumber(payload.rolesWithBroadDataScope, 'audit.rolesWithBroadDataScope');
      assertArray(payload.roles, 'audit.roles');
    }),
  },
  {
    name: 'role-permissions-audit-csv',
    auth: true,
    run: () => checkCsvEndpoint('/role-permissions/audit.csv', 'role permission audit csv', [
      'RoleId',
      'RoleName',
      'HasIssues',
      'BroadDataScopeSections',
    ]),
  },
  {
    name: 'roles-paged',
    auth: true,
    run: () => checkPagedEndpoint('/roles/paged?page=1&pageSize=20', 'roles page'),
  },
  {
    name: 'cases-registry',
    auth: true,
    run: () => checkPagedEndpoint(
      '/v2/cases?page=1&pageSize=20&scope=active&activity=all&inactiveDays=14&sortBy=lastViewedAt&sortDir=desc',
      'cases registry',
    ),
  },
  {
    name: 'legacy-clients',
    auth: true,
    run: () => checkPagedEndpointWithOptionalDetail(
      '/v2/clients?page=1&pageSize=20',
      'legacy clients registry',
      (id) => `/clients/${id}`,
    ),
  },
  {
    name: 'legacy-invoices',
    auth: true,
    run: () => checkPagedEndpointWithOptionalDetail(
      '/invoices?page=1&pageSize=20',
      'legacy invoices registry',
      (id) => `/invoices/${id}`,
    ),
  },
  {
    name: 'legacy-payments',
    auth: true,
    run: () => checkPagedEndpointWithOptionalDetail(
      '/v2/payments?page=1&pageSize=20',
      'legacy payments registry',
      (id) => `/payments/${id}`,
    ),
  },
  {
    name: 'legacy-tasks',
    auth: true,
    run: () => checkPagedEndpointWithOptionalDetail(
      '/tasks?page=1&pageSize=20',
      'legacy task journal',
      (id) => `/tasks/${id}`,
    ),
  },
  {
    name: 'cases-inactive',
    auth: true,
    run: () => checkPagedEndpoint(
      '/v2/cases?page=1&pageSize=20&scope=active&activity=inactive&inactiveDays=14&sortBy=lastViewedAt&sortDir=asc',
      'inactive cases registry',
    ),
  },
  {
    name: 'case-document-types',
    auth: true,
    run: () => checkJsonEndpoint('/v2/cases/document-types', 'case document types', (payload) => {
      assertArray(payload, 'case document types response');
    }),
  },
  {
    name: 'case-document-templates',
    auth: true,
    run: () => checkJsonEndpoint('/document-templates', 'case document templates', (payload) => {
      assertArray(payload, 'case document templates response');
    }),
  },
  {
    name: 'case-document-template-tokens',
    auth: true,
    run: () => checkJsonEndpoint('/document-template-tokens', 'case document template tokens', (payload) => {
      assertArray(payload, 'case document template tokens response');
    }),
  },
  {
    name: 'case-calendar-events',
    auth: true,
    run: () => checkJsonEndpoint(
      `/case-calendar/events?from=${from}&to=${to}&includeTasks=false`,
      'case calendar events',
      (payload) => {
        assertArray(payload, 'case calendar events response');
      },
    ),
  },
  {
    name: 'case-calendar-summary',
    auth: true,
    run: () => checkJsonEndpoint(
      `/case-calendar/summary?from=${from}&to=${to}&includeTasks=false`,
      'case calendar summary',
      (payload) => {
        assertObject(payload, 'case calendar summary response');
        assertNumber(payload.totalEventCount, 'caseCalendarSummary.totalEventCount');
        assertNumber(payload.proceduralDeadlineCount, 'caseCalendarSummary.proceduralDeadlineCount');
        assertNumber(payload.courtEventCount, 'caseCalendarSummary.courtEventCount');
      },
    ),
  },
  {
    name: 'legal-work-report',
    auth: true,
    run: () => checkJsonEndpoint(`/reports/legal-work?from=${from}&to=${to}`, 'legal work report', (payload) => {
      assertObject(payload, 'legal work report response');
      assertArray(payload.cases, 'legal work report cases');
      assertArray(payload.financeByStatus, 'legal work report financeByStatus');
      assertArray(payload.deadlinesByType, 'legal work report deadlinesByType');
    }),
  },
  {
    name: 'case-activity-report',
    auth: true,
    run: () => checkJsonEndpoint(
      '/reports/case-activity?inactiveDays=14&escalationDays=30&activity=all&limit=100',
      'case activity report',
      (payload) => {
        assertObject(payload, 'case activity report response');
      },
    ),
  },
  {
    name: 'case-automation-candidates',
    auth: true,
    run: () => checkJsonEndpoint(
      '/tasks/automation/case-candidates?inactiveDays=14&escalationDays=30&limit=50',
      'case automation candidates',
      (payload) => {
        assertArray(payload, 'case automation candidates response');
      },
    ),
  },
].filter((scenario) => selectedScenarioNames.size === 0 || selectedScenarioNames.has(scenario.name));

const unknownScenarioNames = [...selectedScenarioNames]
  .filter((name) => !knownScenarioNames().includes(name));
if (unknownScenarioNames.length > 0) {
  fail(`Unknown scenarios: ${unknownScenarioNames.join(', ')}. Known scenarios: ${knownScenarioNames().join(', ')}`);
}

if (scenarios.length === 0) {
  fail(`No scenarios selected. Known scenarios: ${knownScenarioNames().join(', ')}`);
}

const requiresAuth = scenarios.some((scenario) => scenario.auth);
if (requiresAuth && !token && !publicOnly) {
  if (!email || !password) {
    fail('Authenticated e2e scenarios require PAYPLANNER_E2E_TOKEN or PAYPLANNER_E2E_EMAIL/PAYPLANNER_E2E_PASSWORD. Use --public-only for frontend/features only.');
  }

  token = await login();
}

const runnableScenarios = publicOnly
  ? scenarios.filter((scenario) => !scenario.auth)
  : scenarios;

if (runnableScenarios.length === 0) {
  fail('No public scenarios selected.');
}

console.log('PayPlanner legal e2e smoke');
console.log(`Frontend: ${frontendUrl}`);
console.log(`API: ${apiUrl}`);
console.log(`Scenarios: ${runnableScenarios.map((scenario) => scenario.name).join(', ')}`);
console.log('');

const rows = [];
let failed = false;

for (const scenario of runnableScenarios) {
  const started = performance.now();
  try {
    await scenario.run();
    rows.push({ name: scenario.name, status: 'ok', ms: performance.now() - started, message: '' });
  } catch (error) {
    failed = true;
    rows.push({
      name: scenario.name,
      status: 'failed',
      ms: performance.now() - started,
      message: error instanceof Error ? error.message : String(error),
    });
  }
}

printRows(rows);

if (failed) {
  process.exitCode = 1;
}

async function login() {
  const payload = await requestJson('/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
    auth: false,
    label: 'login',
  });

  assertObject(payload, 'login response');
  assertNonEmptyString(payload.token, 'login.token');
  return payload.token;
}

async function checkFrontendShell() {
  const response = await requestUrl(`${frontendUrl}/roles`, {
    headers: {
      Accept: 'text/html',
    },
    label: 'frontend shell',
  });
  const html = await response.text();

  if (!html.includes('<div id="root"></div>') && !html.includes('<div id="root">')) {
    throw new Error('frontend shell does not contain the React root element');
  }

  if (!html.includes('/assets/') && !html.includes('type="module"')) {
    throw new Error('frontend shell does not reference bundled scripts');
  }
}

async function checkPagedEndpoint(path, label) {
  return checkJsonEndpoint(path, label, (payload) => {
    assertObject(payload, `${label} response`);
    assertArray(payload.items, `${label}.items`);
    assertNumber(payload.total, `${label}.total`);
  });
}

async function checkJsonEndpoint(path, label, validate) {
  const payload = await requestJson(path, { label });
  validate(payload);
  return payload;
}

async function checkPagedEndpointWithOptionalDetail(path, label, detailPathForId) {
  const payload = await checkPagedEndpoint(path, label);
  if (payload.total <= 0) {
    return;
  }

  const firstItem = payload.items[0];
  assertObject(firstItem, `${label}.items[0]`);
  const id = firstItem.id;
  assertNumber(id, `${label}.items[0].id`);

  await checkJsonEndpoint(detailPathForId(id), `${label} first item detail`, (detailPayload) => {
    assertObject(detailPayload, `${label} first item detail response`);
  });
}

async function checkCsvEndpoint(path, label, expectedColumns) {
  const response = await requestUrl(`${apiUrl}${path}`, {
    headers: {
      Accept: 'text/csv',
      Authorization: `Bearer ${token}`,
    },
    label,
  });

  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.toLowerCase().includes('csv')) {
    throw new Error(`${label} returned non-csv content type: ${contentType || 'empty'}`);
  }

  const csv = await response.text();
  const header = csv.replace(/^\uFEFF/, '').split(/\r?\n/, 1)[0] ?? '';
  const missingColumns = expectedColumns.filter((column) => !header.includes(column));
  if (missingColumns.length > 0) {
    throw new Error(`${label} is missing columns: ${missingColumns.join(', ')}`);
  }
}

async function requestJson(path, options = {}) {
  const response = await requestUrl(`${apiUrl}${path}`, {
    method: options.method ?? 'GET',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...(options.auth === false ? {} : { Authorization: `Bearer ${token}` }),
    },
    body: options.body,
    label: options.label,
  });

  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.toLowerCase().includes('json')) {
    throw new Error(`${options.label ?? path} returned non-json content type: ${contentType || 'empty'}`);
  }

  return response.json();
}

async function requestUrl(url, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);

  try {
    const response = await fetch(url, {
      method: options.method ?? 'GET',
      headers: options.headers,
      body: options.body,
      signal: controller.signal,
    });

    if (!response.ok) {
      const body = await response.text().catch(() => '');
      throw new Error(`${options.label ?? url} returned HTTP ${response.status}${body ? `: ${body.slice(0, 240)}` : ''}`);
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
  console.log(`Usage: node scripts/run-legal-e2e-smoke.mjs [options]

Options:
  --api-url=http://localhost:5080/api
  --frontend-url=http://localhost:5173
  --token=<jwt>
  --email=<email>
  --password=<password>
  --timeout-ms=15000
  --public-only
  --scenarios=frontend-shell,features,profile

Environment:
  PAYPLANNER_E2E_API_URL
  PAYPLANNER_E2E_FRONTEND_URL
  PAYPLANNER_E2E_TOKEN
  PAYPLANNER_E2E_EMAIL
  PAYPLANNER_E2E_PASSWORD
  PAYPLANNER_E2E_TIMEOUT_MS
  PAYPLANNER_E2E_PUBLIC_ONLY
  PAYPLANNER_E2E_SCENARIOS

Known scenarios:
  ${knownScenarioNames().join(', ')}
`);
}

function printRows(rows) {
  console.log('| scenario | status | ms | message |');
  console.log('| --- | --- | ---: | --- |');
  for (const row of rows) {
    console.log(`| ${row.name} | ${row.status} | ${round(row.ms)} | ${escapeTableCell(row.message)} |`);
  }
}

function knownScenarioNames() {
  return [
    'frontend-shell',
    'features',
    'legal-integrations-diagnostics',
    'profile',
    'role-permissions-audit',
    'role-permissions-audit-csv',
    'roles-paged',
    'cases-registry',
    'legacy-clients',
    'legacy-invoices',
    'legacy-payments',
    'legacy-tasks',
    'cases-inactive',
    'legal-work-report',
    'case-activity-report',
    'case-automation-candidates',
  ];
}

function assertObject(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} must be an object`);
  }
}

function assertBoolean(value, label) {
  if (typeof value !== 'boolean') {
    throw new Error(`${label} must be a boolean`);
  }
}

function assertArray(value, label) {
  if (!Array.isArray(value)) {
    throw new Error(`${label} must be an array`);
  }
}

function assertNumber(value, label) {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new Error(`${label} must be a finite number`);
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

function readBoolean(value) {
  const normalized = `${value ?? ''}`.trim().toLowerCase();
  return normalized === '1' || normalized === 'true' || normalized === 'yes';
}

function splitCsv(value) {
  return `${value ?? ''}`
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean);
}

function trimTrailingSlash(value) {
  return value.replace(/\/+$/, '');
}

function toCamelCase(value) {
  return value.replace(/-([a-z])/g, (_, char) => char.toUpperCase());
}

function formatDate(value) {
  return value.toISOString().slice(0, 10);
}

function round(value) {
  return Math.round(value * 10) / 10;
}

function escapeTableCell(value) {
  return `${value ?? ''}`.replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}

function fail(message) {
  console.error(message);
  process.exit(2);
}
