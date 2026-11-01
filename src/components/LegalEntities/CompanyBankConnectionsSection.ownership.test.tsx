import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CompanyBankConnectionsSection } from './CompanyBankConnectionsSection';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { BankConnectionItem, BankSyncLogPage } from '../../types';

const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn().mockResolvedValue(true) }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => toast }));
vi.mock('../../services/api', () => ({ apiService: { getBankConnections: vi.fn(), syncBankConnectionAccounts: vi.fn(), syncBankConnectionStatements: vi.fn(), updateBankAccountSettings: vi.fn(), getBankSyncLogs: vi.fn() } }));
const connection = (id = 5): BankConnectionItem => ({ id, legalEntityId: id, provider: 'Tochka', status: 'Active', accounts: [{ id: id * 10, externalAccountId: 'test', accountNumber: `4070281020000000000${id}`, currency: 'RUB', status: 'Open', isDefault: false, isHidden: false, statementsEnabled: true }] });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { resolve, reject, promise }; }
const props = { companyId: 5, readOnly: false };

describe('bank connection owner', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => {
    vi.resetAllMocks(); session = documentCopyTestSession();
    vi.mocked(apiService.getBankConnections).mockImplementation(async id => [connection(id)]);
    vi.mocked(apiService.getBankSyncLogs).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 });
  });
  afterEach(() => session.restore());

  it('bank owner: readonly default permits journal but denies settings, synchronisation and statements', async () => {
    render(<CompanyBankConnectionsSection companyId={5} />); await screen.findByTestId('company-bank-connections');
    for (const input of screen.getAllByRole('checkbox')) { expect(input).toHaveProperty('disabled', true); fireEvent.click(input); }
    for (const name of ['Обновить счета', 'Загрузить выписки по всем счетам']) { const b = screen.getByRole('button', { name }); expect(b).toHaveProperty('disabled', true); fireEvent.click(b); }
    fireEvent.click(screen.getByRole('button', { name: 'Журнал' })); await screen.findByText('Записей пока нет.');
    expect(apiService.getBankSyncLogs).toHaveBeenCalledExactlyOnceWith(5); expect(apiService.getBankConnections).toHaveBeenCalledTimes(1);
    expect(apiService.syncBankConnectionAccounts).not.toHaveBeenCalled(); expect(apiService.syncBankConnectionStatements).not.toHaveBeenCalled(); expect(apiService.updateBankAccountSettings).not.toHaveBeenCalled();
  });

  it.each(['readonly', 'authority', 'session'] as const)('bank owner: old sync cannot reload, notify or unlock another %s scope', async change => {
    const pending = deferred<Awaited<ReturnType<typeof apiService.syncBankConnectionAccounts>>>(); vi.mocked(apiService.syncBankConnectionAccounts).mockReturnValue(pending.promise);
    const view = render(<CompanyBankConnectionsSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Обновить счета' }));
    if (change === 'readonly') view.rerender(<CompanyBankConnectionsSection {...props} readOnly />);
    else if (change === 'authority') view.rerender(<CompanyBankConnectionsSection {...props} ownerScope="changed-permission" />); else session.replace();
    await act(async () => pending.resolve({ added: 1, updated: 0, skipped: 0 }));
    expect(toast.success).not.toHaveBeenCalled(); expect(toast.error).not.toHaveBeenCalled();
    expect(apiService.getBankConnections).toHaveBeenCalledTimes(change === 'session' ? 1 : 2);
  });

  it.each(['success', 'failure'] as const)('bank owner: a pending journal %s does not belong to the next company', async outcome => {
    const pending = deferred<BankSyncLogPage>(); vi.mocked(apiService.getBankSyncLogs).mockReturnValue(pending.promise);
    const view = render(<CompanyBankConnectionsSection {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Журнал' }));
    view.rerender(<CompanyBankConnectionsSection {...props} companyId={6} />); await screen.findByText('40702810200000000006');
    await act(async () => { if (outcome === 'success') pending.resolve({ items: [], total: 0, page: 1, pageSize: 20 }); else pending.reject(new Error('Private old bank error')); });
    expect(screen.queryByTestId('bank-sync-log')).toBeNull(); expect(toast.error).not.toHaveBeenCalled(); expect(apiService.getBankConnections).toHaveBeenCalledTimes(2);
  });

  it('bank owner: changing session without a render prevents an outgoing sync', async () => {
    render(<CompanyBankConnectionsSection {...props} />); const button = await screen.findByRole('button', { name: 'Обновить счета' }); session.replace(); fireEvent.click(button); expect(apiService.syncBankConnectionAccounts).not.toHaveBeenCalled();
  });

  it('bank owner: duplicate journal reads and write actions share one operation', async () => {
    const pending = deferred<BankSyncLogPage>(); vi.mocked(apiService.getBankSyncLogs).mockReturnValue(pending.promise);
    render(<CompanyBankConnectionsSection {...props} />); const button = await screen.findByRole('button', { name: 'Журнал' }); fireEvent.click(button); fireEvent.click(button); fireEvent.click(screen.getByRole('button', { name: 'Обновить счета' }));
    expect(apiService.getBankSyncLogs).toHaveBeenCalledTimes(1); expect(apiService.syncBankConnectionAccounts).not.toHaveBeenCalled();
    await act(async () => pending.resolve({ items: [], total: 0, page: 1, pageSize: 20 })); await waitFor(() => expect(button).toHaveProperty('disabled', false));
  });

  it('bank owner: a foreign connection list is unavailable, not an editable account list', async () => {
    vi.mocked(apiService.getBankConnections).mockResolvedValue([connection(6)]); render(<CompanyBankConnectionsSection {...props} />); expect(await screen.findByRole('alert')).toBeTruthy(); expect(screen.queryByRole('checkbox')).toBeNull();
  });
});
