import { useState, useMemo, useCallback, useEffect } from 'react';
import { AuthProvider, useAuth } from './contexts/AuthContext';
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
import { Contracts } from './components/Contracts/Contracts';
import { ContractCreatePage } from './components/Contracts/ContractCreatePage';
import { Users } from './components/Users/Users';
import { Roles } from './components/Roles/Roles';
import { UserActivity } from './components/UserActivity/UserActivity';
import { Dictionaries } from './components/Dictionaries/Dictionaries';
import { Payments } from './components/Payments/Payments';
import { MoneyOperations } from './components/Money/MoneyOperations';
import { CompanyProfile } from './components/CompanyProfile/CompanyProfile';
import { Login } from './components/Auth/Login';
import { Register } from './components/Auth/Register';
import { AwaitingApproval } from './components/Auth/AwaitingApproval';
import TopBar from './components/TopBar/TopBar';
import type { Tab } from './types/tabs';
import { useRolePermissions } from './hooks/useRolePermissions';
import { TabProvider } from './contexts/TabContext';
import { useActivityLogger } from './hooks/useActivityLogger';

type AuthView = 'login' | 'register' | 'awaiting';
const LAST_TAB_STORAGE_KEY = 'pp.last_active_tab';

function AppContent() {
  const { user, loading, signOut } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const logActivity = useActivityLogger();
  const tabNames: Record<Tab, string> = useMemo(() => ({
    calendar: 'Календарь',
    reports: 'Отчёты',
    calculator: 'Калькулятор',
    legalEntities: 'Юр. лица',
    clients: 'Клиенты',
    clientDetail: 'Карточка клиента',
    accounts: 'Счета',
    accountsIncome: 'Доходные счета',
    accountsExpense: 'Расходные счета',
    installments: 'Рассрочка',
    acts: 'Акты',
    paymentsIncome: 'Доходные платежи',
    paymentsExpense: 'Расходные платежи',
    payments: 'Платежи',
    moneyBank: 'Деньги: Банк',
    moneyCash: 'Деньги: Касса',
    moneyCards: 'Деньги: Карты',
    moneyTotal: 'Деньги: Итого',
    contracts: 'Договоры',
    contractCreate: 'Новый договор',
    companyProfile: 'Профиль компании',
    users: 'Пользователи',
    roles: 'Роли',
    dictionaries: 'Справочники',
    userActivity: 'Пользовательский контроль',
  }), []);

  const [activeTab, setActiveTab] = useState<Tab>(() => {
    if (typeof window === 'undefined') {
      return 'calendar';
    }
    try {
      const stored = localStorage.getItem(LAST_TAB_STORAGE_KEY) as Tab | null;
      return stored ?? 'calendar';
    } catch {
      return 'calendar';
    }
  });
  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');
  const [authView, setAuthView] = useState<AuthView>('login');

  const handleOpenClient = (clientId: number, caseId?: number) => {
    if (!permissions.clients.canView) {
      return;
    }
    setSelectedClientId(clientId);
    setInitialCaseId(typeof caseId === 'number' ? caseId : 'all');
    setActiveTab('clientDetail');
    logActivity({
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
  };

  const hardSignOut = () => {
    try {
      localStorage.removeItem('auth_token');
      localStorage.removeItem('pp.jwt');
      sessionStorage.removeItem('auth_token');
    } catch {
      /** */
    }
    location.reload();
  };

  const isAdminRole = (user?.role?.name ?? '').toLowerCase() === 'admin';

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
        { tab: 'paymentsIncome' as Tab, allowed: permissions.payments.canView },
        { tab: 'paymentsExpense' as Tab, allowed: permissions.payments.canView },
        { tab: 'payments' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyBank' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyCash' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyCards' as Tab, allowed: permissions.payments.canView },
        { tab: 'moneyTotal' as Tab, allowed: permissions.payments.canView },
        { tab: 'contracts' as Tab, allowed: permissions.contracts.canView },
        { tab: 'contractCreate' as Tab, allowed: permissions.contracts.canCreate },
        { tab: 'dictionaries' as Tab, allowed: permissions.dictionaries.canView },
      ]
        .filter((entry) => entry.allowed)
        .map((entry) => entry.tab),
    [permissions],
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
          return permissions.acts.canView;
        case 'paymentsIncome':
        case 'paymentsExpense':
        case 'payments':
          return permissions.payments.canView;
        case 'moneyBank':
        case 'moneyCash':
        case 'moneyCards':
        case 'moneyTotal':
          return permissions.payments.canView;
        case 'contracts':
          return permissions.contracts.canView;
        case 'contractCreate':
          return permissions.contracts.canCreate;
        case 'companyProfile':
          return true;
        case 'dictionaries':
          return permissions.dictionaries.canView;
        case 'users':
        case 'roles':
        case 'userActivity':
          return isAdminRole;
        default:
          return true;
      }
    },
    [permissions, isAdminRole],
  );

  useEffect(() => {
    if (!user) {
      return;
    }

    logActivity({
      category: 'navigation',
      action: 'open_tab',
      section: activeTab,
      description: `Открыт раздел: ${tabNames[activeTab] ?? activeTab}`,
      status: 'Info',
    });
  }, [activeTab, logActivity, tabNames, user]);

  useEffect(() => {
    if (canViewTab(activeTab)) {
      return;
    }
    if (activeTab === 'clientDetail') {
      setSelectedClientId(null);
    }
    const fallback = accessibleMenuTabs[0];
    if (fallback) {
      setActiveTab(fallback);
    }
  }, [activeTab, accessibleMenuTabs, canViewTab]);

  const renderNoAccess = useCallback(
    (message: string) => (
      <div className="p-8">
        <div className="max-w-4xl mx-auto">
          <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
            <h2 className="text-xl font-bold text-red-900 mb-2">Доступ ограничен</h2>
            <p className="text-red-700">{message}</p>
          </div>
        </div>
      </div>
    ),
    [],
  );

  const handleTabChange = useCallback(
    (tab: Tab) => {
      if (!canViewTab(tab)) return;
      setActiveTab(tab);
    },
    [canViewTab],
  );

  useEffect(() => {
    try {
      localStorage.setItem(LAST_TAB_STORAGE_KEY, activeTab);
    } catch {
      /** */
    }
  }, [activeTab]);

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-center">
          <div className="inline-block animate-spin rounded-full h-12 w-12 border-4 border-slate-200 border-t-slate-900"></div>
          <p className="mt-4 text-slate-600">Загрузка…</p>
        </div>
      </div>
    );
  }

  if (!user) {
    if (authView === 'register') {
      return (
        <Register
          onSuccess={() => setAuthView('awaiting')}
          onBackToLogin={() => setAuthView('login')}
        />
      );
    }
    if (authView === 'awaiting') {
      return <AwaitingApproval onBackToLogin={() => setAuthView('login')} />;
    }
    return (
      <Login
        onShowRegister={() => setAuthView('register')}
        onPendingApproval={() => setAuthView('awaiting')}
      />
    );
  }

  if (!user.isApproved) {
    return <AwaitingApproval onBackToLogin={hardSignOut} />;
  }

  if (!user.isActive) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center p-4">
        <div className="w-full max-w-md bg-white rounded-2xl shadow-xl p-8 text-center">
          <h1 className="text-2xl font-bold text-slate-900 mb-2">Доступ ограничён</h1>
          <p className="text-slate-600 mb-6">
            Ваш аккаунт отключён. Обратитесь к администратору системы.
          </p>
          <button
            onClick={hardSignOut}
            className="w-full py-3 px-4 bg-slate-900 text-white font-medium rounded-lg hover:bg-slate-800 transition-all">
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
          : renderNoAccess('Просмотр отчётов недоступен для вашей роли.');
      case 'calculator':
        return permissions.calculator.canView
          ? <Calculator />
          : renderNoAccess('Калькулятор недоступен для вашей роли.');
      case 'legalEntities':
        return permissions.legalEntities.canView
          ? <LegalEntities />
          : renderNoAccess('Раздел юридических лиц недоступен для вашей роли.');
      case 'clientDetail':
        if (!permissions.clients.canView || !selectedClientId) {
          return renderNoAccess('Просмотр клиентов недоступен для вашей роли.');
        }
        return (
          <ClientDetail
            clientId={selectedClientId}
            initialCaseId={initialCaseId}
            onBack={() => {
              setActiveTab('clients');
              setSelectedClientId(null);
              logActivity({
                category: 'clients',
                action: 'back_to_list',
                section: 'clients',
                description: 'Возврат к списку клиентов',
                status: 'Info',
              });
            }}
          />
        );
      case 'users':
        return isAdminRole ? <Users /> : renderNoAccess('Нужны права администратора.');
      case 'clients':
        return permissions.clients.canView
          ? <Clients />
          : renderNoAccess('Раздел клиентов недоступен для вашей роли.');
      case 'accounts':
        return permissions.accounts.canView
          ? <Accounts onOpenClient={handleOpenClient} />
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');
      case 'accountsIncome':
        return permissions.accounts.canView
          ? <Accounts defaultType="Income" lockType onOpenClient={handleOpenClient} />
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');
      case 'accountsExpense':
        return permissions.accounts.canView
          ? <Accounts defaultType="Expense" lockType onOpenClient={handleOpenClient} />
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');
      case 'installments':
        return permissions.accounts.canView
          ? <Installments />
          : renderNoAccess('Раздел счетов недоступен для вашей роли.');
      case 'acts':
        return permissions.acts.canView
          ? <Acts />
          : renderNoAccess('Раздел актов недоступен для вашей роли.');
      case 'paymentsIncome':
        return permissions.payments.canView
          ? (
              <Payments
                onOpenClient={handleOpenClient}
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
                onOpenClient={handleOpenClient}
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
          ? <Contracts />
          : renderNoAccess('Раздел договоров недоступен для вашей роли.');
      case 'contractCreate':
        return permissions.contracts.canCreate
          ? (
              <ContractCreatePage
                onBack={() => setActiveTab('contracts')}
                onCreated={() => setActiveTab('contracts')}
              />
            )
          : renderNoAccess('Создание договоров недоступно для вашей роли.');
      case 'companyProfile':
        return <CompanyProfile />;
      case 'roles':
        return isAdminRole ? <Roles /> : renderNoAccess('Нужны права администратора.');
      case 'userActivity':
        return isAdminRole ? <UserActivity /> : renderNoAccess('Нужны права администратора.');
      case 'dictionaries':
        return permissions.dictionaries.canView
          ? <Dictionaries />
          : renderNoAccess('Справочники недоступны для вашей роли.');
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
          <TopBar title={tabNames[activeTab] ?? ''} onSignOut={signOut} />
          <main className="flex-1">{renderContent()}</main>
        </div>
      </div>
    </TabProvider>
  );
}

function App() {
  return (
    <AuthProvider>
      <AppContent />
    </AuthProvider>
  );
}

export default App;
