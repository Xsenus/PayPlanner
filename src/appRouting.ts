import type { Tab } from './types/tabs';

export const LAST_TAB_STORAGE_KEY = 'pp.last_active_tab';

const ALL_TABS: Tab[] = [
  'calendar',
  'reports',
  'calculator',
  'legalEntities',
  'clients',
  'contacts',
  'contactsClients',
  'contactsExternal',
  'clientDetail',
  'accounts',
  'accountsIncome',
  'accountsExpense',
  'installments',
  'acts',
  'actsIncome',
  'actsExpense',
  'actCreate',
  'actDetail',
  'paymentsIncome',
  'paymentsExpense',
  'payments',
  'moneyBank',
  'moneyCash',
  'moneyCards',
  'moneyTotal',
  'contracts',
  'contractCreate',
  'contractDetail',
  'companyProfile',
  'profile',
  'users',
  'userDetail',
  'roles',
  'audit',
  'userActivity',
  'dictionaries',
  'settings',
];

function isValidTab(value: string | null): value is Tab {
  return Boolean(value) && ALL_TABS.includes(value as Tab);
}

export function getPathInvoiceId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const match = window.location.pathname.match(/^\/invoices\/(\d+)\/?$/i);
  if (!match) {
    return null;
  }

  const parsed = Number(match[1]);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

export function isInvoicesListPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/invoices\/?$/i.test(window.location.pathname);
}

export function getPathPaymentId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const match = window.location.pathname.match(/^\/payments\/(\d+)\/?$/i);
  if (!match) {
    return null;
  }

  const parsed = Number(match[1]);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

export function isPaymentsListPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/payments\/?$/i.test(window.location.pathname);
}

export function getPathClientId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const match = window.location.pathname.match(/^\/clients\/(\d+)\/?$/i);
  if (!match) {
    return null;
  }

  const parsed = Number(match[1]);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

export function isClientsListPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/clients\/?$/i.test(window.location.pathname);
}

export function getPathContractId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const match = window.location.pathname.match(/^\/contracts\/(\d+)\/?$/i);
  if (!match) {
    return null;
  }

  const parsed = Number(match[1]);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

export function isContractsListPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/contracts\/?$/i.test(window.location.pathname);
}

export function getPathActId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const match = window.location.pathname.match(/^\/acts\/(\d+)\/?$/i);
  if (!match) {
    return null;
  }

  const parsed = Number(match[1]);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

export function isActsListPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/acts\/?$/i.test(window.location.pathname);
}

export function isActCreatePath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/acts\/new\/?$/i.test(window.location.pathname);
}

function isPaymentsTab(tab: Tab): tab is 'payments' | 'paymentsIncome' | 'paymentsExpense' {
  return tab === 'payments' || tab === 'paymentsIncome' || tab === 'paymentsExpense';
}

function isActsTab(tab: string | null): tab is 'acts' | 'actsIncome' | 'actsExpense' {
  return tab === 'acts' || tab === 'actsIncome' || tab === 'actsExpense';
}

function isContractReturnTab(tab: string | null): tab is 'contracts' | 'accounts' | 'accountsIncome' | 'accountsExpense' {
  return tab === 'contracts' || tab === 'accounts' || tab === 'accountsIncome' || tab === 'accountsExpense';
}

function isActReturnTab(tab: string | null): tab is 'acts' | 'actsIncome' | 'actsExpense' {
  return tab === 'acts' || tab === 'actsIncome' || tab === 'actsExpense';
}

export function getInitialTab(): Tab {
  if (typeof window === 'undefined') {
    return 'calendar';
  }

  const params = new URLSearchParams(window.location.search);
  const urlTab = params.get('ppTab');

  if (getPathClientId()) {
    return 'clientDetail';
  }

  if (isClientsListPath()) {
    return 'clients';
  }

  if (getPathInvoiceId() || isInvoicesListPath()) {
    return 'accounts';
  }

  if (getPathPaymentId() || isPaymentsListPath()) {
    return isPaymentsTab(urlTab as Tab) ? (urlTab as Tab) : 'payments';
  }

  if (getPathContractId()) {
    return 'contractDetail';
  }

  if (isContractsListPath()) {
    return 'contracts';
  }

  if (isActCreatePath()) {
    return 'actCreate';
  }

  if (getPathActId()) {
    return 'actDetail';
  }

  if (isActsListPath()) {
    return isActsTab(urlTab) ? urlTab : 'acts';
  }

  if (isValidTab(urlTab)) {
    if (urlTab === 'clientDetail') {
      return 'clients';
    }

    return urlTab;
  }

  try {
    const stored = localStorage.getItem(LAST_TAB_STORAGE_KEY);
    if (isValidTab(stored)) {
      if (stored === 'clientDetail') {
        return 'clients';
      }

      return stored;
    }
  } catch {
    // ignore storage issues
  }

  return 'calendar';
}

export function getInitialUserId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const params = new URLSearchParams(window.location.search);
  const rawValue = params.get('userId');
  if (!rawValue) {
    return null;
  }

  const nextValue = Number(rawValue);
  return Number.isInteger(nextValue) && nextValue > 0 ? nextValue : null;
}

export function getInitialInvoiceId(): number | null {
  return getPathInvoiceId();
}

export function getInitialPaymentId(): number | null {
  return getPathPaymentId();
}

export function getInitialClientId(): number | null {
  return getPathClientId();
}

export function getInitialContractId(): number | null {
  return getPathContractId();
}

export function getInitialContractReturnTab(): 'contracts' | 'accounts' | 'accountsIncome' | 'accountsExpense' | null {
  if (typeof window === 'undefined' || !getPathContractId()) {
    return null;
  }

  const params = new URLSearchParams(window.location.search);
  const rawValue = params.get('ppReturnTab');
  return isContractReturnTab(rawValue) ? rawValue : null;
}

export function getInitialActId(): number | null {
  return getPathActId();
}

export function getInitialActReturnTab(): 'acts' | 'actsIncome' | 'actsExpense' | null {
  if (typeof window === 'undefined' || (!getPathActId() && !isActCreatePath())) {
    return null;
  }

  const params = new URLSearchParams(window.location.search);
  const rawValue = params.get('ppReturnTab');
  return isActReturnTab(rawValue) ? rawValue : null;
}
