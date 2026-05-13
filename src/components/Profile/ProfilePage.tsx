import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { KeyRound, Link2, Loader2, RefreshCw, Save, ShieldCheck, Unlink2, Upload } from 'lucide-react';
import {
  authService,
  type AuthProviderPublicSettings,
  type ExternalIdentity,
  type UpdateProfileDto,
  type User,
} from '../../services/authService';
import { apiService } from '../../services/api';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import {
  DEFAULT_TASK_NOTIFICATION_DEFAULTS,
  parseTaskNotificationDefaults,
  serializeTaskNotificationDefaults,
  TASK_NOTIFICATION_DEFAULTS_SETTING_KEY,
  type TaskNotificationDefaults,
} from '../../utils/taskNotificationDefaults';
import { ProfilePhotoCropModal } from './ProfilePhotoCropModal';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';
import { TaskNotificationDefaultsPanel } from '../Tasks/TaskNotificationDefaultsPanel';

type ProviderId = 'google' | 'vk' | 'yandex' | 'telegram';

type FormState = {
  email: string;
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
};

type PasswordFormState = {
  currentPassword: string;
  newPassword: string;
  confirmPassword: string;
};

type PendingPhotoCropState = {
  src: string;
  fileName: string;
  mimeType: string;
};

const EMPTY_PUBLIC_SETTINGS: AuthProviderPublicSettings = {
  googleEnabled: false,
  vkEnabled: false,
  yandexEnabled: false,
  telegramEnabled: false,
  telegramWidgetEnabled: false,
  telegramBotUsername: null,
};

const EMPTY_FORM: FormState = {
  email: '',
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
};

const EMPTY_PASSWORD_FORM: PasswordFormState = {
  currentPassword: '',
  newPassword: '',
  confirmPassword: '',
};

const PROVIDER_META: Record<ProviderId, { label: string; accent: string; description: string }> = {
  google: {
    label: 'Google',
    accent: 'from-red-50 to-white text-red-600 border-red-200',
    description: 'Вход через Google OAuth',
  },
  vk: {
    label: 'VK ID',
    accent: 'from-blue-50 to-white text-blue-600 border-blue-200',
    description: 'Вход через VK ID',
  },
  yandex: {
    label: 'Яндекс ID',
    accent: 'from-orange-50 to-white text-orange-600 border-orange-200',
    description: 'Вход через Яндекс ID',
  },
  telegram: {
    label: 'Telegram',
    accent: 'from-cyan-50 to-white text-cyan-600 border-cyan-200',
    description: 'Вход через Telegram',
  },
};

const toDateInputValue = (value?: string | null) => (value ? value.split('T')[0] : '');

function buildFormState(user?: User | null): FormState {
  if (!user) return EMPTY_FORM;

  return {
    email: user.email ?? '',
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

function clearExternalAuthQueryParams() {
  const url = new URL(window.location.href);
  url.searchParams.delete('externalAuthState');
  url.searchParams.delete('externalAuthProvider');
  window.history.replaceState({}, document.title, url.toString());
}

function readFileAsDataUrl(file: File) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : '');
    reader.onerror = () => reject(reader.error ?? new Error('Не удалось прочитать изображение.'));
    reader.readAsDataURL(file);
  });
}

export function ProfilePage() {
  const { refreshUser } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();

  const [profile, setProfile] = useState<User | null>(null);
  const [publicSettings, setPublicSettings] = useState<AuthProviderPublicSettings>(EMPTY_PUBLIC_SETTINGS);
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [taskNotificationDefaults, setTaskNotificationDefaults] = useState<TaskNotificationDefaults>(DEFAULT_TASK_NOTIFICATION_DEFAULTS);
  const [passwordForm, setPasswordForm] = useState<PasswordFormState>(EMPTY_PASSWORD_FORM);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [savingTaskNotificationDefaults, setSavingTaskNotificationDefaults] = useState(false);
  const [uploadingPhoto, setUploadingPhoto] = useState(false);
  const [changingPassword, setChangingPassword] = useState(false);
  const [linkingProvider, setLinkingProvider] = useState<ProviderId | null>(null);
  const [unlinkingProvider, setUnlinkingProvider] = useState<ProviderId | null>(null);
  const [pendingLink, setPendingLink] = useState<{ provider: ProviderId; state: string } | null>(null);
  const [pendingPhotoCrop, setPendingPhotoCrop] = useState<PendingPhotoCropState | null>(null);
  const [profilePhotoFailed, setProfilePhotoFailed] = useState(false);

  const popupRef = useRef<Window | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  const resetPhotoInput = useCallback(() => {
    if (fileInputRef.current) {
      fileInputRef.current.value = '';
    }
  }, []);

  const loadProfile = useCallback(async () => {
    const [nextProfile, nextProviders, nextTaskNotificationDefaults] = await Promise.all([
      authService.getProfile(),
      authService.getAuthProviders(),
      apiService.getUserSetting(TASK_NOTIFICATION_DEFAULTS_SETTING_KEY).catch(() => null),
    ]);

    setProfile(nextProfile);
    setForm(buildFormState(nextProfile));
    setPublicSettings(nextProviders);
    setTaskNotificationDefaults(parseTaskNotificationDefaults(nextTaskNotificationDefaults?.value));
    return nextProfile;
  }, []);

  useEffect(() => {
    setLoading(true);
    void loadProfile()
      .catch((error) => {
        toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить профиль пользователя.'));
      })
      .finally(() => setLoading(false));
  }, [loadProfile, toast]);

  const providerCards = useMemo(() => {
    const identities = profile?.externalIdentities ?? [];
    const providers: Array<{ id: ProviderId; available: boolean; identity?: ExternalIdentity }> = [
      { id: 'google', available: publicSettings.googleEnabled, identity: identities.find((item) => item.provider === 'google') },
      { id: 'vk', available: publicSettings.vkEnabled, identity: identities.find((item) => item.provider === 'vk') },
      { id: 'yandex', available: publicSettings.yandexEnabled, identity: identities.find((item) => item.provider === 'yandex') },
      {
        id: 'telegram',
        available: publicSettings.telegramEnabled || publicSettings.telegramWidgetEnabled,
        identity: identities.find((item) => item.provider === 'telegram'),
      },
    ];

    return providers.filter((item) => item.available || item.identity);
  }, [profile?.externalIdentities, publicSettings]);

  const linkedProviders = useMemo(() => providerCards.filter((item) => Boolean(item.identity)), [providerCards]);
  const linkableProviders = useMemo(() => providerCards.filter((item) => item.available && !item.identity), [providerCards]);
  const hasExternalLogin = linkedProviders.length > 0;
  const profileInitials = useMemo(
    () => getInitials(form.fullName || profile?.fullName || profile?.email),
    [form.fullName, profile?.email, profile?.fullName],
  );

  useEffect(() => {
    setProfilePhotoFailed(false);
  }, [profile?.photoUrl]);

  const closePopup = useCallback(() => {
    if (popupRef.current && !popupRef.current.closed) {
      popupRef.current.close();
    }
    popupRef.current = null;
  }, []);

  useEffect(() => {
    if (!pendingLink?.state || !pendingLink.provider) return;

    let disposed = false;
    const interval = window.setInterval(async () => {
      try {
        const status = await authService.getExternalAuthStatus(pendingLink.state);
        if (disposed) return;

        if (status.completed && status.linked) {
          window.clearInterval(interval);
          closePopup();
          setPendingLink(null);
          setLinkingProvider(null);
          clearExternalAuthQueryParams();
          await loadProfile();
          await refreshUser();
          toast.success(`${providerLabel(status.provider)} успешно привязан к профилю.`);
          return;
        }

        if (status.status === 'failed' || status.status === 'expired' || status.status === 'consumed') {
          window.clearInterval(interval);
          closePopup();
          setPendingLink(null);
          setLinkingProvider(null);
          clearExternalAuthQueryParams();

          if (status.status === 'failed') {
            toast.error(status.detail || `Не удалось завершить привязку через ${providerLabel(pendingLink.provider)}.`);
          } else if (status.status === 'expired') {
            toast.error(`Сессия привязки ${providerLabel(pendingLink.provider)} истекла. Запустите её заново.`);
          }
        }
      } catch (error) {
        window.clearInterval(interval);
        closePopup();
        setPendingLink(null);
        setLinkingProvider(null);
        clearExternalAuthQueryParams();
        toast.error(extractDisplayErrorMessage(error, 'Не удалось проверить статус привязки внешнего входа.'));
      }
    }, 2000);

    return () => {
      disposed = true;
      window.clearInterval(interval);
    };
  }, [closePopup, loadProfile, pendingLink, refreshUser, toast]);

  useEffect(() => () => closePopup(), [closePopup]);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const state = params.get('externalAuthState');
    const provider = params.get('externalAuthProvider');
    if (state && provider && (provider === 'google' || provider === 'vk' || provider === 'yandex' || provider === 'telegram')) {
      setPendingLink({ provider, state });
      setLinkingProvider(provider);
    }
  }, []);

  const updateForm = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((current) => ({ ...current, [key]: value }));
  };

  const updatePasswordForm = <K extends keyof PasswordFormState>(key: K, value: PasswordFormState[K]) => {
    setPasswordForm((current) => ({ ...current, [key]: value }));
  };

  const handleSave = async () => {
    setSaving(true);
    try {
      const payload: UpdateProfileDto = {
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
      };

      const nextProfile = await authService.updateProfile(payload);
      setProfile(nextProfile);
      setForm(buildFormState(nextProfile));
      await refreshUser();
      toast.success('Профиль обновлён.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить профиль.'));
    } finally {
      setSaving(false);
    }
  };

  const handleTaskNotificationDefaultsSave = async () => {
    setSavingTaskNotificationDefaults(true);
    try {
      await apiService.upsertUserSetting(
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

  const uploadProfilePhoto = useCallback(
    async (file: File) => {
      setUploadingPhoto(true);
      try {
        const nextProfile = await authService.uploadProfilePhoto(file);
        setProfile(nextProfile);
        setForm(buildFormState(nextProfile));
        setPendingPhotoCrop(null);
        await refreshUser();
        toast.success('Фото профиля обновлено.');
      } catch (error) {
        toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить фото профиля.'));
        throw error;
      } finally {
        setUploadingPhoto(false);
        resetPhotoInput();
      }
    },
    [refreshUser, resetPhotoInput, toast],
  );

  const handlePhotoSelected = useCallback(
    async (file?: File | null) => {
      if (!file) {
        resetPhotoInput();
        return;
      }

      try {
        const src = await readFileAsDataUrl(file);
        setPendingPhotoCrop({
          src,
          fileName: file.name || 'profile-photo',
          mimeType: file.type || 'image/png',
        });
      } catch (error) {
        resetPhotoInput();
        toast.error(extractDisplayErrorMessage(error, 'Не удалось подготовить изображение к загрузке.'));
      }
    },
    [resetPhotoInput, toast],
  );

  const closeCropModal = useCallback(() => {
    if (uploadingPhoto) return;
    setPendingPhotoCrop(null);
    resetPhotoInput();
  }, [resetPhotoInput, uploadingPhoto]);

  const handlePasswordSave = async () => {
    if (!passwordForm.newPassword.trim()) {
      toast.error('Укажите новый пароль.');
      return;
    }

    if (passwordForm.newPassword !== passwordForm.confirmPassword) {
      toast.error('Новый пароль и подтверждение не совпадают.');
      return;
    }

    setChangingPassword(true);
    try {
      const nextProfile = await authService.changePassword({
        currentPassword: passwordForm.currentPassword || undefined,
        newPassword: passwordForm.newPassword,
      });
      setProfile(nextProfile);
      setPasswordForm(EMPTY_PASSWORD_FORM);
      toast.success('Пароль успешно изменён.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось изменить пароль.'));
    } finally {
      setChangingPassword(false);
    }
  };

  const handleLink = async (provider: ProviderId) => {
    setLinkingProvider(provider);
    try {
      if (provider === 'telegram') {
        const started = await authService.startTelegramBotAuth('/?ppTab=profile', 'link');
        setPendingLink({ provider, state: started.state });
        popupRef.current = window.open(started.authUrl, 'pp-profile-telegram-link', 'popup=yes,width=540,height=720');
        if (!popupRef.current) {
          window.location.assign(started.authUrl);
        }
        toast.success('Открылся Telegram Bot. Подтвердите привязку и вернитесь в CRM.');
        return;
      }

      const started = await authService.startExternalAuth(provider, '/?ppTab=profile', 'link');
      setPendingLink({ provider, state: started.state });
      popupRef.current = window.open(started.authUrl, 'pp-profile-external-link', 'popup=yes,width=540,height=720');
      if (!popupRef.current) {
        window.location.assign(started.authUrl);
      }
    } catch (error) {
      setLinkingProvider(null);
      setPendingLink(null);
      closePopup();
      toast.error(extractDisplayErrorMessage(error, `Не удалось запустить привязку через ${providerLabel(provider)}.`));
    }
  };

  const handleDetach = async (provider: ProviderId) => {
    const approved = await confirm({
      title: `Отвязать ${providerLabel(provider)}?`,
      description:
        'Этот способ входа перестанет работать для текущего профиля. Если это единственный способ входа без основного email, система не позволит отвязать его.',
      confirmLabel: 'Отвязать',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) return;

    setUnlinkingProvider(provider);
    try {
      const result = await authService.detachExternalIdentity(provider);
      if (result.user) {
        setProfile(result.user);
        setForm(buildFormState(result.user));
      } else {
        await loadProfile();
      }
      await refreshUser();
      toast.success(`${providerLabel(provider)} отвязан от профиля.`);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, `Не удалось отвязать ${providerLabel(provider)}.`));
    } finally {
      setUnlinkingProvider(null);
    }
  };

  if (loading) {
    return (
      <div className="p-8">
        <div className="rounded-3xl border border-slate-200 bg-white p-8 text-sm text-slate-600 shadow-sm">
          Загружаем ваш профиль...
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
              <div className="text-xs font-semibold uppercase tracking-[0.22em] text-slate-400">Личный кабинет</div>
              <h1 className="mt-2 text-3xl font-semibold text-slate-900">Ваш профиль</h1>
            </div>
            <button
              type="button"
              onClick={() => void loadProfile()}
              className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
            >
              <RefreshCw className="h-4 w-4" />
              Обновить данные
            </button>
          </div>
        </div>

        <div className="grid gap-6 xl:grid-cols-[minmax(0,1.35fr)_minmax(360px,0.92fr)]">
          <div className="space-y-6">
            <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
              <div className="mb-6 flex flex-col gap-6 lg:flex-row lg:items-start lg:justify-between">
                <div>
                  <div className="text-lg font-semibold text-slate-900">Контактные данные</div>
                  <div className="mt-1 text-sm text-slate-500">Основная информация, контакты и мессенджеры.</div>
                </div>

                <div className="flex items-start gap-4 self-start rounded-3xl border border-slate-200 bg-slate-50 px-4 py-4">
                  {profile?.photoUrl && !profilePhotoFailed ? (
                    <img
                      src={profile.photoUrl}
                      alt={profile.fullName ?? 'Профиль'}
                      className="h-24 w-24 rounded-full object-cover shadow-sm ring-2 ring-white"
                      onError={() => setProfilePhotoFailed(true)}
                    />
                  ) : (
                    <div className="flex h-24 w-24 items-center justify-center rounded-full bg-gradient-to-br from-blue-500 to-slate-900 text-2xl font-semibold text-white shadow-sm ring-2 ring-white">
                      {profileInitials}
                    </div>
                  )}

                  <div className="space-y-2">
                    <div className="text-sm font-semibold text-slate-900">Фото профиля</div>
                    <div className="text-sm text-slate-500">
                      JPG, PNG или WEBP до 4 МБ. Перед сохранением можно выбрать область и масштаб.
                    </div>
                    <button
                      type="button"
                      onClick={() => fileInputRef.current?.click()}
                      disabled={uploadingPhoto}
                      className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {uploadingPhoto ? <Loader2 className="h-4 w-4 animate-spin" /> : <Upload className="h-4 w-4" />}
                      {profile?.photoUrl ? 'Изменить фото' : 'Загрузить фото'}
                    </button>
                    <input
                      ref={fileInputRef}
                      type="file"
                      accept=".jpg,.jpeg,.png,.webp,image/jpeg,image/png,image/webp"
                      className="hidden"
                      onChange={(event) => void handlePhotoSelected(event.target.files?.[0] ?? null)}
                    />
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
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Email</span>
                  <input
                    type="email"
                    value={form.email}
                    onChange={(event) => updateForm('email', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Телефон</span>
                  <input
                    type="tel"
                    value={form.phoneNumber}
                    onChange={(event) => updateForm('phoneNumber', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                    placeholder="+7 (999) 123-45-67"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Имя</span>
                  <input
                    type="text"
                    value={form.firstName}
                    onChange={(event) => updateForm('firstName', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Фамилия</span>
                  <input
                    type="text"
                    value={form.lastName}
                    onChange={(event) => updateForm('lastName', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Отчество</span>
                  <input
                    type="text"
                    value={form.middleName}
                    onChange={(event) => updateForm('middleName', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Дата рождения</span>
                  <input
                    type="date"
                    value={form.dateOfBirth}
                    onChange={(event) => updateForm('dateOfBirth', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
              </div>

              <div className="mt-6 grid gap-4 md:grid-cols-2 xl:grid-cols-4">
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">WhatsApp</span>
                  <input
                    type="text"
                    value={form.whatsApp}
                    onChange={(event) => updateForm('whatsApp', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Telegram</span>
                  <input
                    type="text"
                    value={form.telegram}
                    onChange={(event) => updateForm('telegram', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Instagram</span>
                  <input
                    type="text"
                    value={form.instagram}
                    onChange={(event) => updateForm('instagram', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Messenger</span>
                  <input
                    type="text"
                    value={form.messenger}
                    onChange={(event) => updateForm('messenger', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
              </div>

              <div className="mt-4 grid gap-4 md:grid-cols-2">
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Viber</span>
                  <input
                    type="text"
                    value={form.viber}
                    onChange={(event) => updateForm('viber', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
              </div>

              <div className="mt-6 flex justify-end">
                <button
                  type="button"
                  onClick={() => void handleSave()}
                  disabled={saving}
                  className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                  Сохранить профиль
                </button>
              </div>
            </div>
          </div>

          <div className="space-y-6">
            <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <div className="text-lg font-semibold text-slate-900">Напоминания задач</div>
                  <div className="mt-1 text-sm leading-6 text-slate-600">
                    Эти значения автоматически подставляются при создании новой задачи в журнале.
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => void handleTaskNotificationDefaultsSave()}
                  disabled={savingTaskNotificationDefaults}
                  className="inline-flex items-center gap-2 rounded-2xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {savingTaskNotificationDefaults ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                  Сохранить
                </button>
              </div>
              <div className="mt-5">
                <TaskNotificationDefaultsPanel
                  value={taskNotificationDefaults}
                  onChange={setTaskNotificationDefaults}
                  disabled={savingTaskNotificationDefaults}
                />
              </div>
            </div>

            <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
              <div className="flex items-center gap-3">
                <div className="flex h-12 w-12 items-center justify-center rounded-2xl bg-blue-50 text-blue-600">
                  <ShieldCheck className="h-6 w-6" />
                </div>
                <div>
                  <div className="text-lg font-semibold text-slate-900">Способы входа</div>
                  <div className="text-sm text-slate-600">
                    Привязывайте дополнительные провайдеры и входите в один профиль любым доступным способом.
                  </div>
                </div>
              </div>

              <div className="mt-5 space-y-4">
                {linkableProviders.length > 0 ? (
                  <div className="rounded-3xl border border-slate-200 bg-slate-50 p-5">
                    <div className="text-sm font-semibold text-slate-900">Добавить способ входа</div>
                    <div className="mt-1 text-sm text-slate-600">
                      Подключите ещё один сервис, чтобы входить в этот профиль через любой доступный провайдер.
                    </div>
                    <div className="mt-4 flex flex-wrap gap-3">
                      {linkableProviders.map((item) => {
                        const meta = PROVIDER_META[item.id];
                        const isLinking = linkingProvider === item.id;
                        return (
                          <button
                            key={item.id}
                            type="button"
                            onClick={() => void handleLink(item.id)}
                            disabled={isLinking || unlinkingProvider !== null || uploadingPhoto}
                            className="inline-flex items-center gap-2 rounded-2xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-900 transition hover:border-slate-300 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            {isLinking ? <Loader2 className="h-4 w-4 animate-spin" /> : <Link2 className="h-4 w-4" />}
                            Подключить {meta.label}
                          </button>
                        );
                      })}
                    </div>
                  </div>
                ) : null}

                {linkedProviders.map((item) => {
                  const meta = PROVIDER_META[item.id];
                  const isLinking = linkingProvider === item.id;
                  const isUnlinking = unlinkingProvider === item.id;

                  return (
                    <div key={item.id} className={`rounded-3xl border bg-gradient-to-br p-5 ${meta.accent}`}>
                      <div className="flex flex-wrap items-start justify-between gap-4">
                        <div>
                          <div className="text-lg font-semibold text-slate-900">{meta.label}</div>
                          <div className="mt-1 text-sm text-slate-600">{meta.description}</div>
                          {item.identity ? (
                            <div className="mt-3 space-y-1 text-sm text-slate-600">
                              {item.identity.displayName ? <div>Имя провайдера: {item.identity.displayName}</div> : null}
                              {item.identity.providerEmail ? <div>Email: {item.identity.providerEmail}</div> : null}
                              {item.identity.providerUsername ? <div>Username: {item.identity.providerUsername}</div> : null}
                            </div>
                          ) : null}
                        </div>

                        <div className="flex flex-col items-end gap-2">
                          <span className="inline-flex rounded-full bg-emerald-100 px-3 py-1 text-xs font-semibold text-emerald-700">
                            Привязан
                          </span>
                          <button
                            type="button"
                            onClick={() => void handleDetach(item.id)}
                            disabled={isUnlinking || isLinking}
                            className="inline-flex items-center gap-2 rounded-2xl border border-rose-200 bg-white px-4 py-2 text-sm font-semibold text-rose-700 transition hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-60"
                          >
                            {isUnlinking ? <Loader2 className="h-4 w-4 animate-spin" /> : <Unlink2 className="h-4 w-4" />}
                            Отвязать
                          </button>
                        </div>
                      </div>
                    </div>
                  );
                })}

                {!linkableProviders.length && linkedProviders.length > 0 ? (
                  <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600">
                    Все доступные способы входа уже привязаны к вашему профилю.
                  </div>
                ) : null}
              </div>
            </div>

            <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
              <div className="flex items-center gap-3">
                <div className="flex h-12 w-12 items-center justify-center rounded-2xl bg-amber-50 text-amber-600">
                  <KeyRound className="h-6 w-6" />
                </div>
                <div>
                  <div className="text-lg font-semibold text-slate-900">Смена пароля</div>
                  <div className="text-sm text-slate-600">
                    {hasExternalLogin
                      ? 'Текущий пароль можно не вводить, если вы уже входите через привязанные внешние сервисы.'
                      : 'Введите текущий пароль, чтобы подтвердить смену.'}
                  </div>
                </div>
              </div>

              <div className="mt-5 space-y-4">
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">
                    Текущий пароль{hasExternalLogin ? ' (необязательно)' : ''}
                  </span>
                  <input
                    type="password"
                    value={passwordForm.currentPassword}
                    onChange={(event) => updatePasswordForm('currentPassword', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Новый пароль</span>
                  <input
                    type="password"
                    value={passwordForm.newPassword}
                    onChange={(event) => updatePasswordForm('newPassword', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Подтверждение нового пароля</span>
                  <input
                    type="password"
                    value={passwordForm.confirmPassword}
                    onChange={(event) => updatePasswordForm('confirmPassword', event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  />
                </label>
              </div>

              <div className="mt-6 flex justify-end">
                <button
                  type="button"
                  onClick={() => void handlePasswordSave()}
                  disabled={changingPassword}
                  className="inline-flex items-center gap-2 rounded-2xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {changingPassword ? <Loader2 className="h-4 w-4 animate-spin" /> : <KeyRound className="h-4 w-4" />}
                  Изменить пароль
                </button>
              </div>
            </div>

            {profile ? (
              <ObjectAuditLauncher
                objectType="user"
                objectId={profile.id}
                createdAt={profile.createdAt}
                createdByUserName={profile.createdByUserName}
                updatedAt={profile.updatedAt}
                updatedByUserName={profile.updatedByUserName}
                triggerTitle="Аудит профиля"
                triggerDescription="Паспорт профиля и история изменений открываются в отдельном окне."
                title="История профиля"
              />
            ) : null}
          </div>
        </div>
      </div>

      <ProfilePhotoCropModal
        open={Boolean(pendingPhotoCrop)}
        imageSrc={pendingPhotoCrop?.src ?? null}
        fileName={pendingPhotoCrop?.fileName ?? 'profile-photo'}
        mimeType={pendingPhotoCrop?.mimeType ?? 'image/png'}
        submitting={uploadingPhoto}
        onClose={closeCropModal}
        onConfirm={uploadProfilePhoto}
      />
    </div>
  );
}
