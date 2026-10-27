import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { defaultRestrictedRolePermissions } from '../../types/permissions';
import { setRolePermissions } from '../../services/permissionsService';
import { apiService } from '../../services/api';
import { RolePermissionsModal } from './RolePermissionsModal';
import type { Role } from './types';

vi.mock('../../services/permissionsService', () => ({
  getRolePermissions: () => structuredClone(defaultRestrictedRolePermissions),
  refreshRolePermissions: vi.fn().mockResolvedValue(undefined),
  resetRolePermissions: vi.fn(),
  setRolePermissions: vi.fn().mockResolvedValue(undefined),
  subscribeOnPermissionsChange: () => () => {},
}));
vi.mock('../../services/api', () => ({
  apiService: {
    getRoleCalendarVisibleUserOptions: vi.fn().mockResolvedValue([]),
    getNavigationSettings: vi.fn().mockResolvedValue({ groups: [
      { id: 'accounting', name: 'Бухгалтерия', itemIds: ['accounts', 'acts', 'contracts', 'payments'] },
      { id: 'management', name: 'Управление', itemIds: ['users', 'roles', 'dictionaries', 'settings'] },
      { id: 'unsupported', name: 'Рабочая', itemIds: ['workspace'] },
    ] }),
  },
}));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'Asia/Novosibirsk' }));

beforeEach(() => vi.clearAllMocks());

it('revokes the real permissions of a navigation group without changing other sections', async () => {
  const user = userEvent.setup();
  render(<RolePermissionsModal role={{ id: 80, name: 'Секретарь' } as Role} canEdit onClose={() => {}} />);
  await user.click(screen.getByRole('button', { name: /Клиенты и документы/ }));
  const contract = screen.getByRole('heading', { name: 'Договоры' }).closest('section')!;
  const client = screen.getByRole('heading', { name: 'Клиенты' }).closest('section')!;
  await user.click(within(contract).getByRole('checkbox', { name: /^Просмотр(?:\s|$)/ }));
  await user.click(within(client).getByRole('checkbox', { name: /^Просмотр(?:\s|$)/ }));
  const groupButton = await screen.findByRole('button', { name: 'Закрыть «Бухгалтерия»' });
  await user.click(groupButton);
  expect(within(groupButton.parentElement!).getByRole('status').textContent).toContain('Права разделов сняты в форме');
  expect((screen.getByRole('button', { name: 'Закрыть «Рабочая»' }) as HTMLButtonElement).disabled).toBe(true);
  await user.click(screen.getByRole('button', { name: /Сохранить/ }));
  await waitFor(() => expect(setRolePermissions).toHaveBeenLastCalledWith(80, expect.objectContaining({
    contracts: expect.objectContaining({ canView: false, canEdit: false }),
    accounts: expect.objectContaining({ canView: false }),
    clients: expect.objectContaining({ canView: true }),
  })));
});

it('does not allow group permission changes for a protected role', async () => {
  render(<RolePermissionsModal role={{ id: 1, name: 'Администратор', isSystemRole: true } as Role} canEdit onClose={() => {}} />);
  const button = await screen.findByRole('button', { name: 'Закрыть «Управление»' });
  expect((button as HTMLButtonElement).disabled).toBe(true);
});

it('keeps the form open and the proposed group changes when saving fails', async () => {
  const user = userEvent.setup();
  const onClose = vi.fn();
  vi.mocked(setRolePermissions).mockRejectedValueOnce(new Error('Нет связи'));
  render(<RolePermissionsModal role={{ id: 85, name: 'Секретарь' } as Role} canEdit onClose={onClose} />);
  await user.click(screen.getByRole('button', { name: /Клиенты и документы/ }));
  const contract = screen.getByRole('heading', { name: 'Договоры' }).closest('section')!;
  await user.click(within(contract).getByRole('checkbox', { name: /^Просмотр(?:\s|$)/ }));
  const groupButton = await screen.findByRole('button', { name: 'Закрыть «Бухгалтерия»' });
  await user.click(groupButton);
  await user.click(screen.getByRole('button', { name: /Сохранить/ }));
  expect(await screen.findByText('Нет связи')).toBeTruthy();
  expect(onClose).not.toHaveBeenCalled();
  expect(within(groupButton.parentElement!).getByRole('status')).toBeTruthy();
  expect((within(contract).getByRole('checkbox', { name: /^Просмотр(?:\s|$)/ }) as HTMLInputElement).checked).toBe(false);
  await user.click(screen.getByRole('button', { name: /Сохранить/ }));
  await waitFor(() => expect(onClose).toHaveBeenCalledOnce());
});

it('does not offer group actions if the navigation settings cannot be loaded', async () => {
  vi.mocked(apiService.getNavigationSettings).mockRejectedValueOnce(new Error('Нет связи'));
  render(<RolePermissionsModal role={{ id: 86, name: 'Секретарь' } as Role} canEdit onClose={() => {}} />);
  expect(await screen.findByText(/Не удалось загрузить группы меню/)).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Закрыть «Бухгалтерия»' })).toBeNull();
  expect(screen.getByRole('heading', { name: 'Календарь' })).toBeTruthy();
});

it('saves company profile view and edit separately without granting financial access', async () => {
  const user = userEvent.setup();
  render(<RolePermissionsModal role={{ id: 82, name: 'Сотрудник' } as Role} canEdit onClose={() => {}} />);
  await user.click(screen.getByRole('button', { name: /Администрирование прав/ }));
  const section = screen.getByRole('heading', { name: 'Профиль компании' }).closest('section')!;
  expect(within(section).getAllByRole('checkbox')).toHaveLength(2);
  await user.click(within(section).getByRole('checkbox', { name: /^Просмотр(?:\s|$)/ }));
  await user.click(screen.getByRole('button', { name: /Сохранить/ }));
  await waitFor(() => expect(setRolePermissions).toHaveBeenLastCalledWith(82, expect.objectContaining({ companyProfile: expect.objectContaining({ canView: true, canEdit: false }), accounts: expect.objectContaining({ canView: false }), acts: expect.objectContaining({ canView: false }) })));
  await user.click(within(section).getByRole('checkbox', { name: /^Редактирование(?:\s|$)/ }));
  await user.click(screen.getByRole('button', { name: /Сохранить/ }));
  await waitFor(() => expect(setRolePermissions).toHaveBeenLastCalledWith(82, expect.objectContaining({ companyProfile: expect.objectContaining({ canView: true, canEdit: true }) })));
});

it('saves the separate request analytics grant and clears it when viewing is revoked', async () => {
  const user = userEvent.setup();
  render(<RolePermissionsModal role={{ id: 81, name: 'Менеджер' } as Role} canEdit onClose={() => {}} />);
  await user.click(screen.getByRole('button', { name: /Клиенты и документы/ }));
  const section = screen.getByRole('heading', { name: 'Заявки' }).closest('section')!;
  const analytics = within(section).getByRole('checkbox', { name: /Аналитика заявок/ });
  expect((analytics as HTMLInputElement).disabled).toBe(true);
  const view = within(section).getByRole('checkbox', { name: /^Просмотр(?:\s|$)/ });
  await user.click(view);
  await user.click(analytics);
  await user.click(screen.getByRole('button', { name: /Сохранить/ }));
  await waitFor(() => expect(setRolePermissions).toHaveBeenCalledWith(81, expect.objectContaining({
    requests: expect.objectContaining({ canView: true, canViewAnalytics: true, viewScope: 'own' }),
  })));
  await user.click(view);
  expect((analytics as HTMLInputElement).checked).toBe(false);
  expect((analytics as HTMLInputElement).disabled).toBe(true);
  await user.click(screen.getByRole('button', { name: /Сохранить/ }));
  await waitFor(() => expect(setRolePermissions).toHaveBeenLastCalledWith(81, expect.objectContaining({
    requests: expect.objectContaining({ canView: false, canViewAnalytics: false }),
  })));
});
