import { useState } from 'react';
import { AuthProvider, useAuth } from './contexts/AuthContext';
import Navigation from './components/Navigation/Navigation';
import { Calendar } from './components/Calendar/Calendar';
import { Reports } from './components/Reports/Reports';
import { Calculator } from './components/Calculator/Calculator';
import { Clients } from './components/Clients/Clients';
import { ClientDetail } from './components/Clients/ClientDetail';
import { Users } from './components/Users/Users';
import { Roles } from './components/Roles/Roles';
import { Login } from './components/Auth/Login';
import { Register } from './components/Auth/Register';
import { AwaitingApproval } from './components/Auth/AwaitingApproval';
import type { Tab } from './types/tabs';

type AuthView = 'login' | 'register' | 'awaiting';

function AppContent() {
  const { user, loading } = useAuth();

  const [activeTab, setActiveTab] = useState<Tab>('calendar');
  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');
  const [authView, setAuthView] = useState<AuthView>('login');

  const handleOpenClient = (clientId: number, caseId?: number) => {
    setSelectedClientId(clientId);
    setInitialCaseId(typeof caseId === 'number' ? caseId : 'all');
    setActiveTab('clientDetail');
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

  const isAdmin = (user?.role?.name ?? '').toLowerCase() === 'admin';

  const renderContent = () => {
    switch (activeTab) {
      case 'calendar':
        return <Calendar onOpenClient={handleOpenClient} />;
      case 'reports':
        return <Reports />;
      case 'calculator':
        return <Calculator />;
      case 'clients':
        return <Clients />;
      case 'clientDetail':
        return (
          selectedClientId && (
            <ClientDetail
              clientId={selectedClientId}
              initialCaseId={initialCaseId}
              onBack={() => {
                setActiveTab('clients');
                setSelectedClientId(null);
              }}
            />
          )
        );
      case 'users':
        return isAdmin ? (
          <Users />
        ) : (
          <div className="p-8">
            <div className="max-w-4xl mx-auto">
              <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
                <h2 className="text-xl font-bold text-red-900 mb-2">Доступ запрещён</h2>
                <p className="text-red-700">Нужны права администратора.</p>
              </div>
            </div>
          </div>
        );
      case 'roles':
        return isAdmin ? (
          <Roles />
        ) : (
          <div className="p-8">
            <div className="max-w-4xl mx-auto">
              <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
                <h2 className="text-xl font-bold text-red-900 mb-2">Доступ запрещён</h2>
                <p className="text-red-700">Нужны права администратора.</p>
              </div>
            </div>
          </div>
        );
      default:
        return <Calendar onOpenClient={handleOpenClient} />;
    }
  };

  return (
    <div className="min-h-screen bg-gray-50">
      <Navigation
        activeTab={activeTab}
        onTabChange={(tab: Tab) => {
          if ((tab === 'users' || tab === 'roles') && !isAdmin) return;
          setActiveTab(tab);
        }}
      />
      {renderContent()}
    </div>
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
