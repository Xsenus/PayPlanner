import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ObjectAuditLauncher } from './ObjectAuditModal';

const testState = vi.hoisted(() => ({
  isAdmin: vi.fn(),
  permissions: {
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
    user: { id: 7, role: { id: 3, name: 'lawyer' } },
    isAdmin: testState.isAdmin,
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => testState.permissions,
}));

describe('ObjectAuditLauncher access', () => {
  beforeEach(() => {
    testState.isAdmin.mockReturnValue(false);
    testState.permissions.audit.canView = false;
  });

  it('hides object history launcher without audit access', () => {
    render(
      <ObjectAuditLauncher
        objectType="client"
        objectId={10}
        triggerTitle="История клиента"
      />,
    );

    expect(screen.queryByText('История клиента')).toBeNull();
    expect(screen.queryByRole('button', { name: 'История' })).toBeNull();
  });

  it('shows object history launcher with audit access', () => {
    testState.permissions.audit.canView = true;

    render(
      <ObjectAuditLauncher
        objectType="client"
        objectId={10}
        triggerTitle="История клиента"
      />,
    );

    expect(screen.getByText('История клиента')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'История' })).toBeTruthy();
  });

  it('shows object history launcher for admin', () => {
    testState.isAdmin.mockReturnValue(true);

    render(
      <ObjectAuditLauncher
        objectType="client"
        objectId={10}
        triggerTitle="История клиента"
      />,
    );

    expect(screen.getByText('История клиента')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'История' })).toBeTruthy();
  });
});
