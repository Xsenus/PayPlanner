import { useCallback, useEffect, useLayoutEffect, useMemo, useState } from 'react';
import {
  Calendar,
  BarChart,
  Calculator,
  Users,
  Settings,
  UserCog,
  Shield,
  Menu,
  X,
  WalletCards,
  Building2,
  FileCheck2,
  FileSignature,
  Eye,
  Banknote,
  TrendingUp,
  TrendingDown,
  ChevronDown,
  CalendarRange,
  Landmark,
  CircleDollarSign,
  Phone,
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
  const { user, isAdmin } = useAuth();
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
        .pp-sidebar-scroll {
          scrollbar-width: thin;
          scrollbar-color: rgba(148, 163, 184, 0.6) transparent;
        }
        .pp-sidebar-scroll::-webkit-scrollbar {
          width: 8px;
        }
        .pp-sidebar-scroll::-webkit-scrollbar-track {
          background: transparent;
        }
        .pp-sidebar-scroll::-webkit-scrollbar-thumb {
          background-color: rgba(148, 163, 184, 0.6);
          border-radius: 9999px;
          border: 2px solid transparent;
          background-clip: content-box;
        }
        .pp-sidebar-scroll::-webkit-scrollbar-thumb:hover {
          background-color: rgba(226, 232, 240, 0.9);
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
      const contactChildren: SidebarChild[] = [
        { id: 'contacts' as Tab, label: 'Все', icon: Phone },
        { id: 'contactsClients' as Tab, label: 'Клиенты', icon: Users },
        { id: 'contactsExternal' as Tab, label: 'Сторонние', icon: Users },
      ];

      items.push({
        id: 'contacts',
        tab: 'contacts' as Tab,
        label: 'Контакты',
        icon: Phone,
        children: contactChildren,
      });
    }
    if (permissions.accounts.canView) {
      const accountChildren: SidebarChild[] = [
        {
          id: 'accountsIncome' as Tab,
          label: t('accountsIncomeNav') ?? t('invoiceTypeIncomeShort') ?? 'Доходные',
          icon: TrendingUp,
        },
        {
          id: 'accountsExpense' as Tab,
          label: t('accountsExpenseNav') ?? t('invoiceTypeExpenseShort') ?? 'Расходные',
          icon: TrendingDown,
        },
      ];

      items.push({
        id: 'accounts',
        tab: 'accounts' as Tab,
        label: t('accounts') ?? 'Счета',
        icon: WalletCards,
        children: accountChildren,
      });

      items.push({
        id: 'installments',
        tab: 'installments' as Tab,
        label: t('installmentsNav') ?? 'Рассрочка',
        icon: CalendarRange,
      });
    }

    if (permissions.payments.canView) {
      const moneyChildren: SidebarChild[] = [
        { id: 'moneyBank' as Tab, label: 'Банк', icon: Landmark },
        { id: 'moneyTotal' as Tab, label: 'Итого', icon: CircleDollarSign },
      ];

      items.push({
        id: 'money',
        tab: 'moneyBank' as Tab,
        label: 'Деньги',
        icon: CircleDollarSign,
        children: moneyChildren,
      });
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

    if (permissions.payments.canView) {
      items.push({
        id: 'payments',
        tab: 'payments' as Tab,
        label: 'Платежи',
        icon: Banknote,
        children: paymentsChildren,
      });
    }

    if (permissions.acts.canView) {
      items.push({
        id: 'acts',
        tab: 'acts' as Tab,
        label: t('acts') ?? 'Акты',
        icon: FileCheck2,
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

  const canManageRoles = isAdmin() || permissions.roles.canView;

  const adminItems = useMemo<SidebarItem[]>(() => {
    if (!isAdmin() && !canManageRoles) {
      return [];
    }

    return [
      ...(isAdmin()
        ? [{ id: 'users', tab: 'users' as Tab, label: '\u041f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u0438', icon: UserCog }]
        : []),
      { id: 'roles', tab: 'roles' as Tab, label: '\u0420\u043e\u043b\u0438', icon: Shield },
      ...(isAdmin()
        ? [
            { id: 'userActivity', tab: 'userActivity' as Tab, label: '\u041a\u043e\u043d\u0442\u0440\u043e\u043b\u044c \u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u0435\u0439', icon: Eye },
            { id: 'settings', tab: 'settings' as Tab, label: '\u041d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438', icon: Settings },
          ]
        : []),
    ];
  }, [canManageRoles, isAdmin]);

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
        const generalPaymentsActive = item.id === 'payments' && activeTab === 'payments';
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
              <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-white/95 p-1 shadow-lg ring-1 ring-white/10">
                <img src="/logo.ico" alt="ElitPravo CRM" className="h-full w-full object-contain" />
              </div>
              <div className="min-w-0">
                <div className="truncate text-sm font-semibold text-white">crm.elitpravo.com</div>
                <div className="truncate text-xs text-slate-400">ElitPravo CRM</div>
              </div>
            </div>
          ) : (
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-white/95 p-1 shadow-lg ring-1 ring-white/10">
              <img src="/logo.ico" alt="ElitPravo CRM" className="h-full w-full object-contain" />
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

        <nav className="pp-sidebar-scroll flex-1 overflow-y-auto py-4 px-2">
          <ul className="space-y-1.5">
            {sidebarItems.map((item) => {
              const Icon = item.icon;
              const childItems = item.children ?? [];
              const hasChildren = childItems.length > 0;
              const isParentActive = item.tab ? activeTab === item.tab : false;
              const isChildActive = childItems.some((child) => child.id === activeTab);
              const isActive = isParentActive || isChildActive;
              const isExpanded = expandedSections[item.id] ?? false;

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
                                'gap-3 pr-4 py-2 pl-12',
                                childActive
                                  ? 'bg-blue-500/25 text-blue-50 border border-blue-400/40 shadow-inner'
                                  : 'text-slate-300 hover:bg-slate-800/50 hover:text-white',
                              ].join(' ')}>
                              <ChildIcon
                                className={`h-4 w-4 shrink-0 transition-colors ${
                                  childActive ? 'text-blue-100' : 'text-slate-400 group-hover:text-white'
                                }`}
                              />
                              <span className="truncate text-sm font-medium">{child.label}</span>
                              {childActive && (
                                <div className="absolute right-1.5 top-1/2 -translate-y-1/2 h-5 w-0.5 rounded-full bg-blue-100/80" />
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

      </aside>
    </>
  );
}
