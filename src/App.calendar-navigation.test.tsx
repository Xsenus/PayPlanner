import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import App from './App';

vi.mock('./contexts/AuthContext', () => ({
  AuthProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
  useAuth: () => ({
    user: {
      id: 1,
      email: 'admin@example.test',
      fullName: 'Admin',
      role: { id: 1, name: 'admin' },
      isActive: true,
      isApproved: true,
      isEmployee: true,
      createdAt: '2026-05-07T00:00:00Z',
    },
    loading: false,
    signOut: vi.fn(),
    isAdmin: () => true,
    isManager: () => true,
    refreshUser: vi.fn(),
  }),
}));

vi.mock('./hooks/useActivityLogger', () => ({
  useActivityLogger: () => vi.fn(),
}));

vi.mock('./hooks/useRolePermissions', () => {
  const section = {
    canView: true,
    canCreate: true,
    canEdit: true,
    canDelete: true,
    canExport: true,
  };

  return {
    useRolePermissions: () => ({
      calendar: { ...section, canViewAnalytics: true },
      tasks: section,
      reports: section,
      calculator: section,
      legalEntities: section,
      clients: section,
      accounts: section,
      acts: section,
      payments: section,
      contracts: section,
      dictionaries: section,
      roles: section,
      audit: section,
    }),
  };
});

vi.mock('./components/Navigation/Navigation', () => ({
  default: ({ activeTab }: { activeTab: string }) => <nav data-testid="navigation">{activeTab}</nav>,
}));

vi.mock('./components/TopBar/TopBar', () => ({
  default: ({ title }: { title: string }) => <header data-testid="topbar">{title}</header>,
}));

vi.mock('./components/ui/DoubleHorizontalScrollbars', () => ({
  DoubleHorizontalScrollbars: () => null,
}));

vi.mock('./components/Calendar/Calendar', () => ({
  Calendar: ({ onOpenClient }: { onOpenClient?: (clientId: number, caseId?: number) => void }) => (
    <section data-testid="calendar-view">
      <button type="button" onClick={() => onOpenClient?.(42, 7)}>
        open calendar client
      </button>
    </section>
  ),
}));

vi.mock('./components/Clients/ClientDetail', () => ({
  ClientDetail: ({ clientId, onBack }: { clientId: number; onBack: () => void }) => (
    <section data-testid="client-detail">
      <span>client {clientId}</span>
      <button type="button" onClick={onBack}>
        back from client
      </button>
    </section>
  ),
}));

vi.mock('./components/Clients/Clients', () => ({
  Clients: () => <section data-testid="clients-view">clients list</section>,
}));

describe('App calendar client navigation', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/?ppTab=calendar');
    window.localStorage.clear();
    window.sessionStorage.clear();
  });

  it('returns to calendar after opening a client from calendar', async () => {
    render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: 'open calendar client' }));

    expect(await screen.findByTestId('client-detail')).toBeTruthy();
    expect(screen.getByText('client 42')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'back from client' }));

    await waitFor(() => {
      expect(screen.queryByTestId('client-detail')).toBeNull();
      expect(screen.queryByTestId('clients-view')).toBeNull();
      expect(screen.getByTestId('calendar-view')).toBeTruthy();
    });
  });
});
