import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../test/apiMock';
import { defaultRolePermissions, type RolePermissions } from '../types/permissions';
import { useRolePermissions, useRolePermissionsLoaded } from './useRolePermissions';

vi.mock('../services/api', async () => ({
  apiService: (await import('../test/apiMock')).apiServiceMock,
}));

function permissionsWithTaskScope(canView: boolean, viewScope: RolePermissions['tasks']['viewScope']) {
  return {
    ...defaultRolePermissions,
    tasks: {
      ...defaultRolePermissions.tasks,
      canView,
      viewScope,
    },
  };
}

describe('useRolePermissions', () => {
  beforeEach(() => {
    resetApiServiceMock('getRolePermissions');
  });

  it('returns restricted permissions immediately and refreshes when role permissions load', async () => {
    const roleId = 9001;
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(true, 'all'));

    const { result } = renderHook(() => useRolePermissions(roleId));

    expect(result.current.tasks.canView).toBe(false);
    expect(result.current.tasks.viewScope).toBe('own');

    await waitFor(() => {
      expect(result.current.tasks.canView).toBe(true);
      expect(result.current.tasks.viewScope).toBe('all');
    });

    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledWith(roleId);
  });

  it('loads permissions for the next role when role id changes', async () => {
    const firstRoleId = 9002;
    const secondRoleId = 9003;
    apiServiceMock.getRolePermissions.mockImplementation(async (roleId: number) =>
      roleId === firstRoleId
        ? permissionsWithTaskScope(false, 'own')
        : permissionsWithTaskScope(true, 'department'),
    );

    const { result, rerender } = renderHook(
      ({ roleId }) => useRolePermissions(roleId),
      { initialProps: { roleId: firstRoleId } },
    );

    await waitFor(() => {
      expect(result.current.tasks.canView).toBe(false);
    });

    rerender({ roleId: secondRoleId });

    await waitFor(() => {
      expect(result.current.tasks.canView).toBe(true);
      expect(result.current.tasks.viewScope).toBe('department');
    });

    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledWith(firstRoleId);
    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledWith(secondRoleId);
  });

  it('does not report permissions as loaded for a newly assigned role before its request finishes', async () => {
    const roleId = 9010;
    let resolvePermissions!: (permissions: RolePermissions) => void;
    apiServiceMock.getRolePermissions.mockReturnValue(
      new Promise<RolePermissions>((resolve) => {
        resolvePermissions = resolve;
      }),
    );

    const { result, rerender } = renderHook(
      ({ nextRoleId }) => {
        useRolePermissions(nextRoleId);
        return useRolePermissionsLoaded(nextRoleId);
      },
      { initialProps: { nextRoleId: undefined as number | undefined } },
    );

    expect(result.current).toBe(true);

    rerender({ nextRoleId: roleId });

    expect(result.current).toBe(false);

    resolvePermissions(permissionsWithTaskScope(true, 'all'));

    await waitFor(() => {
      expect(result.current).toBe(true);
    });

    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledWith(roleId);
  });
});
