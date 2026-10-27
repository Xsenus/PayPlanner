import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PaymentCreateRecovery } from './PaymentCreateRecovery';
import { apiService } from '../../services/api';
import { acknowledgePaymentCreate, bindPaymentCreateOwner, createPaymentGuarded, fencePaymentCreateAdoption, inspectPaymentCreate,
  openCommittedPaymentCreate, pendingPaymentCreates, type PaymentAttemptState, type PaymentCreatePorts } from '../../services/paymentCreateAttempts';
import type { Payment, PaymentPayload } from '../../types';

vi.mock('../../services/api', () => ({ apiService: { inspectPaymentCreate: vi.fn(), openPaymentCreate: vi.fn(), acknowledgePaymentCreate: vi.fn(), fencePaymentCreateAdoption: vi.fn() } }));
const id = '11111111-1111-4111-8111-111111111111';
const payload: PaymentPayload = { date: '2026-10-26', plannedDate: '2026-10-26', amount: 10, paidAmount: 0,
  type: 'Income', status: 'Pending', description: 'original', isPaid: false, notes: '', systemNotes: '', rescheduleCount: 0 };
const saved: Payment = { ...payload, id: 81, outstandingAmount: 10, hasPartialPayment: false, createdAt: '2026-10-26T00:00:00Z' };
const draft: PaymentAttemptState = { attemptId: id, state: 'Draft', operation: 'primary', sellerCompanyId: null,
  sellerBranchId: null, primaryPaymentId: null, createdAtUtc: '2026-10-26T00:00:00Z', expiresAtUtc: '2026-10-27T00:00:00Z',
  canonicalPaymentId: null, identityVersion: 'payment-create-v1' };
let io: PaymentCreatePorts;
function login(actor: number, epoch: string) {
  localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', epoch); bindPaymentCreateOwner(actor, '/api');
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r; }); return { promise, resolve }; }
beforeEach(async () => {
  localStorage.clear(); login(1, 'A');
  vi.stubGlobal('navigator', { locks: { request: async (_name: string, _options: LockOptions,
    callback: (lease: Lock | null) => Promise<unknown>) => callback({ name: 'owned', mode: 'exclusive' } as Lock) } });
  io = { register: vi.fn().mockResolvedValue(draft), commit: vi.fn().mockRejectedValue(new Error('lost ACK')),
    get: vi.fn().mockResolvedValue(draft), cancel: vi.fn().mockResolvedValue({ attemptId: id, state: 'Cancelled', canonicalPaymentId: null }),
    payment: vi.fn().mockResolvedValue(saved) };
  await expect(createPaymentGuarded(payload, undefined, io)).rejects.toThrow();
  vi.mocked(apiService.inspectPaymentCreate).mockImplementation((entry, cancel) => inspectPaymentCreate(entry, io, cancel));
  vi.mocked(apiService.openPaymentCreate).mockImplementation(entry => openCommittedPaymentCreate(entry, io));
  vi.mocked(apiService.acknowledgePaymentCreate).mockImplementation(acknowledgePaymentCreate);
  vi.mocked(apiService.fencePaymentCreateAdoption).mockImplementation(fencePaymentCreateAdoption);
});
afterEach(() => { vi.clearAllMocks(); bindPaymentCreateOwner(null, '/api'); localStorage.clear(); vi.unstubAllGlobals(); });

describe('payment recovery explicit UI boundaries', () => {
  it('keeps GET Draft blocked, and only authoritative Cancelled permits a new attempt', async () => {
    render(<PaymentCreateRecovery />);
    fireEvent.click(screen.getByRole('button', { name: 'Проверить сохранение' }));
    await screen.findByText('Сохранение не подтверждено. Для нового добавления сначала отмените эту попытку.');
    expect(pendingPaymentCreates()).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Отменить попытку' }));
    await screen.findByText('Незавершённая попытка отменена. Можно добавить платёж заново.');
    expect(pendingPaymentCreates()).toEqual([]); expect(io.commit).toHaveBeenCalledTimes(1);
  });
  it('retains the unresolved marker on Cancel 410 rather than showing a retry/new-create escape', async () => {
    vi.mocked(io.cancel).mockRejectedValue(Object.assign(new Error('HTTP 410'), { status: 410 }));
    render(<PaymentCreateRecovery />);
    fireEvent.click(screen.getByRole('button', { name: 'Отменить попытку' }));
    expect((await screen.findByRole('alert')).textContent).toContain('410');
    expect(pendingPaymentCreates()).toHaveLength(1);
    expect(screen.queryByText(/не нашли/i)).toBeNull(); expect(io.commit).toHaveBeenCalledTimes(1);
  });
  it('does not auto-overlay a current form on committed GET; explicit confirmed open uses canonical GET', async () => {
    vi.mocked(io.get).mockResolvedValue({ ...draft, state: 'Committed', canonicalPaymentId: saved.id });
    const onOpen = vi.fn().mockReturnValue(true); const approve = vi.fn().mockResolvedValueOnce(false).mockResolvedValueOnce(true);
    render(<PaymentCreateRecovery onOpen={onOpen} beforeOpen={approve} />);
    fireEvent.click(screen.getByRole('button', { name: 'Проверить сохранение' }));
    const open = await screen.findByRole('button', { name: 'Открыть платёж №81' });
    expect(onOpen).not.toHaveBeenCalled(); expect(io.payment).not.toHaveBeenCalled();
    fireEvent.click(open); await waitFor(() => expect(approve).toHaveBeenCalledTimes(1));
    expect(onOpen).not.toHaveBeenCalled(); expect(pendingPaymentCreates()).toHaveLength(1);
    fireEvent.click(open); await waitFor(() => expect(onOpen).toHaveBeenCalledWith(saved, expect.objectContaining({ attemptId: id })));
    expect(io.payment).toHaveBeenCalledWith(81, expect.anything(), expect.any(AbortSignal)); expect(pendingPaymentCreates()).toEqual([]);
  });
  it('ignores a late open ACK after editor scope changes and retains its known ID for a fresh explicit open', async () => {
    vi.mocked(io.get).mockResolvedValue({ ...draft, state: 'Committed', canonicalPaymentId: saved.id });
    const ack = deferred<Payment>(); vi.mocked(io.payment).mockReturnValueOnce(ack.promise);
    let view = 1; const onOpen = vi.fn().mockReturnValue(true);
    render(<PaymentCreateRecovery onOpen={onOpen} scopeVersion={() => view} />);
    fireEvent.click(screen.getByRole('button', { name: 'Проверить сохранение' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть платёж №81' }));
    await waitFor(() => expect(io.payment).toHaveBeenCalledTimes(1));
    view = 2; await act(async () => { ack.resolve(saved); });
    expect(onOpen).not.toHaveBeenCalled(); expect(pendingPaymentCreates()).toHaveLength(1);
  });
  it('ignores an A→B→A pending check without leaking or deleting the original owner UUID', async () => {
    const check = deferred<PaymentAttemptState>(); vi.mocked(io.get).mockReturnValueOnce(check.promise);
    const onOpen = vi.fn().mockReturnValue(true); render(<PaymentCreateRecovery onOpen={onOpen} />);
    fireEvent.click(screen.getByRole('button', { name: 'Проверить сохранение' }));
    await waitFor(() => expect(apiService.inspectPaymentCreate).toHaveBeenCalledTimes(1));
    await act(async () => { login(2, 'B'); });
    expect(screen.queryByRole('button', { name: 'Отменить попытку' })).toBeNull();
    await act(async () => { login(1, 'A-new'); check.resolve({ ...draft, state: 'Committed', canonicalPaymentId: saved.id }); });
    expect(onOpen).not.toHaveBeenCalled(); expect(pendingPaymentCreates()[0].attemptId).toBe(id);
    expect(io.payment).not.toHaveBeenCalled();
  });
});
