import { Suspense, lazy, useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { AuthProvider } from './contexts/AuthContext';
import Navigation from './components/Navigation/Navigation';
import { Login } from './components/Auth/Login';
import { Register } from './components/Auth/Register';
import { AwaitingApproval } from './components/Auth/AwaitingApproval';
import TopBar from './components/TopBar/TopBar';
import { RouteErrorBoundary } from './components/ui/RouteErrorBoundary';
import { DoubleHorizontalScrollbars } from './components/ui/DoubleHorizontalScrollbars';
import type { Act, ActInput, Invoice, Payment } from './types';
import type { Tab } from './types/tabs';
import { useAuth } from './contexts/AuthContext';
import { useRolePermissions } from './hooks/useRolePermissions';
import { TabProvider } from './contexts/TabContext';
import { useActivityLogger } from './hooks/useActivityLogger';
import { DialogProvider } from './contexts/DialogContext';
import { ToastProvider } from './contexts/ToastContext';
import type { User as AppUser } from './services/authService';
import { type ContactScope } from './services/api';
import {
  getInitialActId,
  getInitialActReturnTab,
  getInitialClientId,
  getInitialContractId,
  getInitialContractReturnTab,
  getInitialInvoiceId,
  getInitialPaymentId,
  getInitialTab,
  getInitialUserId,
  LAST_TAB_STORAGE_KEY,
} from './appRouting';

type AuthView = 'login' | 'register' | 'awaiting';
type ActListTab = 'acts' | 'actsIncome' | 'actsExpense';
type ActReturnContext =
  | { tab: ActListTab }
  | { tab: 'accounts' | 'accountsIncome' | 'accountsExpense'; invoiceId?: number | null }
  | { tab: 'clientDetail'; clientId: number; section?: 'acts' | 'accounts' }
  | { tab: 'contractDetail'; contractId: number };

const Calendar = lazy(() => import('./components/Calendar/Calendar').then((module) => ({ default: module.Calendar })));
const Reports = lazy(() => import('./components/Reports/Reports').then((module) => ({ default: module.Reports })));
const Calculator = lazy(() => import('./components/Calculator/Calculator').then((module) => ({ default: module.Calculator })));
const Clients = lazy(() => import('./components/Clients/Clients').then((module) => ({ default: module.Clients })));
const Contacts = lazy(() => import('./components/Contacts/UnifiedContacts').then((module) => ({ default: module.UnifiedContacts })));
const PhoneContacts = lazy(() => import('./components/Contacts/Contacts').then((module) => ({ default: module.Contacts })));
const LegalEntities = lazy(() => import('./components/LegalEntities/LegalEntities').then((module) => ({ default: module.LegalEntities })));
const ClientDetail = lazy(() => import('./components/Clients/ClientDetail').then((module) => ({ default: module.ClientDetail })));
const Accounts = lazy(() => import('./components/Accounts/Accounts').then((module) => ({ default: module.Accounts })));
const Installments = lazy(() => import('./components/Accounts/Installments').then((module) => ({ default: module.Installments })));
const Acts = lazy(() => import('./components/Acts/Acts').then((module) => ({ default: module.Acts })));
const ActCreatePage = lazy(() => import('./components/Acts/ActCreatePage').then((module) => ({ default: module.ActCreatePage })));
const ActDetailPage = lazy(() => import('./components/Acts/ActDetailPage').then((module) => ({ default: module.ActDetailPage })));
const Contracts = lazy(() => import('./components/Contracts/Contracts').then((module) => ({ default: module.Contracts })));
const ContractCreatePage = lazy(() => import('./components/Contracts/ContractCreatePage').then((module) => ({ default: module.ContractCreatePage })));
const ContractDetailPage = lazy(() => import('./components/Contracts/ContractDetailPage').then((module) => ({ default: module.ContractDetailPage })));
const Users = lazy(() => import('./components/Users/Users').then((module) => ({ default: module.Users })));
const UserDetailPage = lazy(() => import('./components/Users/UserDetailPage').then((module) => ({ default: module.UserDetailPage })));
const Roles = lazy(() => import('./components/Roles/Roles').then((module) => ({ default: module.Roles })));
const ObjectAuditPage = lazy(() => import('./components/ObjectAudit/ObjectAuditPage').then((module) => ({ default: module.ObjectAuditPage })));
const UserActivity = lazy(() => import('./components/UserActivity/UserActivity').then((module) => ({ default: module.UserActivity })));
const Dictionaries = lazy(() => import('./components/Dictionaries/Dictionaries').then((module) => ({ default: module.Dictionaries })));
const Payments = lazy(() => import('./components/Payments/Payments').then((module) => ({ default: module.Payments })));
const MoneyOperations = lazy(() => import('./components/Money/MoneyOperations').then((module) => ({ default: module.MoneyOperations })));
const CompanyProfile = lazy(() => import('./components/CompanyProfile/CompanyProfile').then((module) => ({ default: module.CompanyProfile })));
const AppSettings = lazy(() => import('./components/Settings/AppSettings').then((module) => ({ default: module.AppSettings })));
const ProfilePage = lazy(() => import('./components/Profile/ProfilePage').then((module) => ({ default: module.ProfilePage })));

function ActiveTabContent({ render }: { render: () => ReactNode }) {
  return <>{render()}</>;
}

function AppContent() {
  const { user, loading, signOut } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const logActivity = useActivityLogger();
  const isAdminRole = (user?.role?.name ?? '').toLowerCase() === 'admin';
  const canManageRoles = isAdminRole || permissions.roles.canView;
  const canViewAudit = isAdminRole || permissions.audit.canView;
  const canViewSettings = canViewAudit;

  const tabNames: Record<string, string> = useMemo(
    () => ({
      calendar: 'Календарь',
      reports: 'Отчеты',
      calculator: 'Калькулятор',
      legalEntities: 'Юр. лица',
      clients: 'Клиенты',
      contacts: 'Контакты',
      contactsClients: 'Контакты: физические лица',
      contactsLegal: 'Контакты: юридические лица',
      contactsExternal: 'Контакты: сторонние',
      clientDetail: 'Карточка клиента',
      accounts: 'Счета',
      accountsIncome: 'Доходные счета',
      accountsExpense: 'Расходные счета',
      installments: 'Рассрочка',
      acts: 'Акты',
      actsIncome: 'Доходные акты',
      actsExpense: 'Расходные акты',
      actCreate: 'Новый акт',
      actDetail: 'Акт',
      paymentsIncome: 'Доходные платежи',
      paymentsExpense: 'Расходные платежи',
      payments: 'Платежи',
      moneyBank: 'Деньги: банк',
      moneyCash: 'Деньги: касса',
      moneyCards: 'Деньги: карты',
      moneyEMoney: 'Деньги: E-money',
      moneyTotal: 'Деньги: итого',
      contracts: 'Договоры',
      contractCreate: 'Новый договор',
      contractDetail: 'Договор',
      companyProfile: 'Профиль компании',
      profile: 'Ваш профиль',
      users: 'Пользователи',
      userDetail: 'Пользователь',
      roles: 'Роли',
      audit: 'Аудит объектов',
      userActivity: 'Контроль пользователей',
      dictionaries: 'Справочники',
      settings: 'Настройки',
    }),
    [],
  );

  const [activeTab, setActiveTab] = useState<Tab>(getInitialTab);
  const [selectedClientId, setSelectedClientId] = useState<number | null>(getInitialClientId);
  const [selectedUserId, setSelectedUserId] = useState<number | null>(getInitialUserId);
  const [selectedInvoiceId, setSelectedInvoiceId] = useState<number | null>(getInitialInvoiceId);
  const [selectedPaymentId, setSelectedPaymentId] = useState<number | null>(getInitialPaymentId);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');
  const [initialClientSection, setInitialClientSection] = useState<'accounts' | 'acts' | undefined>(undefined);
  const [authView, setAuthView] = useState<AuthView>('login');
  const [selectedContractId, setSelectedContractId] = useState<number | null>(getInitialContractId);
  const [selectedActId, setSelectedActId] = useState<number | null>(getInitialActId);
  const [actReturnTab, setActReturnTab] = useState<ActListTab>(getInitialActReturnTab() ?? 'acts');
  const [actReturnContext, setActReturnContext] = useState<ActReturnContext>(() => ({ tab: getInitialActReturnTab() ?? 'acts' }));
  const [actCreateInitialValues, setActCreateInitialValues] = useState<(Partial<ActInput> & { amount?: number | null }) | null>(null);
  const [actCreateInvoiceSeed, setActCreateInvoiceSeed] = useState<Invoice | null>(null);
  const [invoicePageReturnClientId, setInvoicePageReturnClientId] = useState<number | null>(null);
  const [contractReturnTab, setContractReturnTab] = useState<Tab | null>(getInitialContractReturnTab);
  const [clientReturnTab, setClientReturnTab] = useState<{
    tab: Tab;
    contractId?: number | null;
    actId?: number | null;
  } | null>(null);

  const accessibleMenuTabs = useMemo(
    () =>
      [
        { tab: 'calendar' as Tab, allowed: permissions.calendar.canView },
        { tab: 'reports' as Tab, allowed: permissions.reports.canView },
        { tab: 'calculator' as Tab, allowed: permissions.calculator.canView },
        { tab: 'legalEntities' as Tab, allowed: permissions.legalEntities.canView },
        { tab: 'clients' as Tab, allowed: permissions.clients.canView },
        { tab: 'contacts' as Tab, allowed: permissions.clients.canView || permissions.legalEntities.canView },
        { tab: 'contactsClients' as Tab, allowed: permissions.clients.canView },
        { tab: 'contactsLegal' as Tab, allowed: permissions.legalEntities.canView },
        { tab: 'contactsExternal' as Tab, allowed: permissions.clients.canView },
        { tab: 'accounts' as Tab, allowed: permissions.accounts.canView },
        { tab: 'accountsIncome' as Tab, allowed: permissions.accounts.canView },
        { tab: 'accountsExpense' as Tab, allowed: permissions.accounts.canView },
        { tab: 'installments' as Tab, allowed: permissions.accounts.canView },
        { tab: 'acts' as Tab, allowed: permissions.acts.canView },
        { tab: 'actsIncome' as Tab, allowed: permissions.acts.canView },
        { tab: 'actsExpense' as Tab, allowed: permissions.acts.canView },
        { tab: 'payments' as Tab, allowed: permissions.payments.canView },
        { tab: 'paymentsIncome' as Tab, allowed: permissions.payments.canView },
        { tab: 'paymentsExpense' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyBank' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyCash' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyCards' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyEMoney' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyTotal' as Tab, allowed: permissions.payments.canView },
        { tab: 'contracts' as Tab, allowed: permissions.contracts.canView },
        { tab: 'dictionaries' as Tab, allowed: permissions.dictionaries.canView },
        { tab: 'users' as Tab, allowed: isAdminRole },
        { tab: 'roles' as Tab, allowed: canManageRoles },
        { tab: 'audit' as Tab, allowed: canViewAudit },
        { tab: 'settings' as Tab, allowed: canViewSettings },
      ]
        .filter((entry) => entry.allowed)
        .map((entry) => entry.tab),
    [permissions, isAdminRole, canManageRoles, canViewAudit, canViewSettings],
  );

  const canViewTab = useCallback(
    (tab: Tab) => {
      switch (tab) {
        case 'calendar':
          return permissions.calendar.canView;
        case 'reports':
          return permissions.reports.canView;
        case 'calculator':
          return permissions.calculator.canView;
        case 'legalEntities':
          return permissions.legalEntities.canView;
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
          return isAdminRole;
        case 'profile':
          return true;
        case 'users':
        case 'userDetail':
        case 'userActivity':
          return isAdminRole;
        case 'audit':
          return canViewAudit;
        case 'settings':
          return canViewSettings;
        case 'roles':
          return canManageRoles;
        case 'dictionaries':
          return permissions.dictionaries.canView;
        default:
          return true;
      }
    },
    [permissions, isAdminRole, canManageRoles, canViewAudit, canViewSettings],
  );

  const handleOpenClient = useCallback(
    (
      clientId: number,
      caseId?: number,
      options?: { returnTo?: { tab: Tab; contractId?: number | null; actId?: number | null } },
    ) => {
      if (!permissions.clients.canView) {
        return;
      }

      setInitialClientSection(undefined);
      setInvoicePageReturnClientId(null);
      setClientReturnTab(options?.returnTo ?? { tab: 'clients' });
      setSelectedClientId(clientId);
      setInitialCaseId(typeof caseId === 'number' ? caseId : 'all');
      setActiveTab('clientDetail');

      void logActivity({
        category: 'clients',
        action: 'open_client',
        section: 'clientDetail',
        objectType: 'Client',
        objectId: String(clientId),
        description:
          typeof caseId === 'number'
            ? `Открыта карточка клиента ${clientId}, дело ${caseId}`
            : `Открыта карточка клиента ${clientId}`,
        status: 'Info',
      });
    },
    [logActivity, permissions.clients.canView],
  );

  const handleRouteInvoiceChange = useCallback((invoiceId: number | null) => {
    setSelectedInvoiceId(invoiceId);
    if (invoiceId === null) {
      setInvoicePageReturnClientId(null);
    }
  }, []);

  const handleBackToClientFromInvoicePage = useCallback(() => {
    if (!invoicePageReturnClientId) {
      return;
    }

    setInitialClientSection('accounts');
    setSelectedClientId(invoicePageReturnClientId);
    setActiveTab('clientDetail');
  }, [invoicePageReturnClientId]);

  const openInvoicePage = useCallback(
    (invoiceId: number, options?: { type?: 'Income' | 'Expense'; returnClientId?: number | null }) => {
      if (!permissions.accounts.canView || !(invoiceId > 0)) {
        return;
      }

      setInvoicePageReturnClientId(options?.returnClientId ?? null);
      setSelectedInvoiceId(invoiceId);
      setActiveTab(options?.type === 'Expense' ? 'accountsExpense' : options?.type === 'Income' ? 'accountsIncome' : 'accounts');
    },
    [permissions.accounts.canView],
  );

  const openContractDetail = useCallback(
    (contractId: number, options?: { returnTo?: Tab | null }) => {
      if (!permissions.contracts.canView) {
        return;
      }

      setSelectedContractId(contractId);
      setContractReturnTab(options?.returnTo ?? null);
      setActiveTab('contractDetail');
    },
    [permissions.contracts.canView],
  );

  const openPaymentDetail = useCallback(
    (payment: Payment) => {
      if (!permissions.payments.canView || !(payment.id > 0)) {
        return;
      }

      setSelectedPaymentId(payment.id);
      setActiveTab(payment.type === 'Expense' ? 'paymentsExpense' : 'paymentsIncome');
    },
    [permissions.payments.canView],
  );

  const applyActReturnContext = useCallback((context: ActReturnContext) => {
    setActReturnContext(context);
    if (context.tab === 'acts' || context.tab === 'actsIncome' || context.tab === 'actsExpense') {
      setActReturnTab(context.tab);
    } else {
      setActReturnTab('acts');
    }
  }, []);

  const openActDetail = useCallback((actId: number, context: ActReturnContext) => {
    if (!permissions.acts.canView || !(actId > 0)) {
      return;
    }

    setSelectedActId(actId);
    setActCreateInitialValues(null);
    setActCreateInvoiceSeed(null);
    applyActReturnContext(context);
    setActiveTab('actDetail');
  }, [applyActReturnContext, permissions.acts.canView]);

  const openActCreate = useCallback((options?: {
    initialValues?: Partial<ActInput> & { amount?: number | null };
    invoiceSeed?: Invoice | null;
    returnTo?: ActReturnContext;
  }) => {
    if (!permissions.acts.canCreate) {
      return;
    }

    setSelectedActId(null);
    setActCreateInitialValues(options?.initialValues ?? null);
    setActCreateInvoiceSeed(options?.invoiceSeed ?? null);
    applyActReturnContext(options?.returnTo ?? { tab: 'acts' });
    setActiveTab('actCreate');
  }, [applyActReturnContext, permissions.acts.canCreate]);

  const handleActBack = useCallback(() => {
    setActCreateInitialValues(null);
    setActCreateInvoiceSeed(null);
    setSelectedActId(null);

    if (actReturnContext.tab === 'clientDetail') {
      setSelectedClientId(actReturnContext.clientId);
      setInitialClientSection(actReturnContext.section);
      setActiveTab('clientDetail');
      return;
    }

    if (actReturnContext.tab === 'contractDetail') {
      setSelectedContractId(actReturnContext.contractId);
      setActiveTab('contractDetail');
      return;
    }

    if (
      actReturnContext.tab === 'accounts'
      || actReturnContext.tab === 'accountsIncome'
      || actReturnContext.tab === 'accountsExpense'
    ) {
      setSelectedInvoiceId(actReturnContext.invoiceId ?? null);
      setActiveTab(actReturnContext.tab);
      return;
    }

    setActiveTab(actReturnContext.tab);
  }, [actReturnContext]);

  const handleActCreated = useCallback((act: Act) => {
    openActDetail(act.id, actReturnContext);
  }, [actReturnContext, openActDetail]);

  const handleOpenProfile = useCallback(() => {
    setActiveTab('profile');
  }, []);

  const handleCreateUser = useCallback(() => {
    setSelectedUserId(null);
    setActiveTab('userDetail');
  }, []);

  const handleEditUser = useCallback((userId: number) => {
    setSelectedUserId(userId);
    setActiveTab('userDetail');
  }, []);

  const handleUserSaved = useCallback((nextUser: AppUser) => {
    setSelectedUserId(nextUser.id);
    setActiveTab('userDetail');
  }, []);

  const handleBackToUsers = useCallback(() => {
    setSelectedUserId(null);
    setActiveTab('users');
  }, []);

  const handleTabChange = useCallback(
    (tab: Tab) => {
      if (!canViewTab(tab)) {
        return;
      }

      if (tab !== 'userDetail') {
        setSelectedUserId(null);
      }

      if (tab !== 'accounts' && tab !== 'accountsIncome' && tab !== 'accountsExpense') {
        setSelectedInvoiceId(null);
      }

      if (tab !== 'payments' && tab !== 'paymentsIncome' && tab !== 'paymentsExpense') {
        setSelectedPaymentId(null);
      } else if (tab !== activeTab) {
        setSelectedPaymentId(null);
      }

      if (tab !== 'contractDetail') {
        setContractReturnTab(null);
      }

      if (tab !== 'actDetail') {
        setSelectedActId(null);
      }

      if (tab !== 'actCreate') {
        setActCreateInitialValues(null);
        setActCreateInvoiceSeed(null);
      }

      setActiveTab(tab);
    },
    [activeTab, canViewTab],
  );

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    const url = new URL(window.location.href);
    if (activeTab === 'clients') {
      url.pathname = '/clients';
    } else if (activeTab === 'clientDetail') {
      url.pathname = selectedClientId ? `/clients/${selectedClientId}` : '/clients';
    } else if (activeTab === 'accounts' || activeTab === 'accountsIncome' || activeTab === 'accountsExpense') {
      url.pathname = selectedInvoiceId ? `/invoices/${selectedInvoiceId}` : '/invoices';
    } else if (activeTab === 'payments' || activeTab === 'paymentsIncome' || activeTab === 'paymentsExpense') {
      url.pathname = selectedPaymentId ? `/payments/${selectedPaymentId}` : '/payments';
    } else if (activeTab === 'acts' || activeTab === 'actsIncome' || activeTab === 'actsExpense') {
      url.pathname = '/acts';
    } else if (activeTab === 'actCreate') {
      url.pathname = '/acts/new';
    } else if (activeTab === 'actDetail') {
      url.pathname = selectedActId ? `/acts/${selectedActId}` : '/acts';
    } else if (activeTab === 'contracts') {
      url.pathname = '/contracts';
    } else if (activeTab === 'contractDetail') {
      url.pathname = selectedContractId ? `/contracts/${selectedContractId}` : '/contracts';
    } else if (/^\/clients(?:\/\d+)?\/?$/i.test(url.pathname)) {
      url.pathname = '/';
    } else if (/^\/invoices(?:\/\d+)?\/?$/i.test(url.pathname)) {
      url.pathname = '/';
    } else if (/^\/payments(?:\/\d+)?\/?$/i.test(url.pathname)) {
      url.pathname = '/';
    } else if (/^\/acts(?:\/(?:new|\d+))?\/?$/i.test(url.pathname)) {
      url.pathname = '/';
    } else if (/^\/contracts(?:\/\d+)?\/?$/i.test(url.pathname)) {
      url.pathname = '/';
    }

    if (
      activeTab === 'profile'
      || activeTab === 'userDetail'
      || activeTab === 'paymentsIncome'
      || activeTab === 'paymentsExpense'
      || activeTab === 'actsIncome'
      || activeTab === 'actsExpense'
    ) {
      url.searchParams.set('ppTab', activeTab);
    } else {
      url.searchParams.delete('ppTab');
    }

    if (activeTab === 'contractDetail' && contractReturnTab) {
      url.searchParams.set('ppReturnTab', contractReturnTab);
    } else if ((activeTab === 'actDetail' || activeTab === 'actCreate') && actReturnTab !== 'acts') {
      url.searchParams.set('ppReturnTab', actReturnTab);
    } else {
      url.searchParams.delete('ppReturnTab');
    }

    if (activeTab === 'userDetail' && selectedUserId) {
      url.searchParams.set('userId', String(selectedUserId));
    } else {
      url.searchParams.delete('userId');
    }

    window.history.replaceState({}, document.title, `${url.pathname}${url.search}${url.hash}`);
  }, [
    activeTab,
    actReturnTab,
    contractReturnTab,
    selectedActId,
    selectedClientId,
    selectedContractId,
    selectedInvoiceId,
    selectedPaymentId,
    selectedUserId,
  ]);

  useEffect(() => {
    try {
      localStorage.setItem(LAST_TAB_STORAGE_KEY, activeTab);
    } catch {
      // ignore storage issues
    }
  }, [activeTab]);

  useEffect(() => {
    if (activeTab === 'clientDetail' && !selectedClientId) {
      setActiveTab('clients');
      return;
    }

    if (canViewTab(activeTab)) {
      return;
    }

    if (activeTab === 'clientDetail') {
      setSelectedClientId(null);
    }

    if (activeTab === 'userDetail') {
      setSelectedUserId(null);
    }

    const fallback = accessibleMenuTabs[0] ?? 'profile';
    setActiveTab(fallback);
  }, [activeTab, accessibleMenuTabs, canViewTab, selectedClientId]);

  useEffect(() => {
    if (!user) {
      return;
    }

    void logActivity({
      category: 'navigation',
      action: 'open_tab',
      section: activeTab,
      description: `Открыт раздел: ${tabNames[activeTab] ?? activeTab}`,
      status: 'Info',
    });
  }, [activeTab, logActivity, tabNames, user]);

  const hardSignOut = () => {
    try {
      localStorage.removeItem('auth_token');
      localStorage.removeItem('pp.jwt');
      sessionStorage.removeItem('auth_token');
    } catch {
      // ignore
    }

    location.reload();
  };

  const renderNoAccess = useCallback(
    (message: string) => (
      <div className="p-8">
        <div className="mx-auto max-w-4xl rounded-3xl border border-red-200 bg-red-50 p-6 text-center">
          <h2 className="mb-2 text-xl font-bold text-red-900">Доступ ограничен</h2>
          <p className="text-red-700">{message}</p>
        </div>
      </div>
    ),
    [],
  );

  const renderPageLoader = () => (
    <div className="p-8">
      <div className="mx-auto max-w-4xl rounded-3xl border border-slate-200 bg-white p-8 text-center shadow-sm">
        <div className="mx-auto inline-block h-10 w-10 animate-spin rounded-full border-4 border-slate-200 border-t-slate-900" />
        <p className="mt-4 text-sm text-slate-600">Загружаем раздел...</p>
      </div>
    </div>
  );

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-gray-50">
        <div className="text-center">
          <div className="inline-block h-12 w-12 animate-spin rounded-full border-4 border-slate-200 border-t-slate-900" />
          <p className="mt-4 text-slate-600">Загрузка...</p>
        </div>
      </div>
    );
  }

  if (!user) {
    if (authView === 'register') {
      return <Register onSuccess={() => setAuthView('awaiting')} onBackToLogin={() => setAuthView('login')} />;
    }

    if (authView === 'awaiting') {
      return <AwaitingApproval onBackToLogin={() => setAuthView('login')} />;
    }

    return <Login onShowRegister={() => setAuthView('register')} onPendingApproval={() => setAuthView('awaiting')} />;
  }

  if (!user.isApproved) {
    return <AwaitingApproval onBackToLogin={hardSignOut} />;
  }

  if (!user.isActive) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-gray-50 p-4">
        <div className="w-full max-w-md rounded-2xl bg-white p-8 text-center shadow-xl">
          <h1 className="mb-2 text-2xl font-bold text-slate-900">Доступ ограничен</h1>
          <p className="mb-6 text-slate-600">
            Ваш аккаунт отключен. Обратитесь к администратору системы.
          </p>
          <button
            onClick={hardSignOut}
            className="w-full rounded-lg bg-slate-900 px-4 py-3 font-medium text-white transition-all hover:bg-slate-800"
          >
            Выйти
          </button>
        </div>
      </div>
    );
  }

  const renderContent = () => {
    switch (activeTab) {
      case 'calendar':
        return permissions.calendar.canView
          ? <Calendar onOpenClient={handleOpenClient} />
          : renderNoAccess('Раздел календаря недоступен для вашей роли.');

      case 'reports':
        return permissions.reports.canView
          ? <Reports />
          : renderNoAccess('Просмотр отчетов недоступен для вашей роли.');

      case 'calculator':
        return permissions.calculator.canView
          ? <Calculator />
          : renderNoAccess('Калькулятор недоступен для вашей роли.');

      case 'legalEntities':
        return permissions.legalEntities.canView
          ? <LegalEntities />
          : renderNoAccess('Раздел юридических лиц недоступен для вашей роли.');

      case 'clients':
        return permissions.clients.canView
          ? <Clients />
          : renderNoAccess('Раздел клиентов недоступен для вашей роли.');

      case 'contacts':
        return permissions.clients.canView || permissions.legalEntities.canView
          ? (
              <Contacts
                canViewClients={permissions.clients.canView}
                canViewLegalEntities={permissions.legalEntities.canView}
                initialKind="all"
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'contacts' } })}
              />
            )
          : renderNoAccess('Раздел контактов недоступен для вашей роли.');

      case 'contactsClients':
        return permissions.clients.canView
          ? (
              <Contacts
                canViewClients
                canViewLegalEntities={false}
                initialKind="client"
                lockKind
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'contactsClients' } })}
              />
            )
          : renderNoAccess('Раздел контактов недоступен для вашей роли.');

      case 'contactsLegal':
        return permissions.legalEntities.canView
          ? (
              <Contacts
                canViewClients={permissions.clients.canView}
                canViewLegalEntities
                initialKind="legal"
                lockKind
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'contactsLegal' } })}
              />
            )
          : renderNoAccess('Раздел контактов недоступен для вашей роли.');

      case 'contactsExternal': {
        const contactScope: ContactScope =
          activeTab === 'contactsClients' ? 'clients' : activeTab === 'contactsExternal' ? 'external' : 'all';
        const handleContactScopeChange = (nextScope: ContactScope) => {
          setActiveTab(nextScope === 'clients' ? 'contactsClients' : nextScope === 'external' ? 'contactsExternal' : 'contacts');
        };

        return permissions.clients.canView
          ? <PhoneContacts initialScope={contactScope} onScopeChange={handleContactScopeChange} />
          : renderNoAccess('Раздел контактов недоступен для вашей роли.');
      }

      case 'clientDetail':
        if (!permissions.clients.canView) {
          return renderNoAccess('Просмотр карточки клиента недоступен для вашей роли.');
        }

        if (!selectedClientId) {
          return <Clients />;
        }

        return (
          <ClientDetail
            clientId={selectedClientId}
            initialCaseId={initialCaseId}
            initialSection={initialClientSection}
            onOpenInvoicePage={(invoiceId) => openInvoicePage(invoiceId, { returnClientId: selectedClientId })}
            onOpenActPage={(id, options) =>
              openActDetail(id, {
                tab: 'clientDetail',
                clientId: selectedClientId,
                section: options?.section ?? 'acts',
              })
            }
            onCreateActPage={(initialValues, options) =>
              openActCreate({
                initialValues: {
                  ...initialValues,
                  clientId: initialValues?.clientId ?? selectedClientId,
                  status: initialValues?.status ?? 'Created',
                },
                returnTo: {
                  tab: 'clientDetail',
                  clientId: selectedClientId,
                  section: options?.section ?? 'acts',
                },
              })
            }
            onBack={() => {
              const target = clientReturnTab?.tab ?? 'clients';
              if (target === 'contractDetail' && clientReturnTab?.contractId) {
                setSelectedContractId(clientReturnTab.contractId);
              }
              if (target === 'actDetail' && clientReturnTab?.actId) {
                setSelectedActId(clientReturnTab.actId);
              }
              setActiveTab(target);
              setSelectedClientId(null);
              setInitialClientSection(undefined);
              setClientReturnTab(null);
              void logActivity({
                category: 'clients',
                action: 'back_to_list',
                section: target,
                description: 'Возврат к предыдущему разделу',
                status: 'Info',
              });
            }}
          />
        );

      case 'accounts':
        return permissions.accounts.canView
          ? (
              <Accounts
                routeInvoiceId={selectedInvoiceId}
                onRouteInvoiceChange={handleRouteInvoiceChange}
                onRouteInvoicePageBack={handleBackToClientFromInvoicePage}
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'accounts' } })}
                onOpenContract={(id) => openContractDetail(id, { returnTo: 'accounts' })}
                onOpenPayment={openPaymentDetail}
                onOpenActPage={(id, options) => openActDetail(id, { tab: 'accounts', invoiceId: options?.invoiceId ?? null })}
                onCreateActPage={(initialValues, options) =>
                  openActCreate({
                    initialValues,
                    invoiceSeed: options?.invoiceSeed ?? null,
                    returnTo: { tab: 'accounts', invoiceId: options?.invoiceId ?? null },
                  })
                }
              />
            )
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');

      case 'accountsIncome':
        return permissions.accounts.canView
          ? (
              <Accounts
                routeInvoiceId={selectedInvoiceId}
                onRouteInvoiceChange={handleRouteInvoiceChange}
                onRouteInvoicePageBack={handleBackToClientFromInvoicePage}
                defaultType="Income"
                lockType
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'accountsIncome' } })}
                onOpenContract={(id) => openContractDetail(id, { returnTo: 'accountsIncome' })}
                onOpenPayment={openPaymentDetail}
                onOpenActPage={(id, options) =>
                  openActDetail(id, { tab: 'accountsIncome', invoiceId: options?.invoiceId ?? null })
                }
                onCreateActPage={(initialValues, options) =>
                  openActCreate({
                    initialValues,
                    invoiceSeed: options?.invoiceSeed ?? null,
                    returnTo: { tab: 'accountsIncome', invoiceId: options?.invoiceId ?? null },
                  })
                }
              />
            )
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');

      case 'accountsExpense':
        return permissions.accounts.canView
          ? (
              <Accounts
                routeInvoiceId={selectedInvoiceId}
                onRouteInvoiceChange={handleRouteInvoiceChange}
                onRouteInvoicePageBack={handleBackToClientFromInvoicePage}
                defaultType="Expense"
                lockType
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'accountsExpense' } })}
                onOpenContract={(id) => openContractDetail(id, { returnTo: 'accountsExpense' })}
                onOpenPayment={openPaymentDetail}
                onOpenActPage={(id, options) =>
                  openActDetail(id, { tab: 'accountsExpense', invoiceId: options?.invoiceId ?? null })
                }
                onCreateActPage={(initialValues, options) =>
                  openActCreate({
                    initialValues,
                    invoiceSeed: options?.invoiceSeed ?? null,
                    returnTo: { tab: 'accountsExpense', invoiceId: options?.invoiceId ?? null },
                  })
                }
              />
            )
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');

      case 'installments':
        return permissions.accounts.canView
          ? <Installments />
          : renderNoAccess('Раздел рассрочки недоступен для вашей роли.');

      case 'acts':
        return permissions.acts.canView
          ? (
              <Acts
                onOpenAct={(id) => openActDetail(id, { tab: 'acts' })}
                onCreateActPage={(initialValues) => openActCreate({ initialValues, returnTo: { tab: 'acts' } })}
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'acts' } })}
                onOpenInvoiceEditor={(id) => openInvoicePage(id)}
              />
            )
          : renderNoAccess('Раздел актов недоступен для вашей роли.');

      case 'actsIncome':
        return permissions.acts.canView
          ? (
              <Acts
                defaultType="Income"
                lockType
                onOpenAct={(id) => openActDetail(id, { tab: 'actsIncome' })}
                onCreateActPage={(initialValues) => openActCreate({ initialValues, returnTo: { tab: 'actsIncome' } })}
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'actsIncome' } })}
                onOpenInvoiceEditor={(id) => openInvoicePage(id, { type: 'Income' })}
              />
            )
          : renderNoAccess('Раздел актов недоступен для вашей роли.');

      case 'actsExpense':
        return permissions.acts.canView
          ? (
              <Acts
                defaultType="Expense"
                lockType
                onOpenAct={(id) => openActDetail(id, { tab: 'actsExpense' })}
                onCreateActPage={(initialValues) => openActCreate({ initialValues, returnTo: { tab: 'actsExpense' } })}
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'actsExpense' } })}
                onOpenInvoiceEditor={(id) => openInvoicePage(id, { type: 'Expense' })}
              />
            )
          : renderNoAccess('Раздел актов недоступен для вашей роли.');

      case 'actCreate':
        return permissions.acts.canCreate
          ? (
              <ActCreatePage
                onBack={handleActBack}
                onCreated={handleActCreated}
                initialValues={actCreateInitialValues ?? undefined}
                invoiceSeed={actCreateInvoiceSeed ?? undefined}
                defaultResponsibleId={user?.id}
                onOpenInvoice={(id, type) => openInvoicePage(id, type ? { type } : undefined)}
              />
            )
          : renderNoAccess('Создание актов недоступно для вашей роли.');

      case 'actDetail':
        return permissions.acts.canView && selectedActId
          ? (
              <ActDetailPage
                actId={selectedActId}
                onBack={handleActBack}
                onOpenInvoice={(id, type) => openInvoicePage(id, type ? { type } : undefined)}
                onOpenClient={(id) =>
                  handleOpenClient(id, undefined, { returnTo: { tab: 'actDetail', actId: selectedActId } })
                }
              />
            )
          : renderNoAccess('Раздел актов недоступен для вашей роли.');

      case 'paymentsIncome':
        return permissions.payments.canView
          ? (
              <Payments
                onOpenClient={(id) =>
                  handleOpenClient(id, undefined, { returnTo: { tab: 'contractDetail', contractId: selectedContractId } })
                }
                routePaymentId={selectedPaymentId}
                onRoutePaymentChange={setSelectedPaymentId}
                defaultType="Income"
                lockType
                titleKey="paymentsIncomeTitle"
                subtitleKey="paymentsIncomeSubtitle"
                lockedTypeMessageKey="paymentsTypeLockedIncome"
              />
            )
          : renderNoAccess('Журнал платежей недоступен для вашей роли.');

      case 'paymentsExpense':
        return permissions.payments.canView
          ? (
              <Payments
                onOpenClient={(id) =>
                  handleOpenClient(id, undefined, { returnTo: { tab: 'contractDetail', contractId: selectedContractId } })
                }
                routePaymentId={selectedPaymentId}
                onRoutePaymentChange={setSelectedPaymentId}
                defaultType="Expense"
                lockType
                titleKey="paymentsExpenseTitle"
                subtitleKey="paymentsExpenseSubtitle"
                lockedTypeMessageKey="paymentsTypeLockedExpense"
              />
            )
          : renderNoAccess('Журнал платежей недоступен для вашей роли.');

      case 'payments':
        return permissions.payments.canView
          ? (
              <Payments
                onOpenClient={handleOpenClient}
                routePaymentId={selectedPaymentId}
                onRoutePaymentChange={setSelectedPaymentId}
              />
            )
          : renderNoAccess('Журнал платежей недоступен для вашей роли.');

      case 'moneyBank':
        return permissions.payments.canView
          ? <MoneyOperations source="Bank" onOpenInvoice={(id, type) => openInvoicePage(id, type ? { type } : undefined)} />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'moneyCash':
        return permissions.payments.canView
          ? <MoneyOperations source="Cash" onOpenInvoice={(id, type) => openInvoicePage(id, type ? { type } : undefined)} />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'moneyCards':
        return permissions.payments.canView
          ? <MoneyOperations source="Card" onOpenInvoice={(id, type) => openInvoicePage(id, type ? { type } : undefined)} />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'moneyEMoney':
        return permissions.payments.canView
          ? <MoneyOperations source="EMoney" onOpenInvoice={(id, type) => openInvoicePage(id, type ? { type } : undefined)} />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'moneyTotal':
        return permissions.payments.canView
          ? <MoneyOperations source="Total" onOpenInvoice={(id, type) => openInvoicePage(id, type ? { type } : undefined)} />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'contracts':
        return permissions.contracts.canView
          ? (
              <Contracts
                onOpenContract={(id) => {
                  openContractDetail(id);
                }}
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'contracts' } })}
              />
            )
          : renderNoAccess('Раздел договоров недоступен для вашей роли.');

      case 'contractCreate':
        return permissions.contracts.canCreate
          ? <ContractCreatePage onBack={() => setActiveTab('contracts')} onCreated={() => setActiveTab('contracts')} />
          : renderNoAccess('Создание договоров недоступно для вашей роли.');

      case 'contractDetail':
        return permissions.contracts.canView && selectedContractId
          ? (
              <ContractDetailPage
                contractId={selectedContractId}
                onBack={() => {
                  setActiveTab(contractReturnTab ?? 'contracts');
                  setContractReturnTab(null);
                }}
                onOpenClient={(id) =>
                  handleOpenClient(id, undefined, { returnTo: { tab: 'contractDetail', contractId: selectedContractId } })
                }
                onOpenInvoiceEditor={(id) => openInvoicePage(id)}
                onOpenActEditor={(id) => openActDetail(id, { tab: 'contractDetail', contractId: selectedContractId })}
              />
            )
          : renderNoAccess('Раздел договоров недоступен для вашей роли.');

      case 'companyProfile':
        return isAdminRole
          ? <CompanyProfile />
          : renderNoAccess('Раздел доступен только администратору.');

      case 'profile':
        return <ProfilePage />;

      case 'users':
        return isAdminRole
          ? <Users onCreateUser={handleCreateUser} onEditUser={handleEditUser} />
          : renderNoAccess('Недостаточно прав для доступа к настройкам и аудиту.');

      case 'userDetail':
        return isAdminRole
          ? (
              <UserDetailPage
                userId={selectedUserId}
                onBack={handleBackToUsers}
                onSaved={handleUserSaved}
              />
            )
          : renderNoAccess('Нужны права администратора.');

      case 'roles':
        return canManageRoles
          ? <Roles />
          : renderNoAccess('Недостаточно прав для управления ролями и правилами доступа.');

      case 'audit':
        return canViewAudit
          ? <ObjectAuditPage />
          : renderNoAccess('Недостаточно прав для просмотра аудита объектов.');

      case 'userActivity':
        return isAdminRole
          ? <UserActivity />
          : renderNoAccess('Нужны права администратора.');

      case 'dictionaries':
        return permissions.dictionaries.canView
          ? <Dictionaries />
          : renderNoAccess('Справочники недоступны для вашей роли.');

      case 'settings':
        return canViewSettings
          ? <AppSettings />
          : renderNoAccess('Нужны права администратора.');

      default:
        return permissions.calendar.canView
          ? <Calendar onOpenClient={handleOpenClient} />
          : renderNoAccess('Раздел календаря недоступен для вашей роли.');
    }
  };

  return (
    <TabProvider value={{ activeTab, setActiveTab: handleTabChange }}>
      <div className="min-h-screen bg-gray-50">
        <DoubleHorizontalScrollbars />
        <Navigation activeTab={activeTab} onTabChange={handleTabChange} />
        <div className="flex min-h-screen flex-col">
          <TopBar
            title={tabNames[activeTab] ?? ''}
            onSignOut={signOut}
            onOpenProfile={handleOpenProfile}
          />
          <main className="flex-1">
            <RouteErrorBoundary
              resetKey={activeTab}
              onGoHome={() => handleTabChange('calendar')}
            >
              <Suspense fallback={renderPageLoader()}>
                <ActiveTabContent render={renderContent} />
              </Suspense>
            </RouteErrorBoundary>
          </main>
        </div>
      </div>
    </TabProvider>
  );
}

function App() {
  return (
    <AuthProvider>
      <ToastProvider>
        <DialogProvider>
          <AppContent />
        </DialogProvider>
      </ToastProvider>
    </AuthProvider>
  );
}

export default App;
