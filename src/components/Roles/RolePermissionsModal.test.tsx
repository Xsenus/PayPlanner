import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { defaultRestrictedRolePermissions } from '../../types/permissions';
import { setRolePermissions } from '../../services/permissionsService';
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
  apiService: { getRoleCalendarVisibleUserOptions: vi.fn().mockResolvedValue([]) },
}));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'Asia/Novosibirsk' }));

beforeEach(() => vi.clearAllMocks());

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
