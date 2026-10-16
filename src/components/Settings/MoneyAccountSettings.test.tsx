import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { MoneyAccountSettings } from './MoneyAccountSettings';
import type { MoneyAccount } from '../../types';

const createMoneyAccountMock = vi.hoisted(() => vi.fn());
const deleteMoneyAccountMock = vi.hoisted(() => vi.fn());
const getMoneyAccountsMock = vi.hoisted(() => vi.fn());
const getMainCompanySettingsMock = vi.hoisted(() => vi.fn());
const toastErrorMock = vi.hoisted(() => vi.fn());
const toastSuccessMock = vi.hoisted(() => vi.fn());
const updateMoneyAccountMock = vi.hoisted(() => vi.fn());

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn(async () => true),
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    error: toastErrorMock,
    success: toastSuccessMock,
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    createMoneyAccount: createMoneyAccountMock,
    deleteMoneyAccount: deleteMoneyAccountMock,
    getMoneyAccounts: getMoneyAccountsMock,
    getMainCompanySettings: getMainCompanySettingsMock,
    updateMoneyAccount: updateMoneyAccountMock,
  },
}));

describe('MoneyAccountSettings', () => {
  beforeEach(() => {
    createMoneyAccountMock.mockReset();
    deleteMoneyAccountMock.mockReset();
    getMoneyAccountsMock.mockReset();
    getMainCompanySettingsMock.mockReset();
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
    updateMoneyAccountMock.mockReset();

    getMoneyAccountsMock.mockResolvedValue([]);
    getMainCompanySettingsMock.mockResolvedValue({ defaultCompanyId: 1, companies: [
      { id: 1, shortName: 'Компания А', profileStatus: 'Active' }, { id: 2, shortName: 'Компания Б', profileStatus: 'Active' },
    ] });
    createMoneyAccountMock.mockImplementation(async (payload: MoneyAccount) => ({
      ...payload,
      id: 10,
      createdAt: '2026-05-01T10:00:00Z',
      updatedAt: null,
    }));
  });

  it('closes the editor after creating a money account', async () => {
    const user = userEvent.setup();
    const createdAccount: MoneyAccount = {
      id: 10,
      companyId: 1,
      name: 'Основная касса',
      source: 'Cash',
      currency: 'RUB',
      bankName: null,
      bankAccountNumber: null,
      isDefault: true,
      isActive: true,
      createdAt: '2026-05-01T10:00:00Z',
      updatedAt: null,
    };
    getMoneyAccountsMock.mockResolvedValueOnce([]).mockResolvedValueOnce([createdAccount]);

    render(<MoneyAccountSettings />);

    await screen.findByText('Денежные счета');
    await user.click(screen.getByRole('button', { name: 'Добавить счет' }));

    await screen.findByText('Добавить денежный счет');
    await user.type(screen.getByLabelText('Название'), 'Основная касса');
    await user.selectOptions(screen.getByLabelText('Собственная компания'), '1');
    await user.selectOptions(screen.getByLabelText('Источник'), 'Cash');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() =>
      expect(createMoneyAccountMock).toHaveBeenCalledWith(
        expect.objectContaining({
          name: 'Основная касса',
          source: 'Cash',
          currency: 'RUB',
          companyId: 1,
          isActive: true,
        }),
      ),
    );

    await waitFor(() => expect(screen.queryByText('Добавить денежный счет')).toBeNull());
    await waitFor(() => expect(getMoneyAccountsMock).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Основная касса')).toBeTruthy();
    expect(toastSuccessMock).toHaveBeenCalledWith('Денежный счет добавлен.', 'Денежные счета');
  });

  it('names the icon-only editor close button', async () => {
    const user = userEvent.setup();

    render(<MoneyAccountSettings />);

    await screen.findByText('Денежные счета');
    await user.click(screen.getByRole('button', { name: 'Добавить счет' }));

    expect(screen.getByRole('button', { name: 'Закрыть форму денежного счета' })).toBeTruthy();
  });

  it('reloads money accounts after deleting an account', async () => {
    const user = userEvent.setup();
    const account: MoneyAccount = {
      id: 20,
      companyId: 1,
      name: 'Резервная касса',
      source: 'Cash',
      currency: 'RUB',
      bankName: null,
      bankAccountNumber: null,
      isDefault: false,
      isActive: true,
      createdAt: '2026-05-01T10:00:00Z',
      updatedAt: null,
    };

    getMoneyAccountsMock.mockResolvedValueOnce([account]).mockResolvedValueOnce([]);
    deleteMoneyAccountMock.mockResolvedValue(undefined);

    render(<MoneyAccountSettings />);

    await screen.findByText('Резервная касса');
    await user.click(screen.getByRole('button', { name: 'Удалить или отключить Резервная касса' }));

    await waitFor(() => expect(deleteMoneyAccountMock).toHaveBeenCalledWith(20));
    await waitFor(() => expect(getMoneyAccountsMock).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Денежные счета не найдены.')).toBeTruthy();
    expect(toastSuccessMock).toHaveBeenCalledWith('Денежный счет удален или отключен.', 'Денежные счета');
  });

  it('requires an explicit company instead of guessing the configured default', async () => {
    const user = userEvent.setup();
    render(<MoneyAccountSettings />);
    await waitFor(() => expect((screen.getByRole('button', { name: 'Добавить счет' }) as HTMLButtonElement).disabled).toBe(false));
    await user.click(screen.getByRole('button', { name: 'Добавить счет' }));
    expect((screen.getByLabelText('Собственная компания') as HTMLSelectElement).value).toBe('');
    await user.type(screen.getByLabelText('Название'), 'Новый счёт');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect(createMoneyAccountMock).not.toHaveBeenCalled();
    expect(toastErrorMock).toHaveBeenCalledWith('Выберите собственную компанию для денежного счёта.', 'Денежные счета');
    await user.selectOptions(screen.getByLabelText('Собственная компания'), '2');
    await user.type(screen.getByLabelText('БИК банка'), '044525225');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(createMoneyAccountMock).toHaveBeenCalledWith(expect.objectContaining({ companyId: 2, bankBik: '044525225', bankConnectionAccountId: null })));
  });

  it('keeps legacy accounts visible and read-only without assigning the first company', async () => {
    getMoneyAccountsMock.mockResolvedValue([{ id: 77, name: 'Старый счёт', source: 'Cash', currency: 'RUB', isDefault: false, isActive: true, createdAt: '2026-01-01T00:00:00Z' }]);
    render(<MoneyAccountSettings />);
    expect(await screen.findByText('Старый счёт')).toBeTruthy();
    expect(screen.getByText('Историческая запись, только просмотр')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Редактировать Старый счёт' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Удалить или отключить Старый счёт' }) as HTMLButtonElement).disabled).toBe(true);
    expect(updateMoneyAccountMock).not.toHaveBeenCalled();
  });

  it('filters the table by company and keeps the unknown-company group separate', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 10, companyId: 1, name: 'Счёт А', source: 'Bank', isActive: true },
      { id: 11, companyId: 2, name: 'Счёт Б', source: 'Cash', isActive: true },
      { id: 12, companyId: null, name: 'Счёт без владельца', source: 'Cash', isActive: true },
    ]);
    render(<MoneyAccountSettings />);
    await screen.findByText('Счёт А');
    await user.selectOptions(screen.getByLabelText('Компания в списке счетов'), '2');
    expect(screen.queryByText('Счёт А')).toBeNull();
    expect(screen.getByText('Счёт Б')).toBeTruthy();
    expect(screen.queryByText('Счёт без владельца')).toBeNull();
    await user.selectOptions(screen.getByLabelText('Компания в списке счетов'), 'unknown');
    expect(screen.getByText('Счёт без владельца')).toBeTruthy();
    expect(screen.queryByText('Счёт Б')).toBeNull();
  });

  it('keeps bank linkage and physical details when renaming a linked account', async () => {
    const user = userEvent.setup();
    const account = { id: 10, companyId: 1, name: 'Банковский счёт', source: 'Bank', currency: 'RUB', bankBik: '044525225', bankAccountNumber: 'synthetic-account', bankConnectionAccountId: 91, isDefault: true, isActive: true, createdAt: '2026-01-01T00:00:00Z' };
    getMoneyAccountsMock.mockResolvedValue([account]);
    updateMoneyAccountMock.mockResolvedValue(account);
    render(<MoneyAccountSettings />);
    await user.click(await screen.findByRole('button', { name: 'Редактировать Банковский счёт' }));
    expect((screen.getByLabelText('Собственная компания') as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByLabelText('Номер счета, карты или кошелька') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('БИК банка') as HTMLInputElement).disabled).toBe(true);
    await user.clear(screen.getByLabelText('Название'));
    await user.type(screen.getByLabelText('Название'), 'Новое название');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(updateMoneyAccountMock).toHaveBeenCalledWith(10, expect.objectContaining({ name: 'Новое название', companyId: 1, bankConnectionAccountId: 91, bankAccountNumber: 'synthetic-account', bankBik: '044525225' })));
  });

  it('blocks creation when company loading fails while preserving the account list', async () => {
    getMainCompanySettingsMock.mockRejectedValue(new Error('Собственные компании недоступны'));
    getMoneyAccountsMock.mockResolvedValue([{ id: 10, companyId: 1, name: 'Сохранённый счёт', source: 'Bank', isActive: true }]);
    render(<MoneyAccountSettings />);
    expect(await screen.findByText('Сохранённый счёт')).toBeTruthy();
    expect(screen.getByText('Собственные компании недоступны')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Добавить счет' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('ignores a late previous reload for both accounts and own companies', async () => {
    let oldAccounts!: (value: unknown[]) => void;
    let oldCompanies!: (value: unknown) => void;
    getMoneyAccountsMock.mockImplementationOnce(() => new Promise(resolve => { oldAccounts = resolve; }))
      .mockResolvedValue([{ id: 22, companyId: 2, name: 'Актуальный счёт', source: 'Bank', isActive: true }]);
    getMainCompanySettingsMock.mockImplementationOnce(() => new Promise(resolve => { oldCompanies = resolve; }))
      .mockResolvedValue({ companies: [{ id: 2, shortName: 'Актуальная компания' }] });
    render(<MoneyAccountSettings />);
    await userEvent.click(screen.getByRole('button', { name: 'Обновить' }));
    await screen.findByText('Актуальный счёт');
    await act(async () => {
      oldAccounts([{ id: 11, companyId: 1, name: 'Устаревший счёт', source: 'Cash', isActive: true }]);
      oldCompanies({ companies: [{ id: 1, shortName: 'Устаревшая компания' }] });
    });
    expect(screen.queryByText('Устаревший счёт')).toBeNull();
    expect(screen.queryByText('Устаревшая компания')).toBeNull();
    expect(screen.getByText('Актуальный счёт')).toBeTruthy();
  });

  it('keeps keyboard focus in the editor and returns it when Escape closes the form', async () => {
    const user = userEvent.setup();
    render(<MoneyAccountSettings />);
    const add = screen.getByRole('button', { name: 'Добавить счет' });
    await waitFor(() => expect((add as HTMLButtonElement).disabled).toBe(false));
    await user.click(add);
    const dialog = await screen.findByRole('dialog', { name: 'Добавить денежный счет' });
    expect(dialog.contains(document.activeElement)).toBe(true);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(add);
  });

  it('closes the company dropdown with Escape before closing the unsaved editor', async () => {
    const user = userEvent.setup();
    render(<MoneyAccountSettings />);
    const add = screen.getByRole('button', { name: 'Добавить счет' });
    await waitFor(() => expect((add as HTMLButtonElement).disabled).toBe(false));
    await user.click(add);
    await user.type(screen.getByLabelText('Название'), 'Несохранённый счёт');
    await user.click(screen.getByRole('combobox', { name: /Собственная компания/ }));
    await user.keyboard('{Escape}');
    expect(screen.getByRole('dialog', { name: 'Добавить денежный счет' })).toBeTruthy();
    expect((screen.getByLabelText('Название') as HTMLInputElement).value).toBe('Несохранённый счёт');
    expect(screen.queryByRole('listbox')).toBeNull();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
