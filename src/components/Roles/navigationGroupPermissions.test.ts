import { expect, it } from 'vitest';
import { defaultRolePermissions } from '../../types/permissions';
import { navigationGroupSections, revokeNavigationGroup } from './navigationGroupPermissions';

it('resolves shared and administrative menu entries and refuses unknown items', () => {
  const result = navigationGroupSections({ id: 'g', name: 'Управление', itemIds: ['users', 'roles', 'settings', 'audit', '__proto__'] });
  expect(result.unsupported).toEqual(['__proto__']);
  expect(result.sections).toEqual(['roles', 'audit', 'notifications', 'integrations', 'yandexDisk', 's3Storage']);
});

it('clears all capabilities for affected sections without changing independent permissions', () => {
  const input = structuredClone(defaultRolePermissions);
  const result = revokeNavigationGroup(input, ['calendar', 'tasks', 'requests']);
  expect(result.calendar).toMatchObject({ canView: false, canEdit: false, canViewAnalytics: false });
  expect(result.tasks).toMatchObject({ canView: false, canCreate: false, canEditOthers: false, canCompleteOthers: false, canDeleteOthers: false });
  expect(result.requests).toMatchObject({ canView: false, canViewAnalytics: false });
  expect(result.accounts).toEqual(input.accounts);
  expect(input.tasks.canView).toBe(true);
});
