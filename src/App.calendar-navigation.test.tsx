import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import App from './App';
import { LAST_TAB_STORAGE_KEY } from './appRouting';
const userLastTabKey = `${LAST_TAB_STORAGE_KEY}.1`;
import { apiService } from './services/api';

const permissionsState = vi.hoisted(() => ({
  paymentsCanView: true,
  casesCanView: true,
  tasksCanView: true,
  legalEntitiesCanView: true,
  loaded: true,
}));

const authState = vi.hoisted(() => ({
  loading: false,
  userId: 1,
}));

vi.mock('./contexts/AuthContext', () => ({
  AuthProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
  useAuth: () => ({
    user: authState.loading ? null : {
      id: authState.userId,
      email: 'admin@example.test',
      fullName: 'Admin',
      role: { id: 1, name: 'admin' },
      isActive: true,
      isApproved: true,
      isEmployee: true,
      createdAt: '2026-05-07T00:00:00Z',
    },
    loading: authState.loading,
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
    useRolePermissions: () => {
      const blocked = {
        canView: false,
        canCreate: false,
        canEdit: false,
        canDelete: false,
        canExport: false,
      };
      const currentSection = permissionsState.loaded ? section : blocked;
      return {
        calendar: { ...currentSection, canViewAnalytics: permissionsState.loaded },
        tasks: { ...currentSection, canView: permissionsState.loaded && permissionsState.tasksCanView },
        notes: currentSection,
        reports: currentSection,
        calculator: currentSection,
        legalEntities: { ...currentSection, canView: permissionsState.loaded && permissionsState.legalEntitiesCanView },
        clients: currentSection,
        cases: { ...currentSection, canView: permissionsState.loaded && permissionsState.casesCanView },
        requests: currentSection,
        accounts: currentSection,
        acts: currentSection,
        payments: { ...currentSection, canView: permissionsState.loaded && permissionsState.paymentsCanView },
        contracts: currentSection,
        dictionaries: currentSection,
        roles: currentSection,
        audit: currentSection,
        integrations: currentSection,
        yandexDisk: currentSection,
        s3Storage: currentSection,
      };
    },
    useRolePermissionsLoaded: () => permissionsState.loaded,
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

vi.mock('./components/Notifications/ForcedTaskRevisionModal', () => ({
  ForcedTaskRevisionModal: () => <section data-testid="forced-notification-queue" />,
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
  CaseDetailPage: ({
    caseId,
    onOpenFinance,
  }: {
    caseId: number;
    onOpenFinance?: (caseItem: { id: number; clientId: number | null; legalEntityId: number | null }) => void;
  }) => (
    <section data-testid="case-detail">
      <span>case {caseId}</span>
      <span>{window.sessionStorage.getItem(`pp.case-detail.${caseId}.view.v1`)}</span>
      <button type="button" onClick={() => onOpenFinance?.({ id: caseId, clientId: null, legalEntityId: 37 })}>
        open legal entity finance
      </button>
    </section>
  ),
}));

vi.mock('./components/Requests/SalesRequestsPage', () => ({
  SalesRequestsPage: ({ currentUserId, onOpenCaseTasks }: {
    currentUserId?: number | null;
    onOpenCaseTasks?: (caseId: number) => void;
  }) => (
    <section data-testid="requests-view">
      <span>request actor {currentUserId}</span>
      <button type="button" disabled={!onOpenCaseTasks} onClick={() => onOpenCaseTasks?.(91)}>open existing case tasks</button>
    </section>
  ),
}));

vi.mock('./components/LegalEntities/LegalEntities', () => ({
  LegalEntities: ({ initialLegalEntityId }: { initialLegalEntityId?: number | null }) => (
    <section data-testid="legal-entities-view">legal entity {initialLegalEntityId ?? 'none'}</section>
  ),
}));

vi.mock('./components/Clients/ClientDetail', () => ({
  ClientDetail: ({
    clientId,
    onBack,
    onOpenInvoicePage,
  }: {
    clientId: number;
    onBack: () => void;
    onOpenInvoicePage?: (invoiceId: number) => void;
  }) => (
    <section data-testid="client-detail">
      <span>client {clientId}</span>
      <button type="button" onClick={() => onOpenInvoicePage?.(528)}>
        open client invoice
      </button>
      <button type="button" onClick={onBack}>
        back from client
      </button>
    </section>
  ),
}));

vi.mock('./components/Clients/Clients', () => ({
  Clients: () => <section data-testid="clients-view">clients list</section>,
}));

vi.mock('./components/Accounts/Accounts', () => ({
  Accounts: ({ routeInvoiceId }: { routeInvoiceId?: number | null }) => (
    <section data-testid="accounts-view">invoice {routeInvoiceId ?? 'none'}</section>
  ),
}));

vi.mock('./components/Payments/Payments', () => ({
  Payments: ({ routePaymentId, onRoutePaymentExit }: { routePaymentId?: number | null; onRoutePaymentExit?: () => void }) => (
    <section data-testid="payments-view">
      payment {routePaymentId ?? 'none'}
      <button type="button" onClick={onRoutePaymentExit}>finish employee payment</button>
    </section>
  ),
}));

vi.mock('./components/Users/UserDetailPage', () => ({
  UserDetailPage: ({
    userId,
    initialSection,
    canOpenContractorPaymentLinks,
    onOpenContractorPayment,
  }: {
    userId: number | null;
    initialSection?: string;
    canOpenContractorPaymentLinks?: boolean;
    onOpenContractorPayment?: (paymentId: number) => void;
  }) => (
    <section data-testid="user-detail">
      user {userId}
      <span data-testid="user-detail-section">{initialSection ?? 'main'}</span>
      <span data-testid="user-payment-link-access">{String(canOpenContractorPaymentLinks)}</span>
      <button type="button" onClick={() => onOpenContractorPayment?.(2069)}>open employee payment</button>
    </section>
  ),
}));

vi.mock('./components/Profile/ProfilePage', () => ({
  ProfilePage: () => <section data-testid="profile-page">profile</section>,
}));

describe('App calendar client navigation', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/?ppTab=calendar');
    window.localStorage.clear();
    window.sessionStorage.clear();
    permissionsState.paymentsCanView = true;
    permissionsState.casesCanView = true;
    permissionsState.tasksCanView = true;
    permissionsState.legalEntitiesCanView = true;
    permissionsState.loaded = true;
    authState.loading = false;
    authState.userId = 1;
  });

  it('restores the signed-in employee tab after authentication without using the shared legacy key', async () => {
    window.history.replaceState({}, '', '/');
    window.localStorage.setItem(LAST_TAB_STORAGE_KEY, 'payments');
    window.localStorage.setItem(userLastTabKey, 'reports');
    authState.loading = true;
    const view = render(<App />);
    authState.loading = false;
    view.rerender(<App />);
    await waitFor(() => expect(screen.getByTestId('navigation').textContent).toBe('reports'));
    expect(window.localStorage.getItem(LAST_TAB_STORAGE_KEY)).toBe('payments');
    expect(window.localStorage.getItem(userLastTabKey)).toBe('reports');
  });

  it('switches personal tabs A to B and back without overwriting the other employee', async () => {
    window.history.replaceState({}, '', '/');
    window.localStorage.setItem(userLastTabKey, 'reports');
    window.localStorage.setItem(`${LAST_TAB_STORAGE_KEY}.2`, 'calendar');
    const view = render(<App />);
    await waitFor(() => expect(screen.getByTestId('navigation').textContent).toBe('reports'));
    authState.userId = 2;
    view.rerender(<App />);
    await waitFor(() => expect(screen.getByTestId('navigation').textContent).toBe('calendar'));
    expect(window.localStorage.getItem(`${LAST_TAB_STORAGE_KEY}.2`)).toBe('calendar');
    expect(window.localStorage.getItem(userLastTabKey)).toBe('reports');
    authState.userId = 1;
    view.rerender(<App />);
    await waitFor(() => expect(screen.getByTestId('navigation').textContent).toBe('reports'));
    expect(window.localStorage.getItem(`${LAST_TAB_STORAGE_KEY}.2`)).toBe('calendar');
  });

  it('clears a previous employee client detail when switching account', async () => {
    window.history.replaceState({}, '', '/clients/42');
    window.localStorage.setItem(`${LAST_TAB_STORAGE_KEY}.2`, 'calendar');
    const view = render(<App />);
    expect(await screen.findByTestId('client-detail')).toBeTruthy();
    authState.userId = 2;
    view.rerender(<App />);
    await waitFor(() => expect(screen.getByTestId('navigation').textContent).toBe('calendar'));
    expect(screen.queryByTestId('client-detail')).toBeNull();
    expect(window.location.pathname).not.toContain('/clients/42');
  });

  it('does not carry the previous employee request query into the next account', async () => {
    window.history.replaceState({}, '', '/?ppTab=requests&requestId=7&summary=private-sales');
    window.localStorage.setItem(`${LAST_TAB_STORAGE_KEY}.2`, 'requests');
    const view = render(<App />);
    await waitFor(() => expect(screen.getByTestId('navigation').textContent).toBe('requests'));
    authState.userId = 2;
    view.rerender(<App />);
    await waitFor(() => expect(screen.getByTestId('navigation').textContent).toBe('requests'));
    await waitFor(() => expect(new URL(window.location.href).searchParams.has('requestId')).toBe(false));
    expect(new URL(window.location.href).searchParams.has('summary')).toBe(false);
    await waitFor(() => expect(new URL(window.location.href).searchParams.get('ppTab')).toBe('requests'));
  });

  it('preserves the requests section and request link across a refresh', async () => {
    window.history.replaceState({}, '', '/?ppTab=requests&requestId=7');
    const view = render(<App />);
    await screen.findByTestId('requests-view');
    await waitFor(() => expect(new URL(window.location.href).searchParams.get('ppTab')).toBe('requests'));
    expect(new URL(window.location.href).searchParams.get('requestId')).toBe('7');
    view.unmount();
    render(<App />);
    expect(await screen.findByTestId('requests-view')).toBeTruthy();
    expect(new URL(window.location.href).searchParams.get('requestId')).toBe('7');
  });

  it('opens the legal entity from a direct task link', async () => {
    window.history.replaceState({}, '', '/?ppTab=legalEntities&ppLegalEntityId=42');
    render(<App />);
    expect((await screen.findByTestId('legal-entities-view')).textContent).toContain('legal entity 42');
  });

  it('does not open a direct legal entity link without section access', async () => {
    permissionsState.legalEntitiesCanView = false;
    window.history.replaceState({}, '', '/?ppTab=legalEntities&ppLegalEntityId=42');
    render(<App />);
    await waitFor(() => expect(screen.getByTestId('navigation').textContent).not.toBe('legalEntities'));
    expect(screen.queryByTestId('legal-entities-view')).toBeNull();
    expect(screen.queryByText('legal entity 42')).toBeNull();
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

  it('opens the organization card from case finance instead of leaving the active button without an action', async () => {
    window.history.replaceState({}, '', '/cases/164');
    vi.spyOn(apiService, 'getLegalEntity').mockResolvedValue({ id: 37 } as Awaited<ReturnType<typeof apiService.getLegalEntity>>);

    render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: 'open legal entity finance' }));

    expect((await screen.findByTestId('legal-entities-view')).textContent).toContain('legal entity 37');
    expect(apiService.getLegalEntity).toHaveBeenCalledWith(37);
  });

  it('uses the existing case task navigation with a clean route instead of carrying request parameters', async () => {
    window.history.replaceState({}, '', '/?ppTab=requests&requestId=7&summary=private-sales');
    const replaceRoute = vi.spyOn(window.history, 'replaceState');
    render(<App />);

    expect(await screen.findByText('request actor 1')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'open existing case tasks' }));

    expect((await screen.findByTestId('case-detail')).textContent).toContain('case 91');
    expect(replaceRoute).toHaveBeenCalledWith({}, document.title, '/cases/91?ppCaseTab=tasks');
    expect(window.location.pathname).toBe('/cases/91');
    expect(window.location.search).not.toContain('requestId');
    expect(window.location.search).not.toContain('private-sales');
    expect(window.sessionStorage.getItem('pp.case-detail.91.view.v1')).toBe(JSON.stringify({ activeTab: 'tasks' }));
    replaceRoute.mockRestore();
  });

  it.each(['casesCanView', 'tasksCanView'] as const)('does not grant request case navigation without %s', async (permission) => {
    window.history.replaceState({}, '', '/?ppTab=requests&requestId=7');
    permissionsState[permission] = false;
    render(<App />);

    const openCase = await screen.findByRole('button', { name: 'open existing case tasks' });
    expect((openCase as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(openCase);

    expect(screen.queryByTestId('case-detail')).toBeNull();
    expect(window.location.search).toContain('requestId=7');
    expect(window.sessionStorage.getItem('pp.case-detail.91.view.v1')).toBeNull();
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

  it('opens an invoice from the client card even when notifications were the last saved tab', async () => {
    window.history.replaceState({}, '', '/clients/42');
    window.localStorage.setItem(userLastTabKey, 'notifications');

    render(<App />);

    expect(await screen.findByTestId('client-detail')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'open client invoice' }));

    await waitFor(() => {
      expect(screen.queryByTestId('notification-center')).toBeNull();
      expect(screen.getByTestId('accounts-view').textContent).toBe('invoice 528');
      expect(`${window.location.pathname}${window.location.search}`).toBe('/invoices/528');
      expect(window.localStorage.getItem(userLastTabKey)).toBe('accounts');
    });
  });

  it('starts the mandatory notification queue only after current role permissions have loaded', async () => {
    permissionsState.loaded = false;
    const rendered = render(<App />);
    expect(screen.queryByTestId('forced-notification-queue')).toBeNull();
    permissionsState.loaded = true;
    rendered.rerender(<App />);
    await waitFor(() => expect(screen.getByTestId('forced-notification-queue')).toBeTruthy());
    permissionsState.loaded = false;
    rendered.rerender(<App />);
    expect(screen.queryByTestId('forced-notification-queue')).toBeNull();
  });
  it('keeps a routed invoice page while role permissions are loading', async () => {
    window.history.replaceState({}, '', '/invoices/528');
    window.localStorage.setItem(userLastTabKey, 'notifications');
    permissionsState.loaded = false;

    const rendered = render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('accounts');
      expect(`${window.location.pathname}${window.location.search}`).toBe('/invoices/528');
      expect(window.localStorage.getItem(userLastTabKey)).toBe('notifications');
    });

    permissionsState.loaded = true;
    rendered.rerender(<App />);

    await waitFor(() => {
      expect(screen.queryByTestId('notification-center')).toBeNull();
      expect(screen.getByTestId('accounts-view').textContent).toBe('invoice 528');
      expect(`${window.location.pathname}${window.location.search}`).toBe('/invoices/528');
      expect(window.localStorage.getItem(userLastTabKey)).toBe('accounts');
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

  it('does not overwrite a deep-linked user page with notifications while auth is loading', async () => {
    window.history.replaceState({}, '', '/?ppTab=userDetail&userId=10');
    window.localStorage.setItem(userLastTabKey, 'notifications');
    authState.loading = true;

    const rendered = render(<App />);

    await waitFor(() => {
      expect(`${window.location.pathname}${window.location.search}`).toBe('/?ppTab=userDetail&userId=10');
      expect(window.localStorage.getItem(userLastTabKey)).toBe('notifications');
    });

    authState.loading = false;
    rendered.rerender(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('userDetail');
      expect(screen.getByTestId('user-detail').textContent).toContain('user 10');
      expect(`${window.location.pathname}${window.location.search}`).toBe('/?ppTab=userDetail&userId=10');
    });

    expect(window.localStorage.getItem(userLastTabKey)).toBe('userDetail');
  });

  it('does not restore notifications as the last working tab on plain refresh', async () => {
    window.history.replaceState({}, '', '/');
    window.localStorage.setItem(userLastTabKey, 'notifications');

    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('calendar');
      expect(screen.getByTestId('calendar-view')).toBeTruthy();
    });

    expect(window.localStorage.getItem(userLastTabKey)).toBe('calendar');
  });

  it('keeps calendar in the route so F5 returns to calendar without stored state', async () => {
    window.history.replaceState({}, '', '/');

    const rendered = render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('calendar');
      expect(screen.getByTestId('calendar-view')).toBeTruthy();
      expect(`${window.location.pathname}${window.location.search}`).toBe('/?ppTab=calendar');
    });

    rendered.unmount();
    window.localStorage.clear();
    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('calendar');
      expect(screen.getByTestId('calendar-view')).toBeTruthy();
      expect(`${window.location.pathname}${window.location.search}`).toBe('/?ppTab=calendar');
    });
  });

  it('allows the user card to render contractor payment links without leaving the card', async () => {
    window.history.replaceState({}, '', '/?ppTab=userDetail&userId=10');

    render(<App />);

    expect(await screen.findByTestId('user-detail')).toBeTruthy();
    expect(screen.getByTestId('user-payment-link-access').textContent).toBe('true');
    expect(screen.getByTestId('navigation').textContent).toBe('userDetail');
    expect(`${window.location.pathname}${window.location.search}`).toBe('/?ppTab=userDetail&userId=10');
  });

  it('returns to the employee contractor balance after completing a payment opened from the user card', async () => {
    window.history.replaceState({}, '', '/?ppTab=userDetail&userId=10');

    render(<App />);

    fireEvent.click(await screen.findByRole('button', { name: 'open employee payment' }));

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('paymentsExpense');
      expect(screen.getByTestId('payments-view').textContent).toContain('payment 2069');
    });

    fireEvent.click(screen.getByRole('button', { name: 'finish employee payment' }));

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('userDetail');
      expect(screen.getByTestId('user-detail').textContent).toContain('user 10');
      expect(screen.getByTestId('user-detail-section').textContent).toBe('contractorBalance');
      expect(`${window.location.pathname}${window.location.search}`).toBe('/?ppTab=userDetail&userId=10');
    });
  });

  it('clears stale settings section when opening the profile page', async () => {
    window.history.replaceState({}, '', '/?settingsSection=notifications&ppTab=profile');
    window.localStorage.setItem(userLastTabKey, 'notifications');

    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('profile');
      expect(screen.getByTestId('profile-page')).toBeTruthy();
      expect(`${window.location.pathname}${window.location.search}`).toBe('/?ppTab=profile');
    });

    expect(window.localStorage.getItem(userLastTabKey)).toBe('profile');
  });

  it('opens notifications and clears a forbidden last tab after role permissions change', async () => {
    window.history.replaceState({}, '', '/');
    window.localStorage.setItem(userLastTabKey, 'payments');
    permissionsState.paymentsCanView = false;

    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('navigation').textContent).toBe('notifications');
      expect(screen.getByTestId('notification-center')).toBeTruthy();
      expect(`${window.location.pathname}${window.location.search}`).toBe('/notifications');
    });

    expect(window.localStorage.getItem(userLastTabKey)).toBeNull();
  });
});
