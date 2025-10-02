/**
 * Main Application Component
 *
 * Root component that manages routing, authentication, and tab navigation.
 * Wraps all content in ProtectedRoute to ensure only authenticated users can access the app.
 */

import { useState } from 'react';
import { Navigation } from './components/Navigation/Navigation';
import { Calendar } from './components/Calendar/Calendar';
import { Reports } from './components/Reports/Reports';
import { Calculator } from './components/Calculator/Calculator';
import { Clients } from './components/Clients/Clients';
import { ClientDetail } from './components/Clients/ClientDetail';
import { UserManagement } from './components/UserManagement/UserManagement';
import { ProtectedRoute } from './components/Auth/ProtectedRoute';
import { UserMenu } from './components/Auth/UserMenu';

// Define available tab types
type Tab = 'calendar' | 'reports' | 'calculator' | 'clients' | 'clientDetail' | 'users';

/**
 * App Component
 *
 * Main application component that handles:
 * - Authentication protection via ProtectedRoute
 * - Tab navigation between different views
 * - Client detail navigation
 * - User menu display
 */
function App() {
  // Current active tab state
  const [activeTab, setActiveTab] = useState<Tab>('calendar');

  // Client detail view state
  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');

  /**
   * Handle opening a client detail view
   * Navigates to the client detail tab with the specified client and case
   *
   * @param clientId - ID of the client to view
   * @param caseId - Optional case ID to focus on
   */
  const handleOpenClient = (clientId: number, caseId?: number) => {
    setSelectedClientId(clientId);
    setInitialCaseId(typeof caseId === 'number' ? caseId : 'all');
    setActiveTab('clientDetail');
  };

  /**
   * Render the content for the currently active tab
   *
   * @returns JSX element for the active tab content
   */
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
        return <UserManagement />;
      default:
        return <Calendar onOpenClient={handleOpenClient} />;
    }
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {/* Header with navigation and user menu */}
        <div className="sticky top-0 z-50 bg-white border-b border-gray-200">
          <div className="max-w-7xl mx-auto px-3 sm:px-6 py-2 flex items-center justify-between">
            <div className="flex-1">
              <Navigation activeTab={activeTab} onTabChange={(tab) => setActiveTab(tab as Tab)} />
            </div>
            <UserMenu />
          </div>
        </div>

        {/* Main content area */}
        {renderContent()}
      </div>
    </ProtectedRoute>
  );
}

export default App;
