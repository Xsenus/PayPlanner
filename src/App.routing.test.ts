import { beforeEach, describe, expect, it } from 'vitest';
import {
  getInitialContractId,
  getInitialContractReturnTab,
  getInitialPaymentId,
  getInitialTab,
  getPathContractId,
  getPathPaymentId,
  isContractsListPath,
  isPaymentsListPath,
} from './App';

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
