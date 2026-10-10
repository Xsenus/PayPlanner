import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CompanyBankConnectionsSection } from './CompanyBankConnectionsSection';
import { apiService } from '../../services/api';
import type { BankConnectionItem } from '../../types';

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
    const { container } = render(<CompanyBankConnectionsSection companyId={5} />);
    await waitFor(() => expect(apiService.getBankConnections).toHaveBeenCalled());
    expect(container.textContent).toBe('');
  });

  it('shows a retry after a technical failure', async () => {
    vi.mocked(apiService.getBankConnections).mockRejectedValueOnce(new Error('Failed to fetch'));
    render(<CompanyBankConnectionsSection companyId={5} />);
    expect(await screen.findByRole('alert')).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Повторить загрузку' }));
    expect(await screen.findByText('Токен скоро истечёт')).toBeTruthy();
  });

  it('ignores the old company response', async () => {
    let finishOld!: (value: BankConnectionItem[]) => void;
    vi.mocked(apiService.getBankConnections).mockImplementationOnce(() => new Promise((resolve) => { finishOld = resolve; }));
    const { rerender } = render(<CompanyBankConnectionsSection companyId={5} />);
    vi.mocked(apiService.getBankConnections).mockResolvedValue([{ ...connection(), id: 2, legalEntityId: 6, status: 'Active' }]);
    rerender(<CompanyBankConnectionsSection companyId={6} />);
    expect(await screen.findByText('Активно')).toBeTruthy();
    finishOld([connection()]);
    await waitFor(() => expect(screen.queryByText('Токен скоро истечёт')).toBeNull());
    expect(screen.getByText('Активно')).toBeTruthy();
  });

  it('shows statuses and prevents closed account from becoming default', async () => {
    render(<CompanyBankConnectionsSection companyId={5} />);

    expect(await screen.findByText('Токен скоро истечёт')).toBeTruthy();
    expect(screen.getByText('Открыт')).toBeTruthy();
    expect(screen.getByText('Закрыт')).toBeTruthy();
    expect((screen.getByLabelText('По умолчанию 40702810200000000002') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('По умолчанию 40702810200000000001') as HTMLInputElement).disabled).toBe(false);
  });

  it('saves a setting and reloads the connection', async () => {
    vi.mocked(apiService.updateBankAccountSettings).mockResolvedValue(connection().accounts[0]);
    render(<CompanyBankConnectionsSection companyId={5} />);

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
    render(<CompanyBankConnectionsSection companyId={5} />);

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Загрузить выписки по всем счетам' }));

    const run = await screen.findByTestId('bank-statement-run');
    expect(run.textContent).toContain('Частично (1 из 2)');
    expect(run.textContent).toContain('загружено операций: 12');
    expect(run.textContent).toContain('bank unavailable');
  });
});
