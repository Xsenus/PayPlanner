import { useCallback, useEffect, useLayoutEffect, useMemo, useState } from 'react';
import {
  Calendar,
  BarChart,
  Calculator,
  Users,
  Settings,
  UserCog,
  Shield,
  LogOut,
  Menu,
  X,
  WalletCards,
  Building2,
  FileCheck2,
  FileSignature,
  Eye,
  Banknote,
  ChevronDown,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { Tab } from '../../types/tabs';

interface NavigationProps {
  activeTab: Tab;
  onTabChange: (tab: Tab) => void;
}

const LS_KEY = 'pp.sidebar_collapsed';
const WIDTH_OPEN = '17rem';
const WIDTH_COLLAPSED = '5rem';
const STYLE_ID = 'pp-sidebar-style';
const EXPANDED_KEY = 'pp.sidebar_expanded';

export default function Navigation({ activeTab, onTabChange }: NavigationProps) {
  const { t } = useTranslation();
  const { signOut, user, isAdmin } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const [mobileOpen, setMobileOpen] = useState(false);

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

  type SidebarChild = { id: Tab; label: string; icon: typeof Calendar };
  type SidebarItem = {
    id: string;
    label: string;
    icon: typeof Calendar;
    tab?: Tab;
    children?: SidebarChild[];
  };

  const mainItems = useMemo<SidebarItem[]>(() => {
    const items: SidebarItem[] = [];
    if (permissions.calendar.canView) {
      items.push({ id: 'calendar', tab: 'calendar' as Tab, label: t('calendar') ?? 'Календарь', icon: Calendar });
    }
    if (permissions.reports.canView) {
      items.push({ id: 'reports', tab: 'reports' as Tab, label: t('reports') ?? 'Отчёты', icon: BarChart });
    }
    if (permissions.calculator.canView) {
      items.push({ id: 'calculator', tab: 'calculator' as Tab, label: t('calculator') ?? 'Калькулятор', icon: Calculator });
    }
    if (permissions.legalEntities.canView) {
      items.push({
        id: 'legalEntities',
        tab: 'legalEntities' as Tab,
        label: t('legalEntities') ?? 'Юр. лица',
        icon: Building2,
      });
    }
    if (permissions.clients.canView) {
      items.push({ id: 'clients', tab: 'clients' as Tab, label: t('clients') ?? 'Клиенты', icon: Users });
    }
    if (permissions.accounts.canView) {
      items.push({ id: 'accounts', tab: 'accounts' as Tab, label: t('accounts') ?? 'Счета', icon: WalletCards });
    }

    const paymentsChildren: SidebarChild[] = permissions.payments.canView
      ? [
          {
            id: 'paymentsIncome' as Tab,
            label: t('paymentsIncomeNav') ?? t('incomePlural') ?? 'Доходные',
            icon: Banknote,
          },
          {
            id: 'paymentsExpense' as Tab,
            label: t('paymentsExpenseNav') ?? t('expensePlural') ?? 'Расходные',
            icon: Banknote,
          },
        ]
      : [];

    if (permissions.acts.canView || paymentsChildren.length > 0) {
      items.push({
        id: 'acts',
        tab: permissions.acts.canView ? ('acts' as Tab) : undefined,
        label: t('acts') ?? 'Акты',
        icon: FileCheck2,
        children: paymentsChildren.length ? paymentsChildren : undefined,
      });
    }

    if (permissions.contracts.canView) {
      items.push({ id: 'contracts', tab: 'contracts' as Tab, label: t('contracts') ?? 'Договоры', icon: FileSignature });
    }
    if (permissions.dictionaries.canView) {
      items.push({
        id: 'dictionaries',
        tab: 'dictionaries' as Tab,
        label: t('dictionaries') ?? 'Справочники',
        icon: Settings,
      });
    }

    return items;
  }, [permissions, t]);

  const adminItems = useMemo<SidebarItem[]>(() => {
    if (!isAdmin()) {
      return [];
    }

    return [
      { id: 'users', tab: 'users' as Tab, label: 'Пользователи', icon: UserCog },
      { id: 'roles', tab: 'roles' as Tab, label: 'Роли', icon: Shield },
      { id: 'userActivity', tab: 'userActivity' as Tab, label: 'Контроль пользователей', icon: Eye },
    ];
  }, [isAdmin]);

  const sidebarItems = useMemo(() => [...mainItems, ...adminItems], [mainItems, adminItems]);

  const [expandedSections, setExpandedSections] = useState<Record<string, boolean>>(() => {
    if (typeof window === 'undefined') {
      return {};
    }
    try {
      const raw = window.localStorage.getItem(EXPANDED_KEY);
      if (!raw) return {};
      const parsed = JSON.parse(raw) as Record<string, boolean>;
      return parsed && typeof parsed === 'object' ? parsed : {};
    } catch {
      return {};
    }
  });

  useEffect(() => {
    try {
      localStorage.setItem(EXPANDED_KEY, JSON.stringify(expandedSections));
    } catch {
      /** */
    }
  }, [expandedSections]);

  const toggleSection = useCallback((id: string) => {
    setExpandedSections((prev) => {
      const current = prev[id];
      const nextValue = current === undefined ? false : !current;
      return { ...prev, [id]: nextValue };
    });
  }, []);

  useEffect(() => {
    setExpandedSections((prev) => {
      let changed = false;
      const next = { ...prev };
      sidebarItems.forEach((item) => {
        if (!item.children?.length) {
          return;
        }
        const hasActiveChild = item.children.some((child) => child.id === activeTab);
        const generalPaymentsActive = item.id === 'acts' && activeTab === 'payments';
        if ((hasActiveChild || generalPaymentsActive) && !next[item.id]) {
          next[item.id] = true;
          changed = true;
        }
      });
      return changed ? next : prev;
    });
  }, [activeTab, sidebarItems]);

  const sidebarWidthClass = collapsed ? 'w-20' : 'w-[17rem]';

  return (
    <>
      {/* Mobile menu button */}
      <button
        onClick={() => setMobileOpen(!mobileOpen)}
        className="lg:hidden fixed top-4 left-4 z-50 p-2 rounded-lg bg-slate-900 text-white shadow-lg"
        aria-label="Toggle menu">
        {mobileOpen ? <X className="h-6 w-6" /> : <Menu className="h-6 w-6" />}
      </button>

      {/* Mobile overlay */}
      {mobileOpen && (
        <div
          className="lg:hidden fixed inset-0 bg-black/50 z-30"
          onClick={() => setMobileOpen(false)}
        />
      )}

      <aside
        className={[
          'fixed left-0 top-0 z-40 h-screen',
          'flex flex-col transition-all duration-300 ease-in-out',
          'bg-gradient-to-b from-slate-900 via-slate-800 to-slate-900',
          'border-r border-slate-700/50',
          sidebarWidthClass,
          mobileOpen ? 'translate-x-0' : '-translate-x-full lg:translate-x-0',
        ].join(' ')}
        aria-label={t('navigation') ?? 'Навигация'}>
        <div
          className={[
            'h-16 flex items-center border-b border-slate-700/50',
            'bg-slate-900/50',
            collapsed ? 'justify-center px-2' : 'px-5',
          ].join(' ')}>
          {!collapsed ? (
            <div className="flex items-center gap-3">
              <div className="w-9 h-9 bg-gradient-to-br from-blue-500 to-blue-600 rounded-lg flex items-center justify-center shadow-lg">
                <span className="text-white font-bold">PP</span>
              </div>
              <div className="text-xl font-bold bg-gradient-to-r from-white to-blue-100 bg-clip-text text-transparent">
                PayPlanner
              </div>
            </div>
          ) : (
            <div className="w-9 h-9 bg-gradient-to-br from-blue-500 to-blue-600 rounded-lg flex items-center justify-center shadow-lg">
              <span className="text-white font-bold">PP</span>
            </div>
          )}
        </div>

        {/* Desktop toggle button */}
        <div className="hidden lg:block">
          <button
            type="button"
            onClick={() => setCollapsed((v) => !v)}
            className={[
              'w-full py-2 flex items-center justify-center',
              'text-slate-400 hover:text-white hover:bg-slate-800/50',
              'border-b border-slate-700/30 transition-all',
            ].join(' ')}
            title={collapsed ? 'Развернуть меню' : 'Свернуть меню'}
            aria-label={collapsed ? 'Развернуть меню' : 'Свернуть меню'}>
            <div className="text-xs flex items-center gap-2">{collapsed ? '→' : '← Свернуть'}</div>
          </button>
        </div>

        <nav className="flex-1 overflow-y-auto py-4 px-2 thin-scrollbar">
          <ul className="space-y-1.5">
            {sidebarItems.map((item) => {
              const Icon = item.icon;
              const childItems = item.children ?? [];
              const hasChildren = childItems.length > 0;
              const isParentActive = item.tab ? activeTab === item.tab : false;
              const isChildActive =
                childItems.some((child) => child.id === activeTab) ||
                (item.id === 'acts' && activeTab === 'payments');
              const isActive = isParentActive || isChildActive;
              const isExpanded = expandedSections[item.id] ?? true;

              return (
                <li key={item.id} className="space-y-1">
                  <div className="relative">
                    <button
                      onClick={() => {
                        if (item.tab) {
                          onTabChange(item.tab);
                          setMobileOpen(false);
                        } else if (hasChildren) {
                          toggleSection(item.id);
                        }
                      }}
                      aria-current={isActive ? 'page' : undefined}
                      title={item.label}
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
                      {!collapsed && (
                        <span className="truncate font-medium text-sm">{item.label}</span>
                      )}
                      {isActive && !collapsed && (
                        <div className="absolute right-0 top-1/2 -translate-y-1/2 w-1 h-8 bg-white rounded-l-full" />
                      )}
                    </button>
                    {!collapsed && hasChildren ? (
                      <button
                        type="button"
                        onClick={(event) => {
                          event.stopPropagation();
                          toggleSection(item.id);
                        }}
                        className="absolute right-3 top-1/2 -translate-y-1/2 rounded-full p-1 text-slate-400 transition-colors hover:text-white"
                        aria-label={
                          isExpanded
                            ? t('navigationCollapseSection') ?? 'Скрыть подпункты'
                            : t('navigationExpandSection') ?? 'Показать подпункты'
                        }>
                        <ChevronDown className={`h-4 w-4 transition-transform ${isExpanded ? 'rotate-180' : ''}`} />
                      </button>
                    ) : null}
                  </div>

                  {!collapsed && hasChildren && isExpanded ? (
                    <ul className="mt-1 space-y-1 pl-2">
                      {childItems.map((child) => {
                        const ChildIcon = child.icon;
                        const childActive = activeTab === child.id;
                        return (
                          <li key={child.id}>
                            <button
                              onClick={() => {
                                onTabChange(child.id);
                                setMobileOpen(false);
                              }}
                              aria-current={childActive ? 'page' : undefined}
                              title={child.label}
                              className={[
                                'w-full flex items-center rounded-xl transition-all duration-200 select-none group relative',
                                'gap-3 pr-4 py-2 pl-12 border border-transparent',
                                childActive
                                  ? 'bg-gradient-to-r from-blue-500/90 to-blue-600/90 text-white shadow-md shadow-blue-900/20'
                                  : 'text-slate-200/90 bg-slate-900/20 hover:bg-slate-800/50 hover:text-white',
                              ].join(' ')}>
                              <ChildIcon
                                className={`h-4 w-4 shrink-0 transition-colors ${
                                  childActive ? 'text-white' : 'text-slate-400 group-hover:text-white'
                                }`}
                              />
                              <span className="truncate text-sm font-medium">{child.label}</span>
                              {childActive && (
                                <div className="absolute right-0 top-1/2 -translate-y-1/2 w-1 h-6 bg-white rounded-l-full" />
                              )}
                            </button>
                          </li>
                        );
                      })}
                    </ul>
                  ) : null}
                </li>
              );
            })}
          </ul>
        </nav>

        <div className="border-t border-slate-700/50 p-4 bg-slate-900/50">
          {!collapsed ? (
            <div className="mb-4 flex items-center gap-3 px-2">
              <div className="w-10 h-10 rounded-full bg-gradient-to-br from-blue-500 to-blue-600 flex items-center justify-center font-semibold text-white shadow-lg shrink-0">
                {(user?.fullName ?? 'U')
                  .split(' ')
                  .map((p) => p.trim()[0])
                  .filter(Boolean)
                  .slice(0, 2)
                  .join('')
                  .toUpperCase()}
              </div>
              <div className="flex-1 min-w-0">
                <div className="text-sm font-semibold text-white truncate">{user?.fullName}</div>
                <div className="text-xs text-slate-400 truncate">{user?.role?.name}</div>
              </div>
            </div>
          ) : (
            <div className="mb-4 flex justify-center">
              <div className="w-9 h-9 rounded-full bg-gradient-to-br from-blue-500 to-blue-600 flex items-center justify-center font-semibold text-white shadow-lg text-xs">
                {(user?.fullName ?? 'U')
                  .split(' ')
                  .map((p) => p.trim()[0])
                  .filter(Boolean)
                  .slice(0, 2)
                  .join('')
                  .toUpperCase()}
              </div>
            </div>
          )}

          <button
            onClick={handleSignOut}
            className={[
              'w-full flex items-center rounded-lg text-sm transition-all duration-200 group',
              'text-slate-300 hover:bg-red-500/10 hover:text-red-400 border border-slate-700/50 hover:border-red-500/30',
              collapsed ? 'justify-center p-2.5' : 'justify-center gap-2 px-3 py-2.5',
            ].join(' ')}
            title="Выйти">
            <LogOut className="h-4 w-4 group-hover:scale-110 transition-transform" />
            {!collapsed && <span className="font-medium">Выйти</span>}
          </button>
        </div>
      </aside>
    </>
  );
}
