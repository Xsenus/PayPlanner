import { useEffect, useLayoutEffect, useMemo, useState } from 'react';
import {
  Calendar,
  BarChart,
  Calculator,
  Users,
  Settings,
  UserCog,
  LogOut,
  ChevronLeft,
  ChevronRight,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import DictionariesModal from '../Dictionaries/DictionariesModal';
import { Tab } from '../../types/tabs';

interface NavigationProps {
  activeTab: Tab;
  onTabChange: (tab: Tab) => void;
}

const LS_KEY = 'pp.sidebar_collapsed';
const WIDTH_OPEN = '16rem'; // 256px
const WIDTH_COLLAPSED = '4rem'; // 64px
const STYLE_ID = 'pp-sidebar-style';

export default function Navigation({ activeTab, onTabChange }: NavigationProps) {
  const { t } = useTranslation();
  const { signOut, user, isAdmin } = useAuth();
  const [dictOpen, setDictOpen] = useState(false);

  // читаем состояние сайдбара до первого кадра (без мерцания)
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem(LS_KEY) === '1';
    } catch {
      return false;
    }
  });

  // единоразово добавляем стили и класс на body
  useLayoutEffect(() => {
    let styleEl = document.getElementById(STYLE_ID) as HTMLStyleElement | null;
    if (!styleEl) {
      styleEl = document.createElement('style');
      styleEl.id = STYLE_ID;
      styleEl.textContent = `
        /* Сдвигаем контент и даём симметричный правый отступ */
        body.pp-with-sidebar {
          padding-left: var(--pp-sidebar-w, ${WIDTH_OPEN});
          padding-right: max(clamp(12px, 2vw, 24px), env(safe-area-inset-right));
        }
        @media (max-width: 1024px) {
          /* На узких экранах меню как оверлей — слева отступ убираем */
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

  // применяем ширину сайдбара (через CSS var) до первого кадра и при изменении
  useLayoutEffect(() => {
    document.body.style.setProperty('--pp-sidebar-w', collapsed ? WIDTH_COLLAPSED : WIDTH_OPEN);
  }, [collapsed]);

  // сохраняем состояние
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
      // «Справочники» открывает модалку, onTabChange не вызываем
      {
        id: 'dictionaries' as const,
        label: t('dictionaries') ?? 'Справочники',
        icon: Settings,
        onClick: () => setDictOpen(true),
      },
      ...(isAdmin()
        ? [
            {
              id: 'users' as const,
              label: 'Пользователи',
              icon: UserCog,
              onClick: () => onTabChange('users'),
            },
          ]
        : []),
    ],
    [onTabChange, isAdmin, t],
  );

  const sidebarWidthClass = collapsed ? 'w-16' : 'w-64';

  return (
    <>
      <aside
        className={[
          'fixed left-0 top-0 z-40 h-screen bg-white border-r border-gray-200/80',
          'flex flex-col transition-[width] duration-200 ease-in-out',
          sidebarWidthClass,
        ].join(' ')}
        aria-label={t('navigation') ?? 'Навигация'}>
        {/* Шапка */}
        <div
          className={[
            'h-14 flex items-center px-3 border-b border-gray-200/70',
            collapsed ? 'justify-center' : '',
          ].join(' ')}>
          {!collapsed ? (
            <div className="text-base font-semibold tracking-tight">PayPlanner</div>
          ) : (
            <div className="text-base font-semibold tracking-tight">PP</div>
          )}
        </div>

        {/* Тумблер */}
        <button
          type="button"
          onClick={() => setCollapsed((v) => !v)}
          className="absolute -right-3 top-1/2 -translate-y-1/2 rounded-full shadow-md bg-blue-600 text-white p-1.5 hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500/50"
          title={collapsed ? 'Развернуть меню' : 'Свернуть меню'}
          aria-label={collapsed ? 'Развернуть меню' : 'Свернуть меню'}>
          {collapsed ? <ChevronRight className="h-4 w-4" /> : <ChevronLeft className="h-4 w-4" />}
        </button>

        {/* Навигация */}
        <nav className="flex-1 overflow-y-auto py-3">
          <ul className="space-y-1">
            {tabs.map((tab) => {
              const Icon = tab.icon;
              const isActive = activeTab === (tab.id as Tab); // clientDetail не подсвечиваем
              const layout = collapsed ? 'justify-center p-2' : 'justify-start gap-3 px-3 py-2';
              return (
                <li key={tab.id}>
                  <button
                    onClick={tab.onClick}
                    aria-current={isActive ? 'page' : undefined}
                    title={tab.label}
                    className={[
                      'w-full flex items-center rounded-xl transition-colors select-none text-sm',
                      layout,
                      isActive
                        ? 'bg-blue-50 text-blue-700'
                        : 'text-gray-700 hover:bg-gray-100 hover:text-gray-900',
                    ].join(' ')}>
                    <Icon className="h-5 w-5 shrink-0" />
                    {!collapsed && <span className="truncate">{tab.label}</span>}
                  </button>
                </li>
              );
            })}
          </ul>
        </nav>

        {/* Низ: профиль + выход (центрировано) */}
        <div className="border-t border-gray-200/70 p-3">
          <div
            className={
              collapsed ? 'flex items-center justify-center' : 'flex flex-col items-center gap-2'
            }>
            <div className="h-8 w-8 rounded-full bg-gray-200 flex items-center justify-center text-xs font-semibold">
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
                <div className="text-sm font-medium text-center truncate max-w-[12rem]">
                  {user?.fullName}
                </div>
                <div className="text-xs text-gray-500 text-center">
                  <span className="px-1.5 py-0.5 rounded bg-gray-100">{user?.role?.name}</span>
                </div>
              </>
            )}
          </div>

          <button
            onClick={handleSignOut}
            className={[
              'mt-3 w-full flex items-center rounded-lg text-sm text-gray-700 hover:bg-gray-100 hover:text-gray-900 transition-colors',
              collapsed ? 'justify-center p-2' : 'justify-center px-3 py-2',
            ].join(' ')}
            title="Выйти">
            <LogOut className="h-4 w-4" />
            {!collapsed && <span className="ml-2">Выйти</span>}
          </button>
        </div>
      </aside>

      <DictionariesModal open={dictOpen} onClose={() => setDictOpen(false)} />
    </>
  );
}
