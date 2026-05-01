import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { MoneyAccountSettings } from './MoneyAccountSettings';
import type { MoneyAccount } from '../../types';

const createMoneyAccountMock = vi.hoisted(() => vi.fn());
const deleteMoneyAccountMock = vi.hoisted(() => vi.fn());
const getMoneyAccountsMock = vi.hoisted(() => vi.fn());
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
    updateMoneyAccount: updateMoneyAccountMock,
  },
}));

describe('MoneyAccountSettings', () => {
  beforeEach(() => {
    createMoneyAccountMock.mockReset();
    deleteMoneyAccountMock.mockReset();
    getMoneyAccountsMock.mockReset();
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
    updateMoneyAccountMock.mockReset();

    getMoneyAccountsMock.mockResolvedValue([]);
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
    await user.selectOptions(screen.getByLabelText('Источник'), 'Cash');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() =>
      expect(createMoneyAccountMock).toHaveBeenCalledWith(
        expect.objectContaining({
          name: 'Основная касса',
          source: 'Cash',
          currency: 'RUB',
          isActive: true,
        }),
      ),
    );

    await waitFor(() => expect(screen.queryByText('Добавить денежный счет')).toBeNull());
    await waitFor(() => expect(getMoneyAccountsMock).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Основная касса')).toBeTruthy();
    expect(toastSuccessMock).toHaveBeenCalledWith('Денежный счет добавлен.', 'Денежные счета');
  });

  it('reloads money accounts after deleting an account', async () => {
    const user = userEvent.setup();
    const account: MoneyAccount = {
      id: 20,
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
    await user.click(screen.getByTitle('Удалить или отключить'));

    await waitFor(() => expect(deleteMoneyAccountMock).toHaveBeenCalledWith(20));
    await waitFor(() => expect(getMoneyAccountsMock).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Денежные счета не найдены.')).toBeTruthy();
    expect(toastSuccessMock).toHaveBeenCalledWith('Денежный счет удален или отключен.', 'Денежные счета');
  });
});
