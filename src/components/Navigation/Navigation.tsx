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
  ChevronDown,
  ChevronRight,
  TrendingUp,
  TrendingDown,
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
  type SidebarItem =
    | { type: 'link'; id: string; tab: Tab; label: string; icon: typeof Calendar }
    | { type: 'group'; id: string; tab: Tab; label: string; icon: typeof Calendar; children: SidebarChild[] };

  const baseItems = useMemo(() => {
    const items: SidebarItem[] = [];

    if (permissions.calendar.canView) {
      items.push({
        type: 'link',
        id: 'calendar',
        tab: 'calendar',
        label: t('calendar') ?? 'Календарь',
        icon: Calendar,
      });
    }

    if (permissions.reports.canView) {
      items.push({
        type: 'link',
        id: 'reports',
        tab: 'reports',
        label: t('reports') ?? 'Отчёты',
        icon: BarChart,
      });
    }

    if (permissions.calculator.canView) {
      items.push({
        type: 'link',
        id: 'calculator',
        tab: 'calculator',
        label: t('calculator') ?? 'Калькулятор',
        icon: Calculator,
      });
    }

    if (permissions.legalEntities.canView) {
      items.push({
        type: 'link',
        id: 'legalEntities',
        tab: 'legalEntities',
        label: t('legalEntities') ?? 'Юр. лица',
        icon: Building2,
      });
    }

    if (permissions.clients.canView) {
      items.push({
        type: 'link',
        id: 'clients',
        tab: 'clients',
        label: t('clients') ?? 'Клиенты',
        icon: Users,
      });
    }

    if (permissions.accounts.canView) {
      items.push({
        type: 'link',
        id: 'accounts',
        tab: 'accounts',
        label: t('accounts') ?? 'Счета',
        icon: WalletCards,
      });
    }

    if (permissions.acts.canView) {
      const paymentChildren: SidebarChild[] = [];
      if (permissions.payments.canView) {
        paymentChildren.push({
          id: 'paymentsIncome',
          label: t('paymentsIncomeTab') ?? 'Доходные платежи',
          icon: TrendingUp,
        });
        paymentChildren.push({
          id: 'paymentsExpense',
          label: t('paymentsExpenseTab') ?? 'Расходные платежи',
          icon: TrendingDown,
        });
      }

      if (paymentChildren.length > 0) {
        items.push({
          type: 'group',
          id: 'acts',
          tab: 'acts',
          label: t('acts') ?? 'Акты',
          icon: FileCheck2,
          children: paymentChildren,
        });
      } else {
        items.push({
          type: 'link',
          id: 'acts',
          tab: 'acts',
          label: t('acts') ?? 'Акты',
          icon: FileCheck2,
        });
      }
    }

    if (permissions.contracts.canView) {
      items.push({
        type: 'link',
        id: 'contracts',
        tab: 'contracts',
        label: t('contracts') ?? 'Договоры',
        icon: FileSignature,
      });
    }

    if (permissions.dictionaries.canView) {
      items.push({
        type: 'link',
        id: 'dictionaries',
        tab: 'dictionaries',
        label: t('dictionaries') ?? 'Справочники',
        icon: Settings,
      });
    }

    return items;
  }, [permissions, t]);

  const adminItems = useMemo(() => {
    if (!isAdmin()) {
      return [] as SidebarItem[];
    }

    return [
      { type: 'link', id: 'users', tab: 'users', label: 'Пользователи', icon: UserCog },
      { type: 'link', id: 'roles', tab: 'roles', label: 'Роли', icon: Shield },
      { type: 'link', id: 'userActivity', tab: 'userActivity', label: 'Контроль пользователей', icon: Eye },
    ];
  }, [isAdmin]);

  const navItems = useMemo(() => [...baseItems, ...adminItems], [baseItems, adminItems]);

  const GROUP_STATE_KEY = 'pp.sidebar_groups_v1';

  const [expandedGroups, setExpandedGroups] = useState<Record<string, boolean>>(() => {
    try {
      const raw = localStorage.getItem(GROUP_STATE_KEY);
      if (raw) {
        const parsed = JSON.parse(raw) as Record<string, boolean>;
        if (parsed && typeof parsed === 'object') {
          return parsed;
        }
      }
    } catch {
      /** */
    }
    return { acts: true };
  });

  useEffect(() => {
    try {
      localStorage.setItem(GROUP_STATE_KEY, JSON.stringify(expandedGroups));
    } catch {
      /** */
    }
  }, [expandedGroups]);

  const handleToggleGroup = useCallback((groupId: string) => {
    setExpandedGroups((prev) => ({
      ...prev,
      [groupId]: !(prev[groupId] ?? true),
    }));
  }, []);

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

        <nav className="flex-1 overflow-y-auto py-4 px-2">
          <ul className="space-y-1.5">
            {navItems.map((item) => {
              if (item.type === 'link') {
                const Icon = item.icon;
                const isActive = activeTab === item.tab;
                return (
                  <li key={item.id} className="relative">
                    <button
                      type="button"
                      onClick={() => {
                        onTabChange(item.tab);
                        setMobileOpen(false);
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
                  </li>
                );
              }

              const Icon = item.icon;
              const isActive =
                activeTab === item.tab || item.children.some((child) => child.id === activeTab);
              const isExpanded = expandedGroups[item.id] ?? true;
              const hasChildren = item.children.length > 0;
              const toggleLabel = isExpanded
                ? t('sidebarHideSubmenu') ?? 'Скрыть подразделы'
                : t('sidebarShowSubmenu') ?? 'Показать подразделы';

              return (
                <li key={item.id} className="relative">
                  <button
                    type="button"
                    onClick={() => {
                      onTabChange(item.tab);
                      setMobileOpen(false);
                    }}
                    aria-current={isActive ? 'page' : undefined}
                    aria-expanded={isExpanded}
                    title={item.label}
                    className={[
                      'w-full flex items-center rounded-xl transition-all duration-200 select-none group relative',
                      collapsed ? 'justify-center p-3' : hasChildren ? 'gap-3 pr-10 py-3 pl-4' : 'gap-3 px-4 py-3',
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
                    {collapsed && hasChildren ? (
                      <ChevronRight
                        className={`absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 transition-transform ${
                          isExpanded ? 'rotate-90' : ''
                        }`}
                      />
                    ) : null}
                    {isActive && !collapsed && (
                      <div className="absolute right-0 top-1/2 -translate-y-1/2 w-1 h-8 bg-white rounded-l-full" />
                    )}
                  </button>

                  {!collapsed && hasChildren && (
                    <button
                      type="button"
                      onClick={(event) => {
                        event.stopPropagation();
                        handleToggleGroup(item.id);
                      }}
                      className={[
                        'absolute right-3 top-1/2 -translate-y-1/2 rounded-full p-1 transition-colors',
                        isActive
                          ? 'text-white/90 hover:bg-white/10'
                          : 'text-slate-400 hover:text-white hover:bg-slate-700/60',
                      ].join(' ')}
                      aria-label={toggleLabel}>
                      <ChevronDown
                        className={`h-4 w-4 transition-transform ${isExpanded ? '' : '-rotate-90'}`}
                      />
                    </button>
                  )}

                  {!collapsed && hasChildren && isExpanded ? (
                    <ul className="mt-1 space-y-1.5 pl-8">
                      {item.children.map((child) => {
                        const ChildIcon = child.icon;
                        const isChildActive = activeTab === child.id;
                        return (
                          <li key={child.id}>
                            <button
                              type="button"
                              onClick={() => {
                                onTabChange(child.id);
                                setMobileOpen(false);
                              }}
                              className={[
                                'w-full flex items-center gap-3 rounded-lg transition-all duration-200 select-none',
                                'pl-10 pr-4 py-2.5 text-sm font-medium',
                                isChildActive
                                  ? 'bg-slate-800/70 text-white shadow-inner shadow-slate-900/30'
                                  : 'text-slate-300 hover:bg-slate-800/40 hover:text-white',
                              ].join(' ')}>
                              <ChildIcon className="h-4 w-4 shrink-0" />
                              <span className="truncate">{child.label}</span>
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
