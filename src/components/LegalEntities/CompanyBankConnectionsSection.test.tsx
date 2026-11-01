import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { CompanyBankConnectionsSection } from './CompanyBankConnectionsSection';
import { documentCopyTestSession } from '../../test/documentCopySession';
import { apiService } from '../../services/api';
import type { BankConnectionItem } from '../../types';

vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn().mockResolvedValue(true) }) }));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ success: vi.fn(), error: vi.fn(), info: vi.fn() }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getBankConnections: vi.fn(),
    syncBankConnectionAccounts: vi.fn(),
    syncBankConnectionStatements: vi.fn(),
    updateBankAccountSettings: vi.fn(),
    getBankSyncLogs: vi.fn(),
  },
}));

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => session.restore());

const connection = (): BankConnectionItem => ({
  id: 1,
  legalEntityId: 5,
  provider: 'Tochka',
  status: 'TokenExpiring',
  accounts: [
    { id: 10, externalAccountId: 'a', accountNumber: '40702810200000000001', currency: 'RUB', status: 'Open', isHidden: false, isDefault: false, statementsEnabled: true },
    { id: 11, externalAccountId: 'b', accountNumber: '40702810200000000002', currency: 'RUB', status: 'Closed', isHidden: false, isDefault: false, statementsEnabled: true },
  ],
});

describe('CompanyBankConnectionsSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getBankConnections).mockResolvedValue([connection()]);
  });

  it('renders nothing without connections', async () => {
    vi.mocked(apiService.getBankConnections).mockResolvedValue([]);
    const { container } = render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);
    await waitFor(() => expect(apiService.getBankConnections).toHaveBeenCalled());
    expect(container.textContent).toBe('');
  });

  it('shows a retry after a technical failure', async () => {
    vi.mocked(apiService.getBankConnections).mockRejectedValueOnce(new Error('Failed to fetch'));
    render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);
    expect(await screen.findByRole('alert')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));
    expect(await screen.findByText('Токен скоро истечёт')).toBeTruthy();
  });

  it('ignores the old company response', async () => {
    let finishOld!: (value: BankConnectionItem[]) => void;
    vi.mocked(apiService.getBankConnections).mockImplementationOnce(() => new Promise((resolve) => { finishOld = resolve; }));
    const { rerender } = render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);
    vi.mocked(apiService.getBankConnections).mockResolvedValue([{ ...connection(), id: 2, legalEntityId: 6, status: 'Active' }]);
    rerender(<CompanyBankConnectionsSection companyId={6} readOnly={false} />);
    expect(await screen.findByText('Активно')).toBeTruthy();
    finishOld([connection()]);
    await waitFor(() => expect(screen.queryByText('Токен скоро истечёт')).toBeNull());
    expect(screen.getByText('Активно')).toBeTruthy();
  });

  it('shows statuses and prevents closed account from becoming default', async () => {
    render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);

    expect(await screen.findByText('Токен скоро истечёт')).toBeTruthy();
    expect(screen.getByText('Открыт')).toBeTruthy();
    expect(screen.getByText('Закрыт')).toBeTruthy();
    expect((screen.getByLabelText('По умолчанию 40702810200000000002') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('По умолчанию 40702810200000000001') as HTMLInputElement).disabled).toBe(false);
  });

  it('saves a setting and reloads the connection', async () => {
    vi.mocked(apiService.updateBankAccountSettings).mockResolvedValue(connection().accounts[0]);
    render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);

    await userEvent.setup().click(await screen.findByLabelText('Скрыть счёт 40702810200000000001'));

    await waitFor(() => expect(apiService.updateBankAccountSettings).toHaveBeenCalledWith(10, { isHidden: true }));
    expect(apiService.getBankConnections).toHaveBeenCalledTimes(2);
  });

  it('loads statements for the chosen period and reports partial result per account', async () => {
    vi.mocked(apiService.syncBankConnectionStatements).mockResolvedValue({
      status: 'Partial',
      succeeded: 1,
      total: 2,
      accounts: [
        { accountId: 10, inserted: 12, updated: 0, error: null },
        { accountId: 11, inserted: 0, updated: 0, error: 'bank unavailable' },
      ],
    });
    render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Загрузить выписки по всем счетам' }));

    const run = await screen.findByTestId('bank-statement-run');
    expect(run.textContent).toContain('Частично (1 из 2)');
    expect(run.textContent).toContain('загружено операций: 12');
    expect(run.textContent).toContain('bank unavailable');
  });

  it('reuses the request after a lost response and partial result, then starts a new successful run', async () => {
    const success = { status: 'Success' as const, succeeded: 1, total: 1, accounts: [{ accountId: 10, inserted: 1, updated: 0, batchId: 91 }] };
    vi.mocked(apiService.syncBankConnectionStatements)
      .mockRejectedValueOnce(new Error('Failed to fetch'))
      .mockResolvedValueOnce({ ...success, status: 'Partial', succeeded: 0 })
      .mockResolvedValue(success);
    render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);
    const user = userEvent.setup();
    const button = await screen.findByRole('button', { name: 'Загрузить выписки по всем счетам' });
    for (let call = 1; call <= 4; call++) {
      await user.click(button);
      await waitFor(() => expect(apiService.syncBankConnectionStatements).toHaveBeenCalledTimes(call));
      await waitFor(() => expect((button as HTMLButtonElement).disabled).toBe(false));
    }
    const calls = vi.mocked(apiService.syncBankConnectionStatements).mock.calls;
    expect(calls[0][1].requestId).toMatch(/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i);
    expect(calls[1][1]).toEqual(calls[0][1]);
    expect(calls[2][1]).toEqual(calls[0][1]);
    expect(calls[3][1].requestId).not.toBe(calls[0][1].requestId);
  });

  it('does not show or reload an old company when its statement request finishes late', async () => {
    let finishOld!: (value: Awaited<ReturnType<typeof apiService.syncBankConnectionStatements>>) => void;
    vi.mocked(apiService.syncBankConnectionStatements).mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve; }));
    const { rerender } = render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Загрузить выписки по всем счетам' }));
    vi.mocked(apiService.getBankConnections).mockResolvedValue([{ ...connection(), id: 2, legalEntityId: 6, status: 'Active' }]);
    rerender(<CompanyBankConnectionsSection companyId={6} readOnly={false} />);
    expect(await screen.findByText('Активно')).toBeTruthy();
    await act(async () => { finishOld({ status: 'Success', succeeded: 1, total: 1, accounts: [{ accountId: 10, inserted: 1, updated: 0, batchId: 91 }] }); });
    await waitFor(() => expect(screen.queryByTestId('bank-statement-run')).toBeNull());
    expect(apiService.getBankConnections).toHaveBeenCalledTimes(2);
  });

  it('uses a different request when the period changes after a failed call', async () => {
    vi.mocked(apiService.syncBankConnectionStatements).mockRejectedValue(new Error('Failed to fetch'));
    render(<CompanyBankConnectionsSection companyId={5} readOnly={false} />);
    const user = userEvent.setup();
    const button = await screen.findByRole('button', { name: 'Загрузить выписки по всем счетам' });
    await user.click(button);
    await waitFor(() => expect((button as HTMLButtonElement).disabled).toBe(false));
    const first = vi.mocked(apiService.syncBankConnectionStatements).mock.calls[0][1];
    const from = screen.getByLabelText('С');
    fireEvent.change(from, { target: { value: '2026-01-01' } });
    await user.click(button);
    await waitFor(() => expect(apiService.syncBankConnectionStatements).toHaveBeenCalledTimes(2));
    const second = vi.mocked(apiService.syncBankConnectionStatements).mock.calls[1][1];
    expect(second.from).toBe('2026-01-01');
    expect(second.requestId).not.toBe(first.requestId);
  });
});
