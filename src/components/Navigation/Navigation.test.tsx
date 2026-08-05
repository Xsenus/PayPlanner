import userEvent from '@testing-library/user-event';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Navigation from './Navigation';
import { getTabHref } from '../../appRouting';
import { ALL_TABS, type Tab } from '../../types/tabs';
import { apiService } from '../../services/api';
import { defaultRestrictedRolePermissions, defaultRolePermissions, type RolePermissions } from '../../types/permissions';

const authState = vi.hoisted(() => ({
  isAdmin: true,
  roleId: 1,
  roleName: 'admin',
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: authState.roleId, name: authState.roleName } },
    isAdmin: () => authState.isAdmin,
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getPaymentsV2: vi.fn(),
    getRolePermissions: vi.fn(),
  },
}));

const apiMock = vi.mocked(apiService);

const expandedSections = {
  tasks: true,
  contacts: true,
  accounts: true,
  money: true,
  payments: true,
  acts: true,
};

const HIDDEN_NAVIGATION_TABS = [
  'caseDetail',
  'contactsExternal',
  'actCreate',
  'actDetail',
  'contractCreate',
  'contractDetail',
  'clientDetail',
  'companyProfile',
  'profile',
  'userDetail',
  'yandexDisk',
  's3Storage',
] as const satisfies readonly Tab[];

const lawyerWorkSection = {
  canView: true,
  canCreate: true,
  canEdit: true,
  canDelete: false,
  canExport: false,
  viewScope: 'own',
} as const;

const lawyerTaskSection = {
  ...lawyerWorkSection,
  canEditOthers: false,
  canCompleteOthers: false,
  canDeleteOthers: false,
} as const;

function createLawyerTemplatePermissions(): RolePermissions {
  return {
    ...defaultRestrictedRolePermissions,
    calendar: { ...lawyerWorkSection, canViewAnalytics: false, visibleUserIds: [] },
    tasks: { ...lawyerTaskSection },
    clients: { ...lawyerWorkSection },
    cases: { ...lawyerWorkSection },
    notifications: {
      canView: true,
      canCreate: false,
      canEdit: true,
      canDelete: false,
      canExport: false,
      viewScope: 'all',
    },
  };
}

function renderNavigation(onTabChange = vi.fn()) {
  window.localStorage.setItem('pp.sidebar_expanded', JSON.stringify(expandedSections));
  render(<Navigation activeTab="calendar" onTabChange={onTabChange} />);
  return onTabChange;
}

function findLinkByHref(tabHref: string) {
  const normalizedHref = new URL(tabHref, window.location.origin).href;
  return screen
    .getAllByRole('link')
    .find((link) => link.getAttribute('href') === tabHref || (link as HTMLAnchorElement).href === normalizedHref);
}

async function waitForNavigationPermissions() {
  await waitFor(() => {
    expect(findLinkByHref(getTabHref('calendar'))).toBeTruthy();
  });
}

describe('Navigation links', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
    vi.clearAllMocks();
    authState.isAdmin = true;
    authState.roleId = 1;
    authState.roleName = 'admin';
    apiMock.getRolePermissions.mockResolvedValue(defaultRolePermissions);
    apiMock.getPaymentsV2.mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 1,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    });
  });

  it('keeps page padding aligned with the desktop sidebar breakpoint', async () => {
    renderNavigation();
    await waitForNavigationPermissions();

    const responsiveStyles = document.getElementById('pp-sidebar-style');
    expect(responsiveStyles?.textContent).toContain('@media (max-width: 1023px)');
    expect(responsiveStyles?.textContent).not.toContain('@media (max-width: 1024px)');
  });

  it('renders navigation destinations as real links', async () => {
    renderNavigation();
    await waitForNavigationPermissions();

    expect(screen.getByRole('complementary', { name: 'Навигация' })).toBeTruthy();
    expect(screen.getByRole('navigation', { name: 'Навигация' })).toBeTruthy();
    expect(findLinkByHref(getTabHref('calendar'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('notifications'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('tasksApprovals'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('tasksCalendar'))).toBeTruthy();
    expect(screen.getByRole('link', { name: /Клиенты/i }).getAttribute('href')).toBe(getTabHref('clients'));
    expect(findLinkByHref(getTabHref('accountsIncome'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('moneyEMoney'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('settings'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('yandexDisk'))).toBeFalsy();
    expect(findLinkByHref(getTabHref('s3Storage'))).toBeFalsy();
  });

  it('shows settings for a non-admin role that can view integrations only', async () => {
    authState.isAdmin = false;
    authState.roleId = 99;
    authState.roleName = 'lawyer';
    apiMock.getRolePermissions.mockResolvedValueOnce({
      ...defaultRolePermissions,
      integrations: { ...defaultRolePermissions.integrations, canView: true },
    });

    renderNavigation();

    await waitFor(() => {
      expect(findLinkByHref(getTabHref('settings'))).toBeTruthy();
    });
    expect(findLinkByHref(getTabHref('roles'))).toBeFalsy();
    expect(findLinkByHref(getTabHref('audit'))).toBeFalsy();
    expect(findLinkByHref(getTabHref('yandexDisk'))).toBeFalsy();
    expect(findLinkByHref(getTabHref('s3Storage'))).toBeFalsy();
  });

  it('shows only safe lawyer template sections in the left menu', async () => {
    authState.isAdmin = false;
    authState.roleId = 100;
    authState.roleName = 'Юрист';
    apiMock.getRolePermissions.mockResolvedValueOnce(createLawyerTemplatePermissions());

    renderNavigation();

    await waitForNavigationPermissions();

    const visibleTabs: Tab[] = [
      'calendar',
      'notifications',
      'tasks',
      'tasksControl',
      'tasksApprovals',
      'tasksCalendar',
      'clients',
      'cases',
      'contacts',
      'contactsClients',
      'settings',
    ];
    const hiddenTabs: Tab[] = [
      'notes',
      'reports',
      'calculator',
      'legalEntities',
      'contactsLegal',
      'accounts',
      'accountsIncome',
      'accountsExpense',
      'installments',
      'moneyBank',
      'moneyCash',
      'moneyCards',
      'moneyEMoney',
      'moneyTotal',
      'payments',
      'paymentsIncome',
      'paymentsExpense',
      'acts',
      'actsIncome',
      'actsExpense',
      'contracts',
      'dictionaries',
      'roles',
      'audit',
      'users',
      'userActivity',
      'yandexDisk',
      's3Storage',
    ];

    for (const tab of visibleTabs) {
      expect(findLinkByHref(getTabHref(tab)), `lawyer menu should show ${tab}`).toBeTruthy();
    }

    for (const tab of hiddenTabs) {
      expect(findLinkByHref(getTabHref(tab)), `lawyer menu should hide ${tab}`).toBeFalsy();
    }

    expect(apiMock.getPaymentsV2).not.toHaveBeenCalled();
  });

  it('keeps every tab either visible in navigation or explicitly whitelisted as hidden', async () => {
    renderNavigation();
    await waitForNavigationPermissions();

    const hiddenTabs = new Set<Tab>(HIDDEN_NAVIGATION_TABS);
    const visibleTabs = ALL_TABS.filter((tab) => !hiddenTabs.has(tab));
    const renderedTabs = ALL_TABS.filter((tab) => Boolean(findLinkByHref(getTabHref(tab))));

    expect(new Set(ALL_TABS).size).toBe(ALL_TABS.length);
    expect(new Set(HIDDEN_NAVIGATION_TABS).size).toBe(HIDDEN_NAVIGATION_TABS.length);
    expect(renderedTabs.sort()).toEqual(visibleTabs.sort());
  });

  it('opens the task calendar submenu inside the single page app', async () => {
    const onTabChange = renderNavigation();
    await waitForNavigationPermissions();
    const link = findLinkByHref(getTabHref('tasksCalendar'));

    expect(link).toBeTruthy();
    fireEvent.click(link!);

    expect(onTabChange).toHaveBeenCalledWith('tasksCalendar');
  });

  it('supports keyboard access for navigation links', async () => {
    const user = userEvent.setup();
    const onTabChange = renderNavigation();
    await waitForNavigationPermissions();
    const clientsLink = screen.getByRole('link', { name: /Клиенты/i });

    clientsLink.focus();
    expect(document.activeElement).toBe(clientsLink);

    await user.keyboard('{Enter}');

    expect(onTabChange).toHaveBeenCalledWith('clients');
  });

  it('opens the approvals submenu inside the single page app', async () => {
    const onTabChange = renderNavigation();
    await waitForNavigationPermissions();
    const link = findLinkByHref(getTabHref('tasksApprovals'));

    expect(link).toBeTruthy();
    fireEvent.click(link!);

    expect(onTabChange).toHaveBeenCalledWith('tasksApprovals');
  });

  it('keeps normal left clicks inside the single page app', async () => {
    const onTabChange = renderNavigation();
    await waitForNavigationPermissions();
    const link = screen.getByRole('link', { name: /Клиенты/i });

    const clickEvent = new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      button: 0,
    });

    link.dispatchEvent(clickEvent);

    expect(clickEvent.defaultPrevented).toBe(true);
    expect(onTabChange).toHaveBeenCalledWith('clients');
  });

  it('allows browser tab gestures on navigation links', async () => {
    const onTabChange = renderNavigation();
    await waitForNavigationPermissions();
    const link = findLinkByHref(getTabHref('moneyEMoney'));

    expect(link).toBeTruthy();
    link!.removeAttribute('href');

    const ctrlClickEvent = new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      button: 0,
      ctrlKey: true,
    });

    link!.dispatchEvent(ctrlClickEvent);

    expect(ctrlClickEvent.defaultPrevented).toBe(false);
    expect(onTabChange).not.toHaveBeenCalled();

    fireEvent.click(link!, { button: 1 });

    expect(onTabChange).not.toHaveBeenCalled();
  });

  it('shows a payments badge for advance payments that require a decision', async () => {
    apiMock.getPaymentsV2.mockResolvedValueOnce({
      items: [],
      total: 5,
      page: 1,
      pageSize: 1,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 5,
      },
    });

    renderNavigation();

    await waitFor(() => {
      expect(apiMock.getPaymentsV2).toHaveBeenCalledWith({
        advanceReviewStatus: 'RequiresDecision',
        page: 1,
        pageSize: 1,
        sortBy: 'createdAt',
        sortDir: 'desc',
      });
    });
    expect((await screen.findByTestId('advance-review-nav-badge')).textContent).toBe('5');
  });
});
