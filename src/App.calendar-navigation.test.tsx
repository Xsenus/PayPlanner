import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import App from './App';
import { LAST_TAB_STORAGE_KEY } from './appRouting';

const permissionsState = vi.hoisted(() => ({
  paymentsCanView: true,
}));

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
      notes: section,
      reports: section,
      calculator: section,
      legalEntities: section,
      clients: section,
      cases: section,
      accounts: section,
      acts: section,
      payments: { ...section, canView: permissionsState.paymentsCanView },
      contracts: section,
      dictionaries: section,
      roles: section,
      audit: section,
      integrations: section,
      yandexDisk: section,
      s3Storage: section,
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

vi.mock('./components/Notifications/NotificationCenter', () => ({
  NotificationCenter: ({ onOpenTask }: { onOpenTask?: (taskId: number) => void }) => (
    <section data-testid="notification-center">
      <button type="button" onClick={() => onOpenTask?.(101)}>
        open notification task
      </button>
    </section>
  ),
}));

vi.mock('./components/Reports/Reports', () => ({
  Reports: ({
    onOpenCaseSection,
    onOpenTask,
  }: {
    onOpenCaseSection?: (caseId: number, section: 'tasks' | 'documents' | 'court' | 'workOrder') => void;
    onOpenTask?: (taskId: number) => void;
  }) => (
    <section data-testid="reports-view">
      <button type="button" onClick={() => onOpenCaseSection?.(77, 'documents')}>
        open report document
      </button>
      <button type="button" onClick={() => onOpenTask?.(202)}>
        open report task
      </button>
    </section>
  ),
}));

vi.mock('./components/Tasks/TaskJournal', () => ({
  TaskJournal: ({
    routeTaskId,
    onRouteTaskChange,
  }: {
    routeTaskId?: number | null;
    onRouteTaskChange?: (taskId: number | null) => void;
  }) => (
    <section data-testid="task-journal">
      <span>task {routeTaskId}</span>
      <button type="button" onClick={() => onRouteTaskChange?.(null)}>
        save task
      </button>
    </section>
  ),
}));

vi.mock('./components/Cases/CaseDetailPage', () => ({
  CaseDetailPage: ({ caseId }: { caseId: number }) => (
    <section data-testid="case-detail">
      <span>case {caseId}</span>
      <span>{window.sessionStorage.getItem(`pp.case-detail.${caseId}.view.v1`)}</span>
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
    permissionsState.paymentsCanView = true;
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

  it('returns to notifications after saving a task opened from notifications', async () => {
    window.history.replaceState({}, '', '/notifications');

    render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: 'open notification task' }));

    expect(await screen.findByTestId('task-journal')).toBeTruthy();
    expect(screen.getByText('task 101')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'save task' }));

    await waitFor(() => {
      expect(screen.queryByTestId('task-journal')).toBeNull();
      expect(screen.getByTestId('notification-center')).toBeTruthy();
    });
  });

  it('opens report drill-down targets and returns from report task', async () => {
    window.history.replaceState({}, '', '/?ppTab=reports');

    const firstRender = render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: 'open report document' }));

    expect(await screen.findByTestId('case-detail')).toBeTruthy();
    expect(screen.getByText('case 77')).toBeTruthy();
    expect(screen.getByText('{"activeTab":"documents"}')).toBeTruthy();

    firstRender.unmount();
    window.history.replaceState({}, '', '/?ppTab=reports');
    render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: 'open report task' }));

    expect(await screen.findByTestId('task-journal')).toBeTruthy();
    expect(screen.getByText('task 202')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'save task' }));

    await waitFor(() => {
      expect(screen.queryByTestId('task-journal')).toBeNull();
      expect(screen.getByTestId('reports-view')).toBeTruthy();
    });
  });

  it('does not restore a forbidden last tab after role permissions change', async () => {
    window.history.replaceState({}, '', '/');
    window.localStorage.setItem(LAST_TAB_STORAGE_KEY, 'payments');
    permissionsState.paymentsCanView = false;

    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('notifications');
      expect(screen.getByTestId('notification-center')).toBeTruthy();
    });

    expect(window.localStorage.getItem(LAST_TAB_STORAGE_KEY)).toBe('notifications');
  });
});
