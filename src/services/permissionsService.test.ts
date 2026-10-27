// @vitest-environment node

import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./api', () => ({
  apiService: {
    getRolePermissions: vi.fn(),
    updateRolePermissions: vi.fn(),
    resetRolePermissions: vi.fn(),
  },
}));

describe('permissionsService', () => {
  beforeEach(() => vi.clearAllMocks());
  it('does not restore stale rights when a pending refresh completes after a role is saved', async () => {
    vi.resetModules();
    const { apiService } = await import('./api');
    const { defaultRolePermissions } = await import('../types/permissions');
    const { refreshRolePermissions, setRolePermissions, getRolePermissions } = await import('./permissionsService');
    let resolveOld!: (value: typeof defaultRolePermissions) => void;
    vi.mocked(apiService.getRolePermissions).mockReturnValue(new Promise((resolve) => { resolveOld = resolve; }));
    vi.mocked(apiService.updateRolePermissions).mockResolvedValue(undefined as never);
    const oldRefresh = refreshRolePermissions(83);
    const restricted = structuredClone(defaultRolePermissions);
    restricted.accounts.canView = false;
    await setRolePermissions(83, restricted);
    resolveOld(defaultRolePermissions);
    await oldRefresh;
    expect(getRolePermissions(83).accounts.canView).toBe(false);
  });
  it('allows a fresh request after reset while ignoring the older response', async () => {
    vi.resetModules();
    const { apiService } = await import('./api');
    const { defaultRolePermissions } = await import('../types/permissions');
    const { refreshRolePermissions, resetRolePermissions, getRolePermissions } = await import('./permissionsService');
    let resolveOld!: (value: typeof defaultRolePermissions) => void;
    vi.mocked(apiService.getRolePermissions).mockReturnValueOnce(new Promise((resolve) => { resolveOld = resolve; }));
    const restricted = structuredClone(defaultRolePermissions);
    restricted.accounts.canView = false;
    vi.mocked(apiService.getRolePermissions).mockResolvedValueOnce(restricted);
    vi.mocked(apiService.resetRolePermissions).mockResolvedValue(undefined as never);
    const oldRefresh = refreshRolePermissions(84);
    await resetRolePermissions(84);
    await refreshRolePermissions(84);
    resolveOld(defaultRolePermissions);
    await oldRefresh;
    expect(getRolePermissions(84).accounts.canView).toBe(false);
    expect(apiService.getRolePermissions).toHaveBeenCalledTimes(2);
  });
  it('preserves request analytics explicitly granted by the server and restricts missing grants', async () => {
    vi.resetModules();
    const { apiService } = await import('./api');
    const { getRolePermissions, refreshRolePermissions } = await import('./permissionsService');
    expect(getRolePermissions(undefined).requests.canViewAnalytics).toBe(false);
    vi.mocked(apiService.getRolePermissions).mockResolvedValue({
      requests: { canView: true, canViewAnalytics: true, viewScope: 'own' },
    } as never);
    await refreshRolePermissions(81);
    expect(getRolePermissions(81).requests.canViewAnalytics).toBe(true);
    expect(getRolePermissions(81).requests.viewScope).toBe('own');
    vi.mocked(apiService.getRolePermissions).mockResolvedValue({ requests: { canView: true } } as never);
    await refreshRolePermissions(82);
    expect(getRolePermissions(82).requests.canViewAnalytics).toBe(false);
  });

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
