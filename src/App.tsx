import { useCallback, useEffect, useMemo, useState } from 'react';
import { AuthProvider } from './contexts/AuthContext';
import Navigation from './components/Navigation/Navigation';
import { Calendar } from './components/Calendar/Calendar';
import { Reports } from './components/Reports/Reports';
import { Calculator } from './components/Calculator/Calculator';
import { Clients } from './components/Clients/Clients';
import { LegalEntities } from './components/LegalEntities/LegalEntities';
import { ClientDetail } from './components/Clients/ClientDetail';
import { Accounts } from './components/Accounts/Accounts';
import { Installments } from './components/Accounts/Installments';
import { Acts } from './components/Acts/Acts';
import { ActDetailPage } from './components/Acts/ActDetailPage';
import { Contracts } from './components/Contracts/Contracts';
import { ContractCreatePage } from './components/Contracts/ContractCreatePage';
import { ContractDetailPage } from './components/Contracts/ContractDetailPage';
import { Users } from './components/Users/Users';
import { UserDetailPage } from './components/Users/UserDetailPage';
import { Roles } from './components/Roles/Roles';
import { UserActivity } from './components/UserActivity/UserActivity';
import { Dictionaries } from './components/Dictionaries/Dictionaries';
import { Payments } from './components/Payments/Payments';
import { MoneyOperations } from './components/Money/MoneyOperations';
import { CompanyProfile } from './components/CompanyProfile/CompanyProfile';
import { AppSettings } from './components/Settings/AppSettings';
import { Login } from './components/Auth/Login';
import { Register } from './components/Auth/Register';
import { AwaitingApproval } from './components/Auth/AwaitingApproval';
import { ProfilePage } from './components/Profile/ProfilePage';
import TopBar from './components/TopBar/TopBar';
import type { Tab } from './types/tabs';
import { useAuth } from './contexts/AuthContext';
import { useRolePermissions } from './hooks/useRolePermissions';
import { TabProvider } from './contexts/TabContext';
import { useActivityLogger } from './hooks/useActivityLogger';
import { DialogProvider } from './contexts/DialogContext';
import { ToastProvider } from './contexts/ToastContext';
import type { User as AppUser } from './services/authService';

type AuthView = 'login' | 'register' | 'awaiting';

const LAST_TAB_STORAGE_KEY = 'pp.last_active_tab';
const ALL_TABS: Tab[] = [
  'calendar',
  'reports',
  'calculator',
  'legalEntities',
  'clients',
  'clientDetail',
  'accounts',
  'accountsIncome',
  'accountsExpense',
  'installments',
  'acts',
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
  'userActivity',
  'dictionaries',
  'settings',
];

function isValidTab(value: string | null): value is Tab {
  return Boolean(value) && ALL_TABS.includes(value as Tab);
}

function getInitialTab(): Tab {
  if (typeof window === 'undefined') {
    return 'calendar';
  }

  const params = new URLSearchParams(window.location.search);
  const urlTab = params.get('ppTab');
  if (isValidTab(urlTab)) {
    return urlTab;
  }

  try {
    const stored = localStorage.getItem(LAST_TAB_STORAGE_KEY);
    if (isValidTab(stored)) {
      return stored;
    }
  } catch {
    // ignore storage issues
  }

  return 'calendar';
}

function getInitialUserId(): number | null {
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

function AppContent() {
  const { user, loading, signOut } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const logActivity = useActivityLogger();
  const isAdminRole = (user?.role?.name ?? '').toLowerCase() === 'admin';
  const canManageRoles = isAdminRole || permissions.roles.canView;

  const tabNames: Record<Tab, string> = useMemo(
    () => ({
      calendar: 'Календарь',
      reports: 'Отчеты',
      calculator: 'Калькулятор',
      legalEntities: 'Юр. лица',
      clients: 'Клиенты',
      clientDetail: 'Карточка клиента',
      accounts: 'Счета',
      accountsIncome: 'Доходные счета',
      accountsExpense: 'Расходные счета',
      installments: 'Рассрочка',
      acts: 'Акты',
      actDetail: 'Акт',
      paymentsIncome: 'Доходные платежи',
      paymentsExpense: 'Расходные платежи',
      payments: 'Платежи',
      moneyBank: 'Деньги: банк',
      moneyCash: 'Деньги: касса',
      moneyCards: 'Деньги: карты',
      moneyTotal: 'Деньги: итого',
      contracts: 'Договоры',
      contractCreate: 'Новый договор',
      contractDetail: 'Договор',
      companyProfile: 'Профиль компании',
      profile: 'Ваш профиль',
      users: 'Пользователи',
      userDetail: 'Пользователь',
      roles: 'Роли',
      userActivity: 'Контроль пользователей',
      dictionaries: 'Справочники',
      settings: 'Настройки',
    }),
    [],
  );

  const [activeTab, setActiveTab] = useState<Tab>(getInitialTab);
  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [selectedUserId, setSelectedUserId] = useState<number | null>(getInitialUserId);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');
  const [authView, setAuthView] = useState<AuthView>('login');
  const [selectedContractId, setSelectedContractId] = useState<number | null>(null);
  const [selectedActId, setSelectedActId] = useState<number | null>(null);
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
        { tab: 'accounts' as Tab, allowed: permissions.accounts.canView },
        { tab: 'accountsIncome' as Tab, allowed: permissions.accounts.canView },
        { tab: 'accountsExpense' as Tab, allowed: permissions.accounts.canView },
        { tab: 'installments' as Tab, allowed: permissions.accounts.canView },
        { tab: 'acts' as Tab, allowed: permissions.acts.canView },
        { tab: 'payments' as Tab, allowed: permissions.payments.canView },
        { tab: 'paymentsIncome' as Tab, allowed: permissions.payments.canView },
        { tab: 'paymentsExpense' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyBank' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyTotal' as Tab, allowed: permissions.payments.canView },
        { tab: 'contracts' as Tab, allowed: permissions.contracts.canView },
        { tab: 'dictionaries' as Tab, allowed: permissions.dictionaries.canView },
        { tab: 'users' as Tab, allowed: isAdminRole },
        { tab: 'roles' as Tab, allowed: canManageRoles },
        { tab: 'settings' as Tab, allowed: isAdminRole },
      ]
        .filter((entry) => entry.allowed)
        .map((entry) => entry.tab),
    [permissions, isAdminRole, canManageRoles],
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
        case 'clientDetail':
          return permissions.clients.canView;
        case 'accounts':
        case 'accountsIncome':
        case 'accountsExpense':
        case 'installments':
          return permissions.accounts.canView;
        case 'acts':
        case 'actDetail':
          return permissions.acts.canView;
        case 'payments':
        case 'paymentsIncome':
        case 'paymentsExpense':
        case 'moneyBank':
        case 'moneyTotal':
          return permissions.payments.canView;
        case 'moneyCash':
        case 'moneyCards':
          return false;
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
        case 'settings':
          return isAdminRole;
        case 'roles':
          return canManageRoles;
        case 'dictionaries':
          return permissions.dictionaries.canView;
        default:
          return true;
      }
    },
    [permissions, isAdminRole, canManageRoles],
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

      setActiveTab(tab);
    },
    [canViewTab],
  );

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    const url = new URL(window.location.href);
    if (activeTab === 'profile' || activeTab === 'userDetail') {
      url.searchParams.set('ppTab', activeTab);
    } else {
      url.searchParams.delete('ppTab');
    }

    if (activeTab === 'userDetail' && selectedUserId) {
      url.searchParams.set('userId', String(selectedUserId));
    } else {
      url.searchParams.delete('userId');
    }

    window.history.replaceState({}, document.title, `${url.pathname}${url.search}${url.hash}`);
  }, [activeTab, selectedUserId]);

  useEffect(() => {
    try {
      localStorage.setItem(LAST_TAB_STORAGE_KEY, activeTab);
    } catch {
      // ignore storage issues
    }
  }, [activeTab]);

  useEffect(() => {
    if (activeTab === 'moneyCash' || activeTab === 'moneyCards') {
      setActiveTab('moneyBank');
    }
  }, [activeTab]);

  useEffect(() => {
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
  }, [activeTab, accessibleMenuTabs, canViewTab]);

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

      case 'clientDetail':
        if (!permissions.clients.canView || !selectedClientId) {
          return renderNoAccess('Просмотр карточки клиента недоступен для вашей роли.');
        }

        return (
          <ClientDetail
            clientId={selectedClientId}
            initialCaseId={initialCaseId}
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
          ? <Accounts onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'accounts' } })} />
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');

      case 'accountsIncome':
        return permissions.accounts.canView
          ? (
              <Accounts
                defaultType="Income"
                lockType
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'accountsIncome' } })}
              />
            )
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');

      case 'accountsExpense':
        return permissions.accounts.canView
          ? (
              <Accounts
                defaultType="Expense"
                lockType
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'accountsExpense' } })}
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
                onOpenAct={(id) => {
                  setSelectedActId(id);
                  setActiveTab('actDetail');
                }}
                onOpenClient={(id) => handleOpenClient(id, undefined, { returnTo: { tab: 'acts' } })}
                onOpenInvoiceEditor={(id) => {
                  try {
                    localStorage.setItem('pp.openInvoiceId', String(id));
                  } catch {
                    // ignore
                  }
                  setActiveTab('accounts');
                }}
              />
            )
          : renderNoAccess('Раздел актов недоступен для вашей роли.');

      case 'actDetail':
        return permissions.acts.canView && selectedActId
          ? (
              <ActDetailPage
                actId={selectedActId}
                onBack={() => setActiveTab('acts')}
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
          ? <Payments onOpenClient={handleOpenClient} />
          : renderNoAccess('Журнал платежей недоступен для вашей роли.');

      case 'moneyBank':
        return permissions.payments.canView
          ? <MoneyOperations source="Bank" />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'moneyCash':
        return permissions.payments.canView
          ? <MoneyOperations source="Cash" />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'moneyCards':
        return permissions.payments.canView
          ? <MoneyOperations source="Card" />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'moneyTotal':
        return permissions.payments.canView
          ? <MoneyOperations source="Total" />
          : renderNoAccess('Раздел денег недоступен для вашей роли.');

      case 'contracts':
        return permissions.contracts.canView
          ? (
              <Contracts
                onOpenContract={(id) => {
                  setSelectedContractId(id);
                  setActiveTab('contractDetail');
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
                onBack={() => setActiveTab('contracts')}
                onOpenClient={(id) =>
                  handleOpenClient(id, undefined, { returnTo: { tab: 'contractDetail', contractId: selectedContractId } })
                }
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
          : renderNoAccess('Нужны права администратора.');

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

      case 'userActivity':
        return isAdminRole
          ? <UserActivity />
          : renderNoAccess('Нужны права администратора.');

      case 'dictionaries':
        return permissions.dictionaries.canView
          ? <Dictionaries />
          : renderNoAccess('Справочники недоступны для вашей роли.');

      case 'settings':
        return isAdminRole
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
        <Navigation activeTab={activeTab} onTabChange={handleTabChange} />
        <div className="flex min-h-screen flex-col">
          <TopBar
            title={tabNames[activeTab] ?? ''}
            onSignOut={signOut}
            onOpenProfile={handleOpenProfile}
          />
          <main className="flex-1">{renderContent()}</main>
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
