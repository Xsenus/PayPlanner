import { useState } from 'react';
import { Navigation } from './components/Navigation/Navigation';
import { Calendar } from './components/Calendar/Calendar';
import { Reports } from './components/Reports/Reports';
import { Calculator } from './components/Calculator/Calculator';
import { Clients } from './components/Clients/Clients';
import { ClientDetail } from './components/Clients/ClientDetail'; // 👈 импортируй

type Tab = 'calendar' | 'reports' | 'calculator' | 'clients' | 'clientDetail';

function App() {
  const [activeTab, setActiveTab] = useState<Tab>('calendar');

  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');

  const handleOpenClient = (clientId: number, caseId?: number) => {
    setSelectedClientId(clientId);
    setInitialCaseId(typeof caseId === 'number' ? caseId : 'all');
    setActiveTab('clientDetail');
  };

  const renderContent = () => {
    switch (activeTab) {
      case 'calendar':
        return <Calendar onOpenClient={handleOpenClient} />; // 👈 прокинули
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

export default App;
