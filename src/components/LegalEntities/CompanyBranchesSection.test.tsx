import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CompanyBranchesSection } from './CompanyBranchesSection';
import { apiService } from '../../services/api';
import type { CompanyBranch, LegalEntityBankAccount } from '../../types';

const { toastSuccessMock, toastErrorMock } = vi.hoisted(() => ({
  toastSuccessMock: vi.fn(),
  toastErrorMock: vi.fn(),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ success: toastSuccessMock, error: toastErrorMock, info: vi.fn() }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getCompanyBranches: vi.fn(),
    createCompanyBranch: vi.fn(),
    updateCompanyBranch: vi.fn(),
  },
}));

const branch = (patch: Partial<CompanyBranch> = {}): CompanyBranch => ({
  id: 1,
  legalEntityId: 5,
  kind: 'Office',
  name: 'Офис СПб',
  status: 'Active',
  createdAt: '2026-10-09T00:00:00Z',
  ...patch,
});

const accounts: LegalEntityBankAccount[] = [
  { id: 'a', bank: 'Банк А', name: 'А', bik: '044525225', currency: 'RUB', accountNumber: '40702810200000000001', accountType: 'Основной' },
  { id: 'b', bank: 'Банк Б', name: 'Б', bik: '044525104', currency: 'RUB', accountNumber: '40702810200000000002', accountType: 'Дополнительный', isArchived: true },
];

describe('CompanyBranchesSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getCompanyBranches).mockResolvedValue([branch()]);
  });

  it('loads branches including archived ones and shows inherited values', async () => {
    render(<CompanyBranchesSection companyId={5} bankAccounts={accounts} readOnly={false} />);

    expect(await screen.findByText('Офис СПб')).toBeTruthy();
    expect(apiService.getCompanyBranches).toHaveBeenCalledWith(5, true);
    expect(screen.getByText('как у компании')).toBeTruthy();
    expect(screen.getByText('Основной счет компании')).toBeTruthy();
  });

  it('creates a branch and offers only active saved accounts', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.createCompanyBranch).mockResolvedValue(branch({ id: 2, name: 'Точка' }));
    render(<CompanyBranchesSection companyId={5} bankAccounts={accounts} readOnly={false} />);
    await screen.findByText('Офис СПб');

    await user.click(screen.getByRole('button', { name: /Добавить/ }));
    const dialog = screen.getByRole('dialog', { name: 'Подразделение' });
    await user.type(within(dialog).getByText('Название').closest('label')!.querySelector('input')!, ' Точка ');
    await user.click(within(dialog).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiService.createCompanyBranch).toHaveBeenCalled());
    expect(apiService.createCompanyBranch).toHaveBeenCalledWith(
      5,
      expect.objectContaining({ kind: 'Branch', name: 'Точка', kpp: null, bankAccountId: null }),
    );
    expect(toastSuccessMock).toHaveBeenCalled();
  });

  it('requires a name before saving', async () => {
    const user = userEvent.setup();
    render(<CompanyBranchesSection companyId={5} bankAccounts={[]} readOnly={false} />);
    await screen.findByText('Офис СПб');

    await user.click(screen.getByRole('button', { name: /Добавить/ }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Сохранить' }));

    expect(screen.getByText('Укажите название подразделения.')).toBeTruthy();
    expect(apiService.createCompanyBranch).not.toHaveBeenCalled();
  });

  it('moves a branch to the archive keeping its other fields', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.updateCompanyBranch).mockResolvedValue(branch({ status: 'Archived' }));
    render(<CompanyBranchesSection companyId={5} bankAccounts={accounts} readOnly={false} />);
    await screen.findByText('Офис СПб');

    await user.click(screen.getByRole('button', { name: 'В архив' }));

    await waitFor(() =>
      expect(apiService.updateCompanyBranch).toHaveBeenCalledWith(5, 1, expect.objectContaining({ name: 'Офис СПб', status: 'Archived' })),
    );
  });

  it('hides edit controls in read-only mode', async () => {
    render(<CompanyBranchesSection companyId={5} bankAccounts={accounts} readOnly />);
    await screen.findByText('Офис СПб');

    expect(screen.queryByRole('button', { name: /Добавить/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'В архив' })).toBeNull();
  });

  it('shows a load error instead of failing silently', async () => {
    vi.mocked(apiService.getCompanyBranches).mockRejectedValue(new Error('boom'));
    render(<CompanyBranchesSection companyId={5} bankAccounts={[]} readOnly={false} />);

    expect(await screen.findByText(/Не удалось загрузить подразделения|boom/)).toBeTruthy();
  });
});
