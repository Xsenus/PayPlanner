import { useState } from 'react';
import { Calendar, BarChart, Calculator, Users, Settings } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import DictionariesModal from '../Dictionaries/DictionariesModal';

interface NavigationProps {
  activeTab: string;
  onTabChange: (tab: string) => void;
}

export function Navigation({ activeTab, onTabChange }: NavigationProps) {
  const { t } = useTranslation();
  const [dictOpen, setDictOpen] = useState(false);

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
  ];

  return (
    <div className="sticky top-0 z-40 bg-white border-b border-gray-200/80">
      <div className="max-w-7xl mx-auto px-3 sm:px-6">
        <nav
          className="grid grid-cols-5 sm:flex sm:gap-6"
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
                  'w-full flex flex-col items-center justify-center gap-1 py-2 px-2', // mobile
                  'sm:w-auto sm:flex-row sm:justify-start sm:gap-2 sm:py-4 sm:px-1', // desktop
                  'border-b-2 text-xs sm:text-sm font-medium transition-colors',
                  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500/50 rounded-t',
                  isDictionaries
                    ? 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300' // кнопка-настройка всегда нейтральная
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
      </div>

      <DictionariesModal open={dictOpen} onClose={() => setDictOpen(false)} />
    </div>
  );
}
