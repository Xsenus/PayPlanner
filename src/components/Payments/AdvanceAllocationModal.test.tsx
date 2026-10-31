import { webcrypto } from 'node:crypto';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AdvanceAllocationModal } from './AdvanceAllocationModal';
import { apiService } from '../../services/api';
import { API_BASE_URL } from '../../services/apiBaseUrl';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent } from '../../services/authService';
import { acknowledgeBalanceApplication, applyBalanceGuarded, cancelBalanceApplication, canContinueBalanceApplication,
  inspectBalanceApplication, openAcknowledgedBalanceApplication, pendingBalanceApplications, type BalanceApplicationPorts, type ReadyBalanceApplication } from '../../services/balanceApplicationAttempts';
import { assertPaymentCreateOwner, bindPaymentCreateOwner, paymentCreateTokenMatches } from '../../services/paymentCreateAttempts';
import type { ClientBalanceTransaction, Payment } from '../../types';

const { success } = vi.hoisted(() => ({ success: vi.fn() }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success, error: vi.fn() }) }));
vi.mock('../../services/api', () => ({ apiService: {
  getClientBalance: vi.fn(), getLegalEntityBalance: vi.fn(), getClientBalanceTransactions: vi.fn(), getLegalEntityBalanceTransactions: vi.fn(),
  getPaymentsV2: vi.fn(), applyClientBalanceGuarded: vi.fn(), applyLegalEntityBalanceGuarded: vi.fn(),
  pendingBalanceApplications: vi.fn(), canContinueBalanceApplication: vi.fn(), acknowledgeBalanceApplication: vi.fn(),
  inspectPendingBalanceApplication: vi.fn(), cancelPendingBalanceApplication: vi.fn(), openAcknowledgedBalanceApplication: vi.fn(),
} }));
function login(actor = 1, epoch = 'advance-modal') {
  localStorage.setItem('pp_token', `header.${btoa(JSON.stringify({ sub: String(actor) }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', epoch); bindPaymentCreateOwner(actor, API_BASE_URL);
}
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (value: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function debts(items: Payment[]): Awaited<ReturnType<typeof apiService.getPaymentsV2>> {
  return { items, total: items.length, page: 1, pageSize: 100,
    summary: { income: 0, expense: 0, balance: 0, total: items.length, incomeCount: items.length, expenseCount: 0 } };
}
beforeEach(() => {
  vi.resetAllMocks(); localStorage.clear(); sessionStorage.clear(); login(); vi.stubGlobal('crypto', webcrypto);
  const held = new Set<string>();
  vi.stubGlobal('navigator', { locks: { request: async (name: string, _options: LockOptions, action: (lock: Lock | null) => unknown) => {
    if (held.has(name)) return action(null); held.add(name); try { return await action({ name, mode: 'exclusive' } as Lock); } finally { held.delete(name); }
  } } });
});
afterEach(async () => { cleanup(); bindPaymentCreateOwner(null, API_BASE_URL); await Promise.resolve(); localStorage.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function fixture(kind: 'client' | 'legalEntity') {
  const id = kind === 'client' ? 12 : 77; const owned = kind === 'client' ? { clientId: id, legalEntityId: null } : { clientId: null, legalEntityId: id };
  const source = { id: 901, ...owned, amount: 80, paidAmount: 80, type: 'Income', status: 'Completed', isPaid: true,
    advanceReviewStatus: 'AcceptedAsAdvance', description: 'Исходный аванс', date: '2026-10-31', plannedDate: '2026-10-31' } as Payment;
  const target = { id: 902, ...owned, amount: 100, paidAmount: 15, outstandingAmount: 85, type: 'Income', status: 'Processing', isPaid: false,
    description: 'Долг контрагента', date: '2026-10-31', plannedDate: '2026-10-31' } as Payment;
  const rows = [{ id: 1, ...owned, paymentId: source.id, amount: 80, type: 'UnallocatedAdvance', direction: 'Credit',
    externalKey: 'payment-unallocated-advance:901', createdAt: '2026-10-31' }] as ClientBalanceTransaction[];
  const balance = { ...owned, balance: 100 };
  const getBalance = kind === 'client' ? vi.mocked(apiService.getClientBalance) : vi.mocked(apiService.getLegalEntityBalance);
  const getTransactions = kind === 'client' ? vi.mocked(apiService.getClientBalanceTransactions) : vi.mocked(apiService.getLegalEntityBalanceTransactions);
  getBalance.mockResolvedValue(balance); getTransactions.mockResolvedValue(rows);
  vi.mocked(apiService.getPaymentsV2).mockResolvedValue(debts([target]));
  const io: BalanceApplicationPorts = {
    assertActor: actor => { assertPaymentCreateOwner(actor); if (actor.apiScope !== API_BASE_URL
      || !paymentCreateTokenMatches(actor, localStorage.getItem('pp_token'))) throw new Error('Changed actor'); },
    apply: vi.fn(async entry => ({ requestId: entry.requestId, appliedAmount: 25, balance: 75, isReplay: false })),
    inspect: vi.fn(async entry => ({ requestId: entry.requestId, paymentId: entry.paymentId, state: 'Committed' as const,
      result: { requestId: entry.requestId, appliedAmount: 25, balance: 75, isReplay: true as const } })),
    cancel: vi.fn(async entry => ({ requestId: entry.requestId, paymentId: entry.paymentId, state: 'Cancelled' as const, result: null })),
    payment: vi.fn(async () => ({ ...target, paidAmount: 40, outstandingAmount: 60, systemNotes: 'Зачтено с баланса контрагента: 25,00' })),
    balance: vi.fn(async () => ({ ...owned, balance: 65 })),
  };
  vi.mocked(apiService.pendingBalanceApplications).mockImplementation(() => pendingBalanceApplications(io.assertActor));
  vi.mocked(apiService.canContinueBalanceApplication).mockImplementation(canContinueBalanceApplication);
  vi.mocked(apiService.acknowledgeBalanceApplication).mockImplementation(acknowledgeBalanceApplication);
  vi.mocked(apiService.applyClientBalanceGuarded).mockImplementation((owner, command) => applyBalanceGuarded('client', owner, command, io));
  vi.mocked(apiService.applyLegalEntityBalanceGuarded).mockImplementation((owner, command) => applyBalanceGuarded('legalEntity', owner, command, io));
  vi.mocked(apiService.inspectPendingBalanceApplication).mockImplementation(entry => inspectBalanceApplication(entry, io));
  vi.mocked(apiService.cancelPendingBalanceApplication).mockImplementation(entry => cancelBalanceApplication(entry, io));
  vi.mocked(apiService.openAcknowledgedBalanceApplication).mockImplementation(entry => openAcknowledgedBalanceApplication(entry, io));
  const session = captureAuthSessionIdentity();
  const props = { source, context: 'advance-a', canEdit: true, canViewBalance: true, isContextCurrent: () => isAuthSessionIdentityCurrent(session), formatDate: (value: string) => value,
    onClose: vi.fn(), onResolved: vi.fn(async (_value: ReadyBalanceApplication, current: () => boolean) => { if (!current()) throw new Error('Stale refresh'); }) };
  return { kind, id, owned, source, target, rows, balance, getBalance, getTransactions, io, props };
}
async function start(amount = '25') {
  const input = await screen.findByRole('textbox', { name: 'Сумма зачета' });
  await waitFor(() => expect(input.closest('fieldset')?.disabled).toBe(false));
  fireEvent.change(input, { target: { value: amount } }); fireEvent.click(screen.getByRole('button', { name: 'Зачесть аванс' }));
}
async function unknown(f: ReturnType<typeof fixture>) {
  vi.mocked(f.io.apply).mockRejectedValueOnce(new TypeError('lost response')); await start();
  const check = await screen.findByRole('button', { name: 'Проверить зачёт' }); await waitFor(() => expect(check.hasAttribute('disabled')).toBe(false));
  return apiService.pendingBalanceApplications()[0];
}

describe.each(['client', 'legalEntity'] as const)('%s advance allocation', kind => {
  it('uses a guarded source-bound command and acknowledges only after owned refresh', async () => {
    const f = fixture(kind); render(<AdvanceAllocationModal {...f.props} />); await start('25.56789');
    await waitFor(() => expect(f.props.onClose).toHaveBeenCalledOnce());
    expect(f.io.apply).toHaveBeenCalledOnce(); const [, command] = vi.mocked(f.io.apply).mock.calls[0];
    expect(command).toEqual({ paymentId: 902, amount: 25.56789, sourcePaymentId: 901, description: 'Распределение аванса по платежу #901' });
    expect(f.props.onResolved).toHaveBeenCalledOnce(); expect(apiService.pendingBalanceApplications()).toEqual([]); expect(success).toHaveBeenCalledOnce();
    expect(f.getBalance).toHaveBeenCalledWith(f.id, expect.objectContaining({ signal: expect.any(AbortSignal) }));
  });
  it('recovers a lost response without another financial POST', async () => {
    const f = fixture(kind); render(<AdvanceAllocationModal {...f.props} />); const entry = await unknown(f);
    expect(screen.getByRole('button', { name: 'Зачесть аванс' }).hasAttribute('disabled')).toBe(true);
    expect(screen.getByText('Требуется проверка зачёта')).toBeTruthy();
    expect((screen.getByRole('textbox', { name: 'Сумма зачета' }) as HTMLInputElement).value).toBe('25');
    fireEvent.click(screen.getByRole('button', { name: 'Проверить зачёт' })); await waitFor(() => expect(f.props.onClose).toHaveBeenCalledOnce());
    expect(f.io.apply).toHaveBeenCalledOnce(); expect(f.io.inspect).toHaveBeenCalledWith(entry, expect.any(Object), expect.any(AbortSignal));
    expect(apiService.pendingBalanceApplications()).toEqual([]);
  });
  it('cancels explicitly, reloads the source pool, retains input and only then allows a fresh key', async () => {
    const f = fixture(kind); render(<AdvanceAllocationModal {...f.props} />); const old = await unknown(f);
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть попытку зачёта' }));
    await screen.findByText('Попытка закрыта без зачёта. Теперь можно выполнить новый зачёт.');
    expect(f.props.onClose).not.toHaveBeenCalled(); expect(f.props.onResolved).toHaveBeenCalledOnce(); expect(f.props.onResolved.mock.calls[0][0].state).toBe('Cancelled'); expect(f.getBalance).toHaveBeenCalledTimes(2);
    expect((screen.getByRole('textbox', { name: 'Сумма зачета' }) as HTMLInputElement).value).toBe('25');
    await start(); await waitFor(() => expect(f.props.onClose).toHaveBeenCalledOnce());
    expect(vi.mocked(f.io.apply).mock.calls[1][0].requestId).not.toBe(old.requestId); expect(f.io.apply).toHaveBeenCalledTimes(2);
  });
  it('closes an already committed cancellation by its original outcome without a refund', async () => {
    const f = fixture(kind); render(<AdvanceAllocationModal {...f.props} />); const entry = await unknown(f);
    vi.mocked(f.io.cancel).mockResolvedValue({ requestId: entry.requestId, paymentId: 902, state: 'Committed',
      result: { requestId: entry.requestId, appliedAmount: 25, balance: 75, isReplay: true } });
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть попытку зачёта' })); await waitFor(() => expect(f.props.onClose).toHaveBeenCalledOnce());
    expect(f.io.apply).toHaveBeenCalledOnce(); expect(f.props.onResolved).toHaveBeenCalledOnce(); expect(apiService.pendingBalanceApplications()).toEqual([]);
  });
  it('requires a successful pool reload after cancellation and preserves input across its failure', async () => {
    const f = fixture(kind); render(<AdvanceAllocationModal {...f.props} />); await unknown(f);
    f.getBalance.mockRejectedValueOnce(new Error('Pool reload unavailable'));
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть попытку зачёта' })); await screen.findByText('Pool reload unavailable');
    expect(screen.getByRole('button', { name: 'Зачесть аванс' }).hasAttribute('disabled')).toBe(true);
    const retry = screen.getByRole('button', { name: 'Обновить долги и остаток аванса' }); await waitFor(() => expect(retry.hasAttribute('disabled')).toBe(false));
    fireEvent.click(retry); await waitFor(() => expect(screen.getByRole('button', { name: 'Зачесть аванс' }).hasAttribute('disabled')).toBe(false));
    expect((screen.getByRole('textbox', { name: 'Сумма зачета' }) as HTMLInputElement).value).toBe('25'); expect(f.io.apply).toHaveBeenCalledOnce();
  });
  it('keeps a committed receipt across a refresh failure and does not allocate again', async () => {
    const f = fixture(kind); f.props.onResolved.mockRejectedValueOnce(new Error('Refresh unavailable')); render(<AdvanceAllocationModal {...f.props} />);
    await start(); await screen.findByText('Refresh unavailable'); expect(apiService.pendingBalanceApplications()).toHaveLength(1); expect(f.props.onClose).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Проверить зачёт' })); await waitFor(() => expect(f.props.onClose).toHaveBeenCalledOnce());
    expect(f.io.apply).toHaveBeenCalledOnce(); expect(f.props.onResolved).toHaveBeenCalledTimes(2); expect(apiService.pendingBalanceApplications()).toEqual([]);
  });
  it('subtracts prior allocations and refunds from only this source, without prefix collisions', async () => {
    const f = fixture(kind); f.getTransactions.mockResolvedValue([...f.rows,
      { ...f.rows[0], id: 2, type: 'CreditApplied', direction: 'Debit', amount: 20, externalKey: 'payment-unallocated-advance-apply:901:902:any' },
      { ...f.rows[0], id: 3, type: 'Refund', direction: 'Debit', amount: 10, externalKey: 'payment-unallocated-advance-refund:901' },
      { ...f.rows[0], id: 4, type: 'CreditApplied', direction: 'Debit', amount: 500, externalKey: 'payment-unallocated-advance-apply:9010:902:any' }]);
    render(<AdvanceAllocationModal {...f.props} />); const input = await screen.findByRole('textbox', { name: 'Сумма зачета' });
    expect((input as HTMLInputElement).value).toBe('50'); await start('51'); await screen.findByText(/Укажите сумму зачёта не больше/);
    expect(f.io.apply).not.toHaveBeenCalled(); expect(input.closest('fieldset')?.disabled).toBe(false);
  });
  it('filters foreign, cancelled, completed, expense and source rows before selecting a debt', async () => {
    const f = fixture(kind); const foreign = kind === 'client' ? { clientId: 99, legalEntityId: null } : { clientId: null, legalEntityId: 99 };
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue(debts([f.source, { ...f.target, id: 3, ...foreign }, { ...f.target, id: 4, status: 'Cancelled' },
      { ...f.target, id: 5, status: 'Completed' }, { ...f.target, id: 6, type: 'Expense' }, f.target]));
    render(<AdvanceAllocationModal {...f.props} />); await screen.findByRole('textbox', { name: 'Сумма зачета' });
    const options = screen.getAllByRole('option', { hidden: true }); expect(options).toHaveLength(1); expect(options[0].textContent).toContain('#902');
  });
  it('rejects an incorrect balance owner and leaves financial submission disabled', async () => {
    const f = fixture(kind); f.getBalance.mockResolvedValue({ clientId: 99, legalEntityId: null, balance: 100 });
    render(<AdvanceAllocationModal {...f.props} />); await screen.findByText('Данные выбранного аванса не подтверждены.');
    expect(screen.getByRole('button', { name: 'Зачесть аванс' }).hasAttribute('disabled')).toBe(true); expect(f.io.apply).not.toHaveBeenCalled();
  });
  it('does not overlap two clicks even before the busy render', async () => {
    const f = fixture(kind); const reply = deferred<Awaited<ReturnType<BalanceApplicationPorts['apply']>>>(); vi.mocked(f.io.apply).mockReturnValueOnce(reply.promise);
    render(<AdvanceAllocationModal {...f.props} />); await start(); fireEvent.click(screen.getByRole('button', { name: 'Зачесть аванс' }));
    await waitFor(() => expect(f.io.apply).toHaveBeenCalledOnce()); const entry = apiService.pendingBalanceApplications()[0];
    await act(async () => reply.resolve({ requestId: entry.requestId, appliedAmount: 25, balance: 75, isReplay: false }));
    await waitFor(() => expect(f.props.onClose).toHaveBeenCalledOnce()); expect(f.io.apply).toHaveBeenCalledOnce();
  });
  it('keeps a source closed without accepting late read data and aborts all three requests', async () => {
    const f = fixture(kind); const read = deferred<Awaited<ReturnType<typeof apiService.getPaymentsV2>>>(); vi.mocked(apiService.getPaymentsV2).mockReturnValueOnce(read.promise);
    const view = render(<AdvanceAllocationModal {...f.props} />); await waitFor(() => expect(apiService.getPaymentsV2).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: /^Закрыть$/ })); expect(f.props.onClose).toHaveBeenCalledOnce();
    expect(f.getBalance.mock.calls[0][1]?.signal?.aborted).toBe(true); view.rerender(<AdvanceAllocationModal {...f.props} source={null} />);
    await act(async () => read.resolve(debts([f.target]))); expect(screen.queryByRole('dialog')).toBeNull();
  });
  it('requires explicit GET after another window removes the receipt before enabling a fresh allocation', async () => {
    const f = fixture(kind); render(<AdvanceAllocationModal {...f.props} />); await unknown(f);
    const key = Object.keys(localStorage).find(name => name.startsWith('pp.balance-application.v1:'))!;
    act(() => { localStorage.removeItem(key); window.dispatchEvent(new StorageEvent('storage', { key, newValue: null })); });
    expect(screen.getByRole('button', { name: 'Зачесть аванс' }).hasAttribute('disabled')).toBe(true);
    expect(screen.queryByRole('button', { name: 'Закрыть попытку зачёта' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Проверить зачёт' })); await waitFor(() => expect(f.props.onClose).toHaveBeenCalledOnce());
    expect(apiService.openAcknowledgedBalanceApplication).toHaveBeenCalledOnce(); expect(f.io.apply).toHaveBeenCalledOnce(); expect(f.io.cancel).not.toHaveBeenCalled();
  });
  it.each(['source', 'permission', 'context', 'actor', 'reopen', 'unmount'] as const)('discards a late Apply after %s without acknowledgement or a new view callback', async change => {
    const f = fixture(kind); const reply = deferred<Awaited<ReturnType<BalanceApplicationPorts['apply']>>>(); vi.mocked(f.io.apply).mockReturnValueOnce(reply.promise);
    const view = render(<AdvanceAllocationModal {...f.props} />); await start(); await waitFor(() => expect(f.io.apply).toHaveBeenCalledOnce());
    const entry = apiService.pendingBalanceApplications()[0];
    if (change === 'source') view.rerender(<AdvanceAllocationModal {...f.props} source={{ ...f.source, id: 903 }} />);
    if (change === 'permission') view.rerender(<AdvanceAllocationModal {...f.props} canEdit={false} />);
    if (change === 'context') view.rerender(<AdvanceAllocationModal {...f.props} context="advance-b" />);
    if (change === 'actor') act(() => login(2, 'advance-second'));
    if (change === 'reopen') { view.rerender(<AdvanceAllocationModal {...f.props} source={null} />); view.rerender(<AdvanceAllocationModal {...f.props} context="reopen" />); }
    if (change === 'unmount') view.unmount();
    await act(async () => reply.resolve({ requestId: entry.requestId, appliedAmount: 25, balance: 75, isReplay: false }));
    expect(f.props.onResolved).not.toHaveBeenCalled(); expect(f.props.onClose).not.toHaveBeenCalled(); expect(success).not.toHaveBeenCalled();
    expect(apiService.acknowledgeBalanceApplication).not.toHaveBeenCalled();
    if (change === 'actor') expect(f.getBalance).toHaveBeenCalledOnce();
    expect(Object.keys(localStorage).filter(name => name.startsWith('pp.balance-application.v1:'))).toHaveLength(1);
  });
});
