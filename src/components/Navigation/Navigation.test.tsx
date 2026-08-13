import userEvent from '@testing-library/user-event';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Navigation from './Navigation';
import { getTabHref } from '../../appRouting';
import { ALL_TABS, type Tab } from '../../types/tabs';
import { apiService } from '../../services/api';
import { defaultRestrictedRolePermissions, defaultRolePermissions, type RolePermissions } from '../../types/permissions';
import {
  PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT,
  PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_KEY,
} from '../../utils/paymentAdvanceReview';

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
    getNavigationSettings: vi.fn(),
    updateNavigationSettings: vi.fn(),
    getUserSetting: vi.fn(),
    upsertUserSetting: vi.fn(),
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
  'clients',
  'legalEntities',
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
    window.sessionStorage.clear();
    vi.clearAllMocks();
    authState.isAdmin = true;
    authState.roleId = 1;
    authState.roleName = 'admin';
    apiMock.getRolePermissions.mockResolvedValue(defaultRolePermissions);
    apiMock.getNavigationSettings.mockResolvedValue({ order: [], labels: {}, groups: [], updatedAt: null });
    apiMock.updateNavigationSettings.mockImplementation(async (order, labels, groups) => ({ order, labels, groups, updatedAt: null }));
    apiMock.getUserSetting.mockResolvedValue(null);
    apiMock.upsertUserSetting.mockResolvedValue({ key: 'navigation.sidebar.hidden-items.v1', value: '[]' });
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
    expect(screen.getByRole('link', { name: /^Контакты$/i }).getAttribute('href')).toBe(getTabHref('contacts'));
    expect(findLinkByHref(getTabHref('accountsIncome'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('moneyEMoney'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('settings'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('yandexDisk'))).toBeFalsy();
    expect(findLinkByHref(getTabHref('s3Storage'))).toBeFalsy();
  });

  it('uses the common server order and custom section names', async () => {
    apiMock.getNavigationSettings.mockResolvedValueOnce({
      order: ['tasks', 'calendar', 'notifications'],
      labels: { tasks: 'Рабочие задачи' },
      groups: [],
      updatedAt: null,
    });

    renderNavigation();
    await screen.findByRole('link', { name: /Рабочие задачи/i });

    const navigation = screen.getByRole('navigation', { name: 'Навигация' });
    const topLevelLinks = Array.from(navigation.querySelectorAll('ul > li > div > a')).map((link) =>
      (link.textContent ?? '').trim(),
    );

    expect(topLevelLinks[0] ?? '').toContain('Рабочие задачи');
    expect(topLevelLinks[1] ?? '').toContain('Календарь');
    expect(topLevelLinks[2] ?? '').toContain('Уведомления');
  });

  it('lets a user hide and restore only available sections', async () => {
    const user = userEvent.setup();
    renderNavigation();
    await waitForNavigationPermissions();

    await user.click(screen.getByRole('button', { name: 'Ещё разделы' }));
    const reportsCheckbox = screen.getByRole('checkbox', { name: /Отчёты/i });
    await user.click(reportsCheckbox);
    expect(findLinkByHref(getTabHref('reports'))).toBeFalsy();

    await waitFor(() => {
      expect(apiMock.upsertUserSetting).toHaveBeenCalledWith(
        'navigation.sidebar.hidden-items.v1',
        expect.stringContaining('reports'),
      );
    });

    await user.click(reportsCheckbox);
    expect(findLinkByHref(getTabHref('reports'))).toBeTruthy();
  });

  it('lets an administrator save the common order and names', async () => {
    const user = userEvent.setup();
    renderNavigation();
    await waitForNavigationPermissions();

    await user.click(screen.getByRole('button', { name: 'Ещё разделы' }));
    await user.click(screen.getByRole('button', { name: 'Группы и порядок' }));
    const calendarName = screen.getByRole('textbox', { name: /Название раздела Календарь/i });
    await user.clear(calendarName);
    await user.type(calendarName, 'Расписание');
    const calendarDragHandle = screen.getByRole('button', { name: 'Перетащить Календарь' });
    const notificationsRow = screen.getByRole('button', { name: 'Перетащить Уведомления' }).closest('div');
    fireEvent.pointerDown(calendarDragHandle, { button: 0, buttons: 1 });
    fireEvent.pointerMove(notificationsRow!, { buttons: 1 });
    fireEvent.pointerUp(notificationsRow!, { button: 0, buttons: 0 });
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateNavigationSettings).toHaveBeenCalled());
    const [order, labels, groups] = apiMock.updateNavigationSettings.mock.calls.at(-1)!;
    expect(order[0]).toBe('notifications');
    expect(labels.calendar).toBe('Расписание');
    expect(groups).toEqual([]);
    expect(await screen.findByRole('link', { name: /Расписание/i })).toBeTruthy();
  });

  it('lets an administrator create, position and fill a menu group', async () => {
    const user = userEvent.setup();
    renderNavigation();
    await waitForNavigationPermissions();

    await user.click(screen.getByRole('button', { name: 'Ещё разделы' }));
    await user.click(screen.getByRole('button', { name: 'Группы и порядок' }));
    await user.click(screen.getByRole('button', { name: 'Создать группу' }));
    const groupName = screen.getByRole('textbox', { name: /Название группы Новая группа/i });
    await user.clear(groupName);
    await user.type(groupName, 'Бухгалтерия');
    const accountsGroupSelect = screen.getByRole('combobox', { name: /Группа раздела Счета/i }) as HTMLSelectElement;
    const groupValue = Array.from(accountsGroupSelect.options).find((option) => option.text === 'Бухгалтерия')?.value;
    expect(groupValue).toBeTruthy();
    await user.selectOptions(accountsGroupSelect, groupValue!);
    await user.selectOptions(screen.getByRole('combobox', { name: /Группа раздела Акты/i }), groupValue!);
    const groupDragHandle = screen.getByRole('button', { name: 'Перетащить группу Бухгалтерия' });
    const calendarRow = screen.getByRole('button', { name: 'Перетащить Календарь' }).closest('div');
    fireEvent.pointerDown(groupDragHandle, { button: 0, buttons: 1 });
    fireEvent.pointerMove(calendarRow!, { buttons: 1 });
    fireEvent.pointerUp(calendarRow!, { button: 0, buttons: 0 });
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiMock.updateNavigationSettings).toHaveBeenCalled());
    const [order, , groups] = apiMock.updateNavigationSettings.mock.calls.at(-1)!;
    const group = groups.find((candidate) => candidate.name === 'Бухгалтерия');
    expect(group?.itemIds).toEqual(['accounts', 'acts']);
    expect(order[0]).toBe(group?.id);
    expect(order).not.toContain('accounts');
    expect(order).not.toContain('acts');
    expect(await screen.findByRole('button', { name: /Бухгалтерия/i })).toBeTruthy();
  });

  it('shows grouped sections only when they are allowed for the current role', async () => {
    authState.isAdmin = false;
    authState.roleId = 100;
    authState.roleName = 'Юрист';
    apiMock.getRolePermissions.mockResolvedValueOnce(createLawyerTemplatePermissions());
    apiMock.getNavigationSettings.mockResolvedValueOnce({
      order: ['group-contacts', 'reports'],
      labels: {},
      groups: [{ id: 'group-contacts', name: 'Работа с клиентами', itemIds: ['contacts'] }],
      updatedAt: null,
    });

    renderNavigation();

    const group = await screen.findByRole('button', { name: /Работа с клиентами/i });
    expect(group).toBeTruthy();
    expect(findLinkByHref(getTabHref('contacts'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('legalEntities'))).toBeFalsy();
    expect(screen.queryByRole('button', { name: 'Группы и порядок' })).toBeNull();
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
      'clients',
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

    fireEvent.click(screen.getByRole('button', { name: 'Ещё разделы' }));
    expect(screen.getByRole('checkbox', { name: /^Контакты/i })).toBeTruthy();
    expect(screen.queryByRole('checkbox', { name: /Отчёты/i })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Группы и порядок' })).toBeNull();

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
    const clientsLink = screen.getByRole('link', { name: /^Контакты$/i });

    clientsLink.focus();
    expect(document.activeElement).toBe(clientsLink);

    await user.keyboard('{Enter}');

    expect(onTabChange).toHaveBeenCalledWith('contacts');
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
    const link = screen.getByRole('link', { name: /^Контакты$/i });

    const clickEvent = new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      button: 0,
    });

    link.dispatchEvent(clickEvent);

    expect(clickEvent.defaultPrevented).toBe(true);
    expect(onTabChange).toHaveBeenCalledWith('contacts');
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

    const onTabChange = renderNavigation();

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

    const paymentsLink = findLinkByHref(getTabHref('payments'));
    expect(paymentsLink).toBeTruthy();
    fireEvent.click(paymentsLink!);

    expect(window.sessionStorage.getItem(PAYMENT_ADVANCE_REVIEW_FILTER_INTENT_KEY)).toBe('RequiresDecision');
    expect(onTabChange).toHaveBeenCalledWith('payments');
  });

  it('refreshes the payments badge after an advance review decision', async () => {
    apiMock.getPaymentsV2
      .mockResolvedValueOnce({
        items: [], total: 1, page: 1, pageSize: 1,
        summary: { income: 0, expense: 0, balance: 0, incomeCount: 0, expenseCount: 0, total: 1 },
      })
      .mockResolvedValueOnce({
        items: [], total: 0, page: 1, pageSize: 1,
        summary: { income: 0, expense: 0, balance: 0, incomeCount: 0, expenseCount: 0, total: 0 },
      });

    renderNavigation();
    expect((await screen.findByTestId('advance-review-nav-badge')).textContent).toBe('1');

    window.dispatchEvent(new Event(PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT));

    await waitFor(() => {
      expect(screen.queryByTestId('advance-review-nav-badge')).toBeNull();
    });
    expect(apiMock.getPaymentsV2).toHaveBeenCalledTimes(2);
  });
});
