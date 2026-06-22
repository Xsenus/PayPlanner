import { ALL_TABS, type Tab } from './types/tabs';
import type { RolePermissions } from './types/permissions';

export const LAST_TAB_STORAGE_KEY = 'pp.last_active_tab';
export const CASE_DETAIL_TAB_QUERY_PARAM = 'ppCaseTab';

const CASE_DETAIL_TABS = [
  'overview',
  'participants',
  'tasks',
  'court',
  'finance',
  'workOrder',
  'documents',
  'conversation',
  'history',
] as const;

export type CaseDetailRouteTab = typeof CASE_DETAIL_TABS[number];

function isValidTab(value: string | null): value is Tab {
  return value !== null && (ALL_TABS as readonly string[]).includes(value);
}

type TabAccessOptions = {
  isAdminRole?: boolean;
  canManageRoles?: boolean;
  canViewAudit?: boolean;
  canViewSettings?: boolean;
  canViewYandexDisk?: boolean;
  canViewS3Storage?: boolean;
};

export function canAccessTab(tab: Tab, permissions: RolePermissions, options: TabAccessOptions = {}): boolean {
  switch (tab) {
    case 'notifications':
    case 'profile':
      return true;
    case 'calendar':
      return permissions.calendar.canView;
    case 'tasks':
    case 'tasksControl':
    case 'tasksApprovals':
    case 'tasksCalendar':
      return permissions.tasks.canView;
    case 'notes':
      return permissions.notes.canView;
    case 'reports':
      return permissions.reports.canView;
    case 'calculator':
      return permissions.calculator.canView;
    case 'legalEntities':
      return permissions.legalEntities.canView;
    case 'cases':
    case 'caseDetail':
      return permissions.cases.canView;
    case 'clients':
    case 'contacts':
      return permissions.clients.canView || permissions.legalEntities.canView;
    case 'contactsClients':
      return permissions.clients.canView;
    case 'contactsLegal':
      return permissions.legalEntities.canView;
    case 'contactsExternal':
    case 'clientDetail':
      return permissions.clients.canView;
    case 'accounts':
    case 'accountsIncome':
    case 'accountsExpense':
    case 'installments':
      return permissions.accounts.canView;
    case 'acts':
    case 'actsIncome':
    case 'actsExpense':
    case 'actDetail':
      return permissions.acts.canView;
    case 'actCreate':
      return permissions.acts.canCreate;
    case 'payments':
    case 'paymentsIncome':
    case 'paymentsExpense':
    case 'moneyBank':
    case 'moneyCash':
    case 'moneyCards':
    case 'moneyEMoney':
    case 'moneyTotal':
      return permissions.payments.canView;
    case 'contracts':
    case 'contractDetail':
      return permissions.contracts.canView;
    case 'contractCreate':
      return permissions.contracts.canCreate;
    case 'companyProfile':
    case 'users':
    case 'userDetail':
    case 'userActivity':
      return options.isAdminRole === true;
    case 'audit':
      return options.canViewAudit === true;
    case 'yandexDisk':
      return options.canViewYandexDisk === true;
    case 's3Storage':
      return options.canViewS3Storage === true;
    case 'settings':
      return options.canViewSettings === true;
    case 'roles':
      return options.canManageRoles === true;
    case 'dictionaries':
      return permissions.dictionaries.canView;
    default:
      return true;
  }
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

export function getPathTaskId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const match = window.location.pathname.match(/^\/tasks\/(\d+)\/?$/i);
  if (!match) {
    return null;
  }

  const parsed = Number(match[1]);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

export function isTasksListPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/tasks\/?$/i.test(window.location.pathname);
}

export function isTasksCalendarPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/tasks\/calendar\/?$/i.test(window.location.pathname);
}

export function isTasksControlPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/tasks\/control\/?$/i.test(window.location.pathname);
}

export function isTasksApprovalsPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/tasks\/approvals\/?$/i.test(window.location.pathname);
}

export function isNotificationsPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/notifications\/?$/i.test(window.location.pathname);
}

export function isNotesPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/notes\/?$/i.test(window.location.pathname);
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

export function isCasesListPath(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  return /^\/cases\/?$/i.test(window.location.pathname);
}

export function getPathCaseId(): number | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const match = window.location.pathname.match(/^\/cases\/(\d+)\/?$/i);
  if (!match) {
    return null;
  }

  const parsed = Number(match[1]);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
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

function isAccountsTab(tab: string | null): tab is 'accounts' | 'accountsIncome' | 'accountsExpense' {
  return tab === 'accounts' || tab === 'accountsIncome' || tab === 'accountsExpense';
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

function isCaseDetailRouteTab(tab: string | null): tab is CaseDetailRouteTab {
  return tab !== null && (CASE_DETAIL_TABS as readonly string[]).includes(tab);
}

export function getInitialCaseDetailTab(): CaseDetailRouteTab | null {
  if (typeof window === 'undefined' || !getPathCaseId()) {
    return null;
  }

  const params = new URLSearchParams(window.location.search);
  const tab = params.get(CASE_DETAIL_TAB_QUERY_PARAM);
  return isCaseDetailRouteTab(tab) ? tab : null;
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

  if (getPathCaseId()) {
    return 'caseDetail';
  }

  if (isCasesListPath()) {
    return 'cases';
  }

  if (isNotificationsPath()) {
    return 'notifications';
  }

  if (isNotesPath()) {
    return 'notes';
  }

  if (getPathInvoiceId() || isInvoicesListPath()) {
    return isAccountsTab(urlTab) ? urlTab : 'accounts';
  }

  if (getPathPaymentId() || isPaymentsListPath()) {
    return isPaymentsTab(urlTab as Tab) ? (urlTab as Tab) : 'payments';
  }

  if (getPathTaskId() || isTasksListPath()) {
    return 'tasks';
  }

  if (isTasksControlPath()) {
    return 'tasksControl';
  }

  if (isTasksApprovalsPath()) {
    return 'tasksApprovals';
  }

  if (isTasksCalendarPath()) {
    return 'tasksCalendar';
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

export function getTabHref(tab: Tab): string {
  if (tab === 'tasks') {
    return '/tasks';
  }

  if (tab === 'notifications') {
    return '/notifications';
  }

  if (tab === 'notes') {
    return '/notes';
  }

  if (tab === 'tasksApprovals') {
    return '/tasks/approvals';
  }

  if (tab === 'tasksControl') {
    return '/tasks/control';
  }

  if (tab === 'tasksCalendar') {
    return '/tasks/calendar';
  }

  if (tab === 'clients') {
    return '/clients';
  }

  if (tab === 'cases') {
    return '/cases';
  }

  if (isAccountsTab(tab)) {
    return tab === 'accounts' ? '/invoices' : `/invoices?ppTab=${tab}`;
  }

  if (isPaymentsTab(tab)) {
    return tab === 'payments' ? '/payments' : `/payments?ppTab=${tab}`;
  }

  if (isActsTab(tab)) {
    return tab === 'acts' ? '/acts' : `/acts?ppTab=${tab}`;
  }

  if (tab === 'contracts') {
    return '/contracts';
  }

  return `/?ppTab=${tab}`;
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

export function getInitialTaskId(): number | null {
  return getPathTaskId();
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
