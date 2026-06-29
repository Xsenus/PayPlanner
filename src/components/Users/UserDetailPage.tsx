import { useCallback, useEffect, useMemo, useState } from 'react';
import { ArrowLeft, Building2, Loader2, Plus, Save, ShieldCheck, Trash2, UserRound } from 'lucide-react';

import { authService, type ContractorBalance, type ContractorBalanceItem, type Role, type UpdateUserDto, type User } from '../../services/authService';
import { apiService, type MainCompanySettingsDto } from '../../services/api';
import type { BaseDictItem } from '../../types/dictionaries';
import { useAuth } from '../../contexts/AuthContext';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';
import { LookupSelect, type LookupSelectOption } from '../ui/LookupSelect';
import {
  DEFAULT_TASK_NOTIFICATION_DEFAULTS,
  parseTaskNotificationDefaults,
  serializeTaskNotificationDefaults,
  TASK_NOTIFICATION_DEFAULTS_SETTING_KEY,
  type TaskNotificationDefaults,
} from '../../utils/taskNotificationDefaults';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_OPTIONS, USER_TIME_ZONE_SETTING_KEY } from '../../utils/timeZoneOptions';
import { TaskNotificationDefaultsPanel } from '../Tasks/TaskNotificationDefaultsPanel';

type UserDetailSection = 'main' | 'contacts' | 'employment' | 'contractorBalance' | 'notifications' | 'access';

type UserDetailPageProps = {
  userId: number | null;
  onBack: () => void;
  onSaved: (user: User) => void;
  onOpenPayment?: (paymentId: number) => void;
};

type FormState = {
  email: string;
  password: string;
  fullName: string;
  firstName: string;
  lastName: string;
  middleName: string;
  dateOfBirth: string;
  phoneNumber: string;
  whatsApp: string;
  telegram: string;
  instagram: string;
  messenger: string;
  viber: string;
  isEmployee: boolean;
  isExternalContractor: boolean;
  departmentId: string;
  department: string;
  requiresFinancialActionReason: boolean;
  employmentStartDate: string;
  employmentEndDate: string;
  roleId: number;
  isActive: boolean;
  companyIds: number[];
};

type ContractorBalanceGroup = {
  key: string;
  clientLabel: string;
  caseLabel: string;
  outstandingAmount: number;
  paidAmount: number;
  amount: number;
  items: ContractorBalanceItem[];
};

const EMPTY_FORM: FormState = {
  email: '',
  password: '',
  fullName: '',
  firstName: '',
  lastName: '',
  middleName: '',
  dateOfBirth: '',
  phoneNumber: '',
  whatsApp: '',
  telegram: '',
  instagram: '',
  messenger: '',
  viber: '',
  isEmployee: false,
  isExternalContractor: false,
  departmentId: '',
  department: '',
  requiresFinancialActionReason: false,
  employmentStartDate: '',
  employmentEndDate: '',
  roleId: 0,
  isActive: true,
  companyIds: [],
};

const EMPTY_CONTRACTOR_BALANCE: ContractorBalance = {
  owedToMe: 0,
  owedByMe: 0,
  paidToMe: 0,
  paidByMe: 0,
  queue: [],
  paid: [],
};

const SECTION_LABELS: Array<{ id: UserDetailSection; label: string; description: string }> = [
  { id: 'main', label: 'Основное', description: 'Имя, email и базовая информация профиля.' },
  { id: 'contacts', label: 'Контакты', description: 'Телефон, мессенджеры и внешние ссылки.' },
  { id: 'employment', label: 'Работа в системе', description: 'Роль, активность и доступ к компаниям.' },
  { id: 'contractorBalance', label: 'Расчеты', description: 'Задолженность и выплаты привлеченному исполнителю.' },
  { id: 'notifications', label: 'Напоминания задач', description: 'Значения по умолчанию для новых задач пользователя.' },
  { id: 'access', label: 'Способы входа', description: 'Привязанные внешние способы авторизации.' },
];

function toDateInputValue(value?: string | null) {
  return value ? value.split('T')[0] : '';
}

function getInitials(value?: string | null) {
  return (value ?? 'U')
    .split(' ')
    .map((part) => part.trim()[0])
    .filter(Boolean)
    .slice(0, 2)
    .join('')
    .toUpperCase();
}

function providerLabel(provider?: string | null) {
  if (provider === 'vk') return 'VK ID';
  if (provider === 'yandex') return 'Яндекс ID';
  if (provider === 'telegram') return 'Telegram';
  return 'Google';
}

function formatContractorPaymentStatus(status?: string | null) {
  switch (status) {
    case 'Completed':
      return 'Оплачено';
    case 'Pending':
      return 'Ожидает оплаты';
    case 'Overdue':
      return 'Просрочено';
    case 'Processing':
      return 'В работе';
    case 'Cancelled':
      return 'Отменено';
    case 'WaitingForResult':
      return 'После завершения задачи';
    default:
      return status || 'Без статуса';
  }
}

function contractorPaymentStatusClassName(status?: string | null) {
  switch (status) {
    case 'Completed':
      return 'bg-emerald-100 text-emerald-700';
    case 'Cancelled':
      return 'bg-slate-200 text-slate-700';
    case 'Overdue':
      return 'bg-rose-100 text-rose-700';
    case 'WaitingForResult':
      return 'bg-violet-100 text-violet-700';
    default:
      return 'bg-amber-100 text-amber-700';
  }
}

function formatOptionalDate(value?: string | null) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat('ru-RU').format(date);
}

function formatMissingContractorPaymentHint(status?: string | null) {
  return status === 'WaitingForResult'
    ? 'Платеж появится в календаре после завершения задачи'
    : 'Платеж еще не создан';
}

function buildCompanyOption(company: MainCompanySettingsDto['companies'][number]): LookupSelectOption {
  const description = [company.fullName?.trim() || null, company.inn ? `ИНН ${company.inn}` : null, company.kpp ? `КПП ${company.kpp}` : null]
    .filter(Boolean)
    .join(' • ');

  return {
    value: String(company.id),
    label: company.shortName?.trim() || company.fullName?.trim() || `Компания #${company.id}`,
    description: description || undefined,
    keywords: [company.shortName, company.fullName ?? '', company.inn ?? '', company.kpp ?? ''].filter(Boolean),
  };
}

function buildForm(user?: User | null): FormState {
  if (!user) {
    return EMPTY_FORM;
  }

  return {
    email: user.email ?? '',
    password: '',
    fullName: user.fullName ?? '',
    firstName: user.firstName ?? '',
    lastName: user.lastName ?? '',
    middleName: user.middleName ?? '',
    dateOfBirth: toDateInputValue(user.dateOfBirth),
    phoneNumber: user.phoneNumber ?? '',
    whatsApp: user.whatsApp ?? '',
    telegram: user.telegram ?? '',
    instagram: user.instagram ?? '',
    messenger: user.messenger ?? '',
    viber: user.viber ?? '',
    isEmployee: Boolean(user.isEmployee),
    isExternalContractor: Boolean(user.isExternalContractor),
    departmentId: user.departmentId ? String(user.departmentId) : '',
    department: user.department ?? '',
    requiresFinancialActionReason: Boolean(user.requiresFinancialActionReason),
    employmentStartDate: toDateInputValue(user.employmentStartDate),
    employmentEndDate: toDateInputValue(user.employmentEndDate),
    roleId: user.role.id,
    isActive: user.isActive,
    companyIds: [...(user.companyIds ?? user.companies?.map((item) => item.id) ?? [])].sort((a, b) => a - b),
  };
}

function buildUpdatePayload(form: FormState): UpdateUserDto {
  return {
    email: form.email.trim() || undefined,
    fullName: form.fullName.trim() || undefined,
    firstName: form.firstName.trim() || undefined,
    lastName: form.lastName.trim() || undefined,
    middleName: form.middleName.trim() || undefined,
    dateOfBirth: form.dateOfBirth || undefined,
    phoneNumber: form.phoneNumber.trim() || undefined,
    whatsApp: form.whatsApp.trim() || undefined,
    telegram: form.telegram.trim() || undefined,
    instagram: form.instagram.trim() || undefined,
    messenger: form.messenger.trim() || undefined,
    viber: form.viber.trim() || undefined,
    isEmployee: form.isEmployee,
    isExternalContractor: form.isExternalContractor,
    departmentId: form.departmentId ? Number(form.departmentId) : null,
    department: form.departmentId ? undefined : form.department.trim(),
    requiresFinancialActionReason: form.requiresFinancialActionReason,
    employmentStartDate: form.employmentStartDate || undefined,
    employmentEndDate: form.employmentEndDate || undefined,
    roleId: form.roleId || undefined,
    isActive: form.isActive,
    companyIds: form.companyIds,
  };
}

function TextField(props: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  type?: string;
  disabled?: boolean;
  className?: string;
}) {
  return (
    <label className={`space-y-2 ${props.className ?? ''}`}>
      <span className="text-sm font-medium text-slate-700">{props.label}</span>
      <input
        type={props.type ?? 'text'}
        value={props.value}
        onChange={(event) => props.onChange(event.target.value)}
        disabled={props.disabled}
        className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
      />
    </label>
  );
}

export function UserDetailPage({ userId, onBack, onSaved, onOpenPayment }: UserDetailPageProps) {
  const { user: currentUser } = useAuth();
  const toast = useToast();
  const isCreateMode = userId == null;

  const [activeSection, setActiveSection] = useState<UserDetailSection>('main');
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [roles, setRoles] = useState<Role[]>([]);
  const [user, setUser] = useState<User | null>(null);
  const [contractorBalance, setContractorBalance] = useState<ContractorBalance>(EMPTY_CONTRACTOR_BALANCE);
  const [contractorBalanceError, setContractorBalanceError] = useState<string | null>(null);
  const [mainCompanySettings, setMainCompanySettings] = useState<MainCompanySettingsDto | null>(null);
  const [departments, setDepartments] = useState<BaseDictItem[]>([]);
  const [timeZoneId, setTimeZoneId] = useState(() => normalizeUserTimeZoneId());
  const [taskNotificationDefaults, setTaskNotificationDefaults] = useState<TaskNotificationDefaults>(DEFAULT_TASK_NOTIFICATION_DEFAULTS);
  const [selectedCompanyToAddId, setSelectedCompanyToAddId] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [savingTaskNotificationDefaults, setSavingTaskNotificationDefaults] = useState(false);
  const [avatarLoadFailed, setAvatarLoadFailed] = useState(false);

  const loadData = useCallback(async () => {
    const [nextRoles, nextUser, nextContractorBalance, nextMainCompanySettings, nextDepartments, nextTaskNotificationDefaults, nextTimeZone] = await Promise.all([
      authService.getRoles(),
      isCreateMode ? Promise.resolve<User | null>(null) : authService.getUser(userId),
      isCreateMode
        ? Promise.resolve(EMPTY_CONTRACTOR_BALANCE)
        : authService.getUserContractorBalance(userId)
            .then((balance) => {
              setContractorBalanceError(null);
              return balance;
            })
            .catch((error) => {
              setContractorBalanceError(extractDisplayErrorMessage(error, 'Не удалось загрузить расчеты с исполнителем.'));
              return EMPTY_CONTRACTOR_BALANCE;
            }),
      apiService.getMainCompanySettings(),
      apiService.getDict('departments'),
      isCreateMode ? Promise.resolve(null) : apiService.getUserSettingForUser(userId, TASK_NOTIFICATION_DEFAULTS_SETTING_KEY).catch(() => null),
      isCreateMode ? Promise.resolve(null) : apiService.getUserSettingForUser(userId, USER_TIME_ZONE_SETTING_KEY).catch(() => null),
    ]);

    setRoles(nextRoles);
    setUser(nextUser);
    setContractorBalance(nextContractorBalance);
    setMainCompanySettings(nextMainCompanySettings);
    setDepartments(nextDepartments);
    setTaskNotificationDefaults(parseTaskNotificationDefaults(nextTaskNotificationDefaults?.value));
    setTimeZoneId(normalizeUserTimeZoneId(nextTimeZone?.value));

    const nextForm = buildForm(nextUser);
    if (!nextForm.departmentId && nextForm.department) {
      const departmentName = nextForm.department.trim().toLowerCase();
      const matchedDepartment = nextDepartments.find((item) => item.name.trim().toLowerCase() === departmentName);
      if (matchedDepartment) {
        nextForm.departmentId = String(matchedDepartment.id);
        nextForm.department = matchedDepartment.name;
      }
    }
    if (!nextUser && nextRoles.length > 0) {
      nextForm.roleId = nextRoles.find((role) => role.name.toLowerCase() === 'user')?.id ?? nextRoles[0].id;
    }

    setForm(nextForm);
  }, [isCreateMode, userId]);

  useEffect(() => {
    setLoading(true);
    void loadData()
      .catch((error) => toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить карточку пользователя.')))
      .finally(() => setLoading(false));
  }, [loadData, toast]);

  useEffect(() => {
    setAvatarLoadFailed(false);
  }, [user?.photoUrl]);

  const updateForm = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm((current) => ({ ...current, [key]: value }));

  const mainCompanies = useMemo(() => mainCompanySettings?.companies ?? [], [mainCompanySettings]);
  const companiesById = useMemo(() => new Map(mainCompanies.map((company) => [company.id, company])), [mainCompanies]);
  const assignedCompanies = useMemo(
    () => form.companyIds.map((companyId) => companiesById.get(companyId)).filter(Boolean) as MainCompanySettingsDto['companies'],
    [companiesById, form.companyIds],
  );
  const availableCompanyOptions = useMemo(
    () => mainCompanies.filter((company) => !form.companyIds.includes(company.id)).map(buildCompanyOption),
    [form.companyIds, mainCompanies],
  );
  const departmentOptions = useMemo<LookupSelectOption[]>(
    () => departments
      .filter((department) => department.isActive !== false || String(department.id) === form.departmentId)
      .map((department) => ({
        value: String(department.id),
        label: department.name,
        description: department.description?.trim() || undefined,
        keywords: [department.name, department.description ?? ''].filter(Boolean),
      })),
    [departments, form.departmentId],
  );

  const contractorBalanceItems = useMemo(
    () => {
      const items = new Map<string, ContractorBalanceItem>();
      [...contractorBalance.queue, ...contractorBalance.paid].forEach((item) => {
        items.set(`${item.taskId}:${item.paymentId ?? 'planned'}`, item);
      });
      return Array.from(items.values());
    },
    [contractorBalance.paid, contractorBalance.queue],
  );

  const contractorBalanceGroups = useMemo<ContractorBalanceGroup[]>(() => {
    const groups = new Map<string, ContractorBalanceGroup>();

    contractorBalanceItems.forEach((item) => {
      const clientLabel = item.clientName?.trim() || item.legalEntityName?.trim() || 'Без клиента';
      const caseLabel = item.clientCaseTitle?.trim() || 'Без дела';
      const key = `${item.clientId ?? 'c0'}:${item.legalEntityId ?? 'l0'}:${item.clientCaseId ?? 'case0'}:${clientLabel}:${caseLabel}`;
      const group = groups.get(key) ?? {
        key,
        clientLabel,
        caseLabel,
        outstandingAmount: 0,
        paidAmount: 0,
        amount: 0,
        items: [],
      };

      group.outstandingAmount += item.outstandingAmount;
      group.paidAmount += item.paidAmount;
      group.amount += item.amount;
      group.items.push(item);
      groups.set(key, group);
    });

    return Array.from(groups.values())
      .sort((left, right) => right.outstandingAmount - left.outstandingAmount || right.amount - left.amount || left.clientLabel.localeCompare(right.clientLabel, 'ru'));
  }, [contractorBalanceItems]);

  const summaryCards = useMemo(
    () => [
      { label: 'Статус', value: form.isActive ? 'Активен' : 'Отключён' },
      { label: 'Одобрение', value: user?.isApproved ? 'Одобрен' : isCreateMode ? 'Создаётся вручную' : 'Ожидает решения' },
      { label: 'Способов входа', value: String(user?.externalIdentities?.length ?? 0) },
    ],
    [form.isActive, isCreateMode, user?.externalIdentities?.length, user?.isApproved],
  );

  const isProtectedTarget = Boolean(user?.isProtectedUser);
  const canEditProtectedTarget = !isProtectedTarget || currentUser?.id === user?.id;
  const mainFieldsDisabled = isProtectedTarget && !canEditProtectedTarget;
  const administrativeFieldsDisabled = isProtectedTarget;
  const avatarInitials = useMemo(() => getInitials(form.fullName || user?.fullName || user?.email), [form.fullName, user?.email, user?.fullName]);

  const handleEmployeeToggle = (checked: boolean) => {
    setForm((current) => ({ ...current, isEmployee: checked, companyIds: checked ? current.companyIds : [] }));
    if (!checked) {
      setSelectedCompanyToAddId('');
    }
  };

  const handleAddCompany = () => {
    const companyId = Number(selectedCompanyToAddId);
    if (!companyId) return;
    setForm((current) => ({
      ...current,
      companyIds: current.companyIds.includes(companyId) ? current.companyIds : [...current.companyIds, companyId].sort((a, b) => a - b),
    }));
    setSelectedCompanyToAddId('');
  };

  const handleRemoveCompany = (companyId: number) => {
    setForm((current) => ({ ...current, companyIds: current.companyIds.filter((id) => id !== companyId) }));
  };

  const handleDepartmentChange = (value: string) => {
    const department = departments.find((item) => String(item.id) === value);
    setForm((current) => ({
      ...current,
      departmentId: value,
      department: department?.name ?? '',
    }));
  };

  const handleSave = async () => {
    if (!form.email.trim()) return toast.error('Укажите email пользователя.');
    if (isCreateMode && !form.password.trim()) return toast.error('Для нового пользователя нужно задать пароль.');
    if (!form.roleId) return toast.error('Выберите роль пользователя.');

    setSaving(true);
    try {
      const payload = buildUpdatePayload(form);
      let nextUser: User;

      if (isCreateMode) {
        const createdUser = await authService.createUser({
          email: form.email.trim(),
          password: form.password,
          fullName: form.fullName.trim() || form.email.trim(),
          roleId: form.roleId,
          isActive: form.isActive,
          isEmployee: form.isEmployee,
          isExternalContractor: form.isExternalContractor,
          departmentId: form.departmentId ? Number(form.departmentId) : undefined,
          department: form.departmentId ? undefined : form.department.trim() || undefined,
          requiresFinancialActionReason: form.requiresFinancialActionReason,
          companyIds: form.companyIds,
        });
        nextUser = await authService.updateUser(createdUser.id, payload);
      } else {
        nextUser = await authService.updateUser(userId, payload);
      }

      await apiService.upsertUserSettingForUser(nextUser.id, USER_TIME_ZONE_SETTING_KEY, timeZoneId);
      setUser(nextUser);
      setForm(buildForm(nextUser));
      onSaved(nextUser);
      toast.success(isCreateMode ? 'Пользователь создан.' : 'Пользователь обновлён.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить карточку пользователя.'));
    } finally {
      setSaving(false);
    }
  };

  const handleTaskNotificationDefaultsSave = async () => {
    if (isCreateMode || !userId) {
      toast.error('Сначала сохраните пользователя.');
      return;
    }

    setSavingTaskNotificationDefaults(true);
    try {
      await apiService.upsertUserSettingForUser(
        userId,
        TASK_NOTIFICATION_DEFAULTS_SETTING_KEY,
        serializeTaskNotificationDefaults(taskNotificationDefaults),
      );
      toast.success('Настройки напоминаний задач сохранены.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить настройки напоминаний задач.'));
    } finally {
      setSavingTaskNotificationDefaults(false);
    }
  };

  if (loading) {
    return <div className="p-8"><div className="rounded-3xl border border-slate-200 bg-white p-8 text-sm text-slate-600 shadow-sm">Загружаем карточку пользователя...</div></div>;
  }

  return (
    <div className="p-6 lg:p-8">
      <div className="space-y-6">
        <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <button type="button" onClick={onBack} className="inline-flex items-center gap-2 text-sm font-medium text-slate-500 transition hover:text-slate-700"><ArrowLeft className="h-4 w-4" />К списку пользователей</button>
              <div className="mt-3 text-xs font-semibold uppercase tracking-[0.22em] text-slate-400">Пользователи</div>
              <h1 className="mt-2 text-3xl font-semibold text-slate-900">{isCreateMode ? 'Новый пользователь' : form.fullName || 'Карточка пользователя'}</h1>
            </div>
            <button type="button" onClick={() => void handleSave()} disabled={saving || (isProtectedTarget && !canEditProtectedTarget)} className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60">{saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}Сохранить</button>
          </div>
          {isProtectedTarget && !canEditProtectedTarget ? <div className="mt-5 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">Этот системный пользователь управляет собой только самостоятельно.</div> : null}
        </div>
        <div className="grid gap-6 xl:grid-cols-[280px_minmax(0,1fr)]">
          <aside className="space-y-4">
            <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
              <div className="space-y-2">
                {SECTION_LABELS.map((section) => (
                  <button key={section.id} type="button" onClick={() => setActiveSection(section.id)} className={`w-full rounded-2xl border px-4 py-3 text-left transition ${activeSection === section.id ? 'border-blue-200 bg-blue-50 text-blue-700 shadow-sm' : 'border-transparent bg-white text-slate-700 hover:border-slate-200 hover:bg-slate-50'}`}>
                    <div className="text-sm font-semibold">{section.label}</div>
                    <div className="mt-1 text-xs leading-5 text-slate-500">{section.description}</div>
                  </button>
                ))}
              </div>
            </div>
            <div className="grid gap-3 md:grid-cols-3 xl:grid-cols-1">
              {summaryCards.map((card) => (
                <div key={card.label} className="flex min-h-[116px] flex-col items-center justify-center rounded-3xl border border-slate-200 bg-white p-4 text-center shadow-sm">
                  <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">{card.label}</div>
                  <div className="mt-2 text-lg font-semibold text-slate-900">{card.value}</div>
                </div>
              ))}
            </div>
            {!isCreateMode && user ? (
              <ObjectAuditLauncher
                objectType="user"
                objectId={user.id}
                createdAt={user.createdAt}
                createdByUserName={user.createdByUserName}
                updatedAt={user.updatedAt}
                updatedByUserName={user.updatedByUserName}
                triggerTitle="Аудит пользователя"
                triggerDescription="Паспорт пользователя и история изменений открываются в отдельном окне."
                title="История пользователя"
              />
            ) : null}
          </aside>

          <div className="space-y-6">
            {activeSection === 'main' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                <div className="mb-6 flex flex-col justify-between gap-6 lg:flex-row lg:items-start">
                  <div>
                    <div className="text-sm font-semibold text-slate-900">Основная информация</div>
                    <div className="mt-1 text-sm text-slate-500">Фото пользователь обновляет сам в личном профиле. Здесь показывается текущий аватар и основные данные карточки.</div>
                  </div>
                  <div className="flex flex-col items-center gap-3 rounded-3xl border border-slate-200 bg-slate-50 px-5 py-4 text-center">
                    {user?.photoUrl && !avatarLoadFailed ? (
                      <img src={user.photoUrl} alt={form.fullName || user.email || 'Профиль'} className="h-24 w-24 rounded-3xl object-cover shadow-sm" onError={() => setAvatarLoadFailed(true)} />
                    ) : (
                      <div className="flex h-24 w-24 items-center justify-center rounded-3xl bg-gradient-to-br from-blue-500 to-slate-900 text-2xl font-semibold text-white shadow-sm">{avatarInitials}</div>
                    )}
                    <div className="text-xs font-medium text-slate-500">{user?.photoUrl ? 'Фото загружено пользователем' : 'Фото пока не загружено'}</div>
                  </div>
                </div>
                <div className="grid gap-4 md:grid-cols-2">
                  <TextField label="Полное имя" value={form.fullName} onChange={(value) => updateForm('fullName', value)} disabled={mainFieldsDisabled} className="md:col-span-2" />
                  <TextField label="Email" type="email" value={form.email} onChange={(value) => updateForm('email', value)} disabled={mainFieldsDisabled || isProtectedTarget} />
                  {isCreateMode ? <TextField label="Пароль" type="password" value={form.password} onChange={(value) => updateForm('password', value)} /> : null}
                  <TextField label="Имя" value={form.firstName} onChange={(value) => updateForm('firstName', value)} disabled={mainFieldsDisabled} />
                  <TextField label="Фамилия" value={form.lastName} onChange={(value) => updateForm('lastName', value)} disabled={mainFieldsDisabled} />
                  <TextField label="Отчество" value={form.middleName} onChange={(value) => updateForm('middleName', value)} disabled={mainFieldsDisabled} />
                  <TextField label="Дата рождения" type="date" value={form.dateOfBirth} onChange={(value) => updateForm('dateOfBirth', value)} disabled={mainFieldsDisabled} />
                </div>
              </div>
            ) : null}

            {activeSection === 'contacts' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                  <TextField label="Телефон" value={form.phoneNumber} onChange={(value) => updateForm('phoneNumber', value)} disabled={mainFieldsDisabled} />
                  <TextField label="WhatsApp" value={form.whatsApp} onChange={(value) => updateForm('whatsApp', value)} disabled={mainFieldsDisabled} />
                  <TextField label="Telegram" value={form.telegram} onChange={(value) => updateForm('telegram', value)} disabled={mainFieldsDisabled} />
                  <TextField label="Instagram" value={form.instagram} onChange={(value) => updateForm('instagram', value)} disabled={mainFieldsDisabled} />
                  <TextField label="Messenger" value={form.messenger} onChange={(value) => updateForm('messenger', value)} disabled={mainFieldsDisabled} />
                  <TextField label="Viber" value={form.viber} onChange={(value) => updateForm('viber', value)} disabled={mainFieldsDisabled} />
                </div>
              </div>
            ) : null}

            {activeSection === 'employment' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                {administrativeFieldsDisabled ? <div className="mb-4 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">Для системного администратора роль и служебный статус зафиксированы и не меняются через карточку пользователя.</div> : null}
                <div className="grid gap-4 md:grid-cols-2">
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Роль</span>
                    <select value={form.roleId} onChange={(event) => updateForm('roleId', Number(event.target.value) || 0)} disabled={administrativeFieldsDisabled} className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500">
                      {roles.map((role) => <option key={role.id} value={role.id}>{role.name}</option>)}
                    </select>
                  </label>
                  <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                    <input type="checkbox" checked={form.isActive} onChange={(event) => updateForm('isActive', event.target.checked)} disabled={administrativeFieldsDisabled} className="h-4 w-4 rounded border-slate-300 text-blue-600" />
                    Пользователь активен
                  </label>
                  <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                    <input type="checkbox" checked={form.isEmployee} onChange={(event) => handleEmployeeToggle(event.target.checked)} disabled={administrativeFieldsDisabled} className="h-4 w-4 rounded border-slate-300 text-blue-600" />
                    Учитывать как сотрудника
                  </label>
                  <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                    <input type="checkbox" checked={form.isExternalContractor} onChange={(event) => updateForm('isExternalContractor', event.target.checked)} disabled={administrativeFieldsDisabled} className="h-4 w-4 rounded border-slate-300 text-blue-600" />
                    Привлеченный исполнитель
                  </label>
                  <LookupSelect
                    label="Отдел"
                    value={form.departmentId}
                    options={departmentOptions}
                    onChange={handleDepartmentChange}
                    placeholder={departmentOptions.length > 0 ? 'Выберите отдел' : 'Сначала добавьте отдел в справочнике'}
                    title="Выбор отдела"
                    searchPlaceholder="Поиск по названию отдела"
                    emptyOptionLabel="Без отдела"
                    emptyOptionDescription="Не использовать отдел для этого пользователя"
                    noOptionsText="Отделы не найдены"
                    disabled={administrativeFieldsDisabled}
                    helperText="Отдел выбирается из справочника и участвует в правилах видимости по отделу."
                  />
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Часовой пояс</span>
                    <select
                      value={timeZoneId}
                      onChange={(event) => setTimeZoneId(event.target.value)}
                      disabled={administrativeFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:cursor-not-allowed disabled:bg-slate-100 disabled:text-slate-500"
                    >
                      {USER_TIME_ZONE_OPTIONS.map((option) => (
                        <option key={option.value} value={option.value}>{option.label}</option>
                      ))}
                    </select>
                    <span className="block text-xs leading-5 text-slate-500">Используется для задач во внутренних и внешних календарях.</span>
                  </label>
                  <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700 md:col-span-2">
                    <input type="checkbox" checked={form.requiresFinancialActionReason} onChange={(event) => updateForm('requiresFinancialActionReason', event.target.checked)} disabled={administrativeFieldsDisabled} className="h-4 w-4 rounded border-slate-300 text-blue-600" />
                    Требовать причину при действиях с финансовыми документами
                  </label>
                  <TextField label="Начало работы" type="date" value={form.employmentStartDate} onChange={(value) => updateForm('employmentStartDate', value)} disabled={administrativeFieldsDisabled} />
                  <TextField label="Окончание работы" type="date" value={form.employmentEndDate} onChange={(value) => updateForm('employmentEndDate', value)} disabled={administrativeFieldsDisabled} />
                </div>

                <div className="mt-6 rounded-3xl border border-slate-200 bg-slate-50 p-5">
                  <div className="flex items-start gap-3">
                    <div className="mt-0.5 flex h-10 w-10 items-center justify-center rounded-2xl bg-blue-100 text-blue-700"><Building2 className="h-5 w-5" /></div>
                    <div className="min-w-0">
                      <div className="text-sm font-semibold text-slate-900">Доступ к основным компаниям</div>
                      <div className="mt-1 text-sm leading-6 text-slate-600">Сотрудника можно связать с одной или несколькими внутренними компаниями из системных настроек. Эти связи станут базой для дальнейшей фильтрации документов по компаниям.</div>
                    </div>
                  </div>

                  {!form.isEmployee ? (
                    <div className="mt-4 rounded-2xl border border-dashed border-slate-300 bg-white px-4 py-4 text-sm text-slate-500">Сначала включите признак «Учитывать как сотрудника», после этого появится возможность назначить компании.</div>
                  ) : mainCompanies.length === 0 ? (
                    <div className="mt-4 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-4 text-sm text-amber-800">В настройках ещё не выбраны основные компании. Сначала добавьте их в разделе настроек.</div>
                  ) : (
                    <div className="mt-4 space-y-4">
                      <div className="grid gap-3 md:grid-cols-[minmax(0,1fr)_auto] md:items-end">
                        <LookupSelect
                          label="Добавить компанию"
                          value={selectedCompanyToAddId}
                          options={availableCompanyOptions}
                          onChange={setSelectedCompanyToAddId}
                          placeholder={availableCompanyOptions.length > 0 ? 'Выберите компанию' : 'Все компании уже назначены'}
                          title="Выбор компании для сотрудника"
                          searchPlaceholder="Поиск по названию, ИНН или КПП"
                          noOptionsText="Нет доступных компаний для добавления"
                          disabled={administrativeFieldsDisabled || availableCompanyOptions.length === 0}
                          helperText="Назначение определяет, к каким внутренним компаниям относится сотрудник."
                        />
                        <button type="button" onClick={handleAddCompany} disabled={administrativeFieldsDisabled || !selectedCompanyToAddId} className="inline-flex items-center justify-center gap-2 rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300"><Plus className="h-4 w-4" />Добавить</button>
                      </div>

                      {assignedCompanies.length === 0 ? (
                        <div className="rounded-2xl border border-dashed border-slate-300 bg-white px-4 py-4 text-sm text-slate-500">Пока не назначено ни одной компании.</div>
                      ) : (
                        <div className="space-y-3">
                          {assignedCompanies.map((company) => (
                            <div key={company.id} className="flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white px-4 py-4 lg:flex-row lg:items-start lg:justify-between">
                              <div className="min-w-0">
                                <div className="text-sm font-semibold text-slate-900">{company.shortName || company.fullName || `Компания #${company.id}`}</div>
                                <div className="mt-1 text-sm text-slate-600">{company.fullName || 'Полное наименование не указано'}</div>
                                <div className="mt-1 text-xs text-slate-500">{[company.inn ? `ИНН ${company.inn}` : null, company.kpp ? `КПП ${company.kpp}` : null].filter(Boolean).join(' • ') || 'Реквизиты не заполнены'}</div>
                              </div>
                              <button type="button" onClick={() => handleRemoveCompany(company.id)} disabled={administrativeFieldsDisabled} className="inline-flex items-center gap-2 rounded-xl border border-rose-200 bg-white px-3 py-2 text-sm font-medium text-rose-600 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-60"><Trash2 className="h-4 w-4" />Убрать</button>
                            </div>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </div>
              </div>
            ) : null}

            {activeSection === 'contractorBalance' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                <div className="flex flex-wrap items-start justify-between gap-4">
                  <div>
                    <div className="text-sm font-semibold text-slate-900">Расчеты с исполнителем</div>
                    <div className="mt-1 text-sm leading-6 text-slate-600">
                      Сводка по задачам, где этот пользователь назначен привлеченным исполнителем.
                    </div>
                  </div>
                  <div className="grid gap-3 sm:grid-cols-3">
                    <div className="rounded-2xl border border-emerald-100 bg-emerald-50 px-4 py-3">
                      <div className="text-xs font-semibold uppercase tracking-[0.16em] text-emerald-700">К выплате</div>
                      <div className="mt-1 text-lg font-semibold text-emerald-900">{formatCurrencySmart(contractorBalance.owedToMe).full}</div>
                    </div>
                    <div className="rounded-2xl border border-sky-100 bg-sky-50 px-4 py-3">
                      <div className="text-xs font-semibold uppercase tracking-[0.16em] text-sky-700">Выплачено</div>
                      <div className="mt-1 text-lg font-semibold text-sky-900">{formatCurrencySmart(contractorBalance.paidToMe).full}</div>
                    </div>
                    <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
                      <div className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-500">Задач</div>
                      <div className="mt-1 text-lg font-semibold text-slate-900">{contractorBalanceItems.length}</div>
                    </div>
                  </div>
                </div>

                {!form.isExternalContractor ? (
                  <div className="mt-5 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm leading-6 text-amber-800">
                    У пользователя не включен признак «Привлеченный исполнитель». Баланс ниже показывает только уже найденные выплаты по задачам.
                  </div>
                ) : null}

                {contractorBalanceError ? (
                  <div className="mt-5 rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm leading-6 text-rose-700">
                    {contractorBalanceError}
                  </div>
                ) : null}

                {contractorBalanceGroups.length === 0 ? (
                  <div className="mt-5 rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-4 py-5 text-sm text-slate-500">
                    По этому пользователю пока нет задач с блоком «Оплата исполнителю».
                  </div>
                ) : (
                  <div className="mt-5 space-y-3">
                    {contractorBalanceGroups.map((group) => (
                      <div key={group.key} className="rounded-2xl border border-slate-200 bg-slate-50 p-4">
                        <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                          <div className="min-w-0">
                            <div className="truncate text-sm font-semibold text-slate-900">{group.clientLabel}</div>
                            <div className="mt-1 truncate text-xs text-slate-500">{group.caseLabel}</div>
                          </div>
                          <div className="grid gap-2 text-sm sm:grid-cols-3 lg:min-w-[420px]">
                            <div className="rounded-xl bg-white px-3 py-2">
                              <div className="text-xs text-slate-500">К выплате</div>
                              <div className="font-semibold text-emerald-700">{formatCurrencySmart(group.outstandingAmount).full}</div>
                            </div>
                            <div className="rounded-xl bg-white px-3 py-2">
                              <div className="text-xs text-slate-500">Выплачено</div>
                              <div className="font-semibold text-sky-700">{formatCurrencySmart(group.paidAmount).full}</div>
                            </div>
                            <div className="rounded-xl bg-white px-3 py-2">
                              <div className="text-xs text-slate-500">Начислено</div>
                              <div className="font-semibold text-slate-900">{formatCurrencySmart(group.amount).full}</div>
                            </div>
                          </div>
                        </div>
                        <div className="mt-3 space-y-2">
                          {group.items.slice(0, 6).map((item) => (
                            <div key={`${item.taskId}-${item.paymentId ?? 'planned'}`} className="flex flex-col gap-2 rounded-xl bg-white px-3 py-2 text-sm lg:flex-row lg:items-center lg:justify-between">
                              <div className="min-w-0">
                                <a href={`/tasks/${item.taskId}`} className="block truncate font-medium text-blue-700 hover:text-blue-900 hover:underline">
                                  {item.taskTitle}
                                </a>
                                <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-slate-500">
                                  <span className={`rounded-full px-2 py-1 font-semibold ${contractorPaymentStatusClassName(item.paymentStatus)}`}>
                                    {formatContractorPaymentStatus(item.paymentStatus)}
                                  </span>
                                  {item.paymentDueDate ? <span>срок {formatOptionalDate(item.paymentDueDate)}</span> : null}
                                  {item.paidDate ? <span>оплачено {formatOptionalDate(item.paidDate)}</span> : null}
                                  <span>остаток {formatCurrencySmart(item.outstandingAmount).full}</span>
                                </div>
                              </div>
                              <div className="flex shrink-0 flex-wrap items-center gap-3 lg:justify-end">
                                {item.paymentId ? (
                                  onOpenPayment ? (
                                    <button
                                      type="button"
                                      onClick={() => onOpenPayment(item.paymentId as number)}
                                      className="text-xs font-semibold text-slate-500 hover:text-blue-700 hover:underline"
                                    >
                                      Платеж #{item.paymentId}
                                    </button>
                                  ) : (
                                    <a href={`/payments/${item.paymentId}`} className="text-xs font-semibold text-slate-500 hover:text-blue-700 hover:underline">
                                      Платеж #{item.paymentId}
                                    </a>
                                  )
                                ) : null}
                                {!item.paymentId ? (
                                  <span className="max-w-[260px] text-right text-xs font-medium leading-5 text-slate-500">
                                    {formatMissingContractorPaymentHint(item.paymentStatus)}
                                  </span>
                                ) : null}
                                <div className="font-semibold text-slate-900">{formatCurrencySmart(item.amount).full}</div>
                              </div>
                            </div>
                          ))}
                          {group.items.length > 6 ? (
                            <div className="px-3 pt-1 text-xs text-slate-500">Еще задач: {group.items.length - 6}</div>
                          ) : null}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            ) : null}

            {activeSection === 'notifications' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <div className="text-sm font-semibold text-slate-900">Напоминания задач по умолчанию</div>
                    <div className="mt-1 text-sm leading-6 text-slate-600">
                      Эти настройки будут подставляться в новые задачи, которые создаёт этот пользователь.
                    </div>
                  </div>
                  <button
                    type="button"
                    onClick={() => void handleTaskNotificationDefaultsSave()}
                    disabled={isCreateMode || savingTaskNotificationDefaults}
                    className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {savingTaskNotificationDefaults ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                    Сохранить
                  </button>
                </div>
                {isCreateMode ? (
                  <div className="mt-5 rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-4 py-4 text-sm text-slate-500">
                    Настройки напоминаний можно задать после создания пользователя.
                  </div>
                ) : (
                  <div className="mt-5">
                    <TaskNotificationDefaultsPanel
                      value={taskNotificationDefaults}
                      onChange={setTaskNotificationDefaults}
                      disabled={savingTaskNotificationDefaults}
                    />
                  </div>
                )}
              </div>
            ) : null}

            {activeSection === 'access' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                <div className="flex items-start gap-3 rounded-2xl border border-slate-200 bg-slate-50 p-4">
                  <div className="flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-100 text-blue-700"><ShieldCheck className="h-5 w-5" /></div>
                  <div>
                    <div className="text-base font-semibold text-slate-900">Привязанные способы входа</div>
                    <div className="mt-1 text-sm leading-6 text-slate-600">Здесь отображаются внешние провайдеры, уже связанные с этим пользователем.</div>
                  </div>
                </div>
                <div className="mt-5 grid gap-4 md:grid-cols-2">
                  {(user?.externalIdentities?.length ?? 0) > 0 ? (
                    user?.externalIdentities?.map((identity) => (
                      <div key={identity.id} className="rounded-2xl border border-slate-200 p-4">
                        <div className="flex items-center gap-2">
                          <div className="flex h-9 w-9 items-center justify-center rounded-2xl bg-slate-100 text-slate-700"><UserRound className="h-4 w-4" /></div>
                          <div>
                            <div className="font-semibold text-slate-900">{providerLabel(identity.provider)}</div>
                            <div className="text-xs text-slate-500">{identity.providerEmail || identity.providerUsername || identity.providerUserId}</div>
                          </div>
                        </div>
                      </div>
                    ))
                  ) : (
                    <div className="rounded-2xl border border-dashed border-slate-300 p-5 text-sm text-slate-500">У этого пользователя пока нет привязанных внешних способов входа.</div>
                  )}
                </div>
              </div>
            ) : null}
          </div>
        </div>
      </div>
    </div>
  );
}
