import { useState } from 'react';
import { AuthProvider, useAuth } from './contexts/AuthContext';
import { Navigation } from './components/Navigation/Navigation';
import { Calendar } from './components/Calendar/Calendar';
import { Reports } from './components/Reports/Reports';
import { Calculator } from './components/Calculator/Calculator';
import { Clients } from './components/Clients/Clients';
import { ClientDetail } from './components/Clients/ClientDetail';
import { Users } from './components/Users/Users';
import { Login } from './components/Auth/Login';
import { Register } from './components/Auth/Register';
import { AwaitingApproval } from './components/Auth/AwaitingApproval';

type Tab = 'calendar' | 'reports' | 'calculator' | 'clients' | 'clientDetail' | 'users';
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

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-center">
          <div className="inline-block animate-spin rounded-full h-12 w-12 border-4 border-slate-200 border-t-slate-900"></div>
          <p className="mt-4 text-slate-600">Loading...</p>
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
        return <Users />;
      default:
        return <Calendar onOpenClient={handleOpenClient} />;
    }
  };

  return (
    <div className="min-h-screen bg-gray-50">
      <Navigation activeTab={activeTab} onTabChange={(tab) => setActiveTab(tab as Tab)} />
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
