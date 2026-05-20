import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import { RolePermissionAuditPanel } from './RolePermissionAuditPanel';

vi.mock('../../services/api', () => ({
  apiService: {
    getRolePermissionsAudit: vi.fn(),
    exportRolePermissionsAuditCsv: vi.fn(),
  },
}));

describe('RolePermissionAuditPanel', () => {
  beforeEach(() => {
    vi.mocked(apiService.getRolePermissionsAudit).mockReset();
    vi.mocked(apiService.exportRolePermissionsAuditCsv).mockReset();
  });

  it('shows role readiness diagnostics', async () => {
    vi.mocked(apiService.getRolePermissionsAudit).mockResolvedValue({
      generatedAtUtc: '2026-05-20T06:00:00Z',
      totalRoles: 3,
      readyRoles: 2,
      rolesWithIssues: 1,
      rolesWithBroadDataScope: 1,
      catalogSections: ['clients', 'cases'],
      dataScopedSections: ['clients', 'cases'],
      roles: [
        {
          roleId: 10,
          roleName: 'lawyer',
          isSystemRole: false,
          assignedUsersCount: 4,
          configuredSectionsCount: 13,
          totalSectionsCount: 14,
          hasIssues: true,
          missingSections: ['cases'],
          unknownSections: [],
          duplicateSections: [],
          invalidViewScopeSections: ['clients:global'],
          capabilityWithoutViewSections: [],
          restrictedCapabilitySections: [],
          broadDataScopeSections: ['clients'],
        },
      ],
    });

    render(<RolePermissionAuditPanel />);

    expect(await screen.findByText('Аудит готовности ролей')).toBeTruthy();
    expect(screen.getByText('lawyer')).toBeTruthy();
    expect(screen.getByText(/Готово: 2 из 3\. Проблемы: 1\. Широкий доступ: 1\./)).toBeTruthy();
    expect(screen.getByText(/Strict-проверка: требуется согласовать роли с широкой видимостью данных \(1\)\./)).toBeTruthy();
    expect(screen.getByText(/нет секций: 1/)).toBeTruthy();
    expect(screen.getByText(/scope: 1/)).toBeTruthy();
    expect(screen.getByText(/Полная видимость: clients/)).toBeTruthy();
  });

  it('shows strict warning when broad scope is the only concern', async () => {
    vi.mocked(apiService.getRolePermissionsAudit).mockResolvedValue({
      generatedAtUtc: '2026-05-20T06:00:00Z',
      totalRoles: 1,
      readyRoles: 1,
      rolesWithIssues: 0,
      rolesWithBroadDataScope: 1,
      catalogSections: ['clients'],
      dataScopedSections: ['clients'],
      roles: [
        {
          roleId: 12,
          roleName: 'manager',
          isSystemRole: false,
          assignedUsersCount: 2,
          configuredSectionsCount: 1,
          totalSectionsCount: 1,
          hasIssues: false,
          missingSections: [],
          unknownSections: [],
          duplicateSections: [],
          invalidViewScopeSections: [],
          capabilityWithoutViewSections: [],
          restrictedCapabilitySections: [],
          broadDataScopeSections: ['clients'],
        },
      ],
    });

    render(<RolePermissionAuditPanel />);

    expect(await screen.findByText(/Strict-проверка: требуется согласовать роли с широкой видимостью данных \(1\)\./)).toBeTruthy();
    expect(screen.getByText('manager')).toBeTruthy();
    expect(screen.getByText('Strict')).toBeTruthy();
    expect(screen.getByText(/без критичных замечаний/)).toBeTruthy();
  });

  it('refreshes audit on demand', async () => {
    vi.mocked(apiService.getRolePermissionsAudit)
      .mockResolvedValueOnce({
        generatedAtUtc: '2026-05-20T06:00:00Z',
        totalRoles: 1,
        readyRoles: 1,
        rolesWithIssues: 0,
        rolesWithBroadDataScope: 0,
        catalogSections: [],
        dataScopedSections: [],
        roles: [],
      })
      .mockResolvedValueOnce({
        generatedAtUtc: '2026-05-20T07:00:00Z',
        totalRoles: 2,
        readyRoles: 2,
        rolesWithIssues: 0,
        rolesWithBroadDataScope: 0,
        catalogSections: [],
        dataScopedSections: [],
        roles: [],
      });

    const user = userEvent.setup();
    render(<RolePermissionAuditPanel />);

    await screen.findByText(/Готово: 1 из 1/);
    await user.click(screen.getByRole('button', { name: /Обновить/i }));

    await waitFor(() => {
      expect(apiService.getRolePermissionsAudit).toHaveBeenCalledTimes(2);
    });
    expect(await screen.findByText(/Готово: 2 из 2/)).toBeTruthy();
  });

  it('downloads audit csv on demand', async () => {
    vi.mocked(apiService.getRolePermissionsAudit).mockResolvedValue({
      generatedAtUtc: '2026-05-20T06:00:00Z',
      totalRoles: 1,
      readyRoles: 1,
      rolesWithIssues: 0,
      rolesWithBroadDataScope: 0,
      catalogSections: [],
      dataScopedSections: [],
      roles: [],
    });
    vi.mocked(apiService.exportRolePermissionsAuditCsv).mockResolvedValue({
      blob: new Blob(['csv'], { type: 'text/csv' }),
      fileName: 'role-audit.csv',
    });
    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:role-audit');
    const revokeObjectURL = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const click = vi.fn();
    const appendChild = vi.spyOn(document.body, 'appendChild');
    const createElement = vi.spyOn(document, 'createElement');
    createElement.mockImplementation(((tagName: string) => {
      const element = document.createElementNS('http://www.w3.org/1999/xhtml', tagName) as HTMLElement;
      if (tagName.toLowerCase() === 'a') {
        Object.defineProperty(element, 'click', { value: click });
      }
      return element;
    }) as typeof document.createElement);

    const user = userEvent.setup();
    render(<RolePermissionAuditPanel />);

    await screen.findByText(/Готово: 1 из 1/);
    await user.click(screen.getByRole('button', { name: /CSV/i }));

    await waitFor(() => {
      expect(apiService.exportRolePermissionsAuditCsv).toHaveBeenCalledTimes(1);
    });
    expect(createObjectURL).toHaveBeenCalled();
    expect(appendChild).toHaveBeenCalled();
    expect(click).toHaveBeenCalled();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:role-audit');
  });
});
