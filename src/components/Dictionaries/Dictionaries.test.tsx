import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Dictionaries } from './Dictionaries';
import { defaultRolePermissions } from '../../types/permissions';

const apiMocks = vi.hoisted(() => ({
  getDict: vi.fn(),
  getIncomeTypes: vi.fn(),
}));

const permissionMocks = vi.hoisted(() => ({
  useRolePermissions: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getDict: apiMocks.getDict,
    getIncomeTypes: apiMocks.getIncomeTypes,
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: 10, name: 'dictionary-editor' } },
    isAdmin: () => false,
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: permissionMocks.useRolePermissions,
}));

const dictionaryItem = (id: number, name: string) => ({
  id,
  name,
  description: '',
  colorHex: '#2563EB',
  isActive: true,
  createdAt: '2026-06-09T00:00:00+07:00',
});

describe('Dictionaries page layout', () => {
  beforeEach(() => {
    apiMocks.getDict.mockReset();
    apiMocks.getIncomeTypes.mockReset();
    apiMocks.getDict.mockResolvedValue([]);
    apiMocks.getIncomeTypes.mockResolvedValue([dictionaryItem(1, 'Доход от услуг')]);
    permissionMocks.useRolePermissions.mockReturnValue({
      ...defaultRolePermissions,
      dictionaries: {
        ...defaultRolePermissions.dictionaries,
        canView: true,
        canCreate: true,
        canEdit: true,
        canDelete: true,
      },
    });
  });

  it('keeps dictionaries and entries in internal scroll areas', async () => {
    render(<Dictionaries />);

    await waitFor(() => expect(apiMocks.getIncomeTypes).toHaveBeenCalled());

    expect(screen.queryByText('Управляйте справочниками системы')).toBeFalsy();
    expect(screen.queryByText('Просмотр справочников системы')).toBeFalsy();

    expect(screen.getByTestId('dictionaries-layout').className).toContain('items-start');
    expect(screen.getByTestId('dictionaries-nav').className).toContain('max-h-[calc(100vh-12rem)]');
    expect(screen.getByTestId('dictionaries-nav-scroll').className).toContain('overflow-y-auto');
    expect(screen.getByTestId('dictionaries-items-scroll').className).toContain('overflow-y-auto');
  });

  it('names icon-only row actions for assistive technologies', async () => {
    const user = userEvent.setup();
    render(<Dictionaries />);

    await waitFor(() => expect(apiMocks.getIncomeTypes).toHaveBeenCalled());
    await user.click(screen.getByRole('button', { name: /Тип дохода/ }));
    await screen.findByText('Доход от услуг');

    expect(screen.getByRole('button', { name: 'Редактировать Доход от услуг' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Удалить Доход от услуг' })).toBeTruthy();
  });

  it('hides row actions when dictionary role permissions are read-only', async () => {
    permissionMocks.useRolePermissions.mockReturnValue({
      ...defaultRolePermissions,
      dictionaries: {
        ...defaultRolePermissions.dictionaries,
        canView: true,
        canCreate: false,
        canEdit: false,
        canDelete: false,
      },
    });

    render(<Dictionaries />);

    await waitFor(() => expect(apiMocks.getIncomeTypes).toHaveBeenCalled());
    await userEvent.click(screen.getByRole('button', { name: /Тип дохода/ }));
    await screen.findByText('Доход от услуг');

    expect(screen.queryByRole('button', { name: 'Редактировать Доход от услуг' })).toBeFalsy();
    expect(screen.queryByRole('button', { name: 'Удалить Доход от услуг' })).toBeFalsy();
  });

  it('keeps a single new dictionary draft when Add is clicked repeatedly', async () => {
    const user = userEvent.setup();
    render(<Dictionaries />);

    await waitFor(() => expect(apiMocks.getIncomeTypes).toHaveBeenCalled());
    const addButton = screen.getByRole('button', { name: 'Добавить' });
    await user.click(addButton);
    await user.click(addButton);

    expect(screen.getAllByPlaceholderText('Например: Консультация')).toHaveLength(1);
    expect(screen.getByText('Всего: 0')).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByPlaceholderText('Например: Консультация'));
  });
});
