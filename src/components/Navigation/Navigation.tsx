import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent,
  type MouseEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import {
  Calendar,
  CalendarDays,
  BarChart,
  Calculator,
  ListTodo,
  Users,
  Settings,
  UserCog,
  Shield,
  ShieldAlert,
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
  History,
  Bell,
  Briefcase,
  StickyNote,
  GripVertical,
  LayoutGrid,
  ArrowUp,
  ArrowDown,
  Save,
  Folder,
  FolderPlus,
  Trash2,
  Scale,
  MessageCircle,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useToast } from '../../hooks/useToast';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { Tab } from '../../types/tabs';
import { getTabHref } from '../../appRouting';
import { apiService, type NavigationGroupDto } from '../../services/api';
import {
  PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT,
  requestPaymentAdvanceReviewFilter,
} from '../../utils/paymentAdvanceReview';

interface NavigationProps {
  activeTab: Tab;
  onTabChange: (tab: Tab) => void;
}

const LS_KEY = 'pp.sidebar_collapsed';
const WIDTH_OPEN = '17rem';
const WIDTH_COLLAPSED = '5rem';
const STYLE_ID = 'pp-sidebar-style';
const EXPANDED_KEY = 'pp.sidebar_expanded';
const ORDER_KEY = 'pp.sidebar_order';
const HIDDEN_SETTING_KEY = 'navigation.sidebar.hidden-items.v1';
const HIDDEN_STORAGE_KEY = 'pp.sidebar_hidden';

function formatBadgeCount(count: number) {
  return count > 99 ? '99+' : String(count);
}

function normalizeSidebarOrder(raw: string | null, validIds: string[]): string[] {
  if (!raw) {
    return validIds;
  }

  try {
    const parsed = JSON.parse(raw) as unknown;
    const list = Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === 'string') : [];
    const existing = list.filter((itemId) => validIds.includes(itemId));
    const missing = validIds.filter((itemId) => !existing.includes(itemId));
    return existing.length > 0 ? [...existing, ...missing] : validIds;
  } catch {
    return validIds;
  }
}

function moveItemToTarget(items: string[], sourceId: string, targetId: string): string[] {
  const sourceIndex = items.indexOf(sourceId);
  const targetIndex = items.indexOf(targetId);
  if (sourceIndex < 0 || targetIndex < 0 || sourceIndex === targetIndex) return items;

  const next = [...items];
  const [movedItemId] = next.splice(sourceIndex, 1);
  next.splice(targetIndex, 0, movedItemId);
  return next;
}

function normalizeNavigationRootOrder(
  order: string[],
  validItemIds: string[],
  groups: NavigationGroupDto[],
): string[] {
  const assignedItemIds = new Set(groups.flatMap((group) => group.itemIds));
  const validRootIds = new Set([
    ...validItemIds.filter((itemId) => !assignedItemIds.has(itemId)),
    ...groups.map((group) => group.id),
  ]);
  const existing = order.filter((entryId, index) => validRootIds.has(entryId) && order.indexOf(entryId) === index);
  const missingItems = validItemIds.filter((itemId) => validRootIds.has(itemId) && !existing.includes(itemId));
  const missingGroups = groups.map((group) => group.id).filter((groupId) => !existing.includes(groupId));
  return [...existing, ...missingItems, ...missingGroups];
}

export default function Navigation({ activeTab, onTabChange }: NavigationProps) {
  const { t } = useTranslation();
  const toast = useToast();
  const { user, isAdmin } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const orderStorageKey = `${ORDER_KEY}.${user?.id ?? 'guest'}`;
  const hiddenStorageKey = `${HIDDEN_STORAGE_KEY}.${user?.id ?? 'guest'}`;
  const [mobileOpen, setMobileOpen] = useState(false);
  const [advanceDecisionCount, setAdvanceDecisionCount] = useState(0);

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
        /* Граница совпадает с Tailwind lg: при 1024 px меню уже видно,
           поэтому содержимое должно получить отступ той же ширины. */
        @media (max-width: 1023px) {
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

  useEffect(() => {
    let cancelled = false;

    if (!permissions.payments.canView) {
      setAdvanceDecisionCount(0);
      return () => {
        cancelled = true;
      };
    }

    const loadAdvanceDecisionCount = async () => {
      try {
        const data = await apiService.getPaymentsV2({
          advanceReviewStatus: 'RequiresDecision',
          page: 1,
          pageSize: 1,
          sortBy: 'createdAt',
          sortDir: 'desc',
        });
        if (!cancelled) {
          setAdvanceDecisionCount(data.total ?? 0);
        }
      } catch {
        if (!cancelled) {
          setAdvanceDecisionCount(0);
        }
      }
    };

    void loadAdvanceDecisionCount();
    const timer = window.setInterval(loadAdvanceDecisionCount, 60_000);
    window.addEventListener(PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT, loadAdvanceDecisionCount);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
      window.removeEventListener(PAYMENT_ADVANCE_REVIEW_CHANGED_EVENT, loadAdvanceDecisionCount);
    };
  }, [permissions.payments.canView]);

  type SidebarChild = { id: Tab; label: string; icon: typeof Calendar };
  type SidebarItem = {
    id: string;
    label: string;
    icon: typeof Calendar;
    tab?: Tab;
    children?: SidebarChild[];
  };
  type SidebarNode =
    | { type: 'item'; item: SidebarItem }
    | { type: 'group'; id: string; label: string; items: SidebarItem[] };

  const mainItems = useMemo<SidebarItem[]>(() => {
    const items: SidebarItem[] = [
      { id: 'workspace', tab: 'workspace' as Tab, label: 'Рабочий стол', icon: LayoutGrid },
    ];
    if (permissions.calendar.canView) {
      items.push({ id: 'calendar', tab: 'calendar' as Tab, label: t('calendar') ?? 'Календарь', icon: Calendar });
    }
    items.push({
      id: 'notifications',
      tab: 'notifications' as Tab,
      label: 'Уведомления',
      icon: Bell,
    });
    items.push({
      id: 'messenger',
      tab: 'messenger' as Tab,
      label: 'Мессенджер',
      icon: MessageCircle,
    });
    if (permissions.tasks.canView) {
      items.push({
        id: 'tasks',
        tab: 'tasks' as Tab,
        label: 'Журнал задач',
        icon: ListTodo,
        children: [
          {
            id: 'tasksControl' as Tab,
            label: 'На контроле',
            icon: ShieldAlert,
          },
          {
            id: 'tasksApprovals' as Tab,
            label: 'Согласования',
            icon: FileCheck2,
          },
          {
            id: 'tasksCalendar' as Tab,
            label: 'Календарь',
            icon: CalendarDays,
          },
        ],
      });
    }
    if (permissions.notes.canView) {
      items.push({
        id: 'notes',
        tab: 'notes' as Tab,
        label: 'Заметки',
        icon: StickyNote,
      });
    }
    if (permissions.reports.canView) {
      items.push({ id: 'reports', tab: 'reports' as Tab, label: t('reports') ?? 'Отчёты', icon: BarChart });
    }
    if (permissions.calculator.canView) {
      items.push({ id: 'calculator', tab: 'calculator' as Tab, label: t('calculator') ?? 'Калькулятор', icon: Calculator });
    }
    if (permissions.cases.canView) {
      items.push({ id: 'cases', tab: 'cases' as Tab, label: 'Дела', icon: Briefcase });
      items.push({ id: 'courtCases', tab: 'courtCases' as Tab, label: 'Судебные дела', icon: Scale });
    }
    if (permissions.clients.canView || permissions.legalEntities.canView) {
      const contactChildren: SidebarChild[] = [
        ...(permissions.clients.canView
          ? [{ id: 'contactsClients' as Tab, label: 'Люди', icon: Users }]
          : []),
        ...(permissions.legalEntities.canView
          ? [{ id: 'contactsLegal' as Tab, label: 'Организации', icon: Building2 }]
          : []),
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
        { id: 'moneyCash' as Tab, label: 'Касса', icon: Banknote },
        { id: 'moneyCards' as Tab, label: 'Карты', icon: WalletCards },
        { id: 'moneyEMoney' as Tab, label: 'Электронные деньги', icon: WalletCards },
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
      const actChildren: SidebarChild[] = [
        {
          id: 'actsIncome' as Tab,
          label: t('invoiceTypeIncomeShort') ?? 'Доходные',
          icon: TrendingUp,
        },
        {
          id: 'actsExpense' as Tab,
          label: t('invoiceTypeExpenseShort') ?? 'Расходные',
          icon: TrendingDown,
        },
      ];
      items.push({
        id: 'acts',
        tab: 'acts' as Tab,
        label: t('acts') ?? 'Акты',
        icon: FileCheck2,
        children: actChildren,
      });
    }

    if (permissions.contracts.canView) {
      items.push({ id: 'contracts', tab: 'contracts' as Tab, label: t('contracts') ?? 'Договоры', icon: FileSignature });
    }
    if (isAdmin()) {
      items.push({ id: 'payroll', tab: 'payroll' as Tab, label: 'Зарплата', icon: CircleDollarSign });
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
  }, [isAdmin, permissions, t]);

  const canManageRoles = isAdmin() || permissions.roles.canView;
  const canViewAudit = isAdmin() || permissions.audit.canView;
  const canViewIntegrations = isAdmin() || Boolean(permissions.integrations?.canView);
  const canViewYandexDisk = isAdmin() || Boolean(permissions.yandexDisk?.canView);
  const canViewS3Storage = isAdmin() || Boolean(permissions.s3Storage?.canView);
  const canViewSettings = isAdmin() || canViewAudit || permissions.notifications.canView || canViewIntegrations || canViewYandexDisk || canViewS3Storage;

  const adminItems = useMemo<SidebarItem[]>(() => {
    if (!isAdmin() && !canManageRoles && !canViewSettings && !canViewYandexDisk && !canViewS3Storage) {
      return [];
    }

    return [
      ...(isAdmin()
        ? [{ id: 'users', tab: 'users' as Tab, label: '\u041f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u0438', icon: UserCog }]
        : []),
      ...(canManageRoles
        ? [{ id: 'roles', tab: 'roles' as Tab, label: '\u0420\u043e\u043b\u0438', icon: Shield }]
        : []),
      ...(canViewAudit
        ? [{ id: 'audit', tab: 'audit' as Tab, label: 'Аудит объектов', icon: History }]
        : []),
      ...(isAdmin()
        ? [
            { id: 'userActivity', tab: 'userActivity' as Tab, label: '\u041a\u043e\u043d\u0442\u0440\u043e\u043b\u044c \u043f\u043e\u043b\u044c\u0437\u043e\u0432\u0430\u0442\u0435\u043b\u0435\u0439', icon: Eye },
          ]
        : []),
      ...(canViewSettings
        ? [{ id: 'settings', tab: 'settings' as Tab, label: '\u041d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438', icon: Settings }]
        : []),
    ];
  }, [canManageRoles, canViewAudit, canViewSettings, canViewYandexDisk, canViewS3Storage, isAdmin]);

  const availableSidebarItems = useMemo(() => [...mainItems, ...adminItems], [mainItems, adminItems]);
  const availableSidebarItemIds = useMemo(
    () => availableSidebarItems.map((item) => item.id),
    [availableSidebarItems],
  );
  const canManageNavigation = isAdmin();
  const [globalOrderIds, setGlobalOrderIds] = useState<string[]>([]);
  const [customLabels, setCustomLabels] = useState<Record<string, string>>({});
  const [navigationGroups, setNavigationGroups] = useState<NavigationGroupDto[]>([]);
  const [hiddenItemIds, setHiddenItemIds] = useState<string[]>(() => {
    if (typeof window === 'undefined') return [];
    try {
      const parsed = JSON.parse(window.localStorage.getItem(hiddenStorageKey) ?? '[]') as unknown;
      return Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === 'string') : [];
    } catch {
      return [];
    }
  });
  const [moreSectionsOpen, setMoreSectionsOpen] = useState(false);
  const [navigationSettingsOpen, setNavigationSettingsOpen] = useState(false);
  const [navigationSettingsSaving, setNavigationSettingsSaving] = useState(false);
  const [navigationSettingsError, setNavigationSettingsError] = useState('');
  const [draftOrderIds, setDraftOrderIds] = useState<string[]>([]);
  const [draftLabels, setDraftLabels] = useState<Record<string, string>>({});
  const [draftGroups, setDraftGroups] = useState<NavigationGroupDto[]>([]);
  const [draggedDraftItemId, setDraggedDraftItemId] = useState<string | null>(null);
  const [draggedDraftGroupId, setDraggedDraftGroupId] = useState<string | null>(null);
  const draggedDraftItemIdRef = useRef<string | null>(null);
  const draggedDraftGroupIdRef = useRef<string | null>(null);
  const lastDraftPointerTargetIdRef = useRef<string | null>(null);

  const sidebarItems = useMemo(() => {
    return availableSidebarItems.map((item) => ({ ...item, label: customLabels[item.id]?.trim() || item.label }));
  }, [availableSidebarItems, customLabels]);
  const navigationNodes = useMemo<SidebarNode[]>(() => {
    const itemMap = new Map(sidebarItems.map((item) => [item.id, item]));
    const groupMap = new Map(navigationGroups.map((group) => [group.id, group]));
    return normalizeNavigationRootOrder(globalOrderIds, availableSidebarItemIds, navigationGroups)
      .map((entryId): SidebarNode | null => {
        const group = groupMap.get(entryId);
        if (group) {
          const items = group.itemIds
            .map((itemId) => itemMap.get(itemId))
            .filter((item): item is SidebarItem => item !== undefined && !hiddenItemIds.includes(item.id));
          return items.length > 0 ? { type: 'group', id: group.id, label: group.name, items } : null;
        }
        const item = itemMap.get(entryId);
        return item && !hiddenItemIds.includes(item.id) ? { type: 'item', item } : null;
      })
      .filter((node): node is SidebarNode => node !== null);
  }, [availableSidebarItemIds, globalOrderIds, hiddenItemIds, navigationGroups, sidebarItems]);

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

  const handleNavLinkClick = useCallback(
    (event: MouseEvent<HTMLAnchorElement>, tab: Tab) => {
      if (
        event.defaultPrevented ||
        event.button !== 0 ||
        event.metaKey ||
        event.ctrlKey ||
        event.shiftKey ||
        event.altKey
      ) {
        return;
      }

      event.preventDefault();
      onTabChange(tab);
      setMobileOpen(false);
    },
    [onTabChange],
  );

  useEffect(() => {
    let cancelled = false;
    const loadNavigationPreferences = async () => {
      const [navigationResult, hiddenResult] = await Promise.allSettled([
        apiService.getNavigationSettings(),
        apiService.getUserSetting(HIDDEN_SETTING_KEY),
      ]);
      if (cancelled) return;

      if (navigationResult.status === 'fulfilled') {
        setGlobalOrderIds(navigationResult.value.order ?? []);
        setCustomLabels(navigationResult.value.labels ?? {});
        setNavigationGroups(navigationResult.value.groups ?? []);
      } else if (canManageNavigation) {
        setGlobalOrderIds(normalizeSidebarOrder(
          window.localStorage.getItem(orderStorageKey) ?? window.localStorage.getItem(ORDER_KEY),
          availableSidebarItemIds,
        ));
      }

      if (hiddenResult.status === 'fulfilled' && hiddenResult.value?.value) {
        try {
          const parsed = JSON.parse(hiddenResult.value.value) as unknown;
          if (Array.isArray(parsed)) {
            setHiddenItemIds(parsed.filter((item): item is string => typeof item === 'string'));
          }
        } catch {
          // Повреждённая персональная настройка не должна скрыть всё меню.
        }
      }
    };
    void loadNavigationPreferences();
    return () => {
      cancelled = true;
    };
    // Настройки загружаются один раз для учетной записи. Изменение матрицы прав
    // только фильтрует уже загруженный каталог и не должно повторять API-запросы.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user?.id]);

  useEffect(() => {
    try {
      window.localStorage.setItem(hiddenStorageKey, JSON.stringify(hiddenItemIds));
    } catch {
      /** */
    }
  }, [hiddenItemIds, hiddenStorageKey]);

  const toggleItemVisibility = useCallback((itemId: string) => {
    setHiddenItemIds((previous) => {
      const next = previous.includes(itemId)
        ? previous.filter((id) => id !== itemId)
        : [...previous, itemId];
      void apiService.upsertUserSetting(HIDDEN_SETTING_KEY, JSON.stringify(next)).catch(() => {
        setHiddenItemIds(previous);
      });
      return next;
    });
  }, []);

  const openNavigationSettings = useCallback(() => {
    setDraftOrderIds(normalizeNavigationRootOrder(globalOrderIds, availableSidebarItemIds, navigationGroups));
    setDraftLabels({ ...customLabels });
    setDraftGroups(navigationGroups.map((group) => ({ ...group, itemIds: [...group.itemIds] })));
    setNavigationSettingsError('');
    setNavigationSettingsOpen(true);
  }, [availableSidebarItemIds, customLabels, globalOrderIds, navigationGroups]);

  const moveDraftRootEntry = useCallback((entryId: string, direction: -1 | 1) => {
    setDraftOrderIds((previous) => {
      const index = previous.indexOf(entryId);
      const target = index + direction;
      if (index < 0 || target < 0 || target >= previous.length) return previous;
      const next = [...previous];
      [next[index], next[target]] = [next[target], next[index]];
      return next;
    });
  }, []);

  const assignDraftItem = useCallback((itemId: string, targetGroupId: string | null, targetItemId?: string) => {
    setDraftGroups((previousGroups) => previousGroups.map((group) => {
      if (group.id !== targetGroupId) {
        return { ...group, itemIds: group.itemIds.filter((candidateId) => candidateId !== itemId) };
      }
      if (targetItemId && group.itemIds.includes(itemId) && group.itemIds.includes(targetItemId)) {
        return { ...group, itemIds: moveItemToTarget(group.itemIds, itemId, targetItemId) };
      }
      const itemIds = group.itemIds.filter((candidateId) => candidateId !== itemId);
      const targetIndex = targetItemId ? itemIds.indexOf(targetItemId) : -1;
      itemIds.splice(targetIndex >= 0 ? targetIndex : itemIds.length, 0, itemId);
      return { ...group, itemIds };
    }));
    setDraftOrderIds((previousOrder) => {
      if (targetGroupId) return previousOrder.filter((entryId) => entryId !== itemId);
      if (targetItemId && previousOrder.includes(itemId) && previousOrder.includes(targetItemId)) {
        return moveItemToTarget(previousOrder, itemId, targetItemId);
      }
      const withoutItem = previousOrder.filter((entryId) => entryId !== itemId);
      const targetIndex = targetItemId ? withoutItem.indexOf(targetItemId) : -1;
      const next = [...withoutItem];
      next.splice(targetIndex >= 0 ? targetIndex : next.length, 0, itemId);
      return next;
    });
  }, []);

  const moveDraftItem = useCallback((itemId: string, direction: -1 | 1) => {
    const group = draftGroups.find((candidate) => candidate.itemIds.includes(itemId));
    if (!group) {
      moveDraftRootEntry(itemId, direction);
      return;
    }
    setDraftGroups((previous) => previous.map((candidate) => {
      if (candidate.id !== group.id) return candidate;
      const index = candidate.itemIds.indexOf(itemId);
      const target = index + direction;
      if (index < 0 || target < 0 || target >= candidate.itemIds.length) return candidate;
      const itemIds = [...candidate.itemIds];
      [itemIds[index], itemIds[target]] = [itemIds[target], itemIds[index]];
      return { ...candidate, itemIds };
    }));
  }, [draftGroups, moveDraftRootEntry]);

  const addDraftGroup = useCallback(() => {
    const groupId = `group-${typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : Date.now()}`;
    setDraftGroups((previous) => [...previous, { id: groupId, name: 'Новая группа', itemIds: [] }]);
    setDraftOrderIds((previous) => [...previous, groupId]);
  }, []);

  const deleteDraftGroup = useCallback((groupId: string) => {
    const group = draftGroups.find((candidate) => candidate.id === groupId);
    if (!group) return;
    setDraftGroups((previous) => previous.filter((candidate) => candidate.id !== groupId));
    setDraftOrderIds((previous) => {
      const groupIndex = previous.indexOf(groupId);
      const next = previous.filter((entryId) => entryId !== groupId);
      next.splice(groupIndex >= 0 ? groupIndex : next.length, 0, ...group.itemIds);
      return next;
    });
  }, [draftGroups]);

  const handleDraftDragStart = useCallback((itemId: string, event: DragEvent<HTMLElement>) => {
    const target = event.target as HTMLElement;
    if (!target.closest('[data-menu-drag-handle]')) {
      event.preventDefault();
      return;
    }
    setDraggedDraftItemId(itemId);
    draggedDraftItemIdRef.current = itemId;
    lastDraftPointerTargetIdRef.current = itemId;
    event.dataTransfer.setData('text/plain', itemId);
    event.dataTransfer.effectAllowed = 'move';
  }, []);

  const handleDraftGroupDragStart = useCallback((groupId: string, event: DragEvent<HTMLElement>) => {
    const target = event.target as HTMLElement;
    if (target.closest('[data-menu-item-id]')) return;
    if (!target.closest('[data-menu-drag-handle]')) {
      event.preventDefault();
      return;
    }
    setDraggedDraftGroupId(groupId);
    draggedDraftGroupIdRef.current = groupId;
    lastDraftPointerTargetIdRef.current = groupId;
    event.dataTransfer.setData('text/plain', groupId);
    event.dataTransfer.effectAllowed = 'move';
  }, []);

  const handleDraftDragOver = useCallback((itemId: string, event: DragEvent<HTMLElement>) => {
    if ((!draggedDraftItemId && !draggedDraftGroupId) || draggedDraftItemId === itemId) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
  }, [draggedDraftGroupId, draggedDraftItemId]);

  const handleDraftDrop = useCallback((itemId: string, groupId: string | null, event: DragEvent<HTMLElement>) => {
    event.preventDefault();
    event.stopPropagation();
    if (draggedDraftGroupId && !groupId) {
      setDraftOrderIds((previous) => moveItemToTarget(previous, draggedDraftGroupId, itemId));
      setDraggedDraftGroupId(null);
      return;
    }
    if (!draggedDraftItemId || draggedDraftItemId === itemId) {
      setDraggedDraftItemId(null);
      return;
    }

    assignDraftItem(draggedDraftItemId, groupId, itemId);
    setDraggedDraftItemId(null);
    draggedDraftItemIdRef.current = null;
    lastDraftPointerTargetIdRef.current = null;
  }, [assignDraftItem, draggedDraftGroupId, draggedDraftItemId]);

  const handleDraftGroupDrop = useCallback((groupId: string, event: DragEvent<HTMLElement>) => {
    event.preventDefault();
    if (draggedDraftItemId) {
      assignDraftItem(draggedDraftItemId, groupId);
      setDraggedDraftItemId(null);
      draggedDraftItemIdRef.current = null;
      return;
    }
    if (draggedDraftGroupId && draggedDraftGroupId !== groupId) {
      setDraftOrderIds((previous) => moveItemToTarget(previous, draggedDraftGroupId, groupId));
    }
    setDraggedDraftGroupId(null);
    draggedDraftGroupIdRef.current = null;
  }, [assignDraftItem, draggedDraftGroupId, draggedDraftItemId]);

  const handleDraftPointerDown = useCallback((itemId: string, event: ReactPointerEvent<HTMLButtonElement>) => {
    if (event.button !== 0) return;
    setDraggedDraftItemId(itemId);
    draggedDraftItemIdRef.current = itemId;
    lastDraftPointerTargetIdRef.current = itemId;
  }, []);

  const handleDraftGroupPointerDown = useCallback((groupId: string, event: ReactPointerEvent<HTMLButtonElement>) => {
    if (event.button !== 0) return;
    setDraggedDraftGroupId(groupId);
    draggedDraftGroupIdRef.current = groupId;
    lastDraftPointerTargetIdRef.current = groupId;
  }, []);

  const handleDraftPointerMove = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    const activeGroupId = draggedDraftGroupIdRef.current;
    const activeItemId = draggedDraftItemIdRef.current;
    if (activeGroupId) {
      const rootEntry = (event.target as HTMLElement).closest<HTMLElement>('[data-menu-root-entry-id]');
      const targetId = rootEntry?.dataset.menuRootEntryId;
      if (!targetId || targetId === lastDraftPointerTargetIdRef.current) return;
      lastDraftPointerTargetIdRef.current = targetId;
      setDraftOrderIds((previous) => moveItemToTarget(previous, activeGroupId, targetId));
      return;
    }
    if (!activeItemId) return;
    const row = (event.target as HTMLElement).closest<HTMLElement>('[data-menu-item-id]');
    const itemId = row?.dataset.menuItemId;
    if (!itemId || itemId === lastDraftPointerTargetIdRef.current) return;
    lastDraftPointerTargetIdRef.current = itemId;
    const groupContainer = row?.closest<HTMLElement>('[data-menu-group-id]');
    assignDraftItem(activeItemId, groupContainer?.dataset.menuGroupId || null, itemId);
  }, [assignDraftItem]);

  useEffect(() => {
    if (!draggedDraftItemId && !draggedDraftGroupId) return;
    const stopPointerDrag = () => {
      setDraggedDraftItemId(null);
      setDraggedDraftGroupId(null);
      draggedDraftItemIdRef.current = null;
      draggedDraftGroupIdRef.current = null;
      lastDraftPointerTargetIdRef.current = null;
    };
    window.addEventListener('pointerup', stopPointerDrag);
    window.addEventListener('pointercancel', stopPointerDrag);
    return () => {
      window.removeEventListener('pointerup', stopPointerDrag);
      window.removeEventListener('pointercancel', stopPointerDrag);
    };
  }, [draggedDraftGroupId, draggedDraftItemId]);

  const saveNavigationSettings = useCallback(async () => {
    setNavigationSettingsSaving(true);
    setNavigationSettingsError('');
    try {
      if (draftGroups.some((group) => !group.name.trim())) {
        const message = 'Укажите название для каждой группы.';
        setNavigationSettingsError(message);
        toast.error(message, 'Настройки меню не сохранены');
        return;
      }
      const saved = await apiService.updateNavigationSettings(draftOrderIds, draftLabels, draftGroups);
      setGlobalOrderIds(saved.order);
      setCustomLabels(saved.labels);
      setNavigationGroups(saved.groups);
      setNavigationSettingsOpen(false);
      toast.success('Порядок групп и разделов сохранён для всех пользователей.', 'Настройки меню сохранены');
    } catch {
      const message = 'Не удалось сохранить порядок меню. Повторите попытку.';
      setNavigationSettingsError(message);
      toast.error(message, 'Настройки меню не сохранены');
    } finally {
      setNavigationSettingsSaving(false);
    }
  }, [draftGroups, draftLabels, draftOrderIds, toast]);

  useEffect(() => {
    setExpandedSections((prev) => {
      let changed = false;
      const next = { ...prev };
      navigationNodes.flatMap((node) => node.type === 'group' ? node.items : [node.item]).forEach((item) => {
        if (!item.children?.length) {
          return;
        }
        const hasActiveChild = item.children.some((child) => child.id === activeTab);
        const generalPaymentsActive = item.id === 'payments' && activeTab === 'payments';
        const generalTasksActive = item.id === 'tasks' && activeTab === 'tasks';
        if ((hasActiveChild || generalPaymentsActive || generalTasksActive) && !next[item.id]) {
          next[item.id] = true;
          changed = true;
        }
      });
      navigationNodes.filter((node) => node.type === 'group').forEach((group) => {
        const hasActiveItem = group.items.some((item) => item.tab === activeTab || item.children?.some((child) => child.id === activeTab));
        if (hasActiveItem && !next[group.id]) {
          next[group.id] = true;
          changed = true;
        }
      });
      return changed ? next : prev;
    });
  }, [activeTab, navigationNodes]);

  const renderSidebarItem = (item: SidebarItem, nested = false) => {
    const Icon = item.icon;
    const childItems = item.children ?? [];
    const hasChildren = childItems.length > 0;
    const isParentActive = item.tab ? activeTab === item.tab : false;
    const isChildActive = childItems.some((child) => child.id === activeTab);
    const isActive = isParentActive || isChildActive;
    const isExpanded = expandedSections[item.id] ?? false;
    const badgeCount = item.id === 'payments' ? advanceDecisionCount : 0;

    return (
      <li key={item.id} className="space-y-1">
        <div className="relative">
          <a
            href={item.tab ? getTabHref(item.tab) : '#'}
            onClick={(event) => {
              if (item.tab) {
                if (item.id === 'payments' && advanceDecisionCount > 0) {
                  requestPaymentAdvanceReviewFilter('RequiresDecision');
                }
                handleNavLinkClick(event, item.tab);
                return;
              }
              event.preventDefault();
              if (hasChildren) toggleSection(item.id);
            }}
            aria-current={isActive ? 'page' : undefined}
            title={item.label}
            className={[
              'w-full flex items-center rounded-xl transition-all duration-200 select-none group relative',
              collapsed ? 'justify-center p-3' : nested ? 'gap-3 py-2.5 pl-9 pr-4' : 'gap-3 px-4 py-3',
              isActive
                ? 'bg-gradient-to-r from-blue-600 to-blue-700 text-white shadow-lg shadow-blue-900/30'
                : 'text-slate-300 hover:bg-slate-800/50 hover:text-white',
            ].join(' ')}>
            <Icon className={['shrink-0 transition-transform group-hover:scale-110', collapsed ? 'h-6 w-6' : 'h-5 w-5'].join(' ')} />
            {!collapsed ? <span className="truncate text-sm font-medium">{item.label}</span> : null}
            {badgeCount > 0 ? (
              <span
                className={[
                  'inline-flex min-w-5 items-center justify-center rounded-full bg-rose-500 px-1.5 text-[11px] font-bold leading-5 text-white shadow-sm shadow-rose-950/30',
                  collapsed ? 'absolute right-2 top-2' : 'ml-auto mr-7',
                ].join(' ')}
                title={`Платежи, требующие разбора аванса: ${badgeCount}`}
                aria-label={`Платежи, требующие разбора аванса: ${badgeCount}`}
                data-testid="advance-review-nav-badge">
                {formatBadgeCount(badgeCount)}
              </span>
            ) : null}
            {isActive && !collapsed ? <div className="absolute right-0 top-1/2 h-8 w-1 -translate-y-1/2 rounded-l-full bg-white" /> : null}
          </a>
          {!collapsed && hasChildren ? (
            <button
              type="button"
              onClick={(event) => {
                event.stopPropagation();
                toggleSection(item.id);
              }}
              className="absolute right-3 top-1/2 -translate-y-1/2 rounded-full p-1 text-slate-400 transition-colors hover:text-white"
              aria-label={isExpanded ? t('navigationCollapseSection') ?? 'Скрыть подпункты' : t('navigationExpandSection') ?? 'Показать подпункты'}>
              <ChevronDown className={`h-4 w-4 transition-transform ${isExpanded ? 'rotate-180' : ''}`} />
            </button>
          ) : null}
        </div>
        {!collapsed && hasChildren && isExpanded ? (
          <ul className={`mt-1 space-y-1 ${nested ? 'pl-5' : 'pl-2'}`}>
            {childItems.map((child) => {
              const ChildIcon = child.icon;
              const childActive = activeTab === child.id;
              return (
                <li key={child.id}>
                  <a
                    href={getTabHref(child.id)}
                    onClick={(event) => handleNavLinkClick(event, child.id)}
                    aria-current={childActive ? 'page' : undefined}
                    title={child.label}
                    className={[
                      'group relative flex w-full items-center gap-3 rounded-xl py-2 pr-4 text-sm font-medium transition-all duration-200',
                      nested ? 'pl-11' : 'pl-12',
                      childActive ? 'border border-blue-400/40 bg-blue-500/25 text-blue-50 shadow-inner' : 'text-slate-300 hover:bg-slate-800/50 hover:text-white',
                    ].join(' ')}>
                    <ChildIcon className={`h-4 w-4 shrink-0 ${childActive ? 'text-blue-100' : 'text-slate-400 group-hover:text-white'}`} />
                    <span className="truncate">{child.label}</span>
                    {childActive ? <div className="absolute right-1.5 top-1/2 h-5 w-0.5 -translate-y-1/2 rounded-full bg-blue-100/80" /> : null}
                  </a>
                </li>
              );
            })}
          </ul>
        ) : null}
      </li>
    );
  };

  const renderDraftItem = (itemId: string, groupId: string | null, index: number, itemCount: number) => {
    const item = sidebarItems.find((candidate) => candidate.id === itemId);
    if (!item) return null;
    const ItemIcon = item.icon;
    return (
      <div
        key={itemId}
        data-menu-item-id={itemId}
        data-menu-root-entry-id={groupId ? undefined : itemId}
        draggable
        onDragStart={(event) => handleDraftDragStart(itemId, event)}
        onDragEnd={() => {
          setDraggedDraftItemId(null);
          draggedDraftItemIdRef.current = null;
        }}
        onDragOver={(event) => handleDraftDragOver(itemId, event)}
        onDrop={(event) => handleDraftDrop(itemId, groupId, event)}
        className={`flex items-center gap-2 rounded-xl border bg-white p-2 transition ${draggedDraftItemId === itemId ? 'border-blue-400 opacity-60' : 'border-slate-200'}`}>
        <button
          type="button"
          data-menu-drag-handle
          onPointerDown={(event) => handleDraftPointerDown(itemId, event)}
          className="cursor-grab rounded-md p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700 active:cursor-grabbing"
          aria-label={`Перетащить ${item.label}`}
          title="Перетащите в нужное место или группу">
          <GripVertical className="h-4 w-4" />
        </button>
        <ItemIcon className="h-4 w-4 shrink-0 text-slate-500" />
        <input
          value={draftLabels[itemId] ?? item.label}
          maxLength={60}
          onChange={(event) => setDraftLabels((previous) => ({ ...previous, [itemId]: event.target.value }))}
          aria-label={`Название раздела ${item.label}`}
          className="min-w-0 flex-1 rounded-lg border border-slate-300 px-2.5 py-1.5 text-sm text-slate-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        />
        <select
          value={groupId ?? ''}
          onChange={(event) => assignDraftItem(itemId, event.target.value || null)}
          aria-label={`Группа раздела ${item.label}`}
          className="w-36 rounded-lg border border-slate-300 bg-white px-2 py-1.5 text-xs text-slate-700">
          <option value="">Без группы</option>
          {draftGroups.map((group) => <option key={group.id} value={group.id}>{group.name || 'Без названия'}</option>)}
        </select>
        <button type="button" disabled={index === 0} onClick={() => moveDraftItem(itemId, -1)} className="rounded-lg p-1.5 text-slate-600 hover:bg-slate-100 disabled:opacity-30" aria-label={`Поднять ${item.label}`}>
          <ArrowUp className="h-4 w-4" />
        </button>
        <button type="button" disabled={index === itemCount - 1} onClick={() => moveDraftItem(itemId, 1)} className="rounded-lg p-1.5 text-slate-600 hover:bg-slate-100 disabled:opacity-30" aria-label={`Опустить ${item.label}`}>
          <ArrowDown className="h-4 w-4" />
        </button>
      </div>
    );
  };

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
          'pp-navigation-shell fixed left-0 top-0 z-40 h-screen',
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

        <nav className="pp-sidebar-scroll flex-1 overflow-y-auto py-4 px-2" aria-label={t('navigation') ?? 'Навигация'}>
          <ul className="space-y-1.5">
            {navigationNodes.map((node) => {
              if (node.type === 'item') return renderSidebarItem(node.item);
              const isExpanded = expandedSections[node.id] ?? true;
              const isActive = node.items.some((item) => item.tab === activeTab || item.children?.some((child) => child.id === activeTab));
              return (
                <li key={node.id} className="space-y-1" data-testid={`navigation-group-${node.id}`}>
                  <button
                    type="button"
                    onClick={() => toggleSection(node.id)}
                    className={[
                      'relative flex w-full items-center rounded-xl transition-colors',
                      collapsed ? 'justify-center p-3' : 'gap-3 px-4 py-3',
                      isActive ? 'bg-slate-800 text-white' : 'text-slate-200 hover:bg-slate-800/60 hover:text-white',
                    ].join(' ')}
                    aria-expanded={isExpanded}
                    title={node.label}>
                    <Folder className={collapsed ? 'h-6 w-6' : 'h-5 w-5'} />
                    {!collapsed ? <span className="min-w-0 flex-1 truncate text-left text-sm font-semibold">{node.label}</span> : null}
                    {!collapsed ? <ChevronDown className={`h-4 w-4 transition-transform ${isExpanded ? 'rotate-180' : ''}`} /> : null}
                  </button>
                  {!collapsed && isExpanded ? <ul className="space-y-1 border-l border-slate-700/60 pl-1">{node.items.map((item) => renderSidebarItem(item, true))}</ul> : null}
                </li>
              );
            })}
            <li className="border-t border-slate-700/50 pt-2">
              <button
                type="button"
                onClick={(event) => {
                  const trigger = event.currentTarget;
                  if (collapsed) setCollapsed(false);
                  setMoreSectionsOpen((value) => {
                    const next = !value;
                    if (next) {
                      window.setTimeout(() => {
                        if (typeof trigger.scrollIntoView === 'function') {
                          trigger.scrollIntoView({ block: 'start', behavior: 'smooth' });
                        }
                      }, 0);
                    }
                    return next;
                  });
                }}
                aria-expanded={moreSectionsOpen}
                className={[
                  'flex w-full items-center rounded-xl text-slate-300 transition-colors hover:bg-slate-800/60 hover:text-white',
                  collapsed ? 'justify-center p-3' : 'gap-3 px-4 py-3',
                ].join(' ')}
                title="Ещё разделы">
                <LayoutGrid className={collapsed ? 'h-6 w-6' : 'h-5 w-5'} />
                {!collapsed ? <span className="flex-1 text-left text-sm font-medium">Ещё разделы</span> : null}
                {!collapsed ? (
                  <ChevronDown className={`h-4 w-4 transition-transform ${moreSectionsOpen ? 'rotate-180' : ''}`} />
                ) : null}
              </button>

              {!collapsed && moreSectionsOpen ? (
                <div className="mx-1 mt-1 rounded-xl border border-slate-700 bg-slate-900/70 p-2" data-testid="more-sections-panel">
                  <div className="mb-2 px-2 text-xs text-slate-400">Показывать в меню</div>
                  <div className="max-h-64 space-y-0.5 overflow-y-auto">
                    {sidebarItems.map((item) => {
                      const ItemIcon = item.icon;
                      const visible = !hiddenItemIds.includes(item.id);
                      return (
                        <label
                          key={item.id}
                          className="flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1.5 text-sm text-slate-200 hover:bg-slate-800">
                          <SystemCheckbox
                            type="checkbox"
                            checked={visible}
                            onChange={() => toggleItemVisibility(item.id)}
                            className="h-4 w-4 rounded border-slate-500 bg-slate-800 text-blue-600 focus:ring-blue-500"
                          />
                          <ItemIcon className="h-4 w-4 shrink-0 text-slate-400" />
                          <span className="truncate">{item.label}</span>
                        </label>
                      );
                    })}
                  </div>
                  {canManageNavigation ? (
                    <button
                      type="button"
                      onClick={openNavigationSettings}
                      className="mt-2 flex w-full items-center justify-center gap-2 rounded-lg border border-slate-600 px-2 py-2 text-xs font-medium text-slate-200 hover:bg-slate-800">
                      <Settings className="h-4 w-4" />
                      Группы и порядок
                    </button>
                  ) : null}
                </div>
              ) : null}
            </li>
          </ul>
        </nav>

      </aside>

      {navigationSettingsOpen ? (
        <div className="fixed inset-0 z-[70] flex items-center justify-center bg-black/55 p-4" role="dialog" aria-modal="true" aria-labelledby="navigation-settings-title">
          <div className="flex max-h-[85vh] w-full max-w-xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl">
            <div className="flex items-start justify-between border-b border-slate-200 px-4 py-3">
              <div>
                <h2 id="navigation-settings-title" className="text-base font-semibold text-slate-900">Группы, порядок и названия разделов</h2>
                <p className="mt-0.5 text-xs text-slate-500">Создавайте группы и перетаскивайте разделы за маркер. Настройка применяется ко всем пользователям с учётом их прав доступа.</p>
              </div>
              <button type="button" onClick={() => setNavigationSettingsOpen(false)} className="rounded-lg p-1.5 text-slate-500 hover:bg-slate-100" aria-label="Закрыть">
                <X className="h-5 w-5" />
              </button>
            </div>
            <div className="space-y-2 overflow-y-auto p-3" onPointerMove={handleDraftPointerMove}>
              <button type="button" onClick={addDraftGroup} className="mb-1 flex w-full items-center justify-center gap-2 rounded-xl border border-dashed border-blue-300 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-100">
                <FolderPlus className="h-4 w-4" />
                Создать группу
              </button>
              {draftOrderIds.map((entryId, rootIndex) => {
                const group = draftGroups.find((candidate) => candidate.id === entryId);
                if (!group) return renderDraftItem(entryId, null, rootIndex, draftOrderIds.length);
                return (
                  <section
                    key={group.id}
                    data-menu-root-entry-id={group.id}
                    data-menu-group-id={group.id}
                    draggable
                    onDragStart={(event) => handleDraftGroupDragStart(group.id, event)}
                    onDragEnd={() => {
                      setDraggedDraftGroupId(null);
                      draggedDraftGroupIdRef.current = null;
                    }}
                    onDragOver={(event) => event.preventDefault()}
                    onDrop={(event) => handleDraftGroupDrop(group.id, event)}
                    className={`rounded-xl border p-2 transition ${draggedDraftGroupId === group.id ? 'border-blue-500 bg-blue-50/60 opacity-70' : 'border-blue-200 bg-blue-50/40'}`}>
                    <div className="flex items-center gap-2">
                      <button type="button" data-menu-drag-handle onPointerDown={(event) => handleDraftGroupPointerDown(group.id, event)} className="cursor-grab rounded-md p-1 text-blue-400 hover:bg-white hover:text-blue-700 active:cursor-grabbing" aria-label={`Перетащить группу ${group.name}`}>
                        <GripVertical className="h-4 w-4" />
                      </button>
                      <Folder className="h-4 w-4 shrink-0 text-blue-600" />
                      <input
                        value={group.name}
                        maxLength={60}
                        onChange={(event) => setDraftGroups((previous) => previous.map((candidate) => candidate.id === group.id ? { ...candidate, name: event.target.value } : candidate))}
                        aria-label={`Название группы ${group.name}`}
                        className="min-w-0 flex-1 rounded-lg border border-blue-200 bg-white px-2.5 py-1.5 text-sm font-semibold text-slate-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                      />
                      <button type="button" disabled={rootIndex === 0} onClick={() => moveDraftRootEntry(group.id, -1)} className="rounded-lg p-1.5 text-slate-600 hover:bg-white disabled:opacity-30" aria-label={`Поднять группу ${group.name}`}><ArrowUp className="h-4 w-4" /></button>
                      <button type="button" disabled={rootIndex === draftOrderIds.length - 1} onClick={() => moveDraftRootEntry(group.id, 1)} className="rounded-lg p-1.5 text-slate-600 hover:bg-white disabled:opacity-30" aria-label={`Опустить группу ${group.name}`}><ArrowDown className="h-4 w-4" /></button>
                      <button type="button" onClick={() => deleteDraftGroup(group.id)} className="rounded-lg p-1.5 text-rose-600 hover:bg-white" aria-label={`Удалить группу ${group.name}`} title="Разделы вернутся в основное меню"><Trash2 className="h-4 w-4" /></button>
                    </div>
                    <div className="mt-2 space-y-1.5 border-l-2 border-blue-200 pl-3">
                      {group.itemIds.length > 0
                        ? group.itemIds.map((itemId, index) => renderDraftItem(itemId, group.id, index, group.itemIds.length))
                        : <div className="rounded-lg border border-dashed border-blue-200 bg-white/70 px-3 py-3 text-center text-xs text-slate-500">Перетащите сюда раздел или выберите эту группу в списке раздела</div>}
                    </div>
                  </section>
                );
              })}
            </div>
            <div className="border-t border-slate-200 px-4 py-3">
              {navigationSettingsError ? <p className="mb-2 text-sm text-rose-600">{navigationSettingsError}</p> : null}
              <div className="flex justify-end gap-2">
                <button type="button" onClick={() => setNavigationSettingsOpen(false)} className="rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-700 hover:bg-slate-50">Отмена</button>
                <button type="button" disabled={navigationSettingsSaving} onClick={() => void saveNavigationSettings()} className="flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-60">
                  <Save className="h-4 w-4" />
                  {navigationSettingsSaving ? 'Сохранение…' : 'Сохранить'}
                </button>
              </div>
            </div>
          </div>
        </div>
      ) : null}
    </>
  );
}
