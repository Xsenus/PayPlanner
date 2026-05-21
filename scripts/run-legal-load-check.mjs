#!/usr/bin/env node
import { performance } from 'node:perf_hooks';

const DEFAULT_API_URL = 'http://localhost:5080/api';
const DEFAULT_ITERATIONS = 8;
const DEFAULT_WARMUPS = 1;
const DEFAULT_CONCURRENCY = 2;
const DEFAULT_TIMEOUT_MS = 15_000;
const DEFAULT_P95_THRESHOLD_MS = 3_000;

const args = parseArgs(process.argv.slice(2));

if (args.help) {
  printHelp();
  process.exit(0);
}

const apiUrl = trimTrailingSlash(args.apiUrl ?? process.env.PAYPLANNER_LOAD_API_URL ?? DEFAULT_API_URL);
const token = args.token ?? process.env.PAYPLANNER_LOAD_TOKEN ?? '';
const iterations = readPositiveInt(args.iterations ?? process.env.PAYPLANNER_LOAD_ITERATIONS, DEFAULT_ITERATIONS);
const warmups = readNonNegativeInt(args.warmups ?? process.env.PAYPLANNER_LOAD_WARMUPS, DEFAULT_WARMUPS);
const concurrency = readPositiveInt(args.concurrency ?? process.env.PAYPLANNER_LOAD_CONCURRENCY, DEFAULT_CONCURRENCY);
const timeoutMs = readPositiveInt(args.timeoutMs ?? process.env.PAYPLANNER_LOAD_TIMEOUT_MS, DEFAULT_TIMEOUT_MS);
const p95ThresholdMs = readPositiveInt(args.p95ThresholdMs ?? process.env.PAYPLANNER_LOAD_P95_THRESHOLD_MS, DEFAULT_P95_THRESHOLD_MS);
const selectedScenarioNames = new Set(splitCsv(args.scenarios ?? process.env.PAYPLANNER_LOAD_SCENARIOS));
const today = new Date();
const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1)));
const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0)));

const scenarios = [
  {
    name: 'features',
    path: '/features',
    auth: false,
    description: 'public feature flag snapshot',
  },
  {
    name: 'cases-registry',
    path: '/v2/cases?page=1&pageSize=50&scope=active&activity=all&inactiveDays=14&sortBy=lastViewedAt&sortDir=desc',
    auth: true,
    description: 'legal cases registry with activity sorting',
  },
  {
    name: 'legacy-clients',
    path: '/v2/clients?page=1&pageSize=50',
    auth: true,
    description: 'legacy clients registry after migrations',
  },
  {
    name: 'legacy-invoices',
    path: '/invoices?page=1&pageSize=50',
    auth: true,
    description: 'legacy invoices registry after migrations',
  },
  {
    name: 'legacy-payments',
    path: '/v2/payments?page=1&pageSize=50',
    auth: true,
    description: 'legacy payments registry after migrations',
  },
  {
    name: 'legacy-tasks',
    path: '/tasks?page=1&pageSize=50',
    auth: true,
    description: 'legacy task journal after migrations',
  },
  {
    name: 'role-permissions-audit',
    path: '/role-permissions/audit',
    auth: true,
    description: 'role permission readiness audit',
  },
  {
    name: 'role-permissions-audit-csv',
    path: '/role-permissions/audit.csv',
    auth: true,
    accept: 'text/csv',
    description: 'role permission readiness audit csv export',
  },
  {
    name: 'cases-inactive',
    path: '/v2/cases?page=1&pageSize=50&scope=active&activity=inactive&inactiveDays=14&sortBy=lastViewedAt&sortDir=asc',
    auth: true,
    description: 'inactive cases registry filter',
  },
  {
    name: 'case-document-types',
    path: '/v2/cases/document-types',
    auth: true,
    description: 'case document type dictionary',
  },
  {
    name: 'case-document-templates',
    path: '/document-templates',
    auth: true,
    description: 'case document template library',
  },
  {
    name: 'case-document-template-tokens',
    path: '/document-template-tokens',
    auth: true,
    description: 'case document template token registry',
  },
  {
    name: 'case-calendar-events',
    path: `/case-calendar/events?from=${from}&to=${to}&includeTasks=false`,
    auth: true,
    description: 'case calendar legal deadlines and court events',
  },
  {
    name: 'case-calendar-summary',
    path: `/case-calendar/summary?from=${from}&to=${to}&includeTasks=false`,
    auth: true,
    description: 'case calendar summary counters',
  },
  {
    name: 'legal-work-report',
    path: `/reports/legal-work?from=${from}&to=${to}`,
    auth: true,
    description: 'legal workload report',
  },
  {
    name: 'case-activity-report',
    path: '/reports/case-activity?inactiveDays=14&escalationDays=30&activity=all&limit=100',
    auth: true,
    description: 'case activity and inactivity diagnostics',
  },
  {
    name: 'case-automation-candidates',
    path: '/tasks/automation/case-candidates?inactiveDays=14&escalationDays=30&limit=50',
    auth: true,
    description: 'case inactivity automation candidates',
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

if (!token && scenarios.some((scenario) => scenario.auth)) {
  fail('PAYPLANNER_LOAD_TOKEN is required for authenticated scenarios. Use --scenarios=features to run only the public check.');
}

console.log(`PayPlanner legal load check`);
console.log(`API: ${apiUrl}`);
console.log(`Scenarios: ${scenarios.map((scenario) => scenario.name).join(', ')}`);
console.log(`Iterations: ${iterations}; warmups: ${warmups}; concurrency: ${concurrency}; timeout: ${timeoutMs} ms; p95 limit: ${p95ThresholdMs} ms`);
console.log('');

let failed = false;
const rows = [];

for (const scenario of scenarios) {
  const result = await runScenario(scenario);
  rows.push(result);
  if (result.errors > 0 || result.p95Ms > p95ThresholdMs) {
    failed = true;
  }
}

printRows(rows);

if (failed) {
  process.exitCode = 1;
}

async function runScenario(scenario) {
  for (let i = 0; i < warmups; i += 1) {
    await requestScenario(scenario, i, true).catch(() => undefined);
  }

  const durations = [];
  let errors = 0;
  let bytes = 0;
  const statuses = new Map();
  const queue = Array.from({ length: iterations }, (_, index) => index);
  const workers = Array.from({ length: Math.min(concurrency, iterations) }, async () => {
    while (queue.length > 0) {
      const index = queue.shift();
      if (index === undefined) {
        return;
      }

      try {
        const sample = await requestScenario(scenario, index, false);
        durations.push(sample.ms);
        bytes += sample.bytes;
        statuses.set(sample.status, (statuses.get(sample.status) ?? 0) + 1);
        if (!sample.ok) {
          errors += 1;
        }
      } catch (error) {
        errors += 1;
        statuses.set('error', (statuses.get('error') ?? 0) + 1);
        durations.push(timeoutMs);
        console.error(`[${scenario.name}] request failed: ${error instanceof Error ? error.message : String(error)}`);
      }
    }
  });

  await Promise.all(workers);
  durations.sort((left, right) => left - right);

  return {
    name: scenario.name,
    description: scenario.description,
    requests: iterations,
    errors,
    avgMs: average(durations),
    p95Ms: percentile(durations, 0.95),
    maxMs: durations.at(-1) ?? 0,
    bytes,
    statuses: [...statuses.entries()].map(([status, count]) => `${status}:${count}`).join(', '),
  };
}

async function requestScenario(scenario, index, warmup) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  const started = performance.now();
  try {
    const response = await fetch(`${apiUrl}${scenario.path}`, {
      headers: {
        Accept: scenario.accept ?? 'application/json',
        ...(scenario.auth ? { Authorization: `Bearer ${token}` } : {}),
        'X-PayPlanner-Load-Check': warmup ? `warmup-${index}` : `sample-${index}`,
      },
      signal: controller.signal,
    });
    const body = await response.arrayBuffer();
    return {
      ms: performance.now() - started,
      status: response.status,
      ok: response.ok,
      bytes: body.byteLength,
    };
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
  console.log(`Usage: node scripts/run-legal-load-check.mjs [options]

Options:
  --api-url=http://localhost:5080/api
  --token=<jwt>
  --iterations=8
  --warmups=1
  --concurrency=2
  --timeout-ms=15000
  --p95-threshold-ms=3000
  --scenarios=cases-registry,legal-work-report

Environment:
  PAYPLANNER_LOAD_API_URL
  PAYPLANNER_LOAD_TOKEN
  PAYPLANNER_LOAD_ITERATIONS
  PAYPLANNER_LOAD_WARMUPS
  PAYPLANNER_LOAD_CONCURRENCY
  PAYPLANNER_LOAD_TIMEOUT_MS
  PAYPLANNER_LOAD_P95_THRESHOLD_MS
  PAYPLANNER_LOAD_SCENARIOS

Known scenarios:
  ${knownScenarioNames().join(', ')}
`);
}

function printRows(rows) {
  console.log('| scenario | requests | errors | avg ms | p95 ms | max ms | bytes | statuses |');
  console.log('| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |');
  for (const row of rows) {
    console.log(`| ${row.name} | ${row.requests} | ${row.errors} | ${round(row.avgMs)} | ${round(row.p95Ms)} | ${round(row.maxMs)} | ${row.bytes} | ${row.statuses} |`);
  }
}

function knownScenarioNames() {
  return [
    'features',
    'cases-registry',
    'legacy-clients',
    'legacy-invoices',
    'legacy-payments',
    'legacy-tasks',
    'role-permissions-audit',
    'role-permissions-audit-csv',
    'cases-inactive',
    'legal-work-report',
    'case-activity-report',
    'case-automation-candidates',
  ];
}

function readPositiveInt(value, fallback) {
  const parsed = Number.parseInt(`${value ?? ''}`, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function readNonNegativeInt(value, fallback) {
  const parsed = Number.parseInt(`${value ?? ''}`, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback;
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

function average(values) {
  return values.length === 0 ? 0 : values.reduce((sum, value) => sum + value, 0) / values.length;
}

function percentile(values, rank) {
  if (values.length === 0) {
    return 0;
  }

  const index = Math.min(values.length - 1, Math.ceil(values.length * rank) - 1);
  return values[index];
}

function round(value) {
  return Math.round(value * 10) / 10;
}

function fail(message) {
  console.error(message);
  process.exit(2);
}
