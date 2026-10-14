import { SystemDateInput } from '../ui/SystemDateInput';
import { restoredPhotoUrl } from '../../services/restoredRealtime';
import { SystemSelect } from '../ui/SystemSelect';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { KeyRound, Link2, Loader2, RefreshCw, Save, ShieldCheck, Unlink2, Upload } from 'lucide-react';
import {
  authService,
  type AuthProviderPublicSettings,
  type ContractorBalance,
  type ContractorBalanceItem,
  type ExternalIdentity,
  type UpdateProfileDto,
  type User,
} from '../../services/authService';
import { apiService } from '../../services/api';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import {
  DEFAULT_TASK_NOTIFICATION_DEFAULTS,
  parseTaskNotificationDefaults,
  serializeTaskNotificationDefaults,
  TASK_NOTIFICATION_DEFAULTS_SETTING_KEY,
  type TaskNotificationDefaults,
} from '../../utils/taskNotificationDefaults';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_OPTIONS, USER_TIME_ZONE_SETTING_KEY } from '../../utils/timeZoneOptions';
import { ProfilePhotoCropModal } from './ProfilePhotoCropModal';
import { GoogleCalendarPanel } from './GoogleCalendarPanel';
import { YandexCalendarPanel } from './YandexCalendarPanel';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';
import { TaskNotificationDefaultsPanel } from '../Tasks/TaskNotificationDefaultsPanel';
import { WebPushDevicePanel } from '../Notifications/WebPushDevicePanel';
import { MaxMessengerPanel } from './MaxMessengerPanel';

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

type ProfileSectionTab = 'profile' | 'tasks' | 'calendars' | 'security' | 'audit';

type ContractorBalanceGroup = {
  key: string;
  clientId?: number | null;
  clientCaseId?: number | null;
  clientLabel: string;
  caseLabel: string;
  outstandingAmount: number;
  paidAmount: number;
  amount: number;
  items: ContractorBalanceItem[];
};

type TimeZoneSettingLoadResult = {
  value?: string | null;
  missing: boolean;
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

const EMPTY_CONTRACTOR_BALANCE: ContractorBalance = {
  owedToMe: 0,
  owedByMe: 0,
  paidToMe: 0,
  paidByMe: 0,
  queue: [],
  paid: [],
};

const RELATED_LINK_CLASS = 'truncate font-semibold text-blue-700 hover:text-blue-900 hover:underline';
const RELATED_LINK_PROPS = { target: '_blank', rel: 'noreferrer' } as const;

const PROFILE_SECTION_TABS: Array<{ id: ProfileSectionTab; label: string; description: string }> = [
  {
    id: 'profile',
    label: 'Профиль',
    description: 'Контакты, фото и часовой пояс',
  },
  {
    id: 'tasks',
    label: 'Задачи',
    description: 'Напоминания и расчеты',
  },
  {
    id: 'calendars',
    label: 'Календари',
    description: 'Google и Яндекс',
  },
  {
    id: 'security',
    label: 'Вход и безопасность',
    description: 'Способы входа и пароль',
  },
  {
    id: 'audit',
    label: 'Аудит',
    description: 'История изменений',
  },
];

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

// eslint-disable-next-line react-refresh/only-export-components -- чистая функция экспортируется для проверки контракта формы.
export function buildProfileUpdatePayload(form: FormState): UpdateProfileDto {
  return {
    email: form.email.trim() || undefined,
    fullName: form.fullName.trim(),
    firstName: form.firstName.trim(),
    lastName: form.lastName.trim(),
    middleName: form.middleName.trim(),
    dateOfBirth: form.dateOfBirth || undefined,
    clearDateOfBirth: !form.dateOfBirth,
    phoneNumber: form.phoneNumber.trim(),
    whatsApp: form.whatsApp.trim(),
    telegram: form.telegram.trim(),
    instagram: form.instagram.trim(),
    messenger: form.messenger.trim(),
    viber: form.viber.trim(),
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

function formatContractorPaymentDetails(item: ContractorBalanceItem) {
  const details: string[] = [];
  const paymentDate = formatOptionalDate(item.paymentDate);
  const accountDate = formatOptionalDate(item.paymentAccountDate);
  const documentDate = formatOptionalDate(item.paymentDocumentDate);

  if (paymentDate) {
    details.push(`дата платежа ${paymentDate}`);
  }

  if (item.paymentSourceName?.trim()) {
    details.push(`источник ${item.paymentSourceName.trim()}`);
  }

  if (item.paymentAccount?.trim()) {
    details.push(`счет ${item.paymentAccount.trim()}${accountDate ? ` от ${accountDate}` : ''}`);
  } else if (accountDate) {
    details.push(`дата счета ${accountDate}`);
  }

  if (item.paymentOrderNumber?.trim()) {
    details.push(`документ ${item.paymentOrderNumber.trim()}${documentDate ? ` от ${documentDate}` : ''}`);
  } else if (documentDate) {
    details.push(`дата документа ${documentDate}`);
  }

  return details;
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

function isMissingUserSettingError(error: unknown) {
  return error instanceof Error && error.message.includes('HTTP 404');
}

async function loadTimeZoneSetting(): Promise<TimeZoneSettingLoadResult> {
  try {
    const response = await apiService.getUserSetting(USER_TIME_ZONE_SETTING_KEY);
    return { value: response?.value, missing: false };
  } catch (error) {
    return { value: null, missing: isMissingUserSettingError(error) };
  }
}

export function ProfilePage() {
  const { refreshUser } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();

  const [profile, setProfile] = useState<User | null>(null);
  const permissions = useRolePermissions(profile?.role?.id);
  const [publicSettings, setPublicSettings] = useState<AuthProviderPublicSettings>(EMPTY_PUBLIC_SETTINGS);
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [timeZoneId, setTimeZoneId] = useState(() => normalizeUserTimeZoneId());
  const [taskNotificationDefaults, setTaskNotificationDefaults] = useState<TaskNotificationDefaults>(DEFAULT_TASK_NOTIFICATION_DEFAULTS);
  const [contractorBalance, setContractorBalance] = useState<ContractorBalance>(EMPTY_CONTRACTOR_BALANCE);
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
  const [activeSectionTab, setActiveSectionTab] = useState<ProfileSectionTab>('profile');
  const canViewProfileAudit = profile?.role?.name === 'admin' || permissions.audit?.canView === true;
  const canViewProfileCalendars = profile?.role?.name === 'admin' || permissions.calendar?.canView === true;
  const visibleProfileSectionTabs = useMemo(
    () => PROFILE_SECTION_TABS.filter((tab) => {
      if (tab.id === 'audit') return canViewProfileAudit;
      if (tab.id === 'calendars') return canViewProfileCalendars;
      return true;
    }),
    [canViewProfileAudit, canViewProfileCalendars],
  );
  const canUseEmployeeCalendarMode = profile?.role?.name === 'admin'
    || permissions.calendar.viewScope === 'all'
    || permissions.calendar.viewScope === 'department'
    || permissions.calendar.viewScope === 'specific';
  const canOpenContractorPayments = permissions.payments?.canView === true;

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
        clientId: item.clientId,
        clientCaseId: item.clientCaseId,
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

  const popupRef = useRef<Window | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  const resetPhotoInput = useCallback(() => {
    if (fileInputRef.current) {
      fileInputRef.current.value = '';
    }
  }, []);

  const loadProfile = useCallback(async () => {
    const [nextProfile, nextProviders, nextTaskNotificationDefaults, nextTimeZone, nextContractorBalance] = await Promise.all([
      authService.getProfile(),
      authService.getAuthProviders(),
      apiService.getUserSetting(TASK_NOTIFICATION_DEFAULTS_SETTING_KEY).catch(() => null),
      loadTimeZoneSetting(),
      authService.getContractorBalance().catch(() => EMPTY_CONTRACTOR_BALANCE),
    ]);

    setProfile(nextProfile);
    setForm(buildFormState(nextProfile));
    setPublicSettings(nextProviders);
    setTaskNotificationDefaults(parseTaskNotificationDefaults(nextTaskNotificationDefaults?.value));
    const nextTimeZoneId = normalizeUserTimeZoneId(nextTimeZone.value);
    setTimeZoneId(nextTimeZoneId);
    if (nextTimeZone.missing) {
      void apiService.upsertUserSetting(USER_TIME_ZONE_SETTING_KEY, nextTimeZoneId).catch(() => undefined);
    }
    setContractorBalance(nextContractorBalance);
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

  useEffect(() => {
    if (activeSectionTab === 'audit' && !canViewProfileAudit) {
      setActiveSectionTab('profile');
    }
    if (activeSectionTab === 'calendars' && !canViewProfileCalendars) {
      setActiveSectionTab('profile');
    }
  }, [activeSectionTab, canViewProfileAudit, canViewProfileCalendars]);

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
      const payload = buildProfileUpdatePayload(form);

      const nextProfile = await authService.updateProfile(payload);
      await apiService.upsertUserSetting(USER_TIME_ZONE_SETTING_KEY, timeZoneId);
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

  const showContractorBalance = Boolean(profile?.isExternalContractor || contractorBalanceItems.length > 0);

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

        <div className="rounded-3xl border border-slate-200 bg-white p-2 shadow-sm">
          <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-5" role="tablist" aria-label="Разделы профиля пользователя">
            {visibleProfileSectionTabs.map((tab) => {
              const isActive = activeSectionTab === tab.id;
              return (
                <button
                  key={tab.id}
                  type="button"
                  role="tab"
                  aria-selected={isActive}
                  onClick={() => setActiveSectionTab(tab.id)}
                  className={`rounded-2xl px-4 py-3 text-left transition ${
                    isActive
                      ? 'bg-blue-600 text-white shadow-sm'
                      : 'text-slate-600 hover:bg-slate-50 hover:text-slate-900'
                  }`}
                >
                  <span className="block text-sm font-semibold">{tab.label}</span>
                  <span className={`mt-1 block text-xs ${isActive ? 'text-blue-100' : 'text-slate-400'}`}>{tab.description}</span>
                </button>
              );
            })}
          </div>
        </div>

        {activeSectionTab === 'tasks' && showContractorBalance ? (
          <div className="rounded-3xl border border-emerald-100 bg-white p-6 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div>
                <div className="text-lg font-semibold text-slate-900">Расчеты по задачам</div>
                <div className="mt-1 text-sm text-slate-500">Выплаты организации вам как привлеченному исполнителю по выполненным и ожидающим задачам.</div>
              </div>
              <div className="grid gap-3 sm:grid-cols-3">
                <div className="rounded-2xl border border-emerald-100 bg-emerald-50 px-4 py-3">
                  <div className="text-xs font-semibold uppercase tracking-[0.16em] text-emerald-700">К выплате мне</div>
                  <div className="mt-1 text-2xl font-semibold text-emerald-900">{formatCurrencySmart(contractorBalance.owedToMe).full}</div>
                </div>
                <div className="rounded-2xl border border-sky-100 bg-sky-50 px-4 py-3">
                  <div className="text-xs font-semibold uppercase tracking-[0.16em] text-sky-700">Выплачено мне</div>
                  <div className="mt-1 text-2xl font-semibold text-sky-900">{formatCurrencySmart(contractorBalance.paidToMe).full}</div>
                </div>
                <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
                  <div className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-500">Задач</div>
                  <div className="mt-1 text-2xl font-semibold text-slate-900">{contractorBalanceItems.length}</div>
                </div>
              </div>
            </div>
            {contractorBalanceGroups.length === 0 ? (
              <div className="mt-5 rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-4 py-5 text-sm text-slate-500">
                По вашим задачам пока нет выплат через блок «Оплата исполнителю».
              </div>
            ) : (
              <div className="mt-5 space-y-3">
                {contractorBalanceGroups.map((group) => (
                  <div key={group.key} className="rounded-2xl border border-slate-200 bg-slate-50 p-4">
                    <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                      <div className="min-w-0">
                        {group.clientId ? (
                          <a href={`/clients/${group.clientId}`} {...RELATED_LINK_PROPS} className={`block text-sm ${RELATED_LINK_CLASS}`}>
                            {group.clientLabel}
                          </a>
                        ) : (
                          <div className="truncate text-sm font-semibold text-slate-900">{group.clientLabel}</div>
                        )}
                        {group.clientCaseId ? (
                          <a href={`/cases/${group.clientCaseId}`} {...RELATED_LINK_PROPS} className={`mt-1 block text-xs ${RELATED_LINK_CLASS}`}>
                            {group.caseLabel}
                          </a>
                        ) : (
                          <div className="mt-1 truncate text-xs text-slate-500">{group.caseLabel}</div>
                        )}
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
                      {group.items.map((item) => (
                        <div key={`${item.taskId}-${item.paymentId ?? 'planned'}`} className="flex flex-col gap-2 rounded-xl bg-white px-3 py-2 text-sm lg:flex-row lg:items-center lg:justify-between">
                          <div className="min-w-0">
                            <a href={`/tasks/${item.taskId}`} {...RELATED_LINK_PROPS} className="block truncate font-medium text-blue-700 hover:text-blue-900 hover:underline">
                              {item.taskTitle}
                            </a>
                            <div className="mt-1 text-xs text-slate-500">Постановщик: {item.authorName || '—'}</div>
                            <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-slate-500">
                              <span className={`rounded-full px-2 py-1 font-semibold ${contractorPaymentStatusClassName(item.paymentStatus)}`}>
                                {formatContractorPaymentStatus(item.paymentStatus)}
                              </span>
                              {item.paymentDueDate ? <span>срок {formatOptionalDate(item.paymentDueDate)}</span> : null}
                              {item.paidDate ? <span>оплачено {formatOptionalDate(item.paidDate)}</span> : null}
                              {formatContractorPaymentDetails(item).map((detail) => (
                                <span key={detail}>{detail}</span>
                              ))}
                              <span>остаток {formatCurrencySmart(item.outstandingAmount).full}</span>
                            </div>
                          </div>
                          <div className="flex shrink-0 flex-wrap items-center gap-3 lg:justify-end">
                            {item.paymentId ? (
                              canOpenContractorPayments ? (
                                <a
                                  href={`/payments/${item.paymentId}?ppTab=paymentsExpense`}
                                  target="_blank"
                                  rel="noreferrer"
                                  className="text-xs font-semibold text-slate-500 hover:text-blue-700 hover:underline"
                                >
                                  Платеж #{item.paymentId}
                                </a>
                              ) : (
                                <span className="text-xs font-semibold text-slate-500">Платеж #{item.paymentId}</span>
                              )
                            ) : (
                              <span className="max-w-[260px] text-right text-xs font-medium leading-5 text-slate-500">
                                {formatMissingContractorPaymentHint(item.paymentStatus)}
                              </span>
                            )}
                            <div className="font-semibold text-slate-900">{formatCurrencySmart(item.amount).full}</div>
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        ) : null}

        {activeSectionTab === 'profile' ? (
          <div className="space-y-6" role="tabpanel" aria-label="Профиль пользователя">
            <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
              <div className="mb-6 flex flex-col gap-6 lg:flex-row lg:items-start lg:justify-between">
                <div>
                  <div className="text-lg font-semibold text-slate-900">Контактные данные</div>
                  <div className="mt-1 text-sm text-slate-500">Основная информация, контакты и мессенджеры.</div>
                </div>

                <div className="flex items-start gap-4 self-start rounded-3xl border border-slate-200 bg-slate-50 px-4 py-4">
                  {profile && restoredPhotoUrl(profile.photoUrl) && !profilePhotoFailed ? (
                    <img
                      src={restoredPhotoUrl(profile?.photoUrl)}
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
                  <SystemDateInput
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
                <label className="space-y-2">
                  <span className="text-sm font-medium text-slate-700">Часовой пояс</span>
                  <SystemSelect
                    value={timeZoneId}
                    onChange={(event) => setTimeZoneId(event.target.value)}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                  >
                    {USER_TIME_ZONE_OPTIONS.map((option) => (
                      <option key={option.value} value={option.value}>{option.label}</option>
                    ))}
                  </SystemSelect>
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

            {profile ? <MaxMessengerPanel key={profile.id} /> : null}

            <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
              <WebPushDevicePanel
                title="Push-уведомления"
                description="Включите системные уведомления браузера, чтобы видеть новые задачи, изменения сроков и важные события даже без открытого центра уведомлений."
                className="border-blue-100 bg-blue-50/40"
              />
            </div>
          </div>
        ) : null}

        {activeSectionTab === 'tasks' ? (
          <div className="space-y-6" role="tabpanel" aria-label="Настройки задач профиля">
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
          </div>
        ) : null}

        {activeSectionTab === 'calendars' && canViewProfileCalendars ? (
          <div className="grid gap-6 xl:grid-cols-2" role="tabpanel" aria-label="Календари профиля">
            <GoogleCalendarPanel canUseEmployeeCalendarMode={canUseEmployeeCalendarMode} />
            <YandexCalendarPanel canUseEmployeeCalendarMode={canUseEmployeeCalendarMode} />
          </div>
        ) : null}

        {activeSectionTab === 'security' ? (
          <div className="space-y-6" role="tabpanel" aria-label="Вход и безопасность профиля">
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

          </div>
        ) : null}

        {activeSectionTab === 'audit' && profile && canViewProfileAudit ? (
          <div className="space-y-6" role="tabpanel" aria-label="Аудит профиля">
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
          </div>
        ) : null}
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
