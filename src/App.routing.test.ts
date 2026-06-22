import { beforeEach, describe, expect, it } from 'vitest';
import {
  canAccessTab,
  getInitialActReturnTab,
  getInitialCaseDetailTab,
  getInitialActId,
  getInitialClientId,
  getInitialContractId,
  getInitialContractReturnTab,
  getInitialPaymentId,
  getInitialTab,
  getInitialTaskId,
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
  isTasksApprovalsPath,
  isTasksCalendarPath,
  isTasksListPath,
} from './appRouting';
import { defaultRolePermissions, type RolePermissions } from './types/permissions';
import type { Tab } from './types/tabs';

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
    yandexDisk: { ...defaultRolePermissions.yandexDisk, ...overrides.yandexDisk },
    s3Storage: { ...defaultRolePermissions.s3Storage, ...overrides.s3Storage },
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
    window.localStorage.setItem('pp.last_active_tab', 'clientDetail');
    window.history.replaceState({}, '', '/');

    expect(getInitialTab()).toBe('clients');
  });
});

describe('App sidebar routing helpers', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('builds real href values for menu tabs', () => {
    expect(getTabHref('calendar')).toBe('/?ppTab=calendar');
    expect(getTabHref('notifications')).toBe('/notifications');
    expect(getTabHref('notes')).toBe('/notes');
    expect(getTabHref('tasks')).toBe('/tasks');
    expect(getTabHref('tasksApprovals')).toBe('/tasks/approvals');
    expect(getTabHref('tasksCalendar')).toBe('/tasks/calendar');
    expect(getTabHref('clients')).toBe('/clients');
    expect(getTabHref('cases')).toBe('/cases');
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
