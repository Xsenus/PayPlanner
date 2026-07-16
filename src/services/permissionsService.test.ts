import { describe, expect, it, vi } from 'vitest';

vi.mock('./api', () => ({
  apiService: {
    getRolePermissions: vi.fn(),
    updateRolePermissions: vi.fn(),
    resetRolePermissions: vi.fn(),
  },
}));

describe('permissionsService', () => {
  it('keeps permissions restricted when role id is not available yet', async () => {
    vi.resetModules();
    const { apiService } = await import('./api');
    const { getRolePermissions, hasLoadedRolePermissions } = await import('./permissionsService');

    const permissions = getRolePermissions(undefined);

    expect(permissions.tasks.canView).toBe(false);
    expect(permissions.tasks.canEditOthers).toBe(false);
    expect(permissions.tasks.canCompleteOthers).toBe(false);
    expect(permissions.tasks.canDeleteOthers).toBe(false);
    expect(permissions.clients.canView).toBe(false);
    expect(permissions.payments.canView).toBe(false);
    expect(hasLoadedRolePermissions(undefined)).toBe(true);
    expect(apiService.getRolePermissions).not.toHaveBeenCalled();
  });

  it('keeps role permissions restricted while server permissions are loading', async () => {
    vi.resetModules();
    const { apiService } = await import('./api');
    vi.mocked(apiService.getRolePermissions).mockResolvedValue({
      tasks: {
        canView: true,
        canCreate: true,
        canEdit: true,
        canDelete: false,
        canExport: false,
        viewScope: 'own',
        canEditOthers: true,
        canCompleteOthers: false,
        canDeleteOthers: true,
      },
    } as never);

    const {
      getRolePermissions,
      hasLoadedRolePermissions,
      refreshRolePermissions,
    } = await import('./permissionsService');

    const initial = getRolePermissions(42);

    expect(initial.tasks.canView).toBe(false);
    expect(initial.payments.canView).toBe(false);
    expect(hasLoadedRolePermissions(42)).toBe(false);
    expect(apiService.getRolePermissions).toHaveBeenCalledWith(42);

    await refreshRolePermissions(42);
    const loaded = getRolePermissions(42);

    expect(loaded.tasks.canView).toBe(true);
    expect(loaded.tasks.canEditOthers).toBe(true);
    expect(loaded.tasks.canCompleteOthers).toBe(false);
    expect(loaded.tasks.canDeleteOthers).toBe(true);
    expect(loaded.payments.canView).toBe(false);
    expect(hasLoadedRolePermissions(42)).toBe(true);
  });

  it('does not open missing sections when the server returns a partial permissions dto', async () => {
    vi.resetModules();
    const { apiService } = await import('./api');
    vi.mocked(apiService.getRolePermissions).mockResolvedValue({
      clients: {
        canView: true,
        canCreate: true,
        canEdit: true,
        canDelete: false,
        canExport: false,
        viewScope: 'own',
      },
    } as never);

    const { refreshRolePermissions, getRolePermissions } = await import('./permissionsService');

    await refreshRolePermissions(7);
    const permissions = getRolePermissions(7);

    expect(permissions.clients.canView).toBe(true);
    expect(permissions.cases.canView).toBe(false);
    expect(permissions.accounts.canView).toBe(false);
    expect(permissions.payments.canView).toBe(false);
    expect(permissions.roles.canView).toBe(false);
  });
});
