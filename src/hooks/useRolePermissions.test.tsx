import { act, renderHook, waitFor } from '@testing-library/react';
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

  it('refreshes a role on window focus and shares the request between mounted consumers', async () => {
    const roleId = 9020;
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(true, 'all'));
    const first = renderHook(() => useRolePermissions(roleId));
    const second = renderHook(() => useRolePermissions(roleId));
    await waitFor(() => expect(first.result.current.tasks.canView).toBe(true));
    await waitFor(() => expect(second.result.current.tasks.canView).toBe(true));
    apiServiceMock.getRolePermissions.mockClear();
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(false, 'own'));

    act(() => window.dispatchEvent(new Event('focus')));
    await waitFor(() => expect(first.result.current.tasks.canView).toBe(false));
    await waitFor(() => expect(second.result.current.tasks.canView).toBe(false));
    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledTimes(1);
    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledWith(roleId);
  });

  it('refreshes once across consumers on a realtime calendar access change', async () => {
    const roleId = 9021;
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(true, 'all'));
    const first = renderHook(() => useRolePermissions(roleId));
    const second = renderHook(() => useRolePermissions(roleId));
    await waitFor(() => expect(first.result.current.tasks.canView).toBe(true));
    apiServiceMock.getRolePermissions.mockClear();
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(false, 'own'));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'created', notificationType: 'calendarAccessChanged' },
    })));
    await waitFor(() => expect(first.result.current.tasks.canView).toBe(false));
    await waitFor(() => expect(second.result.current.tasks.canView).toBe(false));
    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledTimes(1);
  });

  it('refreshes role permissions when realtime reconnects', async () => {
    const roleId = 9022;
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(true, 'all'));
    const { result } = renderHook(() => useRolePermissions(roleId));
    await waitFor(() => expect(result.current.tasks.canView).toBe(true));
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(false, 'own'));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'reconnected' },
    })));
    await waitFor(() => expect(result.current.tasks.canView).toBe(false));
  });

  it('refreshes when the server signals a change to this user’s role', async () => {
    const roleId = 9024;
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(true, 'all'));
    const { result } = renderHook(() => useRolePermissions(roleId));
    await waitFor(() => expect(result.current.tasks.canView).toBe(true));
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(false, 'own'));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'role-permissions-changed' },
    })));
    await waitFor(() => expect(result.current.tasks.canView).toBe(false));
  });

  it('does not reuse an older in-flight permissions request after a server role-change signal', async () => {
    const roleId = 9025;
    let resolveOld!: (permissions: RolePermissions) => void;
    apiServiceMock.getRolePermissions.mockReturnValueOnce(new Promise<RolePermissions>((resolve) => { resolveOld = resolve; }));
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(false, 'own'));
    const first = renderHook(() => useRolePermissions(roleId));
    const second = renderHook(() => useRolePermissions(roleId));
    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledTimes(1);
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'role-permissions-changed' },
    })));
    await waitFor(() => expect(apiServiceMock.getRolePermissions).toHaveBeenCalledTimes(2));
    await act(async () => { resolveOld(permissionsWithTaskScope(true, 'all')); });
    expect(first.result.current.tasks.canView).toBe(false);
    expect(second.result.current.tasks.canView).toBe(false);
  });

  it('honours a second role-change signal while the first refresh is still in flight', async () => {
    const roleId = 9026;
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(true, 'all'));
    const first = renderHook(() => useRolePermissions(roleId));
    const second = renderHook(() => useRolePermissions(roleId));
    await waitFor(() => expect(first.result.current.tasks.canView).toBe(true));
    apiServiceMock.getRolePermissions.mockClear();
    let resolveOld!: (permissions: RolePermissions) => void;
    apiServiceMock.getRolePermissions.mockReturnValueOnce(new Promise<RolePermissions>((resolve) => { resolveOld = resolve; }));
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(false, 'own'));
    const signal = () => new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'role-permissions-changed' },
    });
    act(() => window.dispatchEvent(signal()));
    act(() => window.dispatchEvent(signal()));
    await waitFor(() => expect(first.result.current.tasks.canView).toBe(false));
    await act(async () => { resolveOld(permissionsWithTaskScope(true, 'all')); });
    expect(second.result.current.tasks.canView).toBe(false);
    expect(apiServiceMock.getRolePermissions).toHaveBeenCalledTimes(2);
  });

  it('ignores unrelated notifications and removes the listener on unmount', async () => {
    const roleId = 9023;
    apiServiceMock.getRolePermissions.mockResolvedValue(permissionsWithTaskScope(true, 'all'));
    const { result, unmount } = renderHook(() => useRolePermissions(roleId));
    await waitFor(() => expect(result.current.tasks.canView).toBe(true));
    apiServiceMock.getRolePermissions.mockClear();
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'created', notificationType: 'taskReminder' },
    })));
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'system', reason: 'created', notificationType: 'calendarAccessChanged' },
    })));
    expect(apiServiceMock.getRolePermissions).not.toHaveBeenCalled();
    unmount();
    act(() => window.dispatchEvent(new CustomEvent('payplanner:user-notifications-changed', {
      detail: { source: 'realtime', reason: 'created', notificationType: 'calendarAccessChanged' },
    })));
    expect(apiServiceMock.getRolePermissions).not.toHaveBeenCalled();
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
