// @vitest-environment node
import { spawn } from 'node:child_process';
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { assertStageCanResume, parseArguments, pool, selectPendingShards, workerCount } from './run-backend-gate.mjs';

const gib = 1024 ** 3;
const states = () => Array.from({ length: 4 }, (_, shard) => ({ shard, status: 'pending', exitCode: undefined as number | undefined, pid: undefined as number | undefined }));

describe('local backend gate resource preflight', () => {
  it('selects bounded concurrency only when both RAM and CPU satisfy the resource budget', () => {
    expect(workerCount('auto', 8 * gib, 4)).toBe(2);
    expect(workerCount('auto', 8 * gib - 1, 4)).toBe(1);
    expect(workerCount('auto', 20 * gib, 3)).toBe(1);
    expect(workerCount('1', 20 * gib, 32)).toBe(1);
    expect(workerCount('2', 8 * gib, 4)).toBe(2);
    expect(workerCount('auto', 12 * gib, 8)).toBe(4);
    expect(workerCount('auto', 12 * gib - 1, 8)).toBe(2);
    expect(workerCount('auto', 20 * gib, 7)).toBe(2);
    expect(workerCount('4', 12 * gib, 8)).toBe(4);
    expect(workerCount('2', 20 * gib, 16)).toBe(2);
    expect(workerCount('auto', 4 * gib, 1)).toBe(1);
  });

  it('fails early on insufficient RAM and does not let a numeric requested count bypass the limit', () => {
    expect(() => workerCount('1', 4 * gib - 1, 8)).toThrow(/RAM/);
    expect(() => workerCount('2', 7 * gib, 8)).toThrow();
    expect(() => workerCount(2, 7 * gib, 8)).toThrow();
    expect(() => workerCount('2', 20 * gib, 2)).toThrow();
    expect(() => workerCount('3', 20 * gib, 8)).toThrow();
    expect(() => workerCount('4', 11 * gib, 8)).toThrow();
    expect(() => workerCount('4', 20 * gib, 7)).toThrow();
  });

  it.each([Number.NaN, Number.POSITIVE_INFINITY, -1])('rejects unknown or impossible resource measurements (%s)', free => {
    expect(() => workerCount('auto', free, 6)).toThrow();
  });

  it('allows one worker under load while auto still admits parallel work only above the ordinary resource thresholds', () => {
    expect(workerCount('1', gib, 6, true)).toBe(1);
    expect(workerCount('auto', gib, 6, true)).toBe(1);
    expect(workerCount('auto', 8 * gib - 1, 8, true)).toBe(1);
    expect(workerCount('auto', 8 * gib, 8, true)).toBe(2);
    expect(workerCount('auto', 12 * gib, 8, true)).toBe(4);
    expect(() => workerCount('2', 20 * gib, 6, true)).toThrow(/resource-controlled auto/);
    expect(() => workerCount('1', Number.NaN, 6, true)).toThrow();
  });
});

describe('strict full-gate CLI', () => {
  it.each(['docs/quality-gates.md', './docs/quality-gates.md', 'docs/../README.md', 'DOCS/QUALITY-GATES.MD'])('refuses to exclude a tested document, including path aliases: %s', path => {
    expect(() => parseArguments(['--results', 'E:/gate', '--untested-doc', path])).toThrow(/tested document/);
  });

  it('allows a preparation-only stage without claiming that tests passed', () => {
    expect(parseArguments(['--results', 'E:/gate', '--prepare-only']).prepareOnly).toBe(true);
  });
  it('requires either sequential or resource-controlled auto execution for a memory-pressure override', () => {
    expect(parseArguments(['--results', 'E:/gate', '--workers', '1', '--allow-memory-pressure']).allowMemoryPressure).toBe(true);
    expect(parseArguments(['--results', 'E:/gate', '--allow-memory-pressure']).workers).toBe('auto');
    expect(() => parseArguments(['--results', 'E:/gate', '--resume', '--workers', '2', '--allow-memory-pressure'])).toThrow(/workers 1 or auto/);
    expect(() => parseArguments(['--results', 'E:/gate', '--workers', '4', '--allow-memory-pressure'])).toThrow(/workers 1 or auto/);
  });
  it('requires an explicit durable results directory and keeps the defaults bounded', () => {
    expect(parseArguments(['--results', 'E:/ProjectTemp/PayPlanner/gate'])).toEqual({
      results: 'E:/ProjectTemp/PayPlanner/gate', configuration: 'Release', workers: 'auto', suite: 'all', resume: false, detach: false, untestedDocs: [],
    });
    expect(parseArguments(['--resume', '--detach', '--configuration', 'Release', '--workers', '2', '--results', 'E:/gate'])).toEqual({
      results: 'E:/gate', configuration: 'Release', workers: '2', suite: 'all', resume: true, detach: true, untestedDocs: [],
    });
  });

  it('makes partial suites explicit and accepts a qualified timing profile without changing the full default', () => {
    const options = parseArguments(['--results', 'E:/gate', '--suite', 'fast', '--workers', '4', '--timings', 'E:/prior/timings.json']);
    expect(options.suite).toBe('fast');
    expect(options.workers).toBe('4');
    expect(options.timings).toBe('E:/prior/timings.json');
    expect(parseArguments(['--results', 'E:/gate', '--suite', 'heavy']).suite).toBe('heavy');
    expect(() => parseArguments(['--results', 'E:/gate', '--suite', 'tiny'])).toThrow(/Suite/);
    expect(() => parseArguments(['--results', 'E:/gate', '--suite'])).toThrow();
    expect(() => parseArguments(['--results', 'E:/gate', '--timings'])).toThrow();
  });

  it.each([[], ['--results'], ['--results', 'x', '--configuration', 'Unknown'], ['--results', 'x', '--unknown'],
    ['--results', 'x', '--workers', '3'], ['--results', 'x', '--workers', 'invalid'], ['--results', 'x', '--workers']])('rejects invalid arguments before expensive work: %j', args => {
    expect(() => parseArguments(args)).toThrow();
  });

  it('rejects invalid detached CLI requests before creating logs or touching infrastructure', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'pp-gate-cli-contract-'));
    try {
      const child = spawn(process.execPath, [resolve('scripts/run-backend-gate.mjs'), '--results', directory, '--workers', '0', '--detach'],
        { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
      let stderr = '';
      child.stderr.on('data', (chunk: Buffer) => { stderr += chunk.toString(); });
      const exit = await new Promise<number | null>((accept, reject) => { child.once('error', reject); child.once('close', accept); });
      expect(exit).toBe(1);
      expect(stderr).toContain('Workers must be auto, 1, 2 or 4');
      expect(await readdir(directory)).toEqual([]);
    } finally { await rm(directory, { recursive: true, force: true }); }
  });
});

describe('interrupted shard selection', () => {
  it('blocks resume across the crash window between process creation and PID persistence', () => {
    const value = states();
    Object.assign(value[1], { launchPending: true });
    expect(() => selectPendingShards(value)).toThrow(/unrecorded launch outcome/);
    expect(() => assertStageCanResume({ activeLaunchPending: true, activePid: undefined })).toThrow(/unrecorded launch outcome/);
    expect(() => assertStageCanResume({ activeLaunchPending: false, activePid: process.pid })).toThrow(/still alive/);
    expect(() => assertStageCanResume({ activeLaunchPending: false, activePid: null })).not.toThrow();
  });

  it('reuses only successful terminal shards and selects failed or untouched ones', () => {
    const value = states();
    value[0] = { ...value[0], status: 'passed', exitCode: 0 };
    value[1] = { ...value[1], status: 'failed', exitCode: 1 };
    expect(selectPendingShards(value)).toEqual([1, 2, 3]);
    for (const exitCode of [1, -1, undefined]) {
      value[0].exitCode = exitCode;
      expect(() => selectPendingShards(value)).toThrow(/unsuccessful/);
    }
  });

  it('refuses to launch replacement work while an observed process remains alive', () => {
    const value = states();
    value[2] = { ...value[2], status: 'running', pid: process.pid };
    expect(() => selectPendingShards(value)).toThrow(/still running/);
  });

  it('permits retry only after the previous child process is observed terminal', async () => {
    const child = spawn(process.execPath, ['-e', 'process.exit(0)'], { windowsHide: true, stdio: 'ignore' });
    const pid = child.pid;
    expect(pid).toBeGreaterThan(0);
    await new Promise<void>((resolve, reject) => { child.once('error', reject); child.once('exit', () => resolve()); });
    const value = states();
    value[1] = { ...value[1], status: 'running', pid };
    expect(selectPendingShards(value)).toEqual([0, 1, 2, 3]);
  });

  it('rejects incomplete catalogs, duplicate shard identities and unknown state labels', () => {
    expect(() => selectPendingShards(states().slice(0, 3))).toThrow();
    expect(() => selectPendingShards(states(), 3)).toThrow();
    const duplicate = states(); duplicate[3].shard = 2;
    expect(() => selectPendingShards(duplicate)).toThrow();
    const unknown = states(); unknown[1].status = 'skipped';
    expect(() => selectPendingShards(unknown)).toThrow();
  });
});

describe('bounded shard worker pool', () => {
  it('preserves native child exit failures, drains siblings, and retries only the failed invocation', async () => {
    const value = states();
    const attempts = [0, 0, 0, 0];
    const invoke = async (index: number) => {
      attempts[index]++;
      const expectedExit = index === 1 && attempts[index] === 1 ? 7 : 0;
      const child = spawn(process.execPath, ['-e', `process.exit(${expectedExit})`], { windowsHide: true, stdio: 'ignore' });
      value[index].pid = child.pid;
      value[index].status = 'running';
      const exit = await new Promise<number | null>((accept, reject) => { child.once('error', reject); child.once('exit', accept); });
      value[index].exitCode = exit ?? -1;
      value[index].pid = undefined;
      value[index].status = exit === 0 ? 'passed' : 'failed';
      if (exit !== 0) throw new Error(`native shard exited ${exit}`);
    };
    await expect(pool(selectPendingShards(value), 2, invoke)).rejects.toThrow('shard invocation(s) failed');
    expect(attempts).toEqual([1, 1, 1, 1]);
    expect(value.map(item => item.exitCode)).toEqual([0, 7, 0, 0]);
    expect(selectPendingShards(value)).toEqual([1]);
    await pool(selectPendingShards(value), 2, invoke);
    expect(attempts).toEqual([1, 2, 1, 1]);
    expect(selectPendingShards(value)).toEqual([]);
  });

  it('drains every running sibling and remaining shard after a failure, preserving the complete outcome', async () => {
    const completions = Array.from({ length: 4 }, () => {
      let complete!: () => void;
      const ready = new Promise<void>(resolve => { complete = resolve; });
      return { ready, complete };
    });
    let active = 0, maximum = 0;
    const started: number[] = [];
    const finished: number[] = [];
    const failure = new Error('synthetic failed shard');
    const pending = pool([0, 1, 2, 3], 2, async (index: number) => {
      started.push(index); active++; maximum = Math.max(maximum, active);
      await completions[index].ready;
      active--; finished.push(index);
      if (index === 1) throw failure;
    });
    const outcome = pending.then(() => null, (error: AggregateError) => error);
    expect(started).toEqual([0, 1]);
    completions.forEach(value => value.complete());
    const error = await outcome;
    expect(maximum).toBe(2);
    expect(active).toBe(0);
    expect(started).toEqual([0, 1, 2, 3]);
    expect(finished.sort()).toEqual([0, 1, 2, 3]);
    expect(error).toBeInstanceOf(AggregateError);
    expect(error?.errors).toEqual([failure]);
  });

  it.each([0, -1, Number.NaN, 1.5])('rejects invalid worker counts instead of silently skipping all work (%s)', async count => {
    const invoked: number[] = [];
    await expect(pool([0, 1], count, async (value: number) => { invoked.push(value); })).rejects.toThrow();
    expect(invoked).toEqual([]);
  });
});
