import { beforeEach, describe, expect, it } from 'vitest';
import {
  getInitialActReturnTab,
  getInitialActId,
  getInitialClientId,
  getInitialContractId,
  getInitialContractReturnTab,
  getInitialPaymentId,
  getInitialTab,
  getInitialTaskId,
  getTabHref,
  getPathActId,
  getPathClientId,
  getPathContractId,
  getPathPaymentId,
  getPathTaskId,
  isActCreatePath,
  isActsListPath,
  isClientsListPath,
  isContractsListPath,
  isPaymentsListPath,
  isTasksCalendarPath,
  isTasksListPath,
} from './appRouting';

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
    expect(getTabHref('tasks')).toBe('/tasks');
    expect(getTabHref('tasksCalendar')).toBe('/tasks/calendar');
    expect(getTabHref('clients')).toBe('/clients');
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
