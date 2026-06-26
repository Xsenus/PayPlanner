import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { Roles } from './Roles';
import { authService } from '../../services/authService';

const testState = vi.hoisted(() => ({
  isAdmin: vi.fn(),
  permissions: {
    roles: {
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: true,
      canExport: false,
      viewScope: 'all',
    },
    audit: {
      canView: false,
      canCreate: false,
      canEdit: false,
      canDelete: false,
      canExport: false,
      viewScope: 'own',
    },
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 7, role: { id: 2, name: 'manager' } },
    isAdmin: testState.isAdmin,
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => testState.permissions,
}));

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => 'Europe/Samara',
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn().mockResolvedValue(true),
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
  }),
}));

vi.mock('../../hooks/usePersistedTableSettings', () => ({
  usePersistedTableSettings: () => ({
    columnOrder: ['name', 'description', 'users', 'updated', 'type', 'access'],
    orderedVisibleColumns: ['name', 'description', 'users', 'updated', 'type', 'access'],
    visibleColumns: {
      name: true,
      description: true,
      users: true,
      updated: true,
      type: true,
      access: true,
    },
    pageSize: 10,
    setPageSize: vi.fn(),
    draggingColumn: null,
    toggleColumn: vi.fn(),
    moveColumn: vi.fn(),
    handleColumnDragStart: vi.fn(),
    handleColumnDragOver: vi.fn(),
    handleColumnDrop: vi.fn(),
    handleColumnDragEnd: vi.fn(),
    handleResizeStart: vi.fn(),
    getColumnWidth: vi.fn(() => 180),
    getTableWidth: vi.fn(() => 1080),
    resetTableSettings: vi.fn(),
  }),
}));

vi.mock('../../services/authService', () => ({
  authService: {
    getRolesPaged: vi.fn(),
    deleteRole: vi.fn(),
  },
}));

vi.mock('./RolePermissionAuditPanel', () => ({
  RolePermissionAuditPanel: () => <div>Аудит готовности ролей</div>,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: ({ triggerTitle }: { triggerTitle: string }) => <div>{triggerTitle}</div>,
}));

function mockRolesResponse() {
  vi.mocked(authService.getRolesPaged).mockResolvedValue({
    items: [
      {
        id: 10,
        name: 'Юрист',
        description: 'Ограниченная роль',
        isSystemRole: false,
        assignedUsersCount: 0,
        accessibleSectionsCount: 2,
        totalSectionsCount: 19,
        createdAt: '2026-06-25T05:00:00Z',
        permissionsUpdatedAt: '2026-06-25T06:00:00Z',
        permissionsUpdatedByUserName: 'Администратор',
      },
    ],
    total: 1,
    page: 1,
    pageSize: 10,
  });
}

describe('Roles access boundaries', () => {
  beforeEach(() => {
    testState.isAdmin.mockReturnValue(false);
    Object.assign(testState.permissions.roles, {
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: true,
      canExport: false,
    });
    Object.assign(testState.permissions.audit, {
      canView: false,
      canCreate: false,
      canEdit: false,
      canDelete: false,
      canExport: false,
    });
    vi.mocked(authService.getRolesPaged).mockReset();
    mockRolesResponse();
  });

  it('keeps role management visible but hides audit widgets without audit access', async () => {
    render(<Roles />);

    expect(await screen.findByText('Юрист')).toBeTruthy();
    expect(screen.queryByText('Аудит готовности ролей')).toBeNull();
    expect(screen.queryByText('Аудит роли')).toBeNull();
  });

  it('shows role audit widgets when the role has audit access', async () => {
    testState.permissions.audit.canView = true;

    render(<Roles />);

    expect(await screen.findByText('Юрист')).toBeTruthy();
    expect(screen.getByText('Аудит готовности ролей')).toBeTruthy();
    expect(screen.getByText('Аудит роли')).toBeTruthy();
  });
});
