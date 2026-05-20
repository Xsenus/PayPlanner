#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

const DEFAULT_API_URL = 'http://localhost:5080/api';
const DEFAULT_FRONTEND_URL = 'http://localhost:5173';
const DEFAULT_OUTPUT_DIR = '.codex-build/legal-readiness';
const DEFAULT_TIMEOUT_MS = 15_000;

const args = parseArgs(process.argv.slice(2));

if (args.help) {
  printHelp();
  process.exit(0);
}

const apiUrl = trimTrailingSlash(args.apiUrl ?? process.env.PAYPLANNER_READINESS_API_URL ?? DEFAULT_API_URL);
const frontendUrl = trimTrailingSlash(args.frontendUrl ?? process.env.PAYPLANNER_READINESS_FRONTEND_URL ?? DEFAULT_FRONTEND_URL);
const outputRoot = args.outputDir ?? process.env.PAYPLANNER_READINESS_OUTPUT_DIR ?? DEFAULT_OUTPUT_DIR;
const timeoutMs = readPositiveInt(args.timeoutMs ?? process.env.PAYPLANNER_READINESS_TIMEOUT_MS, DEFAULT_TIMEOUT_MS);
const publicOnly = readBoolean(args.publicOnly ?? process.env.PAYPLANNER_READINESS_PUBLIC_ONLY);
const skipApiSummary = readBoolean(args.skipApiSummary ?? process.env.PAYPLANNER_READINESS_SKIP_API_SUMMARY);
const skipDatabaseSummary = readBoolean(args.skipDatabaseSummary ?? process.env.PAYPLANNER_READINESS_SKIP_DATABASE_SUMMARY);
const skipE2e = readBoolean(args.skipE2e ?? process.env.PAYPLANNER_READINESS_SKIP_E2E);
const skipLoad = readBoolean(args.skipLoad ?? process.env.PAYPLANNER_READINESS_SKIP_LOAD);
const failOnRoleIssues = !readBoolean(args.noFailOnRoleIssues ?? process.env.PAYPLANNER_READINESS_NO_FAIL_ON_ROLE_ISSUES);
const failOnBroadScopes = readBoolean(args.failOnBroadScopes ?? process.env.PAYPLANNER_READINESS_FAIL_ON_BROAD_SCOPES);
const failOnEnabledLegalFlags = readBoolean(args.failOnEnabledLegalFlags ?? process.env.PAYPLANNER_READINESS_FAIL_ON_ENABLED_LEGAL_FLAGS);
const expectedRolesFile = args.expectedRolesFile ?? process.env.PAYPLANNER_READINESS_EXPECTED_ROLES_FILE ?? '';
const expectedRoles = await readExpectedRoles(args.expectedRoles ?? process.env.PAYPLANNER_READINESS_EXPECTED_ROLES, expectedRolesFile);
const allowedBroadScopeRoles = splitRoleList(args.allowedBroadScopeRoles ?? process.env.PAYPLANNER_READINESS_ALLOWED_BROAD_SCOPE_ROLES);
const redactValuesFile = args.redactValuesFile ?? process.env.PAYPLANNER_READINESS_REDACT_VALUES_FILE ?? '';
const extraRedactValues = await readListValues(args.redactValues ?? process.env.PAYPLANNER_READINESS_REDACT_VALUES, redactValuesFile);
const requireAssignedExpectedRoles = readBoolean(args.requireAssignedExpectedRoles ?? process.env.PAYPLANNER_READINESS_REQUIRE_ASSIGNED_EXPECTED_ROLES);
const e2eScenarios = args.e2eScenarios ?? process.env.PAYPLANNER_READINESS_E2E_SCENARIOS ?? '';
const loadScenarios = args.loadScenarios ?? process.env.PAYPLANNER_READINESS_LOAD_SCENARIOS ?? '';
const loadIterations = readOptionalPositiveInt(args.loadIterations ?? process.env.PAYPLANNER_READINESS_LOAD_ITERATIONS, '--load-iterations');
const loadWarmups = readOptionalNonNegativeInt(args.loadWarmups ?? process.env.PAYPLANNER_READINESS_LOAD_WARMUPS, '--load-warmups');
const loadConcurrency = readOptionalPositiveInt(args.loadConcurrency ?? process.env.PAYPLANNER_READINESS_LOAD_CONCURRENCY, '--load-concurrency');
const loadP95ThresholdMs = readOptionalPositiveInt(args.loadP95ThresholdMs ?? process.env.PAYPLANNER_READINESS_LOAD_P95_THRESHOLD_MS, '--load-p95-threshold-ms');
const maxBackupAgeHours = readOptionalPositiveInt(args.maxBackupAgeHours ?? process.env.PAYPLANNER_READINESS_MAX_BACKUP_AGE_HOURS, '--max-backup-age-hours');
const requireDatabaseBackup = readBoolean(args.requireDatabaseBackup ?? process.env.PAYPLANNER_READINESS_REQUIRE_DATABASE_BACKUP) || maxBackupAgeHours !== '';
const email = args.email ?? process.env.PAYPLANNER_READINESS_EMAIL ?? process.env.PAYPLANNER_E2E_EMAIL ?? '';
const password = args.password ?? process.env.PAYPLANNER_READINESS_PASSWORD ?? process.env.PAYPLANNER_E2E_PASSWORD ?? '';
let token = args.token
  ?? process.env.PAYPLANNER_READINESS_TOKEN
  ?? process.env.PAYPLANNER_E2E_TOKEN
  ?? process.env.PAYPLANNER_LOAD_TOKEN
  ?? '';

if (!publicOnly && !skipApiSummary && !token && (!email || !password)) {
  fail('Release readiness requires a token or PAYPLANNER_READINESS_EMAIL/PAYPLANNER_READINESS_PASSWORD. Use --public-only or --skip-api-summary for frontend-only checks.');
}

if (requireAssignedExpectedRoles && expectedRoles.length === 0) {
  fail('--require-assigned-expected-roles requires --expected-roles, --expected-roles-file, PAYPLANNER_READINESS_EXPECTED_ROLES, or PAYPLANNER_READINESS_EXPECTED_ROLES_FILE.');
}

if (failOnEnabledLegalFlags && skipApiSummary) {
  fail('--fail-on-enabled-legal-flags requires API summary to be enabled.');
}

if ((failOnBroadScopes || expectedRoles.length > 0 || requireAssignedExpectedRoles) && (publicOnly || skipApiSummary)) {
  fail('Role strict checks require API summary and role audit to be enabled.');
}

if (requireDatabaseBackup && (publicOnly || skipApiSummary || skipDatabaseSummary)) {
  fail('--require-database-backup and --max-backup-age-hours require API summary and database summary to be enabled.');
}

if (!token && !publicOnly && !skipApiSummary) {
  token = await login();
}

const generatedAt = new Date();
const stamp = generatedAt.toISOString().replace(/[:.]/g, '-');
const outputDir = path.resolve(outputRoot, stamp);
await mkdir(outputDir, { recursive: true });
const reportFrontendUrl = redactSensitiveText(frontendUrl);
const reportApiUrl = redactSensitiveText(apiUrl);

const report = {
  generatedAtUtc: generatedAt.toISOString(),
  frontendUrl: reportFrontendUrl,
  apiUrl: reportApiUrl,
  outputDir,
  options: {
    publicOnly,
    skipApiSummary,
    skipDatabaseSummary,
    skipE2e,
    skipLoad,
    timeoutMs,
    failOnRoleIssues,
    failOnBroadScopes,
    allowedBroadScopeRoles,
    failOnEnabledLegalFlags,
    expectedRoles,
    expectedRolesFile: expectedRolesFile || null,
    extraRedactValuesCount: extraRedactValues.length,
    redactValuesFile: redactValuesFile || null,
    requireAssignedExpectedRoles,
    e2eScenarios: e2eScenarios || 'default',
    loadScenarios: loadScenarios || 'default',
    loadIterations: loadIterations !== '' ? loadIterations : 'default',
    loadWarmups: loadWarmups !== '' ? loadWarmups : 'default',
    loadConcurrency: loadConcurrency !== '' ? loadConcurrency : 'default',
    loadP95ThresholdMs: loadP95ThresholdMs !== '' ? loadP95ThresholdMs : 'default',
    requireDatabaseBackup,
    maxBackupAgeHours: maxBackupAgeHours !== '' ? maxBackupAgeHours : 'default',
  },
  checks: [],
  summaries: {},
};

console.log('PayPlanner legal release readiness');
console.log(`Frontend: ${reportFrontendUrl}`);
console.log(`API: ${reportApiUrl}`);
console.log(`Output: ${outputDir}`);
console.log('');

if (!skipE2e) {
  const e2eArgs = [
    'scripts/run-legal-e2e-smoke.mjs',
    `--api-url=${apiUrl}`,
    `--frontend-url=${frontendUrl}`,
    `--timeout-ms=${timeoutMs}`,
    ...(publicOnly ? ['--public-only'] : []),
    ...(e2eScenarios ? [`--scenarios=${e2eScenarios}`] : []),
    ...(token ? [`--token=${token}`] : []),
    ...(email ? [`--email=${email}`] : []),
    ...(password ? [`--password=${password}`] : []),
  ];
  report.checks.push(await runNodeCheck('legal-e2e', e2eArgs));
}

if (!skipLoad && !publicOnly) {
  const loadArgs = [
    'scripts/run-legal-load-check.mjs',
    `--api-url=${apiUrl}`,
    `--timeout-ms=${timeoutMs}`,
    ...(loadScenarios ? [`--scenarios=${loadScenarios}`] : []),
    ...(loadIterations !== '' ? [`--iterations=${loadIterations}`] : []),
    ...(loadWarmups !== '' ? [`--warmups=${loadWarmups}`] : []),
    ...(loadConcurrency !== '' ? [`--concurrency=${loadConcurrency}`] : []),
    ...(loadP95ThresholdMs !== '' ? [`--p95-threshold-ms=${loadP95ThresholdMs}`] : []),
    ...(token ? [`--token=${token}`] : []),
  ];
  report.checks.push(await runNodeCheck('legal-load', loadArgs));
}

if (!skipApiSummary) {
  report.summaries.featureFlags = await getFeatureFlagsSummary();

  if (!publicOnly) {
    report.summaries.roleAudit = await getRoleAuditSummary();

    if (!skipDatabaseSummary) {
      report.summaries.database = await getDatabaseSummary();
    }
  }
}

const failedChecks = report.checks.filter((check) => check.exitCode !== 0);
const failedSummaries = Object.entries(report.summaries)
  .filter(([, summary]) => summary?.status === 'failed')
  .map(([name, summary]) => ({
    name,
    message: summary.message ?? 'Unknown summary failure.',
  }));
const roleIssues = report.summaries.roleAudit?.rolesWithIssues ?? 0;
const broadScopeIssues = report.summaries.roleAudit?.rolesWithBroadDataScope ?? 0;
const disallowedBroadScopeRoles = findDisallowedBroadScopeRoles(allowedBroadScopeRoles, report.summaries.roleAudit);
const enabledLegalFlags = Array.isArray(report.summaries.featureFlags?.enabledLegalFlags)
  ? report.summaries.featureFlags.enabledLegalFlags
  : [];
const missingExpectedRoles = findMissingExpectedRoles(expectedRoles, report.summaries.roleAudit);
const unassignedExpectedRoles = findUnassignedExpectedRoles(expectedRoles, report.summaries.roleAudit);
report.result = buildReadinessResult(
  failedChecks,
  failedSummaries,
  roleIssues,
  broadScopeIssues,
  disallowedBroadScopeRoles,
  enabledLegalFlags,
  expectedRoles,
  missingExpectedRoles,
  unassignedExpectedRoles,
  report.summaries.roleAudit?.status,
  report.summaries.database,
  report.options,
);
const failed = report.result.status === 'failed';
report.artifacts = {
  markdownReport: path.join(outputDir, 'readiness-report.md'),
  jsonReport: path.join(outputDir, 'readiness-report.json'),
  roleAuditCsv: report.summaries.roleAudit?.csv?.path ?? null,
};

await writeFile(report.artifacts.jsonReport, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
await writeFile(
  report.artifacts.markdownReport,
  buildMarkdownReport(report),
  'utf8',
);

console.log('');
console.log(`Result: ${report.result.status}`);
if (report.result.blockingReasons.length > 0) {
  console.log(`Blocking: ${report.result.blockingReasons.join(' | ')}`);
}
if (report.result.warnings.length > 0) {
  console.log(`Warnings: ${report.result.warnings.join(' | ')}`);
}
console.log(`Report written: ${report.artifacts.markdownReport}`);
console.log(`JSON written: ${report.artifacts.jsonReport}`);
if (report.artifacts.roleAuditCsv) {
  console.log(`Role audit CSV: ${report.artifacts.roleAuditCsv}`);
}

if (failed) {
  process.exitCode = 1;
}

async function login() {
  const response = await requestJson('/auth/login', {
    method: 'POST',
    auth: false,
    body: JSON.stringify({ email, password }),
    label: 'login',
  });

  if (!response.token || typeof response.token !== 'string') {
    fail('Login response does not contain token.');
  }

  return response.token;
}

async function runNodeCheck(name, commandArgs) {
  const started = performance.now();
  console.log(`Running ${name}...`);

  const result = await runProcess(process.execPath, commandArgs, {
    ...process.env,
    PAYPLANNER_E2E_API_URL: apiUrl,
    PAYPLANNER_E2E_FRONTEND_URL: frontendUrl,
    PAYPLANNER_E2E_TOKEN: token,
    PAYPLANNER_LOAD_API_URL: apiUrl,
    PAYPLANNER_LOAD_TOKEN: token,
  });
  const stdout = redactSensitiveText(result.stdout);
  const stderr = redactSensitiveText(result.stderr);

  const check = {
    name,
    command: formatCommandForReport(process.execPath, commandArgs),
    exitCode: result.exitCode,
    durationMs: Math.round((performance.now() - started) * 10) / 10,
    stdout,
    stderr,
  };

  await writeFile(path.join(outputDir, `${name}.stdout.txt`), stdout, 'utf8');
  await writeFile(path.join(outputDir, `${name}.stderr.txt`), stderr, 'utf8');

  console.log(`${name}: ${check.exitCode === 0 ? 'ok' : `failed (${check.exitCode})`}`);
  return check;
}

function runProcess(command, commandArgs, env) {
  return new Promise((resolve) => {
    const child = spawn(command, commandArgs, {
      cwd: process.cwd(),
      env,
      shell: false,
      windowsHide: true,
    });

    let stdout = '';
    let stderr = '';

    child.stdout.on('data', (chunk) => {
      stdout += chunk.toString();
    });
    child.stderr.on('data', (chunk) => {
      stderr += chunk.toString();
    });
    child.on('error', (error) => {
      stderr += `${error instanceof Error ? error.message : String(error)}\n`;
      resolve({ exitCode: 1, stdout, stderr });
    });
    child.on('close', (code) => {
      resolve({ exitCode: code ?? 1, stdout, stderr });
    });
  });
}

async function getFeatureFlagsSummary() {
  try {
    const payload = await requestJson('/features', { auth: false, label: 'feature flags' });
    const legalFlags = payload?.legal && typeof payload.legal === 'object' ? payload.legal : {};
    const enabledLegalFlags = Object.entries(legalFlags)
      .filter(([, value]) => value === true)
      .map(([key]) => key);

    return {
      status: 'ok',
      enabledLegalFlags,
      legalFlags,
    };
  } catch (error) {
    return {
      status: 'failed',
      message: error instanceof Error ? error.message : String(error),
    };
  }
}

async function getRoleAuditSummary() {
  try {
    const [payload, csv] = await Promise.all([
      requestJson('/role-permissions/audit', { label: 'role permission audit' }),
      requestBytes('/role-permissions/audit.csv', {
        accept: 'text/csv',
        label: 'role permission audit csv',
      }),
    ]);
    const roles = Array.isArray(payload.roles) ? payload.roles : [];
    const broadScopeRoles = roles
      .filter((role) => Array.isArray(role.broadDataScopeSections) && role.broadDataScopeSections.length > 0)
      .map((role) => ({
        roleId: role.roleId,
        roleName: role.roleName,
        broadDataScopeSections: role.broadDataScopeSections,
      }));
    const issueRoles = roles
      .filter((role) => role.hasIssues === true)
      .map((role) => ({
        roleId: role.roleId,
        roleName: role.roleName,
        assignedUsersCount: Number.isFinite(role.assignedUsersCount) ? role.assignedUsersCount : 0,
        missingSections: readArray(role.missingSections),
        unknownSections: readArray(role.unknownSections),
        duplicateSections: readArray(role.duplicateSections),
        invalidViewScopeSections: readArray(role.invalidViewScopeSections),
        capabilityWithoutViewSections: readArray(role.capabilityWithoutViewSections),
        restrictedCapabilitySections: readArray(role.restrictedCapabilitySections),
        issueSummary: buildRoleIssueSummary(role),
      }))
      .sort((left, right) => right.assignedUsersCount - left.assignedUsersCount || `${left.roleName}`.localeCompare(`${right.roleName}`));
    if (!csv.contentType.toLowerCase().includes('csv')) {
      throw new Error(`role permission audit csv returned non-csv content type: ${csv.contentType || 'empty'}`);
    }
    if (csv.bytes.byteLength === 0) {
      throw new Error('role permission audit csv returned an empty file');
    }
    const csvFileName = sanitizeFileName(csv.fileName ?? 'role-permissions-audit.csv');
    const csvPath = path.join(outputDir, csvFileName);
    await writeFile(csvPath, csv.bytes);
    const roleNames = roles
      .map((role) => role.roleName)
      .filter((roleName) => typeof roleName === 'string' && roleName.trim().length > 0)
      .sort((left, right) => left.localeCompare(right));
    const roleAssignments = roles
      .filter((role) => typeof role.roleName === 'string' && role.roleName.trim().length > 0)
      .map((role) => ({
        roleName: role.roleName,
        assignedUsersCount: Number.isFinite(role.assignedUsersCount) ? role.assignedUsersCount : 0,
      }))
      .sort((left, right) => left.roleName.localeCompare(right.roleName));

    return {
      status: 'ok',
      totalRoles: payload.totalRoles ?? roles.length,
      readyRoles: payload.readyRoles ?? 0,
      rolesWithIssues: payload.rolesWithIssues ?? 0,
      rolesWithBroadDataScope: payload.rolesWithBroadDataScope ?? broadScopeRoles.length,
      roleNames,
      roleAssignments,
      issueRoles,
      broadScopeRoles,
      csv: {
        fileName: csvFileName,
        path: csvPath,
        bytes: csv.bytes.byteLength,
        contentType: csv.contentType,
      },
    };
  } catch (error) {
    return {
      status: 'failed',
      message: error instanceof Error ? error.message : String(error),
    };
  }
}

async function getDatabaseSummary() {
  try {
    const [status, backups] = await Promise.all([
      requestJson('/database/status', { label: 'database status' }),
      requestJson('/database/backups', { label: 'database backup overview' }),
    ]);
    const backupItems = Array.isArray(backups.items) ? backups.items : [];
    const lastBackup = backupItems.length > 0
      ? backupItems
          .slice()
          .sort((left, right) => `${right.createdAt}`.localeCompare(`${left.createdAt}`))[0]
      : null;
    const lastBackupAgeHours = lastBackup?.createdAt
      ? calculateAgeHours(lastBackup.createdAt)
      : null;

    return {
      status: 'ok',
      provider: status.provider,
      displayName: status.displayName,
      supportsSqliteMaintenance: status.supportsSqliteMaintenance === true,
      backupFormat: backups.backupFormat,
      selectedFormat: backups.selectedFormat,
      automaticEnabled: backups.automaticEnabled === true,
      scheduleLocal: backups.scheduleLocal,
      retentionDays: backups.retentionDays,
      rootDirectory: backups.rootDirectory,
      backupCount: backupItems.length,
      lastBackup,
      lastBackupAgeHours,
    };
  } catch (error) {
    return {
      status: 'failed',
      message: error instanceof Error ? error.message : String(error),
    };
  }
}

async function requestJson(pathname, options = {}) {
  const response = await requestUrl(`${apiUrl}${pathname}`, {
    method: options.method ?? 'GET',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...(options.auth === false ? {} : { Authorization: `Bearer ${token}` }),
    },
    body: options.body,
    label: options.label,
  });

  return response.json();
}

async function requestBytes(pathname, options = {}) {
  const response = await requestUrl(`${apiUrl}${pathname}`, {
    method: options.method ?? 'GET',
    headers: {
      Accept: options.accept ?? 'application/octet-stream',
      ...(options.auth === false ? {} : { Authorization: `Bearer ${token}` }),
    },
    label: options.label,
  });
  const contentType = response.headers.get('content-type') ?? '';
  const buffer = Buffer.from(await response.arrayBuffer());

  return {
    bytes: buffer,
    contentType,
    fileName: readContentDispositionFileName(response.headers.get('content-disposition')),
  };
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

function buildMarkdownReport(data) {
  const lines = [
    '# PayPlanner Legal Readiness Report',
    '',
    `Generated: ${data.generatedAtUtc}`,
    `Frontend: ${data.frontendUrl}`,
    `API: ${data.apiUrl}`,
    '',
    '## Run Options',
    '',
    `Public only: ${data.options.publicOnly ? 'yes' : 'no'}`,
    `Skip API summary: ${data.options.skipApiSummary ? 'yes' : 'no'}`,
    `Skip database summary: ${data.options.skipDatabaseSummary ? 'yes' : 'no'}`,
    `Skip e2e: ${data.options.skipE2e ? 'yes' : 'no'}`,
    `Skip load: ${data.options.skipLoad ? 'yes' : 'no'}`,
    `Timeout: ${data.options.timeoutMs} ms`,
    `Fail on role issues: ${data.options.failOnRoleIssues ? 'yes' : 'no'}`,
    `Fail on broad scopes: ${data.options.failOnBroadScopes ? 'yes' : 'no'}`,
    `Allowed broad scope roles: ${data.options.allowedBroadScopeRoles.length > 0 ? data.options.allowedBroadScopeRoles.join(', ') : 'none'}`,
    `Fail on enabled legal flags: ${data.options.failOnEnabledLegalFlags ? 'yes' : 'no'}`,
    `Expected roles: ${data.options.expectedRoles.length > 0 ? data.options.expectedRoles.join(', ') : 'none'}`,
    `Expected roles file: ${data.options.expectedRolesFile ?? 'none'}`,
    `Extra redact values: ${data.options.extraRedactValuesCount}`,
    `Extra redact values file: ${data.options.redactValuesFile ?? 'none'}`,
    `Require assigned expected roles: ${data.options.requireAssignedExpectedRoles ? 'yes' : 'no'}`,
    `E2E scenarios: ${data.options.e2eScenarios}`,
    `Load scenarios: ${data.options.loadScenarios}`,
    `Load iterations: ${data.options.loadIterations}`,
    `Load warmups: ${data.options.loadWarmups}`,
    `Load concurrency: ${data.options.loadConcurrency}`,
    `Load p95 threshold ms: ${data.options.loadP95ThresholdMs}`,
    `Require database backup: ${data.options.requireDatabaseBackup ? 'yes' : 'no'}`,
    `Max backup age hours: ${data.options.maxBackupAgeHours}`,
    '',
    '## Checks',
    '',
    '| check | status | ms |',
    '| --- | --- | ---: |',
  ];

  for (const check of data.checks) {
    lines.push(`| ${check.name} | ${check.exitCode === 0 ? 'ok' : `failed (${check.exitCode})`} | ${check.durationMs} |`);
  }

  lines.push('', '## Feature Flags', '');
  if (data.summaries.featureFlags?.status === 'ok') {
    const enabled = data.summaries.featureFlags.enabledLegalFlags;
    lines.push(`Enabled legal flags: ${enabled.length > 0 ? enabled.join(', ') : 'none'}`);
  } else if (data.summaries.featureFlags) {
    lines.push(`Feature flags check failed: ${data.summaries.featureFlags.message}`);
  } else {
    lines.push('Feature flags check skipped.');
  }

  lines.push('', '## Role Audit', '');
  if (data.summaries.roleAudit?.status === 'ok') {
    const roleAudit = data.summaries.roleAudit;
    lines.push(`Roles ready: ${roleAudit.readyRoles} / ${roleAudit.totalRoles}`);
    lines.push(`Roles with critical issues: ${roleAudit.rolesWithIssues}`);
    lines.push(`Roles with broad data scopes: ${roleAudit.rolesWithBroadDataScope}`);
    if (data.options.failOnBroadScopes) {
      const disallowed = data.result.disallowedBroadScopeRoles ?? [];
      lines.push(`Disallowed broad data scope roles: ${disallowed.length > 0 ? disallowed.map((role) => role.roleName).join(', ') : 'none'}`);
    }
    if (data.options.expectedRoles.length > 0) {
      const missing = data.result.missingExpectedRoles ?? [];
      lines.push(`Expected roles check: ${missing.length > 0 ? `missing ${missing.join(', ')}` : 'ok'}`);
      if (data.options.requireAssignedExpectedRoles) {
        const unassigned = data.result.unassignedExpectedRoles ?? [];
        lines.push(`Expected role assignments check: ${unassigned.length > 0 ? `unassigned ${unassigned.join(', ')}` : 'ok'}`);
      }
    }
    if (roleAudit.csv) {
      lines.push(`CSV artifact: ${roleAudit.csv.fileName} (${roleAudit.csv.bytes} bytes)`);
    }
    if (roleAudit.issueRoles.length > 0) {
      lines.push('', 'Roles requiring action:', '');
      lines.push('| role | users | issues |');
      lines.push('| --- | ---: | --- |');
      for (const role of roleAudit.issueRoles.slice(0, 12)) {
        lines.push(`| ${escapeTableCell(role.roleName)} | ${role.assignedUsersCount} | ${escapeTableCell(role.issueSummary)} |`);
      }
      if (roleAudit.issueRoles.length > 12) {
        lines.push(`| ... |  | ${roleAudit.issueRoles.length - 12} more role(s) in CSV |`);
      }
    }
    if (roleAudit.broadScopeRoles.length > 0) {
      lines.push('', 'Broad data scopes:');
      for (const role of roleAudit.broadScopeRoles.slice(0, 10)) {
        lines.push(`- ${role.roleName}: ${role.broadDataScopeSections.join(', ')}`);
      }
    }
  } else if (data.summaries.roleAudit) {
    lines.push(`Role audit failed: ${data.summaries.roleAudit.message}`);
  } else {
    lines.push('Role audit skipped.');
  }

  lines.push('', '## Database And Backups', '');
  if (data.summaries.database?.status === 'ok') {
    const database = data.summaries.database;
    lines.push(`Provider: ${database.displayName ?? database.provider ?? 'unknown'}`);
    lines.push(`Backup format: ${database.backupFormat ?? 'unknown'}`);
    lines.push(`Automatic backups: ${database.automaticEnabled ? 'enabled' : 'disabled'}`);
    lines.push(`Backup count: ${database.backupCount}`);
    lines.push(`Last backup: ${database.lastBackup ? `${database.lastBackup.fileName} (${database.lastBackup.createdAt})` : 'none'}`);
    if (typeof database.lastBackupAgeHours === 'number') {
      lines.push(`Last backup age: ${database.lastBackupAgeHours} hour(s)`);
    }
  } else if (data.summaries.database) {
    lines.push(`Database summary unavailable: ${data.summaries.database.message}`);
  } else {
    lines.push('Database summary skipped.');
  }

  lines.push('', '## Result', '');
  lines.push(data.result.status === 'passed'
    ? (data.result.warnings.length > 0 ? 'Readiness checks passed with warnings.' : 'Readiness checks passed.')
    : 'Readiness checks failed.');

  for (const blockingReason of data.result.blockingReasons) {
    lines.push(`- ${blockingReason}`);
  }

  if (data.result.warnings.length > 0) {
    lines.push('', 'Warnings:');
    for (const warning of data.result.warnings) {
      lines.push(`- ${warning}`);
    }
  }

  return `${lines.join('\n')}\n`;
}

function buildReadinessResult(
  failedChecks,
  failedSummaries,
  roleIssues,
  broadScopeIssues,
  disallowedBroadScopeRoles,
  enabledLegalFlags,
  expectedRoles,
  missingExpectedRoles,
  unassignedExpectedRoles,
  roleAuditStatus,
  databaseSummary,
  options,
) {
  const blockingReasons = [];
  const warnings = [];

  if (failedChecks.length > 0) {
    blockingReasons.push(`Failed checks: ${failedChecks.map((check) => check.name).join(', ')}`);
  }

  for (const failure of failedSummaries) {
    blockingReasons.push(`${failure.name} summary failed: ${failure.message}`);
  }

  if (options.failOnRoleIssues && roleIssues > 0) {
    blockingReasons.push(`Role audit has ${roleIssues} role(s) with critical issues.`);
  } else if (roleIssues > 0) {
    warnings.push(`Role audit has ${roleIssues} non-blocking critical issue(s) because fail-on-role-issues is disabled.`);
  }

  const disallowedBroadScopeCount = disallowedBroadScopeRoles.length;

  if (options.failOnBroadScopes && disallowedBroadScopeCount > 0) {
    blockingReasons.push(`Strict role audit failed because ${disallowedBroadScopeCount} non-allowed role(s) have broad data scopes: ${disallowedBroadScopeRoles.map((role) => role.roleName).join(', ')}.`);
  } else if (broadScopeIssues > 0) {
    const allowedSuffix = options.allowedBroadScopeRoles.length > 0
      ? ` Allowed broad scope roles: ${options.allowedBroadScopeRoles.join(', ')}.`
      : '';
    warnings.push(`Role audit has ${broadScopeIssues} non-blocking broad data scope warning(s).${allowedSuffix}`);
  }

  if (options.failOnEnabledLegalFlags && enabledLegalFlags.length > 0) {
    blockingReasons.push(`Enabled legal feature flags require explicit release approval: ${enabledLegalFlags.join(', ')}.`);
  } else if (enabledLegalFlags.length > 0) {
    warnings.push(`Enabled legal feature flags detected: ${enabledLegalFlags.join(', ')}.`);
  }

  if (expectedRoles.length > 0 && roleAuditStatus !== 'ok') {
    blockingReasons.push('Expected roles check requires a successful role audit summary.');
  } else if (missingExpectedRoles.length > 0) {
    blockingReasons.push(`Expected customer roles are missing: ${missingExpectedRoles.join(', ')}.`);
  } else if (options.requireAssignedExpectedRoles && unassignedExpectedRoles.length > 0) {
    blockingReasons.push(`Expected customer roles have no assigned users: ${unassignedExpectedRoles.join(', ')}.`);
  }

  if (options.requireDatabaseBackup) {
    if (databaseSummary?.status !== 'ok') {
      blockingReasons.push('Database backup check requires a successful database summary.');
    } else if (!databaseSummary.lastBackup || databaseSummary.backupCount <= 0) {
      blockingReasons.push('Database backup check failed because no backups were found.');
    } else if (typeof databaseSummary.lastBackupAgeHours !== 'number') {
      blockingReasons.push('Database backup check failed because the last backup age could not be determined.');
    } else if (typeof options.maxBackupAgeHours === 'number' && databaseSummary.lastBackupAgeHours > options.maxBackupAgeHours) {
      blockingReasons.push(`Database backup is stale: ${databaseSummary.lastBackupAgeHours} hour(s), limit is ${options.maxBackupAgeHours}.`);
    }
  }

  return {
    status: blockingReasons.length > 0 ? 'failed' : 'passed',
    failedChecks: failedChecks.map((check) => check.name),
    failedSummaries,
    roleIssues,
    broadScopeIssues,
    disallowedBroadScopeRoles,
    enabledLegalFlags,
    expectedRoles,
    missingExpectedRoles,
    unassignedExpectedRoles,
    databaseBackup: buildDatabaseBackupResult(databaseSummary, options),
    blockingReasons,
    warnings,
  };
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
  console.log(`Usage: node scripts/run-legal-readiness-report.mjs [options]

Options:
  --api-url=http://localhost:5080/api
  --frontend-url=http://localhost:5173
  --token=<jwt>
  --email=<email>
  --password=<password>
  --timeout-ms=15000
  --output-dir=.codex-build/legal-readiness
  --public-only
  --skip-api-summary
  --skip-database-summary
  --skip-e2e
  --skip-load
  --e2e-scenarios=frontend-shell,features
  --load-scenarios=features,cases-registry
  --load-iterations=30
  --load-warmups=2
  --load-concurrency=4
  --load-p95-threshold-ms=3000
  --require-database-backup
  --max-backup-age-hours=24
  --expected-roles=Admin,Lawyer,Secretary
  --allowed-broad-scope-roles=admin
  --expected-roles-file=docs/customer-roles.txt
  --redact-values=secret-one,secret-two
  --redact-values-file=.secure/readiness-redactions.txt
  --require-assigned-expected-roles
  --no-fail-on-role-issues
  --fail-on-broad-scopes
  --fail-on-enabled-legal-flags

Environment:
  PAYPLANNER_READINESS_API_URL
  PAYPLANNER_READINESS_FRONTEND_URL
  PAYPLANNER_READINESS_TOKEN
  PAYPLANNER_READINESS_EMAIL
  PAYPLANNER_READINESS_PASSWORD
  PAYPLANNER_READINESS_TIMEOUT_MS
  PAYPLANNER_READINESS_OUTPUT_DIR
  PAYPLANNER_READINESS_PUBLIC_ONLY
  PAYPLANNER_READINESS_SKIP_API_SUMMARY
  PAYPLANNER_READINESS_SKIP_DATABASE_SUMMARY
  PAYPLANNER_READINESS_SKIP_E2E
  PAYPLANNER_READINESS_SKIP_LOAD
  PAYPLANNER_READINESS_E2E_SCENARIOS
  PAYPLANNER_READINESS_LOAD_SCENARIOS
  PAYPLANNER_READINESS_LOAD_ITERATIONS
  PAYPLANNER_READINESS_LOAD_WARMUPS
  PAYPLANNER_READINESS_LOAD_CONCURRENCY
  PAYPLANNER_READINESS_LOAD_P95_THRESHOLD_MS
  PAYPLANNER_READINESS_REQUIRE_DATABASE_BACKUP
  PAYPLANNER_READINESS_MAX_BACKUP_AGE_HOURS
  PAYPLANNER_READINESS_EXPECTED_ROLES
  PAYPLANNER_READINESS_ALLOWED_BROAD_SCOPE_ROLES
  PAYPLANNER_READINESS_EXPECTED_ROLES_FILE
  PAYPLANNER_READINESS_REDACT_VALUES
  PAYPLANNER_READINESS_REDACT_VALUES_FILE
  PAYPLANNER_READINESS_REQUIRE_ASSIGNED_EXPECTED_ROLES
  PAYPLANNER_READINESS_NO_FAIL_ON_ROLE_ISSUES
  PAYPLANNER_READINESS_FAIL_ON_BROAD_SCOPES
  PAYPLANNER_READINESS_FAIL_ON_ENABLED_LEGAL_FLAGS
`);
}

function readPositiveInt(value, fallback) {
  const parsed = Number.parseInt(`${value ?? ''}`, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

function readOptionalPositiveInt(value, label) {
  const normalized = `${value ?? ''}`.trim();
  if (!normalized) {
    return '';
  }

  const parsed = Number.parseInt(normalized, 10);
  if (!Number.isFinite(parsed) || parsed <= 0 || `${parsed}` !== normalized) {
    fail(`${label} must be a positive integer.`);
  }

  return parsed;
}

function readOptionalNonNegativeInt(value, label) {
  const normalized = `${value ?? ''}`.trim();
  if (!normalized) {
    return '';
  }

  const parsed = Number.parseInt(normalized, 10);
  if (!Number.isFinite(parsed) || parsed < 0 || `${parsed}` !== normalized) {
    fail(`${label} must be a non-negative integer.`);
  }

  return parsed;
}

function readBoolean(value) {
  const normalized = `${value ?? ''}`.trim().toLowerCase();
  return normalized === '1' || normalized === 'true' || normalized === 'yes';
}

function calculateAgeHours(value) {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return null;
  }

  return Math.max(0, Math.round(((Date.now() - timestamp) / 3_600_000) * 10) / 10);
}

function buildDatabaseBackupResult(databaseSummary, options) {
  if (!options.requireDatabaseBackup) {
    return {
      required: false,
      status: 'skipped',
    };
  }

  return {
    required: true,
    status: databaseSummary?.status === 'ok'
      && Boolean(databaseSummary.lastBackup)
      && databaseSummary.backupCount > 0
      && typeof databaseSummary.lastBackupAgeHours === 'number'
      && (typeof options.maxBackupAgeHours !== 'number' || databaseSummary.lastBackupAgeHours <= options.maxBackupAgeHours)
      ? 'ok'
      : 'failed',
    maxBackupAgeHours: typeof options.maxBackupAgeHours === 'number' ? options.maxBackupAgeHours : null,
    lastBackupAgeHours: typeof databaseSummary?.lastBackupAgeHours === 'number' ? databaseSummary.lastBackupAgeHours : null,
    backupCount: Number.isFinite(databaseSummary?.backupCount) ? databaseSummary.backupCount : 0,
  };
}

async function readExpectedRoles(inlineValue, filePath) {
  const roles = splitRoleList(inlineValue);
  const normalizedFilePath = `${filePath ?? ''}`.trim();
  if (normalizedFilePath) {
    const content = await readFile(path.resolve(normalizedFilePath), 'utf8');
    roles.push(...splitRoleList(content));
  }

  const seen = new Set();
  return roles.filter((roleName) => {
    const key = normalizeRoleName(roleName);
    if (seen.has(key)) {
      return false;
    }
    seen.add(key);
    return true;
  });
}

async function readListValues(inlineValue, filePath) {
  const values = splitRoleList(inlineValue);
  const normalizedFilePath = `${filePath ?? ''}`.trim();
  if (normalizedFilePath) {
    const content = await readFile(path.resolve(normalizedFilePath), 'utf8');
    values.push(...splitRoleList(content));
  }

  const seen = new Set();
  return values.filter((value) => {
    if (seen.has(value)) {
      return false;
    }
    seen.add(value);
    return true;
  });
}

function splitRoleList(value) {
  return `${value ?? ''}`
    .split(/[,\r\n]+/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function findMissingExpectedRoles(expectedRoleNames, roleAudit) {
  if (expectedRoleNames.length === 0 || roleAudit?.status !== 'ok') {
    return [];
  }

  const actualRoleNames = new Set(readArray(roleAudit.roleNames).map(normalizeRoleName));
  return expectedRoleNames.filter((roleName) => !actualRoleNames.has(normalizeRoleName(roleName)));
}

function findUnassignedExpectedRoles(expectedRoleNames, roleAudit) {
  if (expectedRoleNames.length === 0 || roleAudit?.status !== 'ok') {
    return [];
  }

  const actualAssignments = new Map(
    (Array.isArray(roleAudit.roleAssignments) ? roleAudit.roleAssignments : [])
      .map((role) => [
        normalizeRoleName(role.roleName),
        Number.isFinite(role.assignedUsersCount) ? role.assignedUsersCount : 0,
      ]),
  );

  return expectedRoleNames
    .filter((roleName) => actualAssignments.has(normalizeRoleName(roleName)))
    .filter((roleName) => (actualAssignments.get(normalizeRoleName(roleName)) ?? 0) <= 0);
}

function findDisallowedBroadScopeRoles(allowedRoleNames, roleAudit) {
  if (roleAudit?.status !== 'ok') {
    return [];
  }

  const allowed = new Set(allowedRoleNames.map(normalizeRoleName));
  return (Array.isArray(roleAudit.broadScopeRoles) ? roleAudit.broadScopeRoles : [])
    .filter((role) => !allowed.has(normalizeRoleName(role.roleName)));
}

function normalizeRoleName(value) {
  return `${value ?? ''}`.trim().replace(/\s+/g, ' ').toLowerCase();
}

function trimTrailingSlash(value) {
  return value.replace(/\/+$/, '');
}

function toCamelCase(value) {
  return value.replace(/-([a-z])/g, (_, char) => char.toUpperCase());
}

function quoteArg(value) {
  return /\s/.test(value) ? `"${value.replace(/"/g, '\\"')}"` : value;
}

function formatCommandForReport(command, commandArgs) {
  const redactedArgs = [];
  let redactNext = false;

  for (const arg of commandArgs) {
    if (redactNext) {
      redactedArgs.push('<redacted>');
      redactNext = false;
      continue;
    }

    const sensitiveEqualsMatch = /^(--(?:token|password))=/i.exec(arg);
    if (sensitiveEqualsMatch) {
      redactedArgs.push(`${sensitiveEqualsMatch[1]}=<redacted>`);
      continue;
    }

    if (/^--(?:token|password)$/i.test(arg)) {
      redactedArgs.push(arg);
      redactNext = true;
      continue;
    }

    redactedArgs.push(arg);
  }

  return redactSensitiveText(`${command} ${redactedArgs.map(quoteArg).join(' ')}`);
}

function redactSensitiveText(value) {
  let text = `${value ?? ''}`;
  const sensitiveValues = [token, password, ...extraRedactValues]
    .filter((secret) => typeof secret === 'string' && secret.length > 0)
    .sort((left, right) => right.length - left.length);

  for (const secret of sensitiveValues) {
    text = text.split(secret).join('<redacted>');
  }

  return text
    .replace(/(Authorization:\s*Bearer\s+)[^\s\r\n]+/gi, '$1<redacted>')
    .replace(/(--(?:token|password)=)(?:"[^"]*"|'[^']*'|[^\s\r\n]+)/gi, '$1<redacted>')
    .replace(/(--(?:token|password)\s+)(?:"[^"]*"|'[^']*'|[^\s\r\n]+)/gi, '$1<redacted>');
}

function buildRoleIssueSummary(role) {
  const parts = [
    ['missing', readArray(role.missingSections)],
    ['unknown', readArray(role.unknownSections)],
    ['duplicate', readArray(role.duplicateSections)],
    ['invalid scope', readArray(role.invalidViewScopeSections)],
    ['capability without view', readArray(role.capabilityWithoutViewSections)],
    ['restricted capability', readArray(role.restrictedCapabilitySections)],
  ]
    .filter(([, values]) => values.length > 0)
    .map(([label, values]) => `${label}: ${values.slice(0, 4).join(', ')}${values.length > 4 ? ` (+${values.length - 4})` : ''}`);

  return parts.length > 0 ? parts.join('; ') : 'unspecified issue';
}

function readArray(value) {
  return Array.isArray(value)
    ? value.filter((item) => typeof item === 'string' && item.trim().length > 0)
    : [];
}

function escapeTableCell(value) {
  return `${value ?? ''}`.replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}

function readContentDispositionFileName(value) {
  if (!value) {
    return null;
  }

  const utf8Match = /filename\*=UTF-8''([^;]+)/i.exec(value);
  if (utf8Match) {
    return decodeURIComponent(utf8Match[1].trim().replace(/^"|"$/g, ''));
  }

  const asciiMatch = /filename="?([^";]+)"?/i.exec(value);
  return asciiMatch ? asciiMatch[1].trim() : null;
}

function sanitizeFileName(value) {
  return `${value || 'role-permissions-audit.csv'}`
    .replace(/[<>:"/\\|?*\x00-\x1F]/g, '-')
    .replace(/^-+/, '')
    .slice(0, 160) || 'role-permissions-audit.csv';
}

function fail(message) {
  console.error(message);
  process.exit(2);
}
