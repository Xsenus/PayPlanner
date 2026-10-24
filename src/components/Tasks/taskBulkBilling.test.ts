import { describe, expect, it } from 'vitest';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { BILLING_REASONS, billingPayload, billingPreview, checkedBillingResult, freezeBillingSelection } from './taskBulkBilling';
import nativeWireJson from './fixtures/task-bulk-billing-native-wire.json?raw';

const task = (id: number, extra: Partial<TaskJournalItem> = {}) => ({ id, title: `Задача ${id}`, status: 'new', updatedAt: '2026-10-24T01:02:03.1234567Z', ...extra }) as TaskJournalItem;
const success = () => ({ requested: 2, updated: 1, unchanged: 0, skipped: 0, conflicted: 1, items: [
  { id: 1, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' },
  { id: 2, outcome: 'conflict', reason: 'versionConflict', updatedAt: null },
] });

describe('fixed bulk billing contract', () => {
  it('preserves seven fractional digits, explicit null, order, unique IDs and sends only billing fields', () => {
    expect(billingPayload([task(2), task(1, { updatedAt: undefined }), task(2)], 'billable', false)).toEqual({
      ids: [2, 1], billingClassification: 'billable', includeCompleted: false,
      expectedUpdatedAtById: { '2': '2026-10-24T01:02:03.1234567Z', '1': null },
    });
  });
  it('does not truncate raw over-limit selections or accept invalid IDs', () => {
    for (const tasks of [[], [task(0)], [task(-1)], [task(1.2)], Array.from({ length: 501 }, () => task(1))]) {
      expect(() => freezeBillingSelection(tasks, 'scope', 'page')).toThrow();
    }
    expect(freezeBillingSelection(Array.from({ length: 500 }, (_, i) => task(i + 1)), 'scope', 'page').tasks).toHaveLength(500);
  });
  it('copies rows so later selection changes cannot alter a confirmed snapshot', () => {
    const rows = [task(1)]; const snapshot = freezeBillingSelection(rows, 'scope', 'page');
    Object.assign(rows[0], { updatedAt: null }); rows.push(task(2));
    expect(snapshot.tasks).toHaveLength(1); expect(snapshot.tasks[0].updatedAt).toBe('2026-10-24T01:02:03.1234567Z');
  });
  it('validates the exact response partition and never accepts a legacy bulk response as billing success', () => {
    expect(checkedBillingResult(success(), [1, 2]).conflicted).toBe(1);
    expect(() => checkedBillingResult({ requested: 2, updated: 2, skipped: 0 }, [1, 2])).toThrow();
    for (const change of [
      { requested: 1 }, { updated: 2 }, { skipped: -1 }, { conflicted: 0 },
      { items: [success().items[0], success().items[0]] },
      { items: [success().items[0], { ...success().items[1], id: 3 }] },
      { items: [success().items[0], { ...success().items[1], reason: 'privateInvoiceDetail' }] },
      { items: [success().items[0], { ...success().items[1], updatedAt: undefined }] },
      { items: [{ ...success().items[0], reason: 'forbidden' }, success().items[1]] },
      { items: [success().items[0], { ...success().items[1], reason: 'forbidden' }] },
      { updated: 0, skipped: 1, items: [{ ...success().items[0], outcome: 'skipped', reason: 'notAvailable' }, success().items[1]] },
    ]) expect(() => checkedBillingResult({ ...success(), ...change }, [1, 2])).toThrow();
  });
  it('normalises the real omitted-null JSON properties without mutating the raw response', () => {
    const raw = Object.freeze({ requested: 4, updated: 1, unchanged: 1, skipped: 1, conflicted: 1, items: Object.freeze([
      Object.freeze({ id: 1, outcome: 'updated', updatedAt: '2026-10-24T02:03:04.7654321Z' }),
      Object.freeze({ id: 2, outcome: 'unchanged' }),
      Object.freeze({ id: 3, outcome: 'skipped', reason: 'notAvailable' }),
      Object.freeze({ id: 4, outcome: 'conflict', reason: 'versionConflict' }),
    ]) });
    const normalised = checkedBillingResult(raw, [1, 2, 3, 4]);
    expect(normalised).not.toBe(raw); expect(normalised.items).not.toBe(raw.items);
    expect(normalised.items).toEqual([
      { id: 1, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' },
      { id: 2, outcome: 'unchanged', reason: null, updatedAt: null },
      { id: 3, outcome: 'skipped', reason: 'notAvailable', updatedAt: null },
      { id: 4, outcome: 'conflict', reason: 'versionConflict', updatedAt: null },
    ]);
    expect(Object.hasOwn(raw.items[0], 'reason')).toBe(false);
    expect(Object.hasOwn(raw.items[1], 'updatedAt')).toBe(false);
  });
  it('accepts exact actual DTO wire bytes from .NET 9 MVC JSON with nullable omissions', () => {
    // Native TaskBulkBillingResultDto + actual Mvc.JsonOptions, .NET 9.0.21; SHA-256 8347b522914e8411499ab744d69365699301c2dcb8956a1ac3b61637d2e88fcb.
    const raw = JSON.parse(nativeWireJson);
    expect(raw.items[0]).toEqual({ id: 1, outcome: 'updated', updatedAt: '2026-10-24T02:03:04.7654321Z' });
    expect(raw.items[1]).toEqual({ id: 2, outcome: 'unchanged' });
    expect(raw.items[2]).toEqual({ id: 3, outcome: 'skipped', reason: 'notAvailable' });
    expect(raw.items[3]).toEqual({ id: 4, outcome: 'conflict', reason: 'versionConflict' });
    expect(checkedBillingResult(raw, [1, 2, 3, 4])).toEqual({ requested: 4, updated: 1, unchanged: 1, skipped: 1, conflicted: 1, items: [
      { id: 1, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' },
      { id: 2, outcome: 'unchanged', reason: null, updatedAt: null },
      { id: 3, outcome: 'skipped', reason: 'notAvailable', updatedAt: null },
      { id: 4, outcome: 'conflict', reason: 'versionConflict', updatedAt: null },
    ] });
    expect(JSON.stringify(raw)).toBe(nativeWireJson);
  });
  it.each(['unchanged', 'skipped', 'conflict'] as const)('accepts explicit legacy null and omission for a non-updated %s token', (outcome) => {
    const counters = { requested: 1, updated: 0, unchanged: outcome === 'unchanged' ? 1 : 0, skipped: outcome === 'skipped' ? 1 : 0, conflicted: outcome === 'conflict' ? 1 : 0 };
    const row = { id: 1, outcome, reason: outcome === 'unchanged' ? null : outcome === 'skipped' ? 'forbidden' : 'versionConflict' };
    for (const item of [row, { ...row, updatedAt: null }]) {
      expect(checkedBillingResult({ ...counters, items: [item] }, [1]).items[0]).toEqual({ ...row, updatedAt: null });
    }
    expect(() => checkedBillingResult({ ...counters, items: [{ ...row, updatedAt: undefined }] }, [1])).toThrow();
  });
  it.each(['2026-10-24T02:03:04Z', '2026-10-24T02:03:04.0000000Z', '2026-10-24T02:03:04.7654321Z'])('preserves a valid updated UTC token byte for byte: %s', (updatedAt) => {
    const item = { id: 1, outcome: 'updated', updatedAt };
    const raw = { requested: 1, updated: 1, unchanged: 0, skipped: 0, conflicted: 0, items: [item] };
    expect(checkedBillingResult(raw, [1]).items[0]).toEqual({ ...item, reason: null });
    expect(checkedBillingResult({ ...raw, items: [{ ...item, reason: null }] }, [1]).items[0].updatedAt).toBe(updatedAt);
  });
  it.each([null, undefined, '', 'not-a-date', '2026-02-30T02:03:04Z', '0000-10-24T02:03:04Z',
    '2026-10-24T02:03:04', '2026-10-24T02:03:04+00:00', '2026-10-24T02:03:04.12345678Z'])('rejects an invalid explicit updated token: %s', (updatedAt) => {
    expect(() => checkedBillingResult({ requested: 1, updated: 1, unchanged: 0, skipped: 0, conflicted: 0,
      items: [{ id: 1, outcome: 'updated', updatedAt }] }, [1])).toThrow();
  });
  it('rejects missing updated tokens, non-JSON undefined, missing failure reasons and malformed response partitions', () => {
    const raw = { requested: 1, updated: 1, unchanged: 0, skipped: 0, conflicted: 0 };
    for (const item of [{ id: 1, outcome: 'updated' }, { id: 1, outcome: 'updated', reason: undefined, updatedAt: '2026-10-24T02:03:04Z' },
      { id: 1, outcome: 'unchanged', reason: undefined }, { id: 1, outcome: 'skipped' }, { id: 1, outcome: 'conflict' },
      { id: 1, outcome: 'skipped', reason: '' }, { id: 1, outcome: 'skipped', reason: null },
      { id: 1, outcome: 'conflict', reason: 'forbidden' }, { id: 1, outcome: 'skipped', reason: 'versionConflict' },
      { id: 1, outcome: 'updated', reason: 'unknown', updatedAt: '2026-10-24T02:03:04Z' },
      { id: 1, outcome: 'unknown', reason: null, updatedAt: '2026-10-24T02:03:04Z' }]) {
      const counters = { requested: 1, updated: Number(item.outcome === 'updated'), unchanged: Number(item.outcome === 'unchanged'),
        skipped: Number(item.outcome === 'skipped'), conflicted: Number(item.outcome === 'conflict') };
      expect(() => checkedBillingResult({ ...counters, items: [item] }, [1])).toThrow();
    }
    for (const change of [{ requested: 2 }, { unchanged: 1 }, { updated: -1 }, { items: [] },
      { items: [{ id: 2, outcome: 'updated', updatedAt: '2026-10-24T02:03:04Z' }] }]) {
      expect(() => checkedBillingResult({ ...raw, items: [{ id: 1, outcome: 'updated', updatedAt: '2026-10-24T02:03:04Z' }], ...change }, [1])).toThrow();
    }
  });
  it.each([
    ['array outcome', { outcome: ['skipped'], reason: 'forbidden' }],
    ['array reason', { outcome: 'skipped', reason: ['forbidden'] }],
  ] as const)('rejects valid JSON with a coercible %s instead of a contract string', (_label, fields) => {
    const raw = JSON.parse(JSON.stringify({ requested: 1, updated: 0, unchanged: 0, skipped: 1, conflicted: 0,
      items: [{ id: 1, ...fields }] }));
    expect(() => checkedBillingResult(raw, [1])).toThrow();
  });
  it('previews all protected relationships before completion and never uses a display label instead of invoice FK', () => {
    expect(billingPreview(task(1, { billingInvoiceId: 9, billingStatus: 'internal' }), 'internal', true)).toContain('счёт');
    expect(billingPreview(task(1, { contractorPaymentId: 8 }), 'internal', true)).toBe(BILLING_REASONS.contractorPaymentProtected);
    expect(billingPreview(task(1, { billingMode: 'Fixed' }), 'internal', true)).toContain('Договорной');
    expect(billingPreview(task(1, { status: 'done' }), 'internal', false)).toContain('Завершённая');
    expect(billingPreview(task(1, { status: 'cancelled' }), 'internal', true)).toContain('Отменённая');
    expect(billingPreview(task(1), 'billable', true)).toContain('клиент и дело');
    expect(billingPreview(task(1, { status: 'done' }), 'internal', true)).toContain('решение принимает сервер');
  });
  it.each(['billable', 'internal'] as const)('protects a completed result-triggered payout before any %s no-op assessment', (target) => {
    const row = task(1, { status: 'done', clientId: 12, clientCaseId: 15, clientBillingClassification: target,
      contractorPaymentEnabled: true, contractorPaymentOnResult: true, contractorPaymentId: null, contractorPaymentStatus: '',
      contractorPaymentResultDelayDays: 7 });
    expect(billingPreview(row, target, true)).toBe(BILLING_REASONS.contractorPaymentProtected);
    expect(row.updatedAt).toBe('2026-10-24T01:02:03.1234567Z');
  });
  it.each(['billable', 'internal'] as const)('does not protect an unfinished result-triggered payout from %s preview', (target) => {
    expect(billingPreview(task(1, { status: 'inProgress', clientId: 12, clientCaseId: 15, contractorPaymentEnabled: true,
      contractorPaymentOnResult: true, contractorPaymentId: null, contractorPaymentStatus: '', contractorPaymentResultDelayDays: 7 }), target, true))
      .toBe('Предварительно подходит; окончательное решение принимает сервер.');
  });
  it.each(['billable', 'internal'] as const)('does not protect a completed fixed-date payout with no payment from %s preview', (target) => {
    expect(billingPreview(task(1, { status: 'done', clientId: 12, clientCaseId: 15, contractorPaymentEnabled: true,
      contractorPaymentOnResult: false, contractorPaymentDueDate: '2026-10-31', contractorPaymentId: null, contractorPaymentStatus: '' }), target, true))
      .toBe('Предварительно подходит; окончательное решение принимает сервер.');
  });
});
