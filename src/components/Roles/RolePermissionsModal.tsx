import { useEffect, useMemo, useState } from 'react';
import {
  BarChart3,
  Banknote,
  Briefcase,
  Building2,
  Calculator,
  CalendarDays,
  Cloud,
  FileCheck2,
  FileSignature,
  ListTodo,
  Lock,
  Mail,
  RotateCcw,
  Settings,
  Shield,
  ShieldCheck,
  Unlock,
  Users,
  WalletCards,
  X,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import type { Role } from './Roles';
import {
  getRolePermissions,
  refreshRolePermissions,
  resetRolePermissions,
  setRolePermissions,
  subscribeOnPermissionsChange,
} from '../../services/permissionsService';
import {
  MENU_PERMISSION_KEYS,
  type DataVisibilityScope,
  type MenuPermissionKey,
  type MenuSectionKey,
  type RolePermissions,
} from '../../types/permissions';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { getObjectAuditActorLabel } from '../../utils/objectAudit';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

interface RolePermissionsModalProps {
  role: Role;
  canEdit: boolean;
  onClose: () => void;
  onSaved?: () => void;
}

type SectionConfig = {
  key: MenuSectionKey;
  title: string;
  description: string;
  icon: LucideIcon;
};

type PermissionMeta = {
  key: MenuPermissionKey;
  label: string;
  description: string;
};

type GroupKey = 'core' | 'crm' | 'finance' | 'administration';

type GroupConfig = {
  key: GroupKey;
  title: string;
  description: string;
  sections: MenuSectionKey[];
};

const SECTION_CONFIG: Record<MenuSectionKey, SectionConfig> = {
  calendar: {
    key: 'calendar',
    title: 'Календарь',
    description: 'Права на работу с календарём, событиями и аналитикой.',
    icon: CalendarDays,
  },
  reports: {
    key: 'reports',
    title: 'Отчёты',
    description: 'Доступ к отчётам, обзорам и аналитическим данным.',
    icon: BarChart3,
  },
  tasks: {
    key: 'tasks',
    title: 'Журнал задач',
    description: 'Постановка задач, контроль сроков, исполнителей и результатов.',
    icon: ListTodo,
  },
  calculator: {
    key: 'calculator',
    title: 'Калькулятор',
    description: 'Работа с калькулятором и расчётными сценариями.',
    icon: Calculator,
  },
  legalEntities: {
    key: 'legalEntities',
    title: 'Юридические лица',
    description: 'Просмотр и управление карточками юридических лиц.',
    icon: Building2,
  },
  clients: {
    key: 'clients',
    title: 'Клиенты',
    description: 'Доступ к списку клиентов, карточкам и связанным данным.',
    icon: Users,
  },
  cases: {
    key: 'cases',
    title: 'Дела',
    description: 'Доступ к реестру дел, карточкам дел и юридическому рабочему контуру.',
    icon: Briefcase,
  },
  accounts: {
    key: 'accounts',
    title: 'Счета',
    description: 'Работа со счетами, реестром и связанными операциями.',
    icon: WalletCards,
  },
  acts: {
    key: 'acts',
    title: 'Акты',
    description: 'Создание и сопровождение актов оказанных услуг.',
    icon: FileCheck2,
  },
  payments: {
    key: 'payments',
    title: 'Платежи',
    description: 'Доступ к журналам платежей и денежным операциям.',
    icon: Banknote,
  },
  contracts: {
    key: 'contracts',
    title: 'Договоры',
    description: 'Работа с договорами, связями и карточками документов.',
    icon: FileSignature,
  },
  dictionaries: {
    key: 'dictionaries',
    title: 'Справочники',
    description: 'Настройка и поддержка справочников системы.',
    icon: Settings,
  },
  notifications: {
    key: 'notifications',
    title: 'Уведомления',
    description: 'Настройка отправителей, шаблонов и журнала доставки уведомлений.',
    icon: Mail,
  },
  roles: {
    key: 'roles',
    title: 'Роли и правила',
    description: 'Доступ к списку ролей и настройке правил доступа.',
    icon: Shield,
  },
  audit: {
    key: 'audit',
    title: 'Аудит объектов',
    description: 'Просмотр удалённых объектов и восстановление записей.',
    icon: ShieldCheck,
  },
  yandexDisk: {
    key: 'yandexDisk',
    title: 'Я.Диск',
    description: 'Доступ к подключению Яндекс.Диска и операциям с файлами в облачном хранилище.',
    icon: Cloud,
  },
  s3Storage: {
    key: 's3Storage',
    title: 'S3-хранилища',
    description: 'Подключение S3-совместимых хранилищ, проверка доступа и подготовка резервных копий.',
    icon: Cloud,
  },
};

const GROUPS: GroupConfig[] = [
  {
    key: 'core',
    title: 'Основные разделы',
    description: 'Базовые рабочие модули и аналитика.',
    sections: ['calendar', 'tasks', 'reports', 'calculator'],
  },
  {
    key: 'crm',
    title: 'Клиенты и документы',
    description: 'Карточки клиентов, юрлица, договоры и справочники.',
    sections: ['legalEntities', 'clients', 'cases', 'contracts', 'dictionaries'],
  },
  {
    key: 'finance',
    title: 'Финансовый контур',
    description: 'Счета, акты и платёжные операции.',
    sections: ['accounts', 'acts', 'payments'],
  },
  {
    key: 'administration',
    title: 'Администрирование прав',
    description: 'Просмотр ролей, уведомлений и делегирование управления правилами.',
    sections: ['notifications', 'roles', 'audit', 'yandexDisk', 's3Storage'],
  },
];

const DATA_SCOPE_SECTIONS = new Set<MenuSectionKey>([
  'tasks',
  'legalEntities',
  'clients',
  'cases',
  'accounts',
  'acts',
  'payments',
  'contracts',
]);

const DATA_SCOPE_OPTIONS: Array<{ value: DataVisibilityScope; label: string; description: string }> = [
  {
    value: 'own',
    label: 'Только ответственный',
    description: 'Пользователь видит записи, где он указан ответственным или участником документа.',
  },
  {
    value: 'department',
    label: 'Ответственный и отдел',
    description: 'Пользователь видит свои записи и записи ответственных из своего отдела.',
  },
  {
    value: 'all',
    label: 'Все записи',
    description: 'Раздел открыт без ограничения по ответственному.',
  },
];

const DEFAULT_PERMISSION_META: PermissionMeta[] = [
  {
    key: 'canView',
    label: 'Просмотр',
    description: 'Открывать раздел и просматривать данные.',
  },
  {
    key: 'canCreate',
    label: 'Создание',
    description: 'Создавать новые записи и документы.',
  },
  {
    key: 'canEdit',
    label: 'Редактирование',
    description: 'Изменять существующие записи и настройки раздела.',
  },
  {
    key: 'canDelete',
    label: 'Удаление',
    description: 'Удалять записи и очищать данные в разделе.',
  },
  {
    key: 'canExport',
    label: 'Экспорт',
    description: 'Выгружать данные и использовать экспортные действия.',
  },
];

const STORAGE_PERMISSION_META: PermissionMeta[] = [
  {
    key: 'canView',
    label: 'Просмотр',
    description: 'Открывать раздел в настройках, видеть подключения, файлы, папки и журналы операций.',
  },
  {
    key: 'canCreate',
    label: 'Создание, загрузка и копирование',
    description: 'Добавлять подключения, создавать папки, загружать файлы и создавать копии объектов.',
  },
  {
    key: 'canEdit',
    label: 'Редактирование и перемещение',
    description: 'Менять настройки подключений, редактировать текстовые файлы и перемещать объекты.',
  },
  {
    key: 'canDelete',
    label: 'Удаление',
    description: 'Удалять подключения, файлы и папки в хранилище.',
  },
  {
    key: 'canExport',
    label: 'Скачивание',
    description: 'Скачивать файлы и использовать действия выгрузки из хранилища.',
  },
];

const ROLES_PERMISSION_META: PermissionMeta[] = [
  {
    key: 'canView',
    label: 'Просмотр ролей',
    description: 'Открывать список ролей и смотреть состав правил.',
  },
  {
    key: 'canCreate',
    label: 'Создание и копирование',
    description: 'Создавать новые роли и копировать существующие.',
  },
  {
    key: 'canEdit',
    label: 'Настройка правил',
    description: 'Менять имя роли, описание и её правила доступа.',
  },
  {
    key: 'canDelete',
    label: 'Удаление ролей',
    description: 'Удалять пользовательские роли, если они никому не назначены.',
  },
];

const AUDIT_PERMISSION_META: PermissionMeta[] = [
  {
    key: 'canView',
    label: 'Просмотр корзины',
    description: 'Открывать удалённые объекты и читать аудит по ним.',
  },
  {
    key: 'canEdit',
    label: 'Восстановление',
    description: 'Возвращать удалённые объекты в рабочие списки.',
  },
];

const NOTIFICATION_PERMISSION_META: PermissionMeta[] = [
  {
    key: 'canView',
    label: 'Просмотр настроек',
    description: 'Открывать отправителей, шаблоны и журнал доставки уведомлений.',
  },
  {
    key: 'canEdit',
    label: 'Настройка уведомлений',
    description: 'Менять отправителей, шаблоны писем и выполнять повторные отправки.',
  },
];

type PermissionToggleProps = {
  label: string;
  description: string;
  checked: boolean;
  onChange: () => void;
  disabled: boolean;
};

function PermissionToggle({
  label,
  description,
  checked,
  onChange,
  disabled,
}: PermissionToggleProps) {
  return (
    <label
      className={`flex min-h-[108px] items-start gap-4 rounded-2xl border px-4 py-4 transition-colors ${
        checked
          ? 'border-blue-200 bg-blue-50/60'
          : 'border-slate-200 bg-white'
      } ${
        disabled
          ? 'cursor-not-allowed opacity-60'
          : 'cursor-pointer hover:border-slate-300 hover:bg-slate-50'
      }`}
    >
      <input
        type="checkbox"
        className="mt-1 h-4 w-4 shrink-0 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
        checked={checked}
        onChange={onChange}
        disabled={disabled}
      />
      <div className="min-w-0">
        <div className="text-base font-semibold text-slate-900">{label}</div>
        <div className="mt-2 text-sm leading-6 text-slate-500">{description}</div>
      </div>
    </label>
  );
}

function formatDateTime(value?: string | null, timeZoneId?: string | null) {
  if (!value) return '—';

  return formatDateTimeInTimeZone(value, timeZoneId) || value;
}

function formatAuditUserName(value?: string | null) {
  return value?.trim() ? getObjectAuditActorLabel(value) : 'После создания права не менялись';
}

function getOpenedSectionsCount(permissions: RolePermissions, sections: MenuSectionKey[]) {
  return sections.filter((sectionKey) => permissions[sectionKey].canView).length;
}

function getEnabledPermissionCount(sectionKey: MenuSectionKey, permissions: RolePermissions) {
  const section = permissions[sectionKey];

  if (sectionKey === 'audit' || sectionKey === 'notifications') {
    let count = 0;
    if (section.canView) {
      count += 1;
    }
    if (section.canEdit) {
      count += 1;
    }
    return count;
  }

  let count = MENU_PERMISSION_KEYS.filter((permissionKey) => section[permissionKey]).length;

  if (sectionKey === 'calendar' && permissions.calendar.canViewAnalytics) {
    count += 1;
  }

  return count;
}

function getTotalPermissionCount(sectionKey: MenuSectionKey) {
  if (sectionKey === 'calendar') {
    return MENU_PERMISSION_KEYS.length + 1;
  }

  if (sectionKey === 'audit') {
    return AUDIT_PERMISSION_META.length;
  }

  if (sectionKey === 'notifications') {
    return NOTIFICATION_PERMISSION_META.length;
  }

  return MENU_PERMISSION_KEYS.length;
}

function hasDataScope(sectionKey: MenuSectionKey) {
  return DATA_SCOPE_SECTIONS.has(sectionKey);
}

function normalizeDataScope(value?: DataVisibilityScope | null): DataVisibilityScope {
  return value === 'department' || value === 'all' ? value : 'own';
}

export function RolePermissionsModal({
  role,
  canEdit,
  onClose,
  onSaved,
}: RolePermissionsModalProps) {
  const timeZoneId = useUserTimeZone();
  const isProtectedRole = Boolean(role.isSystemRole);
  const isReadOnly = isProtectedRole || !canEdit;

  const [permissions, setPermissionsState] = useState<RolePermissions>(() =>
    getRolePermissions(role.id),
  );
  const [saving, setSaving] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [activeGroup, setActiveGroup] = useState<GroupKey>('core');

  useEffect(() => {
    setPermissionsState(getRolePermissions(role.id));
    setErrorMessage(null);
    setActiveGroup('core');
  }, [role.id]);

  useEffect(() => {
    const unsubscribe = subscribeOnPermissionsChange((changedRoleId) => {
      if (changedRoleId === undefined || changedRoleId === role.id) {
        setPermissionsState(getRolePermissions(role.id));
      }
    });

    return unsubscribe;
  }, [role.id]);

  const activeGroupConfig = useMemo(
    () => GROUPS.find((group) => group.key === activeGroup) ?? GROUPS[0],
    [activeGroup],
  );

  const openedSectionsCount = useMemo(
    () =>
      (Object.keys(permissions) as MenuSectionKey[]).filter((sectionKey) => permissions[sectionKey].canView)
        .length,
    [permissions],
  );

  const updateSection = <K extends MenuSectionKey>(
    key: K,
    updater: (section: RolePermissions[K]) => RolePermissions[K],
  ) => {
    setPermissionsState((prev) => ({
      ...prev,
      [key]: updater(prev[key]),
    }));
  };

  const toggleBasePermission = (sectionKey: MenuSectionKey, permissionKey: MenuPermissionKey) => {
    updateSection(sectionKey, (section) => {
      const nextValue = !section[permissionKey];
      const updated = {
        ...section,
        [permissionKey]: nextValue,
      } as typeof section;

      if (permissionKey === 'canView' && !nextValue) {
        MENU_PERMISSION_KEYS.forEach((key) => {
          if (key !== 'canView') {
            updated[key] = false;
          }
        });

        if (sectionKey === 'calendar') {
          (updated as RolePermissions['calendar']).canViewAnalytics = false;
        }
      }

      return updated;
    });
  };

  const toggleCalendarAnalytics = () => {
    updateSection('calendar', (section) => ({
      ...section,
      canViewAnalytics: !section.canViewAnalytics,
    }));
  };

  const setSectionViewScope = (sectionKey: MenuSectionKey, viewScope: DataVisibilityScope) => {
    if (!hasDataScope(sectionKey)) {
      return;
    }

    updateSection(sectionKey, (section) => ({
      ...section,
      viewScope,
    }));
  };

  const setSectionAll = (sectionKey: MenuSectionKey, value: boolean) => {
    updateSection(sectionKey, (section) => {
      if (sectionKey === 'audit' || sectionKey === 'notifications') {
        return {
          ...section,
          canView: value,
          canCreate: false,
          canEdit: value,
          canDelete: false,
          canExport: false,
        } as typeof section;
      }

      const next = {
        ...section,
        canView: value,
        canCreate: value,
        canEdit: value,
        canDelete: value,
        canExport: sectionKey === 'roles' ? false : value,
      } as typeof section;

      if (sectionKey === 'calendar') {
        (next as RolePermissions['calendar']).canViewAnalytics = value;
      }

      return next;
    });
  };

  const setAllSections = (value: boolean) => {
    setPermissionsState((prev) => {
      const next = { ...prev };

      for (const key of Object.keys(prev) as MenuSectionKey[]) {
        if (key === 'audit') {
          next.audit = {
            ...prev.audit,
            canView: value,
            canCreate: false,
            canEdit: value,
            canDelete: false,
            canExport: false,
          };
          continue;
        }

        if (key === 'notifications') {
          next.notifications = {
            ...prev.notifications,
            canView: value,
            canCreate: false,
            canEdit: value,
            canDelete: false,
            canExport: false,
          };
          continue;
        }

        if (key === 'calendar') {
          next.calendar = {
            ...prev.calendar,
            canView: value,
            canCreate: value,
            canEdit: value,
            canDelete: value,
            canExport: value,
            canViewAnalytics: value,
          };
          continue;
        }

        next[key] = {
          ...prev[key],
          canView: value,
          canCreate: value,
          canEdit: value,
          canDelete: value,
          canExport: key === 'roles' ? false : value,
        } as RolePermissions[typeof key];
      }

      return next;
    });
  };

  const handleSave = async () => {
    setSaving(true);
    setErrorMessage(null);

    try {
      await setRolePermissions(role.id, permissions);
      onSaved?.();
      onClose();
    } catch (error) {
      setErrorMessage(error instanceof Error ? error.message : 'Не удалось сохранить права');
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    setResetting(true);
    setErrorMessage(null);

    try {
      await resetRolePermissions(role.id);
      await refreshRolePermissions(role.id);
      setPermissionsState(getRolePermissions(role.id));
      onSaved?.();
    } catch (error) {
      setErrorMessage(
        error instanceof Error
          ? error.message
          : 'Не удалось сбросить права к стандартным значениям',
      );
    } finally {
      setResetting(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/55 p-4 backdrop-blur-[2px]">
      <div className="flex h-[92vh] w-full max-w-[1260px] flex-col overflow-hidden rounded-[30px] bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-slate-200 px-8 py-6">
          <div className="flex items-start gap-4">
            <div className="rounded-2xl bg-slate-100 p-3 text-slate-700">
              <ShieldCheck className="h-6 w-6" />
            </div>

            <div>
              <div className="flex flex-wrap items-center gap-2">
                <h2 className="text-[30px] font-semibold leading-none text-slate-900">
                  Права роли «{role.name}»
                </h2>
                {isProtectedRole ? (
                  <span className="inline-flex items-center rounded-full border border-amber-200 bg-amber-50 px-2.5 py-1 text-xs font-medium text-amber-700">
                    Системная роль
                  </span>
                ) : null}
              </div>

              <p className="mt-2 text-sm text-slate-500">
                Детальная настройка доступа по разделам. Каждая группа прав редактируется отдельно.
              </p>
            </div>
          </div>

          <button
            type="button"
            onClick={onClose}
            className="rounded-xl p-2 text-slate-400 transition-colors hover:bg-slate-100 hover:text-slate-600"
            aria-label="Закрыть"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="grid min-h-0 flex-1 lg:grid-cols-[320px_minmax(0,1fr)]">
          <aside className="min-h-0 border-r border-slate-200 bg-slate-50/80">
            <div className="flex h-full flex-col gap-5 overflow-y-auto p-5">
              <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                  Глобальные действия
                </div>

                <div className="mt-4 grid gap-2">
                  <button
                    type="button"
                    onClick={() => setAllSections(true)}
                    disabled={isReadOnly || saving || resetting}
                    className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-emerald-200 px-4 py-3 text-sm font-semibold text-emerald-700 transition-colors hover:bg-emerald-50 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    <Unlock className="h-4 w-4" />
                    Принять всё
                  </button>
                  <button
                    type="button"
                    onClick={() => setAllSections(false)}
                    disabled={isReadOnly || saving || resetting}
                    className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-slate-200 px-4 py-3 text-sm font-semibold text-slate-600 transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    <Lock className="h-4 w-4" />
                    Отклонить всё
                  </button>
                </div>
              </div>

              {!isProtectedRole ? (
                <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                  <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                    Сервисное действие
                  </div>

                  <button
                    type="button"
                    onClick={handleReset}
                    disabled={isReadOnly || saving || resetting}
                    className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-xl border border-slate-300 px-4 py-3 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    <RotateCcw className="h-4 w-4" />
                    Сбросить к стандарту
                  </button>
                </div>
              ) : null}

              <div className="space-y-3">
                {GROUPS.map((group) => {
                  const openedInGroup = getOpenedSectionsCount(permissions, group.sections);
                  const isActive = group.key === activeGroup;

                  return (
                    <button
                      key={group.key}
                      type="button"
                      onClick={() => setActiveGroup(group.key)}
                      className={`w-full rounded-2xl border p-4 text-left transition-colors ${
                        isActive
                          ? 'border-blue-200 bg-blue-50 text-blue-900 shadow-sm'
                          : 'border-slate-200 bg-white text-slate-700 hover:border-slate-300 hover:bg-slate-50'
                      }`}
                    >
                      <div className="text-lg font-semibold">{group.title}</div>
                      <div className="mt-1 text-sm leading-6 text-slate-500">{group.description}</div>
                      <div className="mt-4 text-sm font-medium text-slate-500">
                        Разделов открыто: {openedInGroup} из {group.sections.length}
                      </div>
                    </button>
                  );
                })}
              </div>
            </div>
          </aside>

          <div className="min-h-0 overflow-y-auto bg-white">
            <div className="px-6 py-5">
              {errorMessage ? (
                <div className="mb-5 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                  {errorMessage}
                </div>
              ) : null}

              <div className="grid gap-3 xl:grid-cols-3">
                <div className="rounded-2xl border border-slate-200 bg-slate-50/80 px-4 py-4">
                  <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                    Последнее изменение
                  </div>
                  <div className="mt-3 text-xl font-semibold text-slate-900">
                    {role.permissionsUpdatedAt ? formatDateTime(role.permissionsUpdatedAt, timeZoneId) : '—'}
                  </div>
                  <div className="mt-2 text-sm leading-6 text-slate-500">
                    {formatAuditUserName(role.permissionsUpdatedByUserName)}
                  </div>
                </div>

                <div className="rounded-2xl border border-slate-200 bg-slate-50/80 px-4 py-4">
                  <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                    Сводка
                  </div>
                  <div className="mt-3 text-xl font-semibold text-slate-900">
                    {openedSectionsCount} из {role.totalSectionsCount ?? Object.keys(permissions).length} разделов открыто
                  </div>
                  <div className="mt-2 text-sm leading-6 text-slate-500">
                    {(role.assignedUsersCount ?? 0) > 0
                      ? `Роль назначена пользователям: ${role.assignedUsersCount}`
                      : 'Роль пока никому не назначена'}
                  </div>
                </div>

                <div className="rounded-2xl border border-slate-200 bg-slate-50/80 px-4 py-4">
                  <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
                    Режим
                  </div>
                  <div className="mt-3 text-xl font-semibold text-slate-900">
                    {isProtectedRole
                      ? 'Только просмотр'
                      : isReadOnly
                        ? 'Редактирование запрещено'
                        : 'Редактирование доступно'}
                  </div>
                  <div className="mt-2 text-sm leading-6 text-slate-500">
                    {isProtectedRole
                      ? 'Стандартная роль администратора защищена от изменения и удаления.'
                      : isReadOnly
                        ? 'Текущий пользователь может только просматривать права.'
                        : 'Можно применять массовые действия по секциям и по всей роли.'}
                  </div>
                </div>
              </div>

              <div className="mt-8">
                <h3 className="text-[28px] font-semibold text-slate-900">
                  {activeGroupConfig.title}
                </h3>
                <p className="mt-2 text-sm text-slate-500">{activeGroupConfig.description}</p>
              </div>

              <div className="mt-6 space-y-6 pb-6">
                {activeGroupConfig.sections.map((sectionKey) => {
                  const sectionConfig = SECTION_CONFIG[sectionKey];
                  const section = permissions[sectionKey];
                  const permissionMeta =
                    sectionKey === 'roles'
                      ? ROLES_PERMISSION_META
                      : sectionKey === 'audit'
                        ? AUDIT_PERMISSION_META
                        : sectionKey === 'notifications'
                          ? NOTIFICATION_PERMISSION_META
                          : sectionKey === 'yandexDisk' || sectionKey === 's3Storage'
                            ? STORAGE_PERMISSION_META
                            : DEFAULT_PERMISSION_META;
                  const Icon = sectionConfig.icon;
                  const enabledPermissionCount = getEnabledPermissionCount(sectionKey, permissions);
                  const totalPermissionCount = getTotalPermissionCount(sectionKey);
                  const sectionScope = normalizeDataScope(section.viewScope);
                  const sectionScopeDescription =
                    DATA_SCOPE_OPTIONS.find((option) => option.value === sectionScope)?.description ?? '';

                  return (
                    <section
                      key={sectionKey}
                      className="overflow-hidden rounded-[24px] border border-slate-200 bg-white shadow-sm"
                    >
                      <div className="flex flex-col gap-4 border-b border-slate-200 px-5 py-5 xl:flex-row xl:items-center xl:justify-between">
                        <div className="flex items-start gap-4">
                          <div className="rounded-2xl bg-slate-100 p-3 text-slate-600">
                            <Icon className="h-5 w-5" />
                          </div>

                          <div>
                            <div className="flex flex-wrap items-center gap-3">
                              <h4 className="text-2xl font-semibold text-slate-900">
                                {sectionConfig.title}
                              </h4>
                              <span className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-medium text-slate-500">
                                {enabledPermissionCount} из {totalPermissionCount} операций
                              </span>
                            </div>
                            <p className="mt-2 text-sm leading-6 text-slate-500">
                              {sectionConfig.description}
                            </p>
                          </div>
                        </div>

                        <div className="flex flex-wrap gap-2">
                          <button
                            type="button"
                            onClick={() => setSectionAll(sectionKey, true)}
                            disabled={isReadOnly || saving || resetting}
                            className="inline-flex items-center gap-2 rounded-xl border border-emerald-200 px-4 py-2.5 text-sm font-semibold text-emerald-700 transition-colors hover:bg-emerald-50 disabled:cursor-not-allowed disabled:opacity-50"
                          >
                            <Unlock className="h-4 w-4" />
                            Принять всё
                          </button>
                          <button
                            type="button"
                            onClick={() => setSectionAll(sectionKey, false)}
                            disabled={isReadOnly || saving || resetting}
                            className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50"
                          >
                            <Lock className="h-4 w-4" />
                            Отклонить всё
                          </button>
                        </div>
                      </div>

                      <div className="grid gap-4 px-5 py-5 xl:grid-cols-2">
                        {permissionMeta.map((permission) => (
                          <PermissionToggle
                            key={`${sectionKey}-${permission.key}`}
                            label={permission.label}
                            description={permission.description}
                            checked={section[permission.key]}
                            onChange={() => toggleBasePermission(sectionKey, permission.key)}
                            disabled={
                              isReadOnly
                              || saving
                              || resetting
                              || (permission.key !== 'canView' && !section.canView)
                            }
                          />
                        ))}

                        {sectionKey === 'calendar' ? (
                          <PermissionToggle
                            label="Аналитика календаря"
                            description="Просмотр аналитических блоков и расширенных представлений календаря."
                            checked={permissions.calendar.canViewAnalytics}
                            onChange={toggleCalendarAnalytics}
                            disabled={
                              isReadOnly
                              || saving
                              || resetting
                              || !permissions.calendar.canView
                            }
                          />
                        ) : null}

                        {hasDataScope(sectionKey) ? (
                          <div className="rounded-2xl border border-slate-200 bg-slate-50/80 px-4 py-4 xl:col-span-2">
                            <label className="block space-y-2">
                              <span className="text-sm font-semibold text-slate-700">Область видимости данных</span>
                              <select
                                value={sectionScope}
                                onChange={(event) =>
                                  setSectionViewScope(sectionKey, event.target.value as DataVisibilityScope)
                                }
                                disabled={isReadOnly || saving || resetting || !section.canView}
                                className="w-full rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-sm text-slate-900 disabled:bg-slate-100 disabled:text-slate-500"
                              >
                                {DATA_SCOPE_OPTIONS.map((option) => (
                                  <option key={option.value} value={option.value}>
                                    {option.label}
                                  </option>
                                ))}
                              </select>
                            </label>
                            <div className="mt-2 text-sm leading-6 text-slate-500">
                              {section.canView
                                ? sectionScopeDescription
                                : 'Сначала откройте просмотр раздела, затем выберите область видимости.'}
                            </div>
                          </div>
                        ) : null}
                      </div>
                    </section>
                  );
                })}
              </div>
            </div>
          </div>
        </div>

        <div className="border-t border-slate-200 bg-white px-6 py-4 shadow-[0_-10px_24px_rgba(15,23,42,0.04)]">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
            <div className="text-sm text-slate-500">
              {isReadOnly
                ? 'Раздел открыт в режиме просмотра.'
                : 'Сохранение применит изменения ко всем пользователям с этой ролью.'}
            </div>

            <div className="flex flex-col gap-3 sm:flex-row">
              <button
                type="button"
                onClick={onClose}
                className="inline-flex items-center justify-center rounded-xl border border-slate-300 px-5 py-3 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50"
              >
                Закрыть
              </button>

              {!isReadOnly ? (
                <button
                  type="button"
                  onClick={handleSave}
                  disabled={saving || resetting}
                  className="inline-flex items-center justify-center gap-2 rounded-xl bg-slate-900 px-5 py-3 text-sm font-semibold text-white transition-colors hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <ShieldCheck className={`h-4 w-4 ${saving ? 'animate-spin' : ''}`} />
                  {saving ? 'Сохранение...' : 'Сохранить права'}
                </button>
              ) : null}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
