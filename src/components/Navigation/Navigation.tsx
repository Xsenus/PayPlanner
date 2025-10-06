import { useState } from 'react';
import { Calendar, BarChart, Calculator, Users, Settings, UserCog, LogOut } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import DictionariesModal from '../Dictionaries/DictionariesModal';

interface NavigationProps {
  activeTab: string;
  onTabChange: (tab: string) => void;
}

export function Navigation({ activeTab, onTabChange }: NavigationProps) {
  const { t } = useTranslation();
  const { signOut, user, isAdmin } = useAuth();
  const [dictOpen, setDictOpen] = useState(false);

  const handleSignOut = async () => {
    if (confirm('Are you sure you want to sign out?')) {
      await signOut();
    }
  };

  const tabs = [
    {
      id: 'calendar',
      label: t('calendar'),
      icon: Calendar,
      onClick: () => onTabChange('calendar'),
    },
    { id: 'reports', label: t('reports'), icon: BarChart, onClick: () => onTabChange('reports') },
    {
      id: 'calculator',
      label: t('calculator'),
      icon: Calculator,
      onClick: () => onTabChange('calculator'),
    },
    { id: 'clients', label: t('clients'), icon: Users, onClick: () => onTabChange('clients') },
    {
      id: 'dictionaries',
      label: t('dictionaries') ?? 'Справочники',
      icon: Settings,
      onClick: () => setDictOpen(true),
    },
    ...(isAdmin()
      ? [
          {
            id: 'users',
            label: 'Users',
            icon: UserCog,
            onClick: () => onTabChange('users'),
          },
        ]
      : []),
  ];

  return (
    <div className="sticky top-0 z-40 bg-white border-b border-gray-200/80">
      <div className="max-w-7xl mx-auto px-3 sm:px-6">
        <div className="flex items-center justify-between">
          <nav
            className="flex gap-6 flex-1"
            aria-label={t('navigation') ?? 'Navigation'}>
            {tabs.map((tab) => {
              const Icon = tab.icon;
              const isActive = activeTab === tab.id;
              const isDictionaries = tab.id === 'dictionaries';
              return (
                <button
                  key={tab.id}
                  onClick={tab.onClick}
                  aria-current={isActive ? 'page' : undefined}
                  title={tab.label}
                  className={[
                    'flex items-center gap-2 py-4 px-1',
                    'border-b-2 text-sm font-medium transition-colors',
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500/50 rounded-t',
                    isDictionaries
                      ? 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                      : isActive
                      ? 'border-blue-500 text-blue-600'
                      : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300',
                  ].join(' ')}>
                  <Icon className="h-5 w-5" />
                  <span>{tab.label}</span>
                </button>
              );
            })}
          </nav>

          <div className="flex items-center gap-4 py-4">
            <div className="text-sm text-gray-600">
              <span className="font-medium">{user?.fullName}</span>
              <span className="ml-2 text-xs px-2 py-1 bg-gray-100 rounded">
                {user?.role?.name}
              </span>
            </div>
            <button
              onClick={handleSignOut}
              className="flex items-center gap-2 px-3 py-2 text-sm text-gray-700 hover:text-gray-900 hover:bg-gray-100 rounded-lg transition-colors"
              title="Sign out"
            >
              <LogOut className="h-4 w-4" />
              <span className="hidden sm:inline">Sign Out</span>
            </button>
          </div>
        </div>
      </div>

      <DictionariesModal open={dictOpen} onClose={() => setDictOpen(false)} />
    </div>
  );
}
