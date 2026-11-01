import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Calculator } from './Calculator';
import { useClients } from '../../hooks/useClients';
import { PaymentCreateRecoveryError, type PendingPaymentCreate } from '../../services/paymentCreateAttempts';
import { bindPaymentCreateOwner, createPaymentGuarded, pendingPaymentCreates, inspectPaymentCreate,
  openCommittedPaymentCreate, fencePaymentCreateAdoption, acknowledgePaymentCreate, type PaymentCreatePorts,
  type PaymentAttemptState } from '../../services/paymentCreateAttempts';
import type { InstallmentPlanInput, Payment } from '../../types';

const mocks = vi.hoisted(() => ({ create: vi.fn(), confirm: vi.fn(), success: vi.fn(), error: vi.fn(), info: vi.fn(), rejection: vi.fn() }));
const lookupAccess = vi.hoisted(() => ({ canView: true, viewScope: 'all' as 'all' | 'own' }));
vi.mock('../../services/api', () => ({ apiService: { createPayment: mocks.create } }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 1, role: { id: 1 } } }) }));
vi.mock('../../hooks/useClients', () => ({ useClients: vi.fn(() => ({ clients: [] })) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ({ clients: lookupAccess,
  legalEntities: { canView: true }, cases: { canView: true }, payments: { canView: true, canCreate: true } }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: mocks.success, error: mocks.error, info: mocks.info }) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: mocks.confirm }) }));
vi.mock('../../utils/installmentBridge', () => ({ consumeInstallmentDraft: () => null }));
const attempt: PendingPaymentCreate = { attemptId: '11111111-1111-4111-8111-111111111111', operation: 'primary',
  sellerCompanyId: null, sellerBranchId: null, primaryPaymentId: null, createdAtUtc: '2026-10-26T00:00:00Z',
  expiresAtUtc: '2026-10-27T00:00:00Z', canonicalPaymentId: null, identityVersion: 'payment-create-v1',
  actorId: 1, apiScope: '/api', phase: 'dispatched' };
vi.mock('../Payments/PaymentCreateRecovery', () => ({ PaymentCreateRecovery: () => null }));
vi.mock('../Accounts/InstallmentPlanner', () => ({ InstallmentPlanner: (props: {
  onSavePayments: (args: { plan: InstallmentPlanInput }) => Promise<void>;
}) => <button onClick={() => void props.onSavePayments({ plan: {
  items: [{ date: '2026-10-26', sequence: 1, payment: 25.123456, principal: 20, interest: 5.123456, balance: 25 },
    { date: '2026-11-26', sequence: 2, payment: 25, principal: 20, interest: 5, balance: 0 }],
} as InstallmentPlanInput }).catch(mocks.rejection)}>Сохранить тестовый график</button> }));
function saved(id: number): Payment { return { id } as Payment; }
beforeEach(() => {
  lookupAccess.canView = true; lookupAccess.viewScope = 'all';
  vi.clearAllMocks(); mocks.create.mockReset(); mocks.confirm.mockReset().mockResolvedValue(true);
  localStorage.clear(); localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: '1' }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', 'original-A');
});
afterEach(() => { bindPaymentCreateOwner(null, '/api'); localStorage.clear(); vi.unstubAllGlobals(); });

function realCalculatorAttempt() {
  bindPaymentCreateOwner(1, '/api');
  vi.stubGlobal('navigator', { locks: { request: async (_name: string, _options: LockOptions,
    callback: (lease: Lock | null) => Promise<unknown>) => callback({ name: 'owned', mode: 'exclusive' } as Lock) } });
  const draft: PaymentAttemptState = { attemptId: attempt.attemptId, state: 'Draft', operation: 'primary',
    sellerCompanyId: null, sellerBranchId: null, primaryPaymentId: null, createdAtUtc: attempt.createdAtUtc,
    expiresAtUtc: attempt.expiresAtUtc, canonicalPaymentId: null, identityVersion: 'payment-create-v1' };
  const canonical = { id: 81, type: 'Income' } as Payment;
  let finish!: (value: Payment) => void;
  const io: PaymentCreatePorts = { register: vi.fn().mockResolvedValue(draft),
    commit: vi.fn().mockImplementation(() => new Promise<Payment>(resolve => { finish = resolve; })),
    get: vi.fn().mockResolvedValue({ ...draft, state: 'Committed', canonicalPaymentId: 81 }),
    payment: vi.fn().mockResolvedValue(canonical),
    cancel: vi.fn().mockResolvedValue({ attemptId: attempt.attemptId, state: 'Cancelled', canonicalPaymentId: null }) };
  mocks.create.mockImplementation(payload => createPaymentGuarded(payload, undefined, io));
  return { io, canonical, finish: () => finish(canonical) };
}

describe('Calculator direct guarded-create consumer', () => {
  it('client caller: calculator lookup scope changes with visibility and disables denied reads', () => {
    const view = render(<Calculator />); const first = vi.mocked(useClients).mock.lastCall?.[0]; expect(first?.enabled).toBe(true); expect(first?.ownerScope).toBeTruthy();
    lookupAccess.viewScope = 'own'; view.rerender(<Calculator />); expect(vi.mocked(useClients).mock.lastCall?.[0]?.ownerScope).not.toBe(first?.ownerScope);
    lookupAccess.canView = false; view.rerender(<Calculator />); expect(vi.mocked(useClients).mock.lastCall?.[0]?.enabled).toBe(false);
  });
  it('continues the existing ordinary schedule loop after each known success without changing calculated amounts', async () => {
    mocks.create.mockResolvedValueOnce(saved(81)).mockResolvedValueOnce(saved(82));
    render(<Calculator />); fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await waitFor(() => expect(mocks.success).toHaveBeenCalledTimes(1));
    expect(mocks.create).toHaveBeenCalledTimes(2);
    expect(mocks.create.mock.calls[0][0]).toMatchObject({ amount: 25.123456, paidAmount: 0, plannedDate: '2026-10-26' });
    expect(mocks.create.mock.calls[1][0]).toMatchObject({ amount: 25, paidAmount: 0, plannedDate: '2026-11-26' });
  });
  it('continues both real helper schedule creates after each own normal receipt is removed by live adoption', async () => {
    const f = realCalculatorAttempt();
    const draft: PaymentAttemptState = { ...attempt, state: 'Draft' };
    vi.mocked(f.io.register).mockResolvedValueOnce(draft).mockResolvedValueOnce({ ...draft, attemptId: '22222222-2222-4222-8222-222222222222' });
    vi.mocked(f.io.commit).mockResolvedValueOnce(saved(81)).mockResolvedValueOnce(saved(82));
    render(<Calculator />); fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await waitFor(() => expect(mocks.success).toHaveBeenCalledTimes(1));
    expect(f.io.register).toHaveBeenCalledTimes(2); expect(f.io.commit).toHaveBeenCalledTimes(2);
    expect(pendingPaymentCreates()).toEqual([]); expect(mocks.rejection).not.toHaveBeenCalled();
    expect(JSON.parse(vi.mocked(f.io.commit).mock.calls[0][1])).toMatchObject({ amount: 25.123456, paidAmount: 0 });
    expect(JSON.parse(vi.mocked(f.io.commit).mock.calls[1][1])).toMatchObject({ amount: 25, paidAmount: 0 });
  });
  it('stops after an unknown second payment and refuses to restart the already partially saved whole schedule', async () => {
    mocks.create.mockResolvedValueOnce(saved(81)).mockRejectedValueOnce(new PaymentCreateRecoveryError(attempt, true));
    render(<Calculator />); fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await screen.findByText(/Подтверждено платежей: 1/);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await waitFor(() => expect(mocks.rejection).toHaveBeenCalledTimes(2));
    expect(mocks.create).toHaveBeenCalledTimes(2); expect(mocks.success).not.toHaveBeenCalled();
    mocks.confirm.mockResolvedValueOnce(false);
    fireEvent.click(screen.getByRole('button', { name: 'Начать другой график' }));
    await waitFor(() => expect(mocks.confirm).toHaveBeenCalledTimes(1));
    expect(screen.getByText(/Подтверждено платежей: 1/)).toBeTruthy();
  });
  it('does not continue the next schedule payment or show old success after A→B→A while an ACK waits', async () => {
    let resolve!: (value: Payment) => void; mocks.create.mockReturnValueOnce(new Promise<Payment>(done => { resolve = done; }));
    render(<Calculator />); fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await waitFor(() => expect(mocks.create).toHaveBeenCalledTimes(1));
    await act(async () => {
      localStorage.setItem('pp.auth-session-epoch.v1', 'B'); window.dispatchEvent(new StorageEvent('storage'));
      localStorage.setItem('pp.auth-session-epoch.v1', 'new-A'); window.dispatchEvent(new StorageEvent('storage'));
      resolve(saved(81));
    });
    expect(mocks.create).toHaveBeenCalledTimes(1); expect(mocks.success).not.toHaveBeenCalled();
  });
  it('does not silently continue the schedule after the originating Calculator was unmounted', async () => {
    let resolve!: (value: Payment) => void; mocks.create.mockReturnValueOnce(new Promise<Payment>(done => { resolve = done; }));
    const { unmount } = render(<Calculator />);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await waitFor(() => expect(mocks.create).toHaveBeenCalledTimes(1));
    unmount(); await act(async () => { resolve(saved(81)); });
    expect(mocks.create).toHaveBeenCalledTimes(1); expect(mocks.success).not.toHaveBeenCalled();
  });
  it('retains the real helper receipt after unmount and blocks a fresh Calculator from registering a replacement', async () => {
    const f = realCalculatorAttempt(); const first = render(<Calculator />);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await waitFor(() => expect(f.io.commit).toHaveBeenCalledTimes(1));
    first.unmount(); await act(async () => { f.finish(); });
    await waitFor(() => expect(pendingPaymentCreates()[0]).toMatchObject({ phase: 'committed', canonicalPaymentId: 81 }));
    render(<Calculator />); fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await waitFor(() => expect(mocks.rejection).toHaveBeenCalledTimes(1));
    expect(f.io.register).toHaveBeenCalledTimes(1); expect(f.io.commit).toHaveBeenCalledTimes(1);
  });
  it('external canonical adoption fences a real pending Calculator flow and never starts its next automatic payment', async () => {
    const f = realCalculatorAttempt(); render(<Calculator />);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить тестовый график' }));
    await waitFor(() => expect(f.io.commit).toHaveBeenCalledTimes(1));
    await act(async () => {
      const entry = pendingPaymentCreates()[0]; await inspectPaymentCreate(entry, f.io);
      const canonical = await openCommittedPaymentCreate(entry, f.io);
      await fencePaymentCreateAdoption(entry, canonical); await acknowledgePaymentCreate(entry, canonical);
    });
    await screen.findByText(/график автоматически не продолжается/);
    expect(f.io.commit).toHaveBeenCalledTimes(1); expect(mocks.rejection).not.toHaveBeenCalled();
    await act(async () => { f.finish(); });
    expect(f.io.commit).toHaveBeenCalledTimes(1); expect(mocks.success).not.toHaveBeenCalled();
    expect(pendingPaymentCreates()).toEqual([]);
  });
});
