import { beforeEach, describe, expect, it } from 'vitest';
import {
  canAccessTab,
  getInitialActReturnTab,
  getInitialCaseDetailTab,
  getInitialActId,
  getInitialClientId,
  getInitialLegalEntityId,
  getInitialContractId,
  getInitialContractReturnTab,
  getInitialPaymentId,
  getInitialTab,
  getInitialTaskId,
  getInitialUserId,
  getTabHref,
  getPathActId,
  getPathCaseId,
  getPathClientId,
  getPathContractId,
  getPathPaymentId,
  getPathTaskId,
  isActCreatePath,
  isActsListPath,
  isCasesListPath,
  isClientsListPath,
  isContractsListPath,
  isNotificationsPath,
  isNotesPath,
  isPaymentsListPath,
  selectAccessibleFallbackTab,
  isTasksApprovalsPath,
  isTasksUntriagedPath,
  isTasksCalendarPath,
  isTasksListPath,
  TAB_ACCESS_RULES,
} from './appRouting';
import { defaultRestrictedRolePermissions, defaultRolePermissions, type RolePermissions } from './types/permissions';
import { ALL_TABS, type Tab } from './types/tabs';

function createPermissions(overrides: Partial<RolePermissions> = {}): RolePermissions {
  return {
    ...defaultRolePermissions,
    calendar: { ...defaultRolePermissions.calendar, ...overrides.calendar },
    tasks: { ...defaultRolePermissions.tasks, ...overrides.tasks },
    notes: { ...defaultRolePermissions.notes, ...overrides.notes },
    reports: { ...defaultRolePermissions.reports, ...overrides.reports },
    calculator: { ...defaultRolePermissions.calculator, ...overrides.calculator },
    legalEntities: { ...defaultRolePermissions.legalEntities, ...overrides.legalEntities },
    clients: { ...defaultRolePermissions.clients, ...overrides.clients },
    cases: { ...defaultRolePermissions.cases, ...overrides.cases },
    accounts: { ...defaultRolePermissions.accounts, ...overrides.accounts },
    acts: { ...defaultRolePermissions.acts, ...overrides.acts },
    payments: { ...defaultRolePermissions.payments, ...overrides.payments },
    contracts: { ...defaultRolePermissions.contracts, ...overrides.contracts },
    dictionaries: { ...defaultRolePermissions.dictionaries, ...overrides.dictionaries },
    notifications: { ...defaultRolePermissions.notifications, ...overrides.notifications },
    roles: { ...defaultRolePermissions.roles, ...overrides.roles },
    audit: { ...defaultRolePermissions.audit, ...overrides.audit },
    integrations: { ...defaultRolePermissions.integrations, ...overrides.integrations },
    yandexDisk: { ...defaultRolePermissions.yandexDisk, ...overrides.yandexDisk },
    s3Storage: { ...defaultRolePermissions.s3Storage, ...overrides.s3Storage },
  };
}

const deniedSection = {
  canView: false,
  canCreate: false,
  canEdit: false,
  canDelete: false,
  canExport: false,
  viewScope: 'own',
} as const;

const workLimitedSection = {
  canView: true,
  canCreate: true,
  canEdit: true,
  canDelete: false,
  canExport: false,
  viewScope: 'own',
} as const;

const readOnlySection = {
  canView: true,
  canCreate: false,
  canEdit: false,
  canDelete: false,
  canExport: false,
  viewScope: 'own',
} as const;

const deniedTaskSection = {
  ...deniedSection,
  canEditOthers: false,
  canCompleteOthers: false,
  canDeleteOthers: false,
} as const;

const workLimitedTaskSection = {
  ...workLimitedSection,
  canEditOthers: false,
  canCompleteOthers: false,
  canDeleteOthers: false,
} as const;

function createOperationalRolePermissions(role: 'secretary' | 'lawyer' | 'accountant'): RolePermissions {
  if (role === 'lawyer') {
    return createLawyerTemplatePermissions();
  }

  const permissions = createPermissions({
    calendar: { ...workLimitedSection, canViewAnalytics: false },
    tasks: role === 'accountant' ? deniedTaskSection : workLimitedTaskSection,
    clients: role === 'accountant' ? readOnlySection : workLimitedSection,
    cases: role === 'accountant' ? readOnlySection : workLimitedSection,
    reports: deniedSection,
    accounts: role === 'accountant' ? workLimitedSection : deniedSection,
    acts: role === 'accountant' ? workLimitedSection : deniedSection,
    payments: role === 'accountant' ? workLimitedSection : deniedSection,
    contracts: role === 'accountant' ? workLimitedSection : deniedSection,
    notifications: {
      ...readOnlySection,
      canEdit: true,
    },
    roles: deniedSection,
    audit: deniedSection,
    integrations: deniedSection,
    yandexDisk: deniedSection,
    s3Storage: deniedSection,
  });

  return permissions;
}

function createLawyerTemplatePermissions(): RolePermissions {
  return {
    ...defaultRestrictedRolePermissions,
    calendar: { ...workLimitedSection, canViewAnalytics: false, visibleUserIds: [] },
    tasks: { ...workLimitedTaskSection },
    notifications: { ...readOnlySection, canEdit: true },
  };
}

describe('App payment routing helpers', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('parses a payment id from the pathname', () => {
    window.history.replaceState({}, '', '/payments/880');

    expect(getPathPaymentId()).toBe(880);
    expect(getInitialPaymentId()).toBe(880);
  });

  it('detects the payments list pathname', () => {
    window.history.replaceState({}, '', '/payments');

    expect(isPaymentsListPath()).toBe(true);
    expect(getInitialTab()).toBe('payments');
  });

  it('restores the income payments tab from a payment deep-link', () => {
    window.localStorage.setItem('pp.last_active_tab', 'calendar');
    window.history.replaceState({}, '', '/payments/880?ppTab=paymentsIncome');

    expect(getInitialTab()).toBe('paymentsIncome');
  });

  it('falls back to the generic payments tab for unrelated ppTab values', () => {
    window.history.replaceState({}, '', '/payments/880?ppTab=profile');

    expect(getInitialTab()).toBe('payments');
  });
});

describe('App task routing helpers', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('parses a task id from the pathname', () => {
    window.history.replaceState({}, '', '/tasks/58');

    expect(getPathTaskId()).toBe(58);
    expect(getInitialTaskId()).toBe(58);
    expect(getInitialTab()).toBe('tasks');
  });

  it('detects the tasks list pathname', () => {
    window.history.replaceState({}, '', '/tasks');

    expect(isTasksListPath()).toBe(true);
    expect(getInitialTab()).toBe('tasks');
  });

  it('detects the task calendar pathname', () => {
    window.history.replaceState({}, '', '/tasks/calendar');

    expect(isTasksCalendarPath()).toBe(true);
    expect(getInitialTab()).toBe('tasksCalendar');
  });

  it('detects the task approvals pathname', () => {
    window.history.replaceState({}, '', '/tasks/approvals');

    expect(isTasksApprovalsPath()).toBe(true);
    expect(getInitialTab()).toBe('tasksApprovals');
  });

  it('opens untriaged tasks from their direct address', () => {
    window.history.replaceState({}, '', '/tasks/untriaged');

    expect(isTasksUntriagedPath()).toBe(true);
    expect(getInitialTab()).toBe('tasksUntriaged');
  });
});

describe('App contract routing helpers', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('parses a contract id from the pathname', () => {
    window.history.replaceState({}, '', '/contracts/77');

    expect(getPathContractId()).toBe(77);
    expect(getInitialContractId()).toBe(77);
    expect(getInitialTab()).toBe('contractDetail');
  });

  it('detects the contracts list pathname', () => {
    window.history.replaceState({}, '', '/contracts');

    expect(isContractsListPath()).toBe(true);
    expect(getInitialTab()).toBe('contracts');
  });

  it('restores the contract return tab from a contract deep-link', () => {
    window.history.replaceState({}, '', '/contracts/77?ppReturnTab=accountsIncome');

    expect(getInitialContractReturnTab()).toBe('accountsIncome');
  });

  it('ignores unsupported return tabs for a contract deep-link', () => {
    window.history.replaceState({}, '', '/contracts/77?ppReturnTab=profile');

    expect(getInitialContractReturnTab()).toBe(null);
  });
});

describe('App client routing helpers', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('parses a client id from the pathname', () => {
    window.history.replaceState({}, '', '/clients/42');

    expect(getPathClientId()).toBe(42);
    expect(getInitialClientId()).toBe(42);
    expect(getInitialTab()).toBe('clientDetail');
  });

  it('opens a legal entity only for a valid id on its own section', () => {
    window.history.replaceState({}, '', '/?ppTab=legalEntities&ppLegalEntityId=42');
    expect(getInitialTab()).toBe('legalEntities');
    expect(getInitialLegalEntityId()).toBe(42);
    for (const value of ['0', '-1', '1e3', '9007199254740992', 'abc']) {
      window.history.replaceState({}, '', `/?ppTab=legalEntities&ppLegalEntityId=${value}`);
      expect(getInitialLegalEntityId()).toBeNull();
    }
    window.history.replaceState({}, '', '/?ppTab=tasks&ppLegalEntityId=42');
    expect(getInitialLegalEntityId()).toBeNull();
  });

  it('detects the clients list pathname', () => {
    window.history.replaceState({}, '', '/clients');

    expect(isClientsListPath()).toBe(true);
    expect(getInitialTab()).toBe('clients');
  });

  it('detects the cases registry pathname', () => {
    window.history.replaceState({}, '', '/cases');

    expect(isCasesListPath()).toBe(true);
    expect(getInitialTab()).toBe('cases');
  });

  it('detects the court cases registry pathname', () => {
    window.history.replaceState({}, '', '/court-cases');

    expect(getInitialTab()).toBe('courtCases');
  });

  it('parses a case id from the pathname', () => {
    window.history.replaceState({}, '', '/cases/15');

    expect(getPathCaseId()).toBe(15);
    expect(getInitialTab()).toBe('caseDetail');
  });

  it('restores the case detail tab from a case deep-link', () => {
    window.history.replaceState({}, '', '/cases/15?ppCaseTab=documents');

    expect(getPathCaseId()).toBe(15);
    expect(getInitialCaseDetailTab()).toBe('documents');
    expect(getInitialTab()).toBe('caseDetail');
  });

  it('ignores unsupported case detail tabs', () => {
    window.history.replaceState({}, '', '/cases/15?ppCaseTab=unknown');

    expect(getInitialCaseDetailTab()).toBe(null);
    expect(getInitialTab()).toBe('caseDetail');
  });

  it('does not restore clientDetail from storage without a client id', () => {
    window.localStorage.setItem('pp.last_active_tab.1', 'clientDetail');
    window.history.replaceState({}, '', '/');

    expect(getInitialTab(1)).toBe('clients');
  });

  it.each([
    ['caseDetail', 'cases'], ['userDetail', 'users'], ['contractDetail', 'contracts'],
    ['contractCreate', 'contracts'], ['actDetail', 'acts'], ['actCreate', 'acts'],
  ] as const)('restores stored %s to %s without an object from another session', (stored, expected) => {
    window.localStorage.setItem('pp.last_active_tab.1', stored);
    window.history.replaceState({}, '', '/');
    expect(getInitialTab(1)).toBe(expected);
  });
});

describe('App sidebar routing helpers', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('builds real href values for menu tabs', () => {
    expect(getTabHref('workspace')).toBe('/?ppTab=workspace');
    expect(getTabHref('calendar')).toBe('/?ppTab=calendar');
    expect(getTabHref('notifications')).toBe('/notifications');
    expect(getTabHref('notes')).toBe('/notes');
    expect(getTabHref('tasks')).toBe('/tasks');
    expect(getTabHref('tasksUntriaged')).toBe('/tasks/untriaged');
    expect(getTabHref('tasksApprovals')).toBe('/tasks/approvals');
    expect(getTabHref('tasksCalendar')).toBe('/tasks/calendar');
    expect(getTabHref('clients')).toBe('/clients');
    expect(getTabHref('cases')).toBe('/cases');
    expect(getTabHref('courtCases')).toBe('/court-cases');
    expect(getTabHref('accountsIncome')).toBe('/invoices?ppTab=accountsIncome');
    expect(getTabHref('paymentsExpense')).toBe('/payments?ppTab=paymentsExpense');
    expect(getTabHref('actsIncome')).toBe('/acts?ppTab=actsIncome');
    expect(getTabHref('moneyEMoney')).toBe('/?ppTab=moneyEMoney');
    expect(getTabHref('contactsLegal')).toBe('/?ppTab=contactsLegal');
  });

  it('restores invoice child tabs from menu links', () => {
    window.history.replaceState({}, '', '/invoices?ppTab=accountsExpense');

    expect(getInitialTab()).toBe('accountsExpense');
  });

  it('restores non-path menu tabs from query links', () => {
    window.history.replaceState({}, '', '/?ppTab=moneyEMoney');
    expect(getInitialTab()).toBe('moneyEMoney');

    window.history.replaceState({}, '', '/?ppTab=contactsLegal');
    expect(getInitialTab()).toBe('contactsLegal');
  });

  it('detects the notifications pathname', () => {
    window.history.replaceState({}, '', '/notifications');

    expect(isNotificationsPath()).toBe(true);
    expect(getInitialTab()).toBe('notifications');
  });

  it('keeps an explicit working tab when a stale notifications path is present', () => {
    window.history.replaceState({}, '', '/notifications?ppTab=profile');

    expect(isNotificationsPath()).toBe(true);
    expect(getInitialTab()).toBe('profile');
  });

  it('keeps an explicit user detail tab when a stale notifications path is present', () => {
    window.history.replaceState({}, '', '/notifications?ppTab=userDetail&userId=17');

    expect(isNotificationsPath()).toBe(true);
    expect(getInitialTab()).toBe('userDetail');
    expect(getInitialUserId()).toBe(17);
  });

  it('detects the notes pathname', () => {
    window.history.replaceState({}, '', '/notes');

    expect(isNotesPath()).toBe(true);
    expect(getInitialTab()).toBe('notes');
  });
});

describe('App negative deep-link guards', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it.each([
    {
      path: '/cases/1',
      expectedTab: 'caseDetail' as Tab,
      permissions: createPermissions({ cases: { ...defaultRolePermissions.cases, canView: false } }),
    },
    {
      path: '/acts/new',
      expectedTab: 'actCreate' as Tab,
      permissions: createPermissions({ acts: { ...defaultRolePermissions.acts, canCreate: false } }),
    },
    {
      path: '/acts/1',
      expectedTab: 'actDetail' as Tab,
      permissions: createPermissions({ acts: { ...defaultRolePermissions.acts, canView: false } }),
    },
    {
      path: '/contracts/1',
      expectedTab: 'contractDetail' as Tab,
      permissions: createPermissions({ contracts: { ...defaultRolePermissions.contracts, canView: false } }),
    },
    {
      path: '/invoices/1',
      expectedTab: 'accounts' as Tab,
      permissions: createPermissions({ accounts: { ...defaultRolePermissions.accounts, canView: false } }),
    },
    {
      path: '/payments/1',
      expectedTab: 'payments' as Tab,
      permissions: createPermissions({ payments: { ...defaultRolePermissions.payments, canView: false } }),
    },
    {
      path: '/tasks/1',
      expectedTab: 'tasks' as Tab,
      permissions: createPermissions({ tasks: { ...defaultRolePermissions.tasks, canView: false } }),
    },
    {
      path: '/notes',
      expectedTab: 'notes' as Tab,
      permissions: createPermissions({ notes: { ...defaultRolePermissions.notes, canView: false } }),
    },
    {
      path: '/clients/1',
      expectedTab: 'clientDetail' as Tab,
      permissions: createPermissions({ clients: { ...defaultRolePermissions.clients, canView: false } }),
    },
  ])('denies $path when the required permission is missing', ({ path, expectedTab, permissions }) => {
    window.history.replaceState({}, '', path);

    expect(getInitialTab()).toBe(expectedTab);
    expect(canAccessTab(expectedTab, permissions)).toBe(false);
  });

  it('keeps tab access rules exhaustive for every declared tab', () => {
    expect(Object.keys(TAB_ACCESS_RULES).sort()).toEqual([...ALL_TABS].sort());
  });

  it.each([
    {
      tab: 'actCreate' as Tab,
      permissions: createPermissions({
        acts: { ...defaultRolePermissions.acts, canView: false, canCreate: true },
      }),
    },
    {
      tab: 'contractCreate' as Tab,
      permissions: createPermissions({
        contracts: { ...defaultRolePermissions.contracts, canView: false, canCreate: true },
      }),
    },
  ])('denies $tab when create is enabled but the parent section is closed', ({ tab, permissions }) => {
    expect(canAccessTab(tab, permissions)).toBe(false);
  });

  it('prefers notifications when a denied route needs a safe fallback', () => {
    expect(selectAccessibleFallbackTab(['calendar', 'tasks', 'notifications'])).toBe('notifications');
  });

  it('falls back to profile when notifications are not available', () => {
    expect(selectAccessibleFallbackTab(['calendar', 'profile'])).toBe('profile');
    expect(selectAccessibleFallbackTab([])).toBe('profile');
  });
});

describe('App real role route access matrix', () => {
  it.each([
    {
      role: 'secretary' as const,
      allowedTabs: ['workspace', 'calendar', 'tasks', 'tasksUntriaged', 'tasksCalendar', 'clients', 'cases', 'notifications', 'settings'] as Tab[],
      deniedTabs: ['payroll', 'accounts', 'acts', 'payments', 'contracts', 'reports', 'roles', 'audit', 'yandexDisk', 's3Storage', 'companyProfile', 'users', 'userDetail', 'userActivity'] as Tab[],
    },
    {
      role: 'lawyer' as const,
      allowedTabs: ['workspace', 'calendar', 'tasks', 'tasksUntriaged', 'tasksCalendar', 'notifications', 'settings'] as Tab[],
      deniedTabs: ['payroll', 'clients', 'clientDetail', 'contacts', 'contactsClients', 'contactsExternal', 'cases', 'caseDetail', 'accounts', 'acts', 'payments', 'contracts', 'reports', 'roles', 'audit', 'yandexDisk', 's3Storage', 'companyProfile', 'users', 'userDetail', 'userActivity'] as Tab[],
    },
    {
      role: 'accountant' as const,
      allowedTabs: ['workspace', 'calendar', 'clients', 'cases', 'accounts', 'acts', 'payments', 'contracts', 'notifications', 'settings'] as Tab[],
      deniedTabs: ['payroll', 'tasks', 'tasksUntriaged', 'tasksControl', 'tasksApprovals', 'tasksCalendar', 'reports', 'roles', 'audit', 'yandexDisk', 's3Storage', 'companyProfile', 'users', 'userDetail', 'userActivity'] as Tab[],
    },
  ])('applies expected menu access for $role', ({ role, allowedTabs, deniedTabs }) => {
    const permissions = createOperationalRolePermissions(role);
    const options = {
      canManageRoles: permissions.roles.canView,
      canViewAudit: permissions.audit.canView,
      canViewSettings: permissions.notifications.canView || permissions.integrations.canView || permissions.yandexDisk.canView || permissions.s3Storage.canView,
      canViewYandexDisk: permissions.yandexDisk.canView,
      canViewS3Storage: permissions.s3Storage.canView,
    };

    for (const tab of allowedTabs) {
      expect(canAccessTab(tab, permissions, options), `${role} should access ${tab}`).toBe(true);
    }

    for (const tab of deniedTabs) {
      expect(canAccessTab(tab, permissions, options), `${role} should not access ${tab}`).toBe(false);
    }
  });

  it('keeps lawyer template direct tab access explicit for every app tab', () => {
    const permissions = createLawyerTemplatePermissions();
    const options = {
      canManageRoles: permissions.roles.canView,
      canViewAudit: permissions.audit.canView,
      canViewSettings: permissions.notifications.canView || permissions.integrations.canView || permissions.yandexDisk.canView || permissions.s3Storage.canView,
      canViewYandexDisk: permissions.yandexDisk.canView,
      canViewS3Storage: permissions.s3Storage.canView,
    };

    const allowedTabs = new Set<Tab>([
      'workspace',
      'notifications',
      'messenger',
      'profile',
      'calendar',
      'tasks',
      'tasksUntriaged',
      'tasksControl',
      'tasksApprovals',
      'tasksCalendar',
      'settings',
    ]);

    for (const tab of ALL_TABS) {
      expect(canAccessTab(tab, permissions, options), `lawyer direct tab access mismatch for ${tab}`).toBe(allowedTabs.has(tab));
    }
  });
});

describe('App act routing helpers', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('parses an act id from the pathname', () => {
    window.history.replaceState({}, '', '/acts/91');

    expect(getPathActId()).toBe(91);
    expect(getInitialActId()).toBe(91);
    expect(getInitialTab()).toBe('actDetail');
  });

  it('detects the acts list pathname', () => {
    window.history.replaceState({}, '', '/acts');

    expect(isActsListPath()).toBe(true);
    expect(getInitialTab()).toBe('acts');
  });

  it('detects the act create pathname', () => {
    window.history.replaceState({}, '', '/acts/new');

    expect(isActCreatePath()).toBe(true);
    expect(getInitialTab()).toBe('actCreate');
  });

  it('restores the act return tab from an act create deep-link', () => {
    window.history.replaceState({}, '', '/acts/new?ppReturnTab=actsExpense');

    expect(getInitialActReturnTab()).toBe('actsExpense');
  });
});
