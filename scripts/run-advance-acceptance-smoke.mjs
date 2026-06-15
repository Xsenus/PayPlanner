import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, copyFileSync, openSync, closeSync, rmSync, readFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const root = process.cwd();
const keepTemp = process.argv.includes('--keep-temp');
const artifactDir = path.resolve(
  root,
  readArgValue('--artifact-dir') ?? process.env.PAYPLANNER_ADVANCE_ACCEPTANCE_ARTIFACT_DIR ?? 'artifacts/advance-acceptance',
);
const checks = [];
const tempDir = mkdtempSync(path.join(tmpdir(), 'payplanner-advance-smoke-'));
const tempDb = path.join(tempDir, 'payplanner.db');
const port = 5300 + Math.floor(Math.random() * 300);
const baseUrl = `http://127.0.0.1:${port}`;
const backendLog = path.join(tempDir, 'backend.out.log');
const backendErr = path.join(tempDir, 'backend.err.log');

function readArgValue(name) {
  const prefix = `${name}=`;
  const value = process.argv.find((arg) => arg.startsWith(prefix));
  return value ? value.slice(prefix.length) : null;
}

function run(command, args, options = {}) {
  console.log(`\n> ${command} ${args.join(' ')}`);
  const startedAt = Date.now();
  let executable = command;
  let finalArgs = args;
  if (command === 'npx' && args[0] === 'vitest') {
    executable = path.join(root, 'node_modules', '.bin', process.platform === 'win32' ? 'vitest.cmd' : 'vitest');
    finalArgs = args.slice(1);
  }

  const result = spawnSync(executable, finalArgs, {
    cwd: root,
    stdio: 'inherit',
    shell: process.platform === 'win32' && executable.endsWith('.cmd'),
    ...options,
  });

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    throw new Error(`${command} exited with code ${result.status}`);
  }

  return {
    command,
    args,
    exitCode: result.status,
    durationMs: Date.now() - startedAt,
  };
}

async function fetchJson(url, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), options.timeoutMs ?? 20_000);
  try {
    const response = await fetch(url, {
      ...options,
      signal: controller.signal,
    });
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`${url} returned ${response.status}: ${text}`);
    }
    return text ? JSON.parse(text) : null;
  } finally {
    clearTimeout(timeout);
  }
}

async function fetchStatus(url, headers) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 20_000);
  try {
    const response = await fetch(url, {
      headers,
      signal: controller.signal,
    });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(`${url} returned ${response.status}: ${text}`);
    }
    return response.status;
  } finally {
    clearTimeout(timeout);
  }
}

function assertCondition(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function assertMoneyClose(actual, expected, message) {
  if (Math.abs(Number(actual) - Number(expected)) > 0.01) {
    throw new Error(`${message}. Expected ${expected}, got ${actual}`);
  }
}

function hasBalanceTransaction(transactions, predicate) {
  return Array.isArray(transactions) && transactions.some((transaction) => {
    try {
      return predicate(transaction);
    } catch {
      return false;
    }
  });
}

async function waitForApi() {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${baseUrl}/api/health`);
      if (response.ok) {
        return;
      }
    } catch {
      // Try again until the backend finishes startup.
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error('Backend did not become ready in 60 seconds');
}

async function stopProcessTree(child) {
  if (!child || child.killed) {
    return;
  }

  if (process.platform === 'win32' && child.pid) {
    spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], {
      stdio: 'ignore',
      shell: false,
    });
  } else {
    child.kill('SIGTERM');
  }

  await new Promise((resolve) => {
    const timeout = setTimeout(resolve, 3_000);
    child.once('exit', () => {
      clearTimeout(timeout);
      resolve();
    });
  });
}

async function removeTempDir() {
  for (let attempt = 1; attempt <= 5; attempt += 1) {
    try {
      rmSync(tempDir, { recursive: true, force: true });
      return;
    } catch (error) {
      if (attempt === 5) {
        throw error;
      }
      await new Promise((resolve) => setTimeout(resolve, attempt * 500));
    }
  }
}

function assertBackendLogsClean() {
  const patterns = [
    /^\s*(crit|critical|fail):/i,
    /\bLogLevel\.(Critical|Error)\b/i,
    /\bUnhandled exception\b/i,
    /\bSystem\.[A-Za-z.]*Exception\b/i,
  ];

  for (const file of [backendLog, backendErr]) {
    if (!existsSync(file)) {
      continue;
    }

    const content = readFileSync(file, 'utf8');
    const hit = content
      .split(/\r?\n/)
      .find((line) => patterns.some((pattern) => pattern.test(line)));

    if (hit) {
      throw new Error(`Backend smoke log contains an error marker in ${path.basename(file)}: ${hit.trim().slice(0, 240)}`);
    }
  }
}

function copyLocalDatabase() {
  const sourceDb = path.join(root, 'payplanner.db');
  if (!existsSync(sourceDb)) {
    throw new Error(`Local SQLite database not found: ${sourceDb}`);
  }

  copyFileSync(sourceDb, tempDb);
  for (const suffix of ['-wal', '-shm']) {
    const source = `${sourceDb}${suffix}`;
    if (existsSync(source)) {
      copyFileSync(source, `${tempDb}${suffix}`);
    }
  }
}

async function runApiSmoke() {
  const startedAt = Date.now();
  copyLocalDatabase();

  const out = openSync(backendLog, 'w');
  const err = openSync(backendErr, 'w');
  const backend = spawn(
    'dotnet',
    ['run', '--no-build', '--no-launch-profile', '--project', 'backend/PayPlanner.Api.csproj', '--urls', baseUrl],
    {
      cwd: root,
      shell: false,
      detached: false,
      stdio: ['ignore', out, err],
      env: {
        ...process.env,
        PAYPLANNER_DB_PROVIDER: 'sqlite',
        ASPNETCORE_URLS: baseUrl,
        ConnectionStrings__Default: `Data Source=${tempDb}`,
        Jwt__Secret: 'local-smoke-test-secret-for-payplanner-1234567890',
        Logging__LogLevel__Microsoft: 'Warning',
        Logging__LogLevel__Default: 'Warning',
      },
    },
  );

  try {
    await waitForApi();
    const login = await fetchJson(`${baseUrl}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        email: 'admin@payplanner.local',
        password: 'ChangeMeAdmin#12345',
      }),
    });
    const token = login?.token ?? login?.accessToken;
    if (!token) {
      throw new Error('Login response does not contain a token');
    }

    const headers = { Authorization: `Bearer ${token}` };
    const clients = await fetchJson(`${baseUrl}/api/v2/clients?page=1&pageSize=1`, { headers });
    const smokeClient = clients?.items?.[0];
    assertCondition(smokeClient?.id, 'Smoke cannot create an advance payment because no client exists in the local database copy');

    const smokeStamp = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14);
    const smokeAmount = 1234.56;
    const smokeDate = '2026-06-15T09:00:00.000Z';
    const balanceBefore = await fetchJson(`${baseUrl}/api/clients/${smokeClient.id}/balance`, { headers });
    const createdPayment = await fetchJson(`${baseUrl}/api/v2/payments`, {
      method: 'POST',
      headers: {
        ...headers,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        amount: smokeAmount,
        paidAmount: smokeAmount,
        clientId: smokeClient.id,
        counterpartyName: smokeClient.name ?? smokeClient.company ?? 'Advance smoke client',
        date: smokeDate,
        plannedDate: smokeDate,
        description: `Advance acceptance smoke ${smokeStamp}`,
        notes: 'Created by npm run test:advance-acceptance on a temporary SQLite copy',
        isPaid: true,
        paidDate: smokeDate,
        lastPaymentDate: smokeDate,
        status: 'Completed',
        type: 'Income',
        documentKind: 'Payment',
        systemNotes: '',
        rescheduleCount: 0,
      }),
    });
    assertCondition(createdPayment?.id, 'Smoke payment was not created');
    assertCondition(
      createdPayment.advanceReviewStatus === 'RequiresDecision',
      `Expected created smoke payment to require advance decision, got ${createdPayment.advanceReviewStatus}`,
    );

    const acceptedPayment = await fetchJson(`${baseUrl}/api/payments/${createdPayment.id}/advance-review/accept`, {
      method: 'POST',
      headers: {
        ...headers,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ reason: 'Acceptance smoke: money accepted as an unallocated advance' }),
    });
    assertCondition(
      acceptedPayment?.advanceReviewStatus === 'AcceptedAsAdvance',
      `Expected accepted smoke payment to be AcceptedAsAdvance, got ${acceptedPayment?.advanceReviewStatus}`,
    );

    const balance = await fetchJson(`${baseUrl}/api/clients/${smokeClient.id}/balance`, { headers });
    assertMoneyClose(
      Number(balance?.balance ?? 0),
      Number(balanceBefore?.balance ?? 0) + smokeAmount,
      'Expected client balance to increase after accepting smoke advance',
    );

    const targetDebt = await fetchJson(`${baseUrl}/api/v2/payments`, {
      method: 'POST',
      headers: {
        ...headers,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        amount: smokeAmount,
        paidAmount: 0,
        clientId: smokeClient.id,
        counterpartyName: smokeClient.name ?? smokeClient.company ?? 'Advance smoke client',
        date: '2026-06-16T09:00:00.000Z',
        plannedDate: '2026-06-16T09:00:00.000Z',
        description: `Advance allocation smoke debt ${smokeStamp}`,
        notes: 'Created by npm run test:advance-acceptance on a temporary SQLite copy',
        isPaid: false,
        status: 'Pending',
        type: 'Income',
        documentKind: 'Invoice',
        systemNotes: '',
        rescheduleCount: 0,
      }),
    });
    assertCondition(targetDebt?.id, 'Smoke target debt payment was not created');

    await fetchJson(`${baseUrl}/api/clients/${smokeClient.id}/balance/apply`, {
      method: 'POST',
      headers: {
        ...headers,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        paymentId: targetDebt.id,
        amount: smokeAmount,
        sourcePaymentId: createdPayment.id,
        description: 'Acceptance smoke: apply unallocated advance to target debt',
      }),
    });

    const allocatedSource = await fetchJson(`${baseUrl}/api/v2/payments/${createdPayment.id}`, { headers });
    const completedTarget = await fetchJson(`${baseUrl}/api/v2/payments/${targetDebt.id}`, { headers });
    const balanceAfterApply = await fetchJson(`${baseUrl}/api/clients/${smokeClient.id}/balance`, { headers });
    const balanceTransactions = await fetchJson(`${baseUrl}/api/clients/${smokeClient.id}/balance/transactions`, { headers });

    assertCondition(
      allocatedSource?.advanceReviewStatus === 'Allocated',
      `Expected source advance to be Allocated, got ${allocatedSource?.advanceReviewStatus}`,
    );
    assertCondition(
      completedTarget?.status === 'Completed',
      `Expected target debt to be Completed, got ${completedTarget?.status}`,
    );
    assertMoneyClose(completedTarget?.paidAmount ?? 0, smokeAmount, 'Expected target debt paid amount to equal applied advance');
    assertMoneyClose(
      balanceAfterApply?.balance ?? 0,
      balanceBefore?.balance ?? 0,
      'Expected client balance to return to its initial value after applying smoke advance',
    );
    assertCondition(
      hasBalanceTransaction(
        balanceTransactions,
        (transaction) =>
          transaction.paymentId === createdPayment.id &&
          transaction.type === 'UnallocatedAdvance' &&
          transaction.direction === 'Credit' &&
          Math.abs(Number(transaction.amount) - smokeAmount) <= 0.01,
      ),
      'Expected an UnallocatedAdvance credit transaction for the accepted smoke advance',
    );
    assertCondition(
      hasBalanceTransaction(
        balanceTransactions,
        (transaction) =>
          transaction.paymentId === targetDebt.id &&
          transaction.type === 'CreditApplied' &&
          transaction.direction === 'Debit' &&
          typeof transaction.externalKey === 'string' &&
          transaction.externalKey.startsWith(`payment-unallocated-advance-apply:${createdPayment.id}:`) &&
          Math.abs(Number(transaction.amount) - smokeAmount) <= 0.01,
      ),
      'Expected a CreditApplied debit transaction linked to the accepted smoke advance',
    );

    const diagnosticsQuery = '\u0412\u0438\u043d\u043e\u043a\u0443\u0440\u043e\u0432';
    const diagnostics = await fetchJson(
      `${baseUrl}/api/payments/advance-review/diagnostics?q=${encodeURIComponent(diagnosticsQuery)}&from=2026-03-02&to=2026-03-02&amount=7100&amountTolerance=0&limit=10`,
      { headers },
    );
    const candidates = await fetchJson(`${baseUrl}/api/payments/advance-review/candidates?limit=5`, { headers });
    const pending = await fetchJson(`${baseUrl}/api/v2/payments?advanceReviewStatus=RequiresDecision&page=1&pageSize=1`, { headers });
    const reportStatus = await fetchStatus(`${baseUrl}/api/reports/advances?from=2026-01-01&to=2026-12-31`, headers);
    const releaseStatus = await fetchStatus(`${baseUrl}/api/app-version/latest`, headers);

    const result = {
      createdAdvancePaymentId: createdPayment.id,
      targetDebtPaymentId: targetDebt.id,
      acceptedAdvanceStatus: acceptedPayment.advanceReviewStatus,
      allocatedAdvanceStatus: allocatedSource.advanceReviewStatus,
      targetDebtStatus: completedTarget.status,
      clientBalanceBefore: balanceBefore?.balance ?? null,
      clientBalanceAfterAccept: balance?.balance ?? null,
      clientBalanceAfterApply: balanceAfterApply?.balance ?? null,
      balanceTransactionsChecked: true,
      diagnosticsTotal: diagnostics?.total ?? null,
      candidatesTotal: candidates?.total ?? null,
      requiresDecisionTotal: pending?.total ?? null,
      advancesReportStatus: reportStatus,
      latestReleaseStatus: releaseStatus,
      durationMs: Date.now() - startedAt,
    };

    console.log('\nAdvance API smoke result:');
    console.log(JSON.stringify(result, null, 2));

    return result;
  } finally {
    await stopProcessTree(backend);
    closeSync(out);
    closeSync(err);
  }
}

function writeAcceptanceReport(apiSmokeResult) {
  const generatedAt = new Date();
  const stamp = generatedAt.toISOString().replace(/[:.]/g, '-');
  const reportPath = path.join(artifactDir, `advance-acceptance-${stamp}.json`);
  const report = {
    generatedAtUtc: generatedAt.toISOString(),
    sourceDatabase: path.join(root, 'payplanner.db'),
    databaseMode: 'temporary-sqlite-copy',
    temporaryDatabase: keepTemp ? tempDb : null,
    checks,
    apiSmoke: {
      ...apiSmokeResult,
      backendLogsChecked: true,
    },
  };

  mkdirSync(artifactDir, { recursive: true });
  writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
  console.log(`Advance acceptance report written: ${reportPath}`);
}

try {
  checks.push(run('dotnet', [
    'test',
    'backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj',
    '--filter',
    'FullyQualifiedName~PaymentsControllerTests.AdvanceReviewAcceptanceFlow_CreatesAcceptsAndAppliesUnallocatedAdvance|FullyQualifiedName~ReportsControllerTests.Advances_GroupsAdvanceFactOperationsByCounterpartyAndRecognition',
    '--no-build',
  ]));

  checks.push(run('npx', [
    'vitest',
    'run',
    'src/components/Payments/Payments.integration.test.tsx',
    'src/components/Money/MoneyOperations.integration.test.tsx',
    'src/components/Clients/ClientReconciliationAct.test.tsx',
    'src/services/api.test.ts',
    '--no-file-parallelism',
  ]));

  const apiSmokeResult = await runApiSmoke();
  assertBackendLogsClean();
  writeAcceptanceReport(apiSmokeResult);
  console.log('\nAdvance acceptance smoke passed.');
} finally {
  if (keepTemp) {
    console.log(`Temporary files kept at: ${tempDir}`);
  } else {
    await removeTempDir();
  }
}
