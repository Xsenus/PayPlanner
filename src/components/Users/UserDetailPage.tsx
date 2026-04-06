import { useCallback, useEffect, useMemo, useState } from 'react';
import { ArrowLeft, Loader2, Save, ShieldCheck, UserRound } from 'lucide-react';
import { authService, type Role, type UpdateUserDto, type User } from '../../services/authService';
import { useAuth } from '../../contexts/AuthContext';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

type UserDetailSection = 'main' | 'contacts' | 'employment' | 'access';

type UserDetailPageProps = {
  userId: number | null;
  onBack: () => void;
  onSaved: (user: User) => void;
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
  employmentStartDate: string;
  employmentEndDate: string;
  roleId: number;
  isActive: boolean;
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
  employmentStartDate: '',
  employmentEndDate: '',
  roleId: 0,
  isActive: true,
};

const SECTION_LABELS: Array<{ id: UserDetailSection; label: string; description: string }> = [
  { id: 'main', label: 'Основное', description: 'Имя, email и базовая информация профиля.' },
  { id: 'contacts', label: 'Контакты', description: 'Телефон, мессенджеры и внешние ссылки.' },
  { id: 'employment', label: 'Работа в системе', description: 'Роль, активность и кадровые параметры.' },
  { id: 'access', label: 'Способы входа', description: 'Какие внешние способы входа уже привязаны.' },
];

function toDateInputValue(value?: string | null) {
  return value ? value.split('T')[0] : '';
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
    employmentStartDate: toDateInputValue(user.employmentStartDate),
    employmentEndDate: toDateInputValue(user.employmentEndDate),
    roleId: user.role.id,
    isActive: user.isActive,
  };
}

function providerLabel(provider?: string | null) {
  if (provider === 'vk') return 'VK ID';
  if (provider === 'yandex') return 'Яндекс ID';
  if (provider === 'telegram') return 'Telegram';
  return 'Google';
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
    employmentStartDate: form.employmentStartDate || undefined,
    employmentEndDate: form.employmentEndDate || undefined,
    roleId: form.roleId || undefined,
    isActive: form.isActive,
  };
}

export function UserDetailPage({ userId, onBack, onSaved }: UserDetailPageProps) {
  const { user: currentUser } = useAuth();
  const toast = useToast();
  const isCreateMode = userId == null;
  const [activeSection, setActiveSection] = useState<UserDetailSection>('main');
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [roles, setRoles] = useState<Role[]>([]);
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  const loadData = useCallback(async () => {
    const rolesPromise = authService.getRoles();
    const userPromise = isCreateMode ? Promise.resolve<User | null>(null) : authService.getUser(userId);
    const [nextRoles, nextUser] = await Promise.all([rolesPromise, userPromise]);

    setRoles(nextRoles);
    setUser(nextUser);

    const nextForm = buildForm(nextUser);
    if (!nextUser && nextRoles.length > 0) {
      nextForm.roleId = nextRoles.find((role) => role.name.toLowerCase() === 'user')?.id ?? nextRoles[0].id;
    }

    setForm(nextForm);
  }, [isCreateMode, userId]);

  useEffect(() => {
    setLoading(true);
    void loadData()
      .catch((error) => {
        toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить карточку пользователя.'));
      })
      .finally(() => setLoading(false));
  }, [loadData, toast]);

  const updateForm = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm((current) => ({ ...current, [key]: value }));

  const summaryCards = useMemo(
    () => [
      { label: 'Статус', value: form.isActive ? 'Активен' : 'Отключен' },
      {
        label: 'Одобрение',
        value: user?.isApproved ? 'Одобрен' : isCreateMode ? 'Создается вручную' : 'Ожидает решения',
      },
      { label: 'Привязанных входов', value: String(user?.externalIdentities?.length ?? 0) },
    ],
    [form.isActive, isCreateMode, user?.externalIdentities?.length, user?.isApproved],
  );

  const isProtectedTarget = Boolean(user?.isProtectedUser);
  const canEditProtectedTarget = !isProtectedTarget || currentUser?.id === user?.id;
  const mainFieldsDisabled = isProtectedTarget && !canEditProtectedTarget;
  const administrativeFieldsDisabled = isProtectedTarget;
  const avatarInitials = useMemo(
    () => getInitials(form.fullName || user?.fullName || user?.email),
    [form.fullName, user?.email, user?.fullName],
  );

  const handleSave = async () => {
    if (!form.email.trim()) {
      toast.error('Укажите email пользователя.');
      return;
    }

    if (isCreateMode && !form.password.trim()) {
      toast.error('Для нового пользователя нужно задать пароль.');
      return;
    }

    if (!form.roleId) {
      toast.error('Выберите роль пользователя.');
      return;
    }

    setSaving(true);
    try {
      const updatePayload = buildUpdatePayload(form);
      let nextUser: User;

      if (isCreateMode) {
        const createdUser = await authService.createUser({
          email: form.email.trim(),
          password: form.password,
          fullName: form.fullName.trim() || form.email.trim(),
          roleId: form.roleId,
          isActive: form.isActive,
        });

        nextUser = await authService.updateUser(createdUser.id, updatePayload);
      } else {
        nextUser = await authService.updateUser(userId, updatePayload);
      }

      setUser(nextUser);
      setForm(buildForm(nextUser));
      onSaved(nextUser);
      toast.success(isCreateMode ? 'Пользователь создан.' : 'Пользователь обновлен.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить карточку пользователя.'));
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="p-8">
        <div className="rounded-3xl border border-slate-200 bg-white p-8 text-sm text-slate-600 shadow-sm">
          Загружаем карточку пользователя...
        </div>
      </div>
    );
  }

  return (
    <div className="p-6 lg:p-8">
      <div className="space-y-6">
        <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <button
                type="button"
                onClick={onBack}
                className="inline-flex items-center gap-2 text-sm font-medium text-slate-500 transition hover:text-slate-700"
              >
                <ArrowLeft className="h-4 w-4" />
                К списку пользователей
              </button>
              <div className="mt-3 text-xs font-semibold uppercase tracking-[0.22em] text-slate-400">
                Пользователи
              </div>
              <h1 className="mt-2 text-3xl font-semibold text-slate-900">
                {isCreateMode ? 'Новый пользователь' : form.fullName || 'Карточка пользователя'}
              </h1>
            </div>

            <button
              type="button"
              onClick={() => void handleSave()}
              disabled={saving || (isProtectedTarget && !canEditProtectedTarget)}
              className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Сохранить
            </button>
          </div>

          {isProtectedTarget && !canEditProtectedTarget ? (
            <div className="mt-5 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
              Этот системный пользователь управляет собой только самостоятельно. Для остальных профиль открыт только на просмотр.
            </div>
          ) : null}
        </div>

        <div className="grid gap-6 xl:grid-cols-[280px_minmax(0,1fr)]">
          <aside className="space-y-4">
            <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
              <div className="space-y-2">
                {SECTION_LABELS.map((section) => (
                  <button
                    key={section.id}
                    type="button"
                    onClick={() => setActiveSection(section.id)}
                    className={`w-full rounded-2xl border px-4 py-3 text-left transition ${
                      activeSection === section.id
                        ? 'border-blue-200 bg-blue-50 text-blue-700 shadow-sm'
                        : 'border-transparent bg-white text-slate-700 hover:border-slate-200 hover:bg-slate-50'
                    }`}
                  >
                    <div className="text-sm font-semibold">{section.label}</div>
                    <div className="mt-1 text-xs leading-5 text-slate-500">{section.description}</div>
                  </button>
                ))}
              </div>
            </div>

            <div className="grid gap-3 md:grid-cols-3 xl:grid-cols-1">
              {summaryCards.map((card) => (
                <div
                  key={card.label}
                  className="flex min-h-[116px] flex-col items-center justify-center rounded-3xl border border-slate-200 bg-white p-4 text-center shadow-sm"
                >
                  <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">
                    {card.label}
                  </div>
                  <div className="mt-2 text-lg font-semibold text-slate-900">{card.value}</div>
                </div>
              ))}
            </div>
          </aside>

          <div className="space-y-6">
            {activeSection === 'main' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                <div className="mb-6 flex flex-col justify-between gap-6 lg:flex-row lg:items-start">
                  <div>
                    <div className="text-sm font-semibold text-slate-900">Основная информация</div>
                    <div className="mt-1 text-sm text-slate-500">
                      Фото пользователь обновляет сам в личном профиле. Здесь показывается актуальный аватар и базовые данные.
                    </div>
                  </div>

                  <div className="flex flex-col items-center gap-3 rounded-3xl border border-slate-200 bg-slate-50 px-5 py-4 text-center">
                    {user?.photoUrl ? (
                      <img
                        src={user.photoUrl}
                        alt={form.fullName || user.email || 'Профиль'}
                        className="h-24 w-24 rounded-3xl object-cover shadow-sm"
                      />
                    ) : (
                      <div className="flex h-24 w-24 items-center justify-center rounded-3xl bg-gradient-to-br from-blue-500 to-slate-900 text-2xl font-semibold text-white shadow-sm">
                        {avatarInitials}
                      </div>
                    )}
                    <div className="text-xs font-medium text-slate-500">
                      {user?.photoUrl ? 'Фото загружено пользователем' : 'Фото пока не загружено'}
                    </div>
                  </div>
                </div>

                <div className="grid gap-4 md:grid-cols-2">
                  <label className="space-y-2 md:col-span-2">
                    <span className="text-sm font-medium text-slate-700">Полное имя</span>
                    <input
                      type="text"
                      value={form.fullName}
                      onChange={(event) => updateForm('fullName', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Email</span>
                    <input
                      type="email"
                      value={form.email}
                      onChange={(event) => updateForm('email', event.target.value)}
                      disabled={mainFieldsDisabled || isProtectedTarget}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  {isCreateMode ? (
                    <label className="space-y-2">
                      <span className="text-sm font-medium text-slate-700">Пароль</span>
                      <input
                        type="password"
                        value={form.password}
                        onChange={(event) => updateForm('password', event.target.value)}
                        className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                      />
                    </label>
                  ) : null}
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Имя</span>
                    <input
                      type="text"
                      value={form.firstName}
                      onChange={(event) => updateForm('firstName', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Фамилия</span>
                    <input
                      type="text"
                      value={form.lastName}
                      onChange={(event) => updateForm('lastName', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Отчество</span>
                    <input
                      type="text"
                      value={form.middleName}
                      onChange={(event) => updateForm('middleName', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Дата рождения</span>
                    <input
                      type="date"
                      value={form.dateOfBirth}
                      onChange={(event) => updateForm('dateOfBirth', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                </div>
              </div>
            ) : null}

            {activeSection === 'contacts' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Телефон</span>
                    <input
                      type="text"
                      value={form.phoneNumber}
                      onChange={(event) => updateForm('phoneNumber', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">WhatsApp</span>
                    <input
                      type="text"
                      value={form.whatsApp}
                      onChange={(event) => updateForm('whatsApp', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Telegram</span>
                    <input
                      type="text"
                      value={form.telegram}
                      onChange={(event) => updateForm('telegram', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Instagram</span>
                    <input
                      type="text"
                      value={form.instagram}
                      onChange={(event) => updateForm('instagram', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Messenger</span>
                    <input
                      type="text"
                      value={form.messenger}
                      onChange={(event) => updateForm('messenger', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Viber</span>
                    <input
                      type="text"
                      value={form.viber}
                      onChange={(event) => updateForm('viber', event.target.value)}
                      disabled={mainFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                </div>
              </div>
            ) : null}

            {activeSection === 'employment' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                {administrativeFieldsDisabled ? (
                  <div className="mb-4 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                    Для системного администратора роль и служебный статус зафиксированы и не меняются через карточку пользователя.
                  </div>
                ) : null}
                <div className="grid gap-4 md:grid-cols-2">
                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Роль</span>
                    <select
                      value={form.roleId}
                      onChange={(event) => updateForm('roleId', Number(event.target.value) || 0)}
                      disabled={administrativeFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    >
                      {roles.map((role) => (
                        <option key={role.id} value={role.id}>
                          {role.name}
                        </option>
                      ))}
                    </select>
                  </label>

                  <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                    <input
                      type="checkbox"
                      checked={form.isActive}
                      onChange={(event) => updateForm('isActive', event.target.checked)}
                      disabled={administrativeFieldsDisabled}
                      className="h-4 w-4 rounded border-slate-300 text-blue-600"
                    />
                    Пользователь активен
                  </label>

                  <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                    <input
                      type="checkbox"
                      checked={form.isEmployee}
                      onChange={(event) => updateForm('isEmployee', event.target.checked)}
                      disabled={administrativeFieldsDisabled}
                      className="h-4 w-4 rounded border-slate-300 text-blue-600"
                    />
                    Учитывать как сотрудника
                  </label>

                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Начало работы</span>
                    <input
                      type="date"
                      value={form.employmentStartDate}
                      onChange={(event) => updateForm('employmentStartDate', event.target.value)}
                      disabled={administrativeFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>

                  <label className="space-y-2">
                    <span className="text-sm font-medium text-slate-700">Окончание работы</span>
                    <input
                      type="date"
                      value={form.employmentEndDate}
                      onChange={(event) => updateForm('employmentEndDate', event.target.value)}
                      disabled={administrativeFieldsDisabled}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3 disabled:bg-slate-100 disabled:text-slate-500"
                    />
                  </label>
                </div>
              </div>
            ) : null}

            {activeSection === 'access' ? (
              <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
                <div className="flex items-start gap-3 rounded-2xl border border-slate-200 bg-slate-50 p-4">
                  <div className="flex h-11 w-11 items-center justify-center rounded-2xl bg-blue-100 text-blue-700">
                    <ShieldCheck className="h-5 w-5" />
                  </div>
                  <div>
                    <div className="text-base font-semibold text-slate-900">Привязанные способы входа</div>
                    <div className="mt-1 text-sm leading-6 text-slate-600">
                      Здесь отображаются внешние провайдеры, уже связанные с этим пользователем.
                      Управлять привязкой пользователь может из своего профиля.
                    </div>
                  </div>
                </div>

                <div className="mt-5 grid gap-4 md:grid-cols-2">
                  {(user?.externalIdentities?.length ?? 0) > 0 ? (
                    user?.externalIdentities?.map((identity) => (
                      <div key={identity.id} className="rounded-2xl border border-slate-200 p-4">
                        <div className="flex items-center gap-2">
                          <div className="flex h-9 w-9 items-center justify-center rounded-2xl bg-slate-100 text-slate-700">
                            <UserRound className="h-4 w-4" />
                          </div>
                          <div>
                            <div className="font-semibold text-slate-900">{providerLabel(identity.provider)}</div>
                            <div className="text-xs text-slate-500">
                              {identity.providerEmail || identity.providerUsername || identity.providerUserId}
                            </div>
                          </div>
                        </div>
                      </div>
                    ))
                  ) : (
                    <div className="rounded-2xl border border-dashed border-slate-300 p-5 text-sm text-slate-500">
                      У этого пользователя пока нет привязанных внешних способов входа.
                    </div>
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
