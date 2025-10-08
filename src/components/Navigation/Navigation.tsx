import { useEffect, useLayoutEffect, useMemo, useState } from 'react';
import {
  Calendar,
  BarChart,
  Calculator,
  Users,
  Settings,
  UserCog,
  Shield,
  LogOut,
  ChevronLeft,
  ChevronRight,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import { Tab } from '../../types/tabs';

interface NavigationProps {
  activeTab: Tab;
  onTabChange: (tab: Tab) => void;
}

const LS_KEY = 'pp.sidebar_collapsed';
const WIDTH_OPEN = '17rem';
const WIDTH_COLLAPSED = '5rem';
const STYLE_ID = 'pp-sidebar-style';

export default function Navigation({ activeTab, onTabChange }: NavigationProps) {
  const { t } = useTranslation();
  const { signOut, user, isAdmin } = useAuth();

  const [collapsed, setCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(LS_KEY) === '1';
    } catch {
      return false;
    }
  });

  useLayoutEffect(() => {
    let styleEl = document.getElementById(STYLE_ID) as HTMLStyleElement | null;
    if (!styleEl) {
      styleEl = document.createElement('style');
      styleEl.id = STYLE_ID;
      styleEl.textContent = `
        body.pp-with-sidebar {
          padding-left: var(--pp-sidebar-w, ${WIDTH_OPEN});
          padding-right: max(clamp(12px, 2vw, 24px), env(safe-area-inset-right));
        }
        @media (max-width: 1024px) {
          body.pp-with-sidebar { padding-left: 0 !important; }
        }
      `;
      document.head.appendChild(styleEl);
    }
    document.body.classList.add('pp-with-sidebar');
    return () => {
      document.body.classList.remove('pp-with-sidebar');
    };
  }, []);

  useLayoutEffect(() => {
    document.body.style.setProperty('--pp-sidebar-w', collapsed ? WIDTH_COLLAPSED : WIDTH_OPEN);
  }, [collapsed]);

  useEffect(() => {
    try {
      localStorage.setItem(LS_KEY, collapsed ? '1' : '0');
    } catch {
      /** */
    }
  }, [collapsed]);

  const handleSignOut = async () => {
    if (confirm('Вы действительно хотите выйти из системы?')) {
      await signOut();
    }
  };

  const tabs = useMemo(
    () => [
      {
        id: 'calendar' as const,
        label: t('calendar') ?? 'Календарь',
        icon: Calendar,
        onClick: () => onTabChange('calendar'),
      },
      {
        id: 'reports' as const,
        label: t('reports') ?? 'Отчёты',
        icon: BarChart,
        onClick: () => onTabChange('reports'),
      },
      {
        id: 'calculator' as const,
        label: t('calculator') ?? 'Калькулятор',
        icon: Calculator,
        onClick: () => onTabChange('calculator'),
      },
      {
        id: 'clients' as const,
        label: t('clients') ?? 'Клиенты',
        icon: Users,
        onClick: () => onTabChange('clients'),
      },
      ...(isAdmin()
        ? [
            {
              id: 'dictionaries' as const,
              label: t('dictionaries') ?? 'Справочники',
              icon: Settings,
              onClick: () => onTabChange('dictionaries'),
            },
            {
              id: 'users' as const,
              label: 'Пользователи',
              icon: UserCog,
              onClick: () => onTabChange('users'),
            },
            {
              id: 'roles' as const,
              label: 'Роли',
              icon: Shield,
              onClick: () => onTabChange('roles'),
            },
          ]
        : []),
    ],
    [onTabChange, isAdmin, t],
  );

  const sidebarWidthClass = collapsed ? 'w-20' : 'w-[17rem]';

  return (
    <>
      <aside
        className={[
          'fixed left-0 top-0 z-40 h-screen',
          'flex flex-col transition-all duration-300 ease-in-out',
          'bg-gradient-to-b from-slate-900 via-slate-800 to-slate-900',
          'border-r border-slate-700/50 backdrop-blur-sm',
          sidebarWidthClass,
        ].join(' ')}
        aria-label={t('navigation') ?? 'Навигация'}>
        <div
          className={[
            'h-16 flex items-center border-b border-slate-700/50',
            'bg-slate-900/50 backdrop-blur-sm',
            collapsed ? 'justify-center px-2' : 'px-5',
          ].join(' ')}>
          {!collapsed ? (
            <div className="flex items-center gap-2">
              <div className="w-8 h-8 bg-gradient-to-br from-blue-500 to-blue-600 rounded-lg flex items-center justify-center shadow-lg">
                <span className="text-white font-bold text-sm">PP</span>
              </div>
              <div className="text-lg font-bold bg-gradient-to-r from-white to-blue-100 bg-clip-text text-transparent">
                PayPlanner
              </div>
            </div>
          ) : (
            <div className="w-9 h-9 bg-gradient-to-br from-blue-500 to-blue-600 rounded-lg flex items-center justify-center shadow-lg">
              <span className="text-white font-bold text-base">PP</span>
            </div>
          )}
        </div>

        <button
          type="button"
          onClick={() => setCollapsed((v) => !v)}
          className="absolute -right-3 top-20 rounded-full shadow-lg bg-gradient-to-r from-blue-600 to-blue-700 text-white p-1.5 hover:from-blue-700 hover:to-blue-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500/50 transition-all hover:scale-110"
          title={collapsed ? 'Развернуть меню' : 'Свернуть меню'}
          aria-label={collapsed ? 'Развернуть меню' : 'Свернуть меню'}>
          {collapsed ? <ChevronRight className="h-4 w-4" /> : <ChevronLeft className="h-4 w-4" />}
        </button>

        <nav className="flex-1 overflow-y-auto py-4 px-2">
          <ul className="space-y-1.5">
            {tabs.map((tab) => {
              const Icon = tab.icon;
              const isActive = activeTab === (tab.id as Tab);
              return (
                <li key={tab.id}>
                  <button
                    onClick={tab.onClick}
                    aria-current={isActive ? 'page' : undefined}
                    title={tab.label}
                    className={[
                      'w-full flex items-center rounded-xl transition-all duration-200 select-none group relative',
                      collapsed ? 'justify-center p-3' : 'gap-3 px-4 py-3',
                      isActive
                        ? 'bg-gradient-to-r from-blue-600 to-blue-700 text-white shadow-lg shadow-blue-900/30'
                        : 'text-slate-300 hover:bg-slate-800/50 hover:text-white',
                    ].join(' ')}>
                    <Icon
                      className={[
                        'shrink-0 transition-transform group-hover:scale-110',
                        collapsed ? 'h-6 w-6' : 'h-5 w-5',
                      ].join(' ')}
                    />
                    {!collapsed && <span className="truncate font-medium text-sm">{tab.label}</span>}
                    {isActive && (
                      <div className="absolute right-0 top-1/2 -translate-y-1/2 w-1 h-8 bg-white rounded-l-full" />
                    )}
                  </button>
                </li>
              );
            })}
          </ul>
        </nav>

        <div className="border-t border-slate-700/50 p-3 bg-slate-900/50 backdrop-blur-sm">
          <div
            className={
              collapsed
                ? 'flex items-center justify-center mb-3'
                : 'flex flex-col items-center gap-3 mb-3'
            }>
            <div
              className={[
                'rounded-full bg-gradient-to-br from-blue-500 to-blue-600 flex items-center justify-center font-semibold text-white shadow-lg',
                collapsed ? 'h-9 w-9 text-xs' : 'h-10 w-10 text-sm',
              ].join(' ')}>
              {(user?.fullName ?? 'U')
                .split(' ')
                .map((p) => p.trim()[0])
                .filter(Boolean)
                .slice(0, 2)
                .join('')
                .toUpperCase()}
            </div>
            {!collapsed && (
              <>
                <div className="text-sm font-medium text-center truncate max-w-[12rem] text-white">
                  {user?.fullName}
                </div>
                <div className="text-xs text-slate-400 text-center">
                  <span className="px-2 py-1 rounded-lg bg-slate-800/80 border border-slate-700/50">
                    {user?.role?.name}
                  </span>
                </div>
              </>
            )}
          </div>

          <button
            onClick={handleSignOut}
            className={[
              'w-full flex items-center rounded-lg text-sm transition-all duration-200 group',
              'text-slate-300 hover:bg-red-500/10 hover:text-red-400 border border-slate-700/50 hover:border-red-500/30',
              collapsed ? 'justify-center p-2.5' : 'justify-center px-3 py-2.5',
            ].join(' ')}
            title="Выйти">
            <LogOut className="h-4 w-4 group-hover:scale-110 transition-transform" />
            {!collapsed && <span className="ml-2 font-medium">Выйти</span>}
          </button>
        </div>
      </aside>
    </>
  );
}
