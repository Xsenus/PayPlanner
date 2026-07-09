import { useCallback, useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react';
import { ArrowLeft, Bell, Bot, CheckCircle2, Link2, Mail, MessageSquare, Monitor, Pencil, Plus, RefreshCw, Save, Send, Smartphone, Trash2, Unlink } from 'lucide-react';
import {
  apiService,
  type NotificationRolePreferenceDto,
  type NotificationChannel,
  type NotificationChannelDto,
  type NotificationDeliveryDto,
  type TelegramBotCommandDto,
  type NotificationPurposeDto,
  type NotificationSenderDto,
  type NotificationSettingsDto,
  type NotificationTemplateDto,
  type UpsertNotificationSenderRequest,
  type UserNotificationPreferenceDto,
  type UserNotificationTypeDto,
  type WebPushDiagnosticsDto,
} from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import {
  isUserNotificationChannelEnabled,
  isUserNotificationTypeEnabled,
  normalizeUserNotificationTypePreferences,
  toggleUserNotificationChannelPreference,
  toggleUserNotificationTypePreference,
  type UserNotificationPreferenceChannel,
  type UserNotificationTypePreferences,
} from '../../utils/userNotificationPreferences';
import { AdminWebPushGuide } from '../Notifications/NotificationHelp';

type SenderForm = {
  id?: number;
  channel: NotificationChannel;
  name: string;
  isEnabled: boolean;
  purposes: string[];
  sortOrder: number;
  smtpHost: string;
  smtpPort: number;
  smtpUsername: string;
  smtpPassword: string;
  keepExistingPassword: boolean;
  fromEmail: string;
  fromName: string;
  enableSsl: boolean;
  botToken: string;
  keepExistingToken: boolean;
  botUsername: string;
  updateMode: 'disabled' | 'webhook' | 'polling';
  webhookSecret: string;
  keepExistingWebhookSecret: boolean;
  proxyUrl: string;
  proxyUsername: string;
  proxyPassword: string;
  keepExistingProxyPassword: boolean;
  profileName: string;
  profileDescription: string;
  profileShortDescription: string;
  profileCommandsText: string;
};

type SmsSettingsForm = {
  enabled: boolean;
  providerKey: string;
  senderName: string;
  apiToken: string;
  keepExistingApiToken: boolean;
  dailyLimit: number;
  maxMessageLength: number;
  notes: string;
};

const DEFAULT_PURPOSES = ['manualTest'];
const ROLE_CHANNEL_OPTIONS: Array<{ value: UserNotificationPreferenceChannel; label: string; Icon: typeof Monitor }> = [
  { value: 'inApp', label: 'Центр', Icon: Monitor },
  { value: 'toast', label: 'Всплывающие', Icon: Bell },
  { value: 'forcedModal', label: 'Окно', Icon: Monitor },
  { value: 'webPush', label: 'Push', Icon: Smartphone },
  { value: 'email', label: 'Email', Icon: Mail },
  { value: 'telegram', label: 'Telegram', Icon: Send },
  { value: 'sms', label: 'SMS', Icon: Smartphone },
];

const TYPE_DOMAIN_ORDER = ['task', 'calendar', 'note', 'payment', 'invoice', 'act', 'contract', 'case', 'court', 'system'];

function groupNotificationTypesByDomain(types: UserNotificationTypeDto[]) {
  const order = new Map(TYPE_DOMAIN_ORDER.map((key, index) => [key, index]));
  const groups = new Map<string, { key: string; label: string; items: UserNotificationTypeDto[] }>();

  for (const item of types) {
    const key = item.domain || 'system';
    const group = groups.get(key) ?? { key, label: item.domainLabel || 'Система', items: [] };
    group.items.push(item);
    groups.set(key, group);
  }

  return Array.from(groups.values())
    .sort((left, right) => (order.get(left.key) ?? 999) - (order.get(right.key) ?? 999));
}

function notificationPriorityLabel(priority?: string) {
  switch ((priority ?? '').toLowerCase()) {
    case 'critical':
      return 'Критичный приоритет';
    case 'high':
      return 'Высокий приоритет';
    case 'silent':
      return 'Тихое уведомление';
    default:
      return 'Обычный приоритет';
  }
}

function notificationPriorityBadgeClass(priority?: string) {
  switch ((priority ?? '').toLowerCase()) {
    case 'critical':
      return 'bg-rose-50 text-rose-700';
    case 'high':
      return 'bg-amber-50 text-amber-700';
    case 'silent':
      return 'bg-slate-100 text-slate-500';
    default:
      return 'bg-blue-50 text-blue-700';
  }
}

type SenderRoute =
  | { mode: 'list' }
  | { mode: 'new'; channel: NotificationChannel }
  | { mode: 'edit'; channel: NotificationChannel; id: number };

const SETTINGS_SECTION_PARAM = 'settingsSection';
const SETTINGS_NOTIFICATION_SECTION = 'notifications';
const NOTIFICATION_CHANNEL_PARAM = 'notificationChannel';
const NOTIFICATION_SENDER_PARAM = 'notificationSender';

function isNotificationChannel(value: string | null): value is NotificationChannel {
  return value === 'email' || value === 'telegram';
}

function readNotificationRoute(): SenderRoute {
  if (typeof window === 'undefined') {
    return { mode: 'list' };
  }

  const params = new URLSearchParams(window.location.search);
  const channel = params.get(NOTIFICATION_CHANNEL_PARAM);
  const sender = params.get(NOTIFICATION_SENDER_PARAM);

  if (!isNotificationChannel(channel) || !sender) {
    return { mode: 'list' };
  }

  if (sender === 'new') {
    return { mode: 'new', channel };
  }

  const id = Number(sender);
  return Number.isInteger(id) && id > 0
    ? { mode: 'edit', channel, id }
    : { mode: 'list' };
}

function buildNotificationRouteHref(route: SenderRoute) {
  if (typeof window === 'undefined') {
    return '';
  }

  const url = new URL(window.location.href);
  url.searchParams.set(SETTINGS_SECTION_PARAM, SETTINGS_NOTIFICATION_SECTION);

  if (route.mode === 'list') {
    url.searchParams.delete(NOTIFICATION_CHANNEL_PARAM);
    url.searchParams.delete(NOTIFICATION_SENDER_PARAM);
  } else {
    url.searchParams.set(NOTIFICATION_CHANNEL_PARAM, route.channel);
    url.searchParams.set(NOTIFICATION_SENDER_PARAM, route.mode === 'new' ? 'new' : String(route.id));
  }

  return `${url.pathname}${url.search}${url.hash}`;
}

function pushNotificationRoute(route: SenderRoute) {
  if (typeof window === 'undefined') return;
  window.history.pushState(null, '', buildNotificationRouteHref(route));
}

type NotificationTemplateDraft = {
  key: string;
  isEnabled: boolean;
  subject: string;
  body: string;
};

interface NotificationSettingsProps {
  canEdit?: boolean;
}

function createBlankForm(channel: NotificationChannel = 'email', nextOrder = 10): SenderForm {
  return {
    channel,
    name: channel === 'telegram' ? 'Telegram bot' : 'Email отправитель',
    isEnabled: true,
    purposes: DEFAULT_PURPOSES,
    sortOrder: nextOrder,
    smtpHost: '',
    smtpPort: 587,
    smtpUsername: '',
    smtpPassword: '',
    keepExistingPassword: true,
    fromEmail: '',
    fromName: 'PayPlanner',
    enableSsl: true,
    botToken: '',
    keepExistingToken: true,
    botUsername: '',
    updateMode: 'disabled',
    webhookSecret: '',
    keepExistingWebhookSecret: true,
    proxyUrl: '',
    proxyUsername: '',
    proxyPassword: '',
    keepExistingProxyPassword: true,
    profileName: '',
    profileDescription: '',
    profileShortDescription: '',
    profileCommandsText: defaultCommandsText(),
  };
}

function smsFormFromSettings(settings?: NotificationSettingsDto | null): SmsSettingsForm {
  return {
    enabled: settings?.sms.enabled ?? false,
    providerKey: settings?.sms.providerKey ?? '',
    senderName: settings?.sms.senderName ?? '',
    apiToken: '',
    keepExistingApiToken: true,
    dailyLimit: settings?.sms.dailyLimit ?? 100,
    maxMessageLength: settings?.sms.maxMessageLength ?? 480,
    notes: settings?.sms.notes ?? '',
  };
}

function formFromSender(sender: NotificationSenderDto): SenderForm {
  return {
    id: sender.id,
    channel: sender.channel,
    name: sender.name,
    isEnabled: sender.isEnabled,
    purposes: sender.purposes.length ? sender.purposes : DEFAULT_PURPOSES,
    sortOrder: sender.sortOrder,
    smtpHost: sender.email?.smtpHost ?? '',
    smtpPort: sender.email?.smtpPort ?? 587,
    smtpUsername: sender.email?.smtpUsername ?? '',
    smtpPassword: '',
    keepExistingPassword: true,
    fromEmail: sender.email?.fromEmail ?? '',
    fromName: sender.email?.fromName ?? 'PayPlanner',
    enableSsl: sender.email?.enableSsl ?? true,
    botToken: '',
    keepExistingToken: true,
    botUsername: sender.telegram?.botUsername ?? '',
    updateMode: normalizeUpdateMode(sender.telegram?.updateMode),
    webhookSecret: '',
    keepExistingWebhookSecret: true,
    proxyUrl: sender.telegram?.proxyUrl ?? '',
    proxyUsername: sender.telegram?.proxyUsername ?? '',
    proxyPassword: '',
    keepExistingProxyPassword: true,
    profileName: sender.telegram?.profileName ?? '',
    profileDescription: sender.telegram?.profileDescription ?? '',
    profileShortDescription: sender.telegram?.profileShortDescription ?? '',
    profileCommandsText: commandsToText(sender.telegram?.profileCommands),
  };
}

function channelLabel(channel: NotificationChannel) {
  if (channel === 'telegram') return 'Telegram';
  if (channel === 'sms') return 'SMS';
  return 'Email';
}

function channelCatalogIcon(key: string): typeof Monitor {
  if (key === 'email') return Mail;
  if (key === 'telegram') return Send;
  if (key === 'webPush') return Bell;
  if (key === 'sms') return Smartphone;
  if (key === 'max') return MessageSquare;
  return Monitor;
}

function channelCatalogStatusLabel(channel: NotificationChannelDto) {
  if (channel.isEnabled && channel.isSendable) return channel.key === 'inApp' ? 'Работает в CRM' : 'Подключаемый';
  return channel.isFuture ? 'Запланирован' : 'Отключен';
}

function channelCatalogStatusClass(channel: NotificationChannelDto) {
  if (channel.isEnabled && channel.isSendable) return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  return channel.isFuture ? 'border-amber-200 bg-amber-50 text-amber-700' : 'border-gray-200 bg-gray-50 text-gray-600';
}

function statusClass(ready: boolean, enabled: boolean) {
  if (!enabled) return 'bg-slate-100 text-slate-700 border-slate-200';
  return ready ? 'bg-emerald-50 text-emerald-700 border-emerald-200' : 'bg-amber-50 text-amber-700 border-amber-200';
}

function toApiNotificationPreference(preferences: UserNotificationTypePreferences): UserNotificationPreferenceDto {
  return {
    disabledTypes: preferences.disabledTypes,
    disabledChannels: preferences.disabledChannels,
  };
}

export function NotificationSettings({ canEdit = true }: NotificationSettingsProps) {
  const toast = useToast();
  const { confirm } = useDialog();
  const [settings, setSettings] = useState<NotificationSettingsDto | null>(null);
  const [channelCatalog, setChannelCatalog] = useState<NotificationChannelDto[]>([]);
  const [webPushDiagnostics, setWebPushDiagnostics] = useState<WebPushDiagnosticsDto | null>(null);
  const [smsForm, setSmsForm] = useState<SmsSettingsForm>(() => smsFormFromSettings(null));
  const [form, setForm] = useState<SenderForm>(() => createBlankForm());
  const [route, setRoute] = useState<SenderRoute>(() => readNotificationRoute());
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [smsSaving, setSmsSaving] = useState(false);
  const [emailPolicySaving, setEmailPolicySaving] = useState(false);
  const [templateSaving, setTemplateSaving] = useState(false);
  const [selectedTemplateKey, setSelectedTemplateKey] = useState('');
  const [templateDraft, setTemplateDraft] = useState<NotificationTemplateDraft | null>(null);
  const [testing, setTesting] = useState(false);
  const [testRecipient, setTestRecipient] = useState('');
  const [testSubject, setTestSubject] = useState('Тестовое уведомление PayPlanner');
  const [testMessage, setTestMessage] = useState('Проверка отправителя уведомлений.');
  const [message, setMessage] = useState<string | null>(null);
  const [deliveries, setDeliveries] = useState<NotificationDeliveryDto[]>([]);
  const [deliveriesTotal, setDeliveriesTotal] = useState(0);
  const [deliveriesLoading, setDeliveriesLoading] = useState(false);
  const [deliveryStatus, setDeliveryStatus] = useState('failed');
  const [retryingDeliveryId, setRetryingDeliveryId] = useState<number | null>(null);
  const [notificationTypes, setNotificationTypes] = useState<UserNotificationTypeDto[]>([]);
  const [rolePreferences, setRolePreferences] = useState<NotificationRolePreferenceDto[]>([]);
  const [selectedRoleId, setSelectedRoleId] = useState<number | null>(null);
  const [rolePreferencesLoading, setRolePreferencesLoading] = useState(false);
  const [rolePreferencesSaving, setRolePreferencesSaving] = useState(false);

  const loadSettings = useCallback(async () => {
    setLoading(true);
    setMessage(null);
    try {
      const [next, channels, pushDiagnostics] = await Promise.all([
        apiService.getNotificationSettings(),
        apiService.getNotificationChannels(),
        apiService.getWebPushDiagnostics().catch(() => null),
      ]);
      setSettings(next);
      setSmsForm(smsFormFromSettings(next));
      setChannelCatalog(channels);
      setWebPushDiagnostics(pushDiagnostics);
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось загрузить настройки уведомлений');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    void loadSettings();
  }, [loadSettings]);

  const loadRolePreferences = useCallback(async () => {
    setRolePreferencesLoading(true);
    try {
      const [types, response] = await Promise.all([
        apiService.getUserNotificationTypes(),
        apiService.getNotificationRolePreferences(),
      ]);
      setNotificationTypes(types);
      setRolePreferences(response.roles);
      setSelectedRoleId((current) => {
        if (current && response.roles.some((item) => item.roleId === current)) {
          return current;
        }

        return response.roles[0]?.roleId ?? null;
      });
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось загрузить ролевые настройки уведомлений');
      toast.error(text, 'Уведомления');
      setNotificationTypes([]);
      setRolePreferences([]);
      setSelectedRoleId(null);
    } finally {
      setRolePreferencesLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    void loadRolePreferences();
  }, [loadRolePreferences]);

  const loadDeliveries = useCallback(async () => {
    setDeliveriesLoading(true);
    try {
      const result = await apiService.getNotificationDeliveries({
        status: deliveryStatus === 'all' ? null : deliveryStatus,
        page: 1,
        pageSize: 20,
      });
      setDeliveries(result.items);
      setDeliveriesTotal(result.total);
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось загрузить журнал доставки');
      toast.error(text, 'Уведомления');
      setDeliveries([]);
      setDeliveriesTotal(0);
    } finally {
      setDeliveriesLoading(false);
    }
  }, [deliveryStatus, toast]);

  useEffect(() => {
    if (route.mode === 'list') {
      void loadDeliveries();
    }
  }, [loadDeliveries, route.mode]);

  useEffect(() => {
    const handlePopState = () => setRoute(readNotificationRoute());
    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, []);

  const senders = useMemo(() => settings?.senders ?? [], [settings?.senders]);
  const purposes = useMemo(() => settings?.purposes ?? [], [settings?.purposes]);
  const emailSenders = useMemo(() => senders.filter((item) => item.channel === 'email'), [senders]);
  const telegramSenders = useMemo(() => senders.filter((item) => item.channel === 'telegram'), [senders]);
  const selectedSender = form.id ? senders.find((item) => item.id === form.id) : null;
  const templates = useMemo(() => settings?.templates ?? [], [settings?.templates]);
  const selectedTemplate = useMemo(
    () => templates.find((item) => item.key === selectedTemplateKey) ?? templates[0] ?? null,
    [selectedTemplateKey, templates],
  );
  const knownNotificationTypeKeys = useMemo(() => notificationTypes.map((item) => item.key), [notificationTypes]);
  const notificationTypeGroups = useMemo(() => groupNotificationTypesByDomain(notificationTypes), [notificationTypes]);
  const selectedRolePreference = useMemo(
    () => rolePreferences.find((item) => item.roleId === selectedRoleId) ?? rolePreferences[0] ?? null,
    [rolePreferences, selectedRoleId],
  );
  const selectedRolePreferences = useMemo<UserNotificationTypePreferences>(
    () => normalizeUserNotificationTypePreferences(
      selectedRolePreference?.preferences as Partial<UserNotificationTypePreferences> | null,
      knownNotificationTypeKeys,
    ),
    [knownNotificationTypeKeys, selectedRolePreference?.preferences],
  );

  useEffect(() => {
    if (!templates.length) return;
    if (!selectedTemplateKey || !templates.some((item) => item.key === selectedTemplateKey)) {
      setSelectedTemplateKey(templates[0].key);
    }
  }, [selectedTemplateKey, templates]);

  useEffect(() => {
    if (!selectedTemplate) {
      setTemplateDraft(null);
      return;
    }

    setTemplateDraft({
      key: selectedTemplate.key,
      isEnabled: selectedTemplate.isEnabled,
      subject: selectedTemplate.subject,
      body: selectedTemplate.body,
    });
  }, [selectedTemplate]);

  const updateSelectedRolePreferences = (updater: (current: UserNotificationTypePreferences) => UserNotificationTypePreferences) => {
    if (!selectedRolePreference) return;

    const nextPreferences = updater(selectedRolePreferences);
    setRolePreferences((current) => current.map((item) => (
      item.roleId === selectedRolePreference.roleId
        ? {
          ...item,
          hasCustomPreferences: true,
          preferences: toApiNotificationPreference(nextPreferences),
        }
        : item
    )));
  };

  const saveRolePreferences = async () => {
    if (!selectedRolePreference || !canEdit) return;

    setRolePreferencesSaving(true);
    try {
      const updated = await apiService.updateNotificationRolePreferences(
        selectedRolePreference.roleId,
        toApiNotificationPreference(selectedRolePreferences),
      );
      setRolePreferences((current) => current.map((item) => (item.roleId === updated.roleId ? updated : item)));
      toast.success('Ролевые настройки уведомлений сохранены.', 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось сохранить ролевые настройки уведомлений');
      toast.error(text, 'Уведомления');
    } finally {
      setRolePreferencesSaving(false);
    }
  };

  const resetRolePreferencesToDefault = async () => {
    if (!selectedRolePreference || !canEdit) return;

    setRolePreferencesSaving(true);
    try {
      const updated = await apiService.updateNotificationRolePreferences(
        selectedRolePreference.roleId,
        {
          disabledTypes: [],
          disabledChannels: {},
          useDefault: true,
        },
      );
      setRolePreferences((current) => current.map((item) => (
        item.roleId === updated.roleId ? updated : item
      )));
      toast.success('Для роли включён стандартный профиль уведомлений.', 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось вернуть стандартный профиль уведомлений');
      toast.error(text, 'Уведомления');
    } finally {
      setRolePreferencesSaving(false);
    }
  };

  const updateContractTerminationClientEmails = async (enabled: boolean) => {
    if (!settings || !canEdit) return;

    setEmailPolicySaving(true);
    try {
      const next = await apiService.updateEmailNotificationSettings({
        enabled: settings.email.enabled,
        contractTerminationClientEmailsEnabled: enabled,
        smtpHost: settings.email.smtpHost,
        smtpPort: settings.email.smtpPort,
        smtpUsername: settings.email.smtpUsername,
        smtpPassword: '',
        keepExistingPassword: true,
        fromEmail: settings.email.fromEmail,
        fromName: settings.email.fromName,
        enableSsl: settings.email.enableSsl,
      });
      setSettings(next);
      toast.success(
        enabled
          ? 'Автоматические письма клиентам при расторжении договора включены.'
          : 'Автоматические письма клиентам при расторжении договора отключены.',
        'Уведомления',
      );
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось сохранить настройку email-уведомлений клиентов.');
      toast.error(text, 'Уведомления');
    } finally {
      setEmailPolicySaving(false);
    }
  };

  const saveSmsSettings = async () => {
    if (!settings || !canEdit) return;

    setSmsSaving(true);
    try {
      const next = await apiService.updateSmsNotificationSettings({
        enabled: smsForm.enabled,
        providerKey: smsForm.providerKey.trim(),
        senderName: smsForm.senderName.trim(),
        apiToken: smsForm.apiToken,
        keepExistingApiToken: smsForm.keepExistingApiToken,
        dailyLimit: smsForm.dailyLimit,
        maxMessageLength: smsForm.maxMessageLength,
        notes: smsForm.notes.trim(),
      });
      setSettings(next);
      setSmsForm(smsFormFromSettings(next));
      toast.success('Настройки SMS-уведомлений сохранены.', 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось сохранить настройки SMS-уведомлений.');
      toast.error(text, 'Уведомления');
    } finally {
      setSmsSaving(false);
    }
  };

  const replaceTemplate = (template: NotificationTemplateDto) => {
    setSettings((current) => current
      ? {
          ...current,
          templates: current.templates.map((item) => (item.key === template.key ? template : item)),
        }
      : current);
  };

  const saveTemplate = async () => {
    if (!templateDraft || !canEdit) return;

    setTemplateSaving(true);
    try {
      const updated = await apiService.updateNotificationTemplate(templateDraft.key, {
        isEnabled: templateDraft.isEnabled,
        subject: templateDraft.subject,
        body: templateDraft.body,
      });
      replaceTemplate(updated);
      toast.success('Шаблон уведомления сохранён.', 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось сохранить шаблон уведомления.');
      toast.error(text, 'Уведомления');
    } finally {
      setTemplateSaving(false);
    }
  };

  const resetTemplate = async () => {
    if (!selectedTemplate || !canEdit) return;

    setTemplateSaving(true);
    try {
      const updated = await apiService.resetNotificationTemplate(selectedTemplate.key);
      replaceTemplate(updated);
      toast.success('Шаблон восстановлен по умолчанию.', 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось восстановить шаблон уведомления.');
      toast.error(text, 'Уведомления');
    } finally {
      setTemplateSaving(false);
    }
  };

  useEffect(() => {
    if (!settings || route.mode === 'list') return;

    if (route.mode === 'new') {
      const nextOrder = resolveNextOrder(senders.filter((item) => item.channel === route.channel));
      setForm(createBlankForm(route.channel, nextOrder));
      return;
    }

    const actual = senders.find((item) => item.id === route.id && item.channel === route.channel);
    if (actual) {
      setForm(formFromSender(actual));
    }
  }, [route, senders, settings]);

  const openSenderList = () => {
    pushNotificationRoute({ mode: 'list' });
    setRoute({ mode: 'list' });
    setMessage(null);
  };

  const openNewSender = (channel: NotificationChannel) => {
    if (!canEdit) return;
    const nextOrder = resolveNextOrder(senders.filter((item) => item.channel === channel));
    setForm(createBlankForm(channel, nextOrder));
    const nextRoute: SenderRoute = { mode: 'new', channel };
    pushNotificationRoute(nextRoute);
    setRoute(nextRoute);
    setMessage(null);
  };

  const openEditSender = (sender: NotificationSenderDto) => {
    setForm(formFromSender(sender));
    const nextRoute: SenderRoute = { mode: 'edit', channel: sender.channel, id: sender.id };
    pushNotificationRoute(nextRoute);
    setRoute(nextRoute);
    setMessage(null);
  };

  const togglePurpose = (purpose: string) => {
    setForm((prev) => {
      const exists = prev.purposes.includes(purpose);
      const nextPurposes = exists
        ? prev.purposes.filter((item) => item !== purpose)
        : [...prev.purposes, purpose];
      return { ...prev, purposes: nextPurposes.length ? nextPurposes : DEFAULT_PURPOSES };
    });
  };

  const buildPayload = (): UpsertNotificationSenderRequest => ({
    channel: form.channel,
    name: form.name.trim(),
    isEnabled: form.isEnabled,
    purposes: form.purposes,
    sortOrder: form.sortOrder,
    email: form.channel === 'email'
      ? {
          smtpHost: form.smtpHost.trim(),
          smtpPort: form.smtpPort,
          smtpUsername: form.smtpUsername.trim(),
          smtpPassword: form.smtpPassword,
          keepExistingPassword: form.keepExistingPassword,
          fromEmail: form.fromEmail.trim(),
          fromName: form.fromName.trim(),
          enableSsl: form.enableSsl,
        }
      : null,
    telegram: form.channel === 'telegram'
      ? {
          botToken: form.botToken,
          keepExistingToken: form.keepExistingToken,
          botUsername: form.botUsername.trim(),
          updateMode: form.updateMode,
          webhookSecret: form.webhookSecret,
          keepExistingWebhookSecret: form.keepExistingWebhookSecret,
          proxyUrl: form.proxyUrl.trim(),
          proxyUsername: form.proxyUsername.trim(),
          proxyPassword: form.proxyPassword,
          keepExistingProxyPassword: form.keepExistingProxyPassword,
          profileName: form.profileName.trim(),
          profileDescription: form.profileDescription.trim(),
          profileShortDescription: form.profileShortDescription.trim(),
          profileCommands: parseCommandsText(form.profileCommandsText),
        }
      : null,
  });

  const saveSender = async () => {
    if (!canEdit) return;
    setSaving(true);
    setMessage(null);
    try {
      const payload = buildPayload();
      const shouldSyncTelegramProfile = form.channel === 'telegram' && (!form.id || !form.keepExistingToken || Boolean(form.botToken.trim()));
      const next = form.id
        ? await apiService.updateNotificationSender(form.id, payload)
        : await apiService.createNotificationSender(payload);
      setSettings(next);
      const saved = form.id
        ? next.senders.find((item) => item.id === form.id)
        : next.senders.find((item) => item.name === payload.name && item.channel === payload.channel) ?? next.senders[next.senders.length - 1];
      if (saved) {
        setForm(formFromSender(saved));
        const nextRoute: SenderRoute = { mode: 'edit', channel: saved.channel, id: saved.id };
        pushNotificationRoute(nextRoute);
        setRoute(nextRoute);
      }
      if (saved?.channel === 'telegram' && shouldSyncTelegramProfile) {
        try {
          const syncResult = await apiService.syncTelegramBotProfile(saved.id);
          const refreshed = await apiService.getNotificationSettings();
          setSettings(refreshed);
          const refreshedSender = refreshed.senders.find((item) => item.id === saved.id);
          if (refreshedSender) {
            setForm(formFromSender(refreshedSender));
          }
          const text = syncResult.success
            ? 'Отправитель сохранён, информация Telegram-бота подтянута.'
            : syncResult.message || 'Отправитель сохранён, но Telegram-бот не прошёл проверку.';
          setMessage(text);
          if (syncResult.success) {
            toast.success(text, 'Уведомления');
          } else {
            toast.error(text, 'Уведомления');
          }
        } catch (error) {
          const text = extractDisplayErrorMessage(error, 'Отправитель сохранён, но Telegram-бот не прошёл проверку');
          setMessage(text);
          toast.error(text, 'Уведомления');
        }
        return;
      }
      const text = 'Отправитель уведомлений сохранён.';
      setMessage(text);
      toast.success(text, 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось сохранить отправителя уведомлений');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setSaving(false);
    }
  };

  const deleteSender = async () => {
    if (!form.id || !canEdit) return;
    const approved = await confirm({
      title: 'Удалить отправителя',
      description: `Удалить "${form.name}" из настроек уведомлений`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setSaving(true);
    try {
      await apiService.deleteNotificationSender(form.id);
      const next = await apiService.getNotificationSettings();
      setSettings(next);
      setForm(createBlankForm('email', resolveNextOrder(next.senders)));
      openSenderList();
      toast.success('Отправитель удалён.', 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось удалить отправителя');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setSaving(false);
    }
  };

  const sendTest = async () => {
    if (!canEdit) return;
    if (!testRecipient.trim()) {
      const text = form.channel === 'telegram' ? 'Укажите Telegram chat id' : 'Укажите email получателя';
      setMessage(text);
      toast.error(text, 'Уведомления');
      return;
    }

    setTesting(true);
    setMessage(null);
    try {
      const result = await apiService.sendNotificationTest({
        channel: form.channel,
        senderId: form.id ?? null,
        email: form.channel === 'email' ? testRecipient.trim() : null,
        telegramChatId: form.channel === 'telegram' ? Number(testRecipient.trim()) : null,
        subject: testSubject.trim(),
        message: testMessage.trim(),
      });
      const text = result.success
        ? 'Тестовое уведомление отправлено.'
        : result.error || 'Тестовое уведомление не отправлено.';
      setMessage(text);
      if (result.success) {
        toast.success(text, 'Уведомления');
      } else {
        toast.error(text, 'Уведомления');
      }
      void loadDeliveries();
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось отправить тестовое уведомление');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setTesting(false);
    }
  };

  const retryDelivery = async (delivery: NotificationDeliveryDto) => {
    if (!canEdit) return;
    setRetryingDeliveryId(delivery.id);
    try {
      const result = await apiService.retryNotificationDelivery(delivery.id);
      const latest = result[result.length - 1];
      const text = latest?.status === 'sent'
        ? 'Уведомление повторно отправлено.'
        : latest?.error || 'Повторная отправка завершилась без успешной доставки.';
      if (latest?.status === 'sent') {
        toast.success(text, 'Уведомления');
      } else {
        toast.error(text, 'Уведомления');
      }
      await loadDeliveries();
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось повторить отправку уведомления');
      toast.error(text, 'Уведомления');
    } finally {
      setRetryingDeliveryId(null);
    }
  };

  const syncTelegramWebhook = async () => {
    if (!form.id || !canEdit) return;
    setSaving(true);
    setMessage(null);
    try {
      const result = await apiService.syncTelegramNotificationWebhook(form.id);
      const text = result.message || 'Webhook Telegram-бота синхронизирован.';
      toast.success(text, 'Уведомления');
      setMessage(text);
      await loadSettings();
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось синхронизировать webhook Telegram-бота');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setSaving(false);
    }
  };

  const deleteTelegramWebhook = async () => {
    if (!form.id || !canEdit) return;
    setSaving(true);
    setMessage(null);
    try {
      const result = await apiService.deleteTelegramNotificationWebhook(form.id);
      const text = result.message || 'Webhook Telegram-бота отключён.';
      toast.success(text, 'Уведомления');
      setMessage(text);
      await loadSettings();
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось отключить webhook Telegram-бота');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setSaving(false);
    }
  };

  const syncTelegramProfile = async () => {
    if (!form.id || !canEdit) return;
    setSaving(true);
    setMessage(null);
    try {
      const result = await apiService.syncTelegramBotProfile(form.id);
      const next = await apiService.getNotificationSettings();
      setSettings(next);
      const actual = next.senders.find((item) => item.id === form.id);
      if (actual) {
        setForm(formFromSender(actual));
      }
      const text = result.message || 'Информация Telegram-бота синхронизирована.';
      setMessage(text);
      toast.success(text, 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось проверить и синхронизировать Telegram-бота');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setSaving(false);
    }
  };

  const applyTelegramProfile = async () => {
    if (!form.id || !canEdit) return;
    setSaving(true);
    setMessage(null);
    try {
      const result = await apiService.applyTelegramBotProfile(form.id, {
        name: form.profileName.trim(),
        description: form.profileDescription.trim(),
        shortDescription: form.profileShortDescription.trim(),
        commands: parseCommandsText(form.profileCommandsText),
      });
      const next = await apiService.getNotificationSettings();
      setSettings(next);
      const actual = next.senders.find((item) => item.id === form.id);
      if (actual) {
        setForm(formFromSender(actual));
      }
      const text = result.message || 'Профиль Telegram-бота обновлён.';
      setMessage(text);
      toast.success(text, 'Уведомления');
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось обновить профиль Telegram-бота');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-6">
      <div className="grid gap-4 md:grid-cols-3">
        <SummaryTile label="Email отправителей" value={emailSenders.length} ready={emailSenders.filter((item) => item.ready && item.isEnabled).length} />
        <SummaryTile label="Telegram-ботов" value={telegramSenders.length} ready={telegramSenders.filter((item) => item.ready && item.isEnabled).length} />
        <SummaryTile label="Привязано Telegram" value={settings?.telegram.linkedUsersCount ?? 0} ready={settings?.telegram.ready ? 1 : 0} />
      </div>

      {route.mode === 'list' ? (
        <div className="space-y-5">
          {message && (
            <div className="rounded-xl border border-blue-100 bg-blue-50 px-4 py-3 text-sm text-blue-800">
              {message}
            </div>
          )}

          <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div>
                <h4 className="text-base font-semibold text-gray-900">Каналы уведомлений</h4>
                <p className="mt-1 max-w-3xl text-sm text-gray-500">
                  Здесь видно, какие способы доставки уже работают, а какие зарезервированы для следующих интеграций. Запланированные каналы не участвуют в отправке, пока не выбран и не подключен провайдер.
                </p>
              </div>
              <span className="rounded-full border border-blue-100 bg-blue-50 px-3 py-1 text-xs font-semibold text-blue-700">
                {channelCatalog.filter((item) => item.isEnabled).length} из {channelCatalog.length || 0} активны
              </span>
            </div>

            {loading && channelCatalog.length === 0 ? (
              <div className="mt-4 flex items-center gap-2 text-sm text-gray-500">
                <RefreshCw className="h-4 w-4 animate-spin" />
                Загружаем каналы уведомлений...
              </div>
            ) : (
              <div className="mt-5 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                {channelCatalog.map((channel) => {
                  const Icon = channelCatalogIcon(channel.key);
                  return (
                    <div key={channel.key} className="rounded-xl border border-gray-200 p-4">
                      <div className="flex items-start gap-3">
                        <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg border border-gray-200 bg-gray-50 text-gray-600">
                          <Icon className="h-5 w-5" />
                        </span>
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <h5 className="text-sm font-semibold text-gray-900">{channel.label}</h5>
                            <span className={`rounded-full border px-2 py-0.5 text-[11px] font-semibold ${channelCatalogStatusClass(channel)}`}>
                              {channelCatalogStatusLabel(channel)}
                            </span>
                          </div>
                          <p className="mt-2 text-xs leading-5 text-gray-500">{channel.description}</p>
                          <div className="mt-3 flex flex-wrap gap-1.5 text-[11px] font-semibold">
                            {channel.supportsUserPreferences && (
                              <span className="rounded-full bg-blue-50 px-2 py-1 text-blue-700">Личные настройки</span>
                            )}
                            {channel.supportsDeliveryLog && (
                              <span className="rounded-full bg-slate-100 px-2 py-1 text-slate-600">Журнал доставки</span>
                            )}
                            {channel.requiresProvider && (
                              <span className="rounded-full bg-amber-50 px-2 py-1 text-amber-700">Нужен провайдер</span>
                            )}
                          </div>
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>

          <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div>
                <h4 className="text-base font-semibold text-gray-900">Диагностика Web Push</h4>
                <p className="mt-1 max-w-3xl text-sm text-gray-500">
                  Здесь видно, готов ли сервер к браузерным push-уведомлениям и есть ли активные устройства сотрудников.
                </p>
              </div>
              <span className={`rounded-full border px-3 py-1 text-xs font-semibold ${
                webPushDiagnostics?.ready
                  ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
                  : 'border-amber-200 bg-amber-50 text-amber-700'
              }`}>
                {webPushDiagnostics?.ready ? 'Готово' : 'Требуется настройка'}
              </span>
            </div>

            {webPushDiagnostics ? (
              <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
                <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
                  <div className="text-xs font-semibold uppercase text-gray-500">Ключи сервера</div>
                  <div className="mt-2 space-y-1 text-sm text-gray-700">
                    <div>Public key: {webPushDiagnostics.publicKeyConfigured ? 'есть' : 'нет'}</div>
                    <div>Private key: {webPushDiagnostics.privateKeyConfigured ? 'есть' : 'нет'}</div>
                    <div>Subject: {webPushDiagnostics.subjectConfigured ? 'есть' : 'нет'}</div>
                  </div>
                </div>
                <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
                  <div className="text-xs font-semibold uppercase text-gray-500">Устройства</div>
                  <div className="mt-2 text-2xl font-semibold text-gray-900">{webPushDiagnostics.activeSubscriptionsCount}</div>
                  <div className="mt-1 text-xs text-gray-500">
                    всего: {webPushDiagnostics.totalSubscriptionsCount}, отключено: {webPushDiagnostics.revokedSubscriptionsCount}
                  </div>
                </div>
                <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
                  <div className="text-xs font-semibold uppercase text-gray-500">Пользователи</div>
                  <div className="mt-2 text-2xl font-semibold text-gray-900">{webPushDiagnostics.usersWithActiveSubscriptionsCount}</div>
                  <div className="mt-1 text-xs text-gray-500">
                    с активными push-устройствами
                  </div>
                </div>
                <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
                  <div className="text-xs font-semibold uppercase text-gray-500">Доставка</div>
                  <div className="mt-2 text-sm text-gray-700">
                    Успешно: {formatDateTime(webPushDiagnostics.lastSuccessfulDeliveryAt)}
                  </div>
                  <div className="mt-1 text-sm text-gray-700">
                    Ошибка: {formatDateTime(webPushDiagnostics.lastFailedDeliveryAt)}
                  </div>
                  {webPushDiagnostics.lastDeliveryError ? (
                    <div className="mt-1 truncate text-xs text-red-600" title={webPushDiagnostics.lastDeliveryError}>
                      {webPushDiagnostics.lastDeliveryError}
                    </div>
                  ) : null}
                </div>
              </div>
            ) : (
              <div className="mt-4 flex items-center gap-2 text-sm text-gray-500">
                <RefreshCw className="h-4 w-4 animate-spin" />
                Загружаем диагностику Web Push...
              </div>
            )}
            <AdminWebPushGuide />
          </div>

          <SmsSettingsPanel
            settings={settings}
            form={smsForm}
            saving={smsSaving}
            canEdit={canEdit}
            setForm={setSmsForm}
            onSave={saveSmsSettings}
          />

          <MaxDiagnosticsPanel settings={settings} />

          <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div>
                <h4 className="text-base font-semibold text-gray-900">Автоматические письма клиентам</h4>
                <p className="mt-1 max-w-3xl text-sm text-gray-500">
                  Отдельный выключатель для писем, которые система отправляет клиентам без ручного подтверждения.
                  Общая отправка Email и SMTP-настройки по-прежнему управляются отправителями ниже.
                </p>
              </div>
              <label className="inline-flex items-center gap-3 rounded-xl border border-gray-200 px-4 py-3 text-sm font-semibold text-gray-700">
                <input
                  type="checkbox"
                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                  checked={settings?.email.contractTerminationClientEmailsEnabled ?? false}
                  disabled={!canEdit || loading || emailPolicySaving}
                  onChange={(event) => void updateContractTerminationClientEmails(event.target.checked)}
                />
                Письмо клиенту при расторжении договора
              </label>
            </div>
            <p className="mt-3 text-xs text-gray-500">
              Пока настройка выключена, расторжение договора только сохранит статус и условия. Письмо не уйдёт, даже если у клиента указан email.
              Для фактической отправки также должен быть включён и настроен Email-отправитель.
            </p>
          </div>

          <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div>
                <h4 className="text-base font-semibold text-gray-900">Ролевые правила уведомлений</h4>
                <p className="mt-1 max-w-3xl text-sm text-gray-500">
                  Базовые настройки типов и каналов для пользователей выбранной роли. Личные настройки пользователя в центре уведомлений имеют приоритет над правилом роли.
                </p>
                {selectedRolePreference ? (
                  <div className="mt-2 flex flex-wrap items-center gap-2 text-xs">
                    <span className={`rounded-full border px-2 py-1 font-semibold ${
                      selectedRolePreference.hasCustomPreferences
                        ? 'border-blue-200 bg-blue-50 text-blue-700'
                        : 'border-emerald-200 bg-emerald-50 text-emerald-700'
                    }`}>
                      {selectedRolePreference.hasCustomPreferences ? 'Ручное правило' : `Стандартный профиль: ${selectedRolePreference.defaultProfileLabel}`}
                    </span>
                    <span className="text-gray-500">{selectedRolePreference.defaultProfileDescription}</span>
                  </div>
                ) : null}
              </div>
              <div className="flex flex-wrap items-center gap-2">
                <button
                  type="button"
                  onClick={() => void resetRolePreferencesToDefault()}
                  disabled={!canEdit || rolePreferencesLoading || rolePreferencesSaving || !selectedRolePreference || !selectedRolePreference.hasCustomPreferences}
                  className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-semibold text-gray-700 hover:bg-gray-50 disabled:opacity-60"
                >
                  <RefreshCw className={`h-4 w-4 ${rolePreferencesSaving ? 'animate-spin' : ''}`} />
                  Стандартный профиль
                </button>
                <button
                  type="button"
                  onClick={() => void saveRolePreferences()}
                  disabled={!canEdit || rolePreferencesLoading || rolePreferencesSaving || !selectedRolePreference}
                  className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60"
                >
                  <Save className={`h-4 w-4 ${rolePreferencesSaving ? 'animate-pulse' : ''}`} />
                  Сохранить правила
                </button>
              </div>
            </div>

            {rolePreferencesLoading ? (
              <div className="mt-4 flex items-center gap-2 text-sm text-gray-500">
                <RefreshCw className="h-4 w-4 animate-spin" />
                Загружаем ролевые правила...
              </div>
            ) : rolePreferences.length === 0 ? (
              <div className="mt-4 rounded-xl border border-dashed border-gray-200 px-4 py-6 text-sm text-gray-500">
                Роли пока не найдены.
              </div>
            ) : (
              <div className="mt-5 grid gap-5 xl:grid-cols-[280px_minmax(0,1fr)]">
                <div className="space-y-2">
                  {rolePreferences.map((role) => (
                    <button
                      key={role.roleId}
                      type="button"
                      onClick={() => setSelectedRoleId(role.roleId)}
                      className={`w-full rounded-xl border px-3 py-3 text-left transition ${
                        selectedRolePreference?.roleId === role.roleId
                          ? 'border-blue-200 bg-blue-50 text-blue-900'
                          : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      <span className="block text-sm font-semibold">{role.roleName}</span>
                      <span className="mt-1 block text-xs text-gray-500">
                        {role.isSystemRole ? 'Системная роль' : 'Настраиваемая роль'}
                      </span>
                      <span className="mt-2 block text-xs font-medium text-gray-500">
                        {role.hasCustomPreferences ? 'Ручное правило' : role.defaultProfileLabel}
                      </span>
                    </button>
                  ))}
                </div>

                <div className="space-y-3">
                  {notificationTypeGroups.map((group) => (
                    <div key={group.key} className="rounded-xl border border-gray-200 bg-gray-50/70 p-3">
                      <div className="mb-3 flex items-center justify-between gap-3">
                        <h5 className="text-sm font-semibold text-gray-900">{group.label}</h5>
                        <span className="rounded-full bg-white px-2 py-0.5 text-xs font-semibold text-gray-500">
                          {group.items.length}
                        </span>
                      </div>
                      <div className="grid gap-3 md:grid-cols-2">
                        {group.items.map((item) => {
                          const isEnabled = isUserNotificationTypeEnabled(selectedRolePreferences, item.key);
                          const availableChannels = ROLE_CHANNEL_OPTIONS.filter((channel) => item.channels.includes(channel.value));
                          return (
                            <div
                              key={item.key}
                              className={`rounded-xl border p-3 transition ${
                                isEnabled ? 'border-blue-200 bg-white' : 'border-gray-200 bg-white/70 text-gray-500'
                              }`}
                            >
                              <label className="flex cursor-pointer gap-3">
                                <input
                                  type="checkbox"
                                  checked={isEnabled}
                                  disabled={!canEdit || !selectedRolePreference}
                                  onChange={(event) => {
                                    updateSelectedRolePreferences((current) => toggleUserNotificationTypePreference(current, item.key, event.target.checked));
                                  }}
                                  className="mt-1 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                                />
                                <span>
                                  <span className="block text-sm font-semibold text-gray-900">{item.label}</span>
                                  <span className="mt-1 block text-xs leading-5 text-gray-500">{item.description}</span>
                                  <span className="mt-2 flex flex-wrap gap-1.5">
                                    <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-semibold ${notificationPriorityBadgeClass(item.priority)}`}>
                                      {notificationPriorityLabel(item.priority)}
                                    </span>
                                    {item.requiresAction ? (
                                      <span className="inline-flex rounded-full bg-amber-50 px-2 py-0.5 text-xs font-semibold text-amber-700">Требует действия</span>
                                    ) : null}
                                    {item.showToastByDefault ? (
                                      <span className="inline-flex rounded-full bg-blue-50 px-2 py-0.5 text-xs font-semibold text-blue-700">Toast: {item.toastPriority}</span>
                                    ) : null}
                                    {item.pushEligible ? (
                                      <span className="inline-flex rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-semibold text-emerald-700">Push: {item.pushPriority}</span>
                                    ) : null}
                                    {item.groupingWindowSeconds > 0 ? (
                                      <span className="inline-flex rounded-full bg-gray-100 px-2 py-0.5 text-xs font-semibold text-gray-500">
                                        Группировка {item.groupingWindowSeconds} сек.
                                      </span>
                                    ) : null}
                                  </span>
                                </span>
                              </label>
                              <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-3">
                                {availableChannels.map(({ value, label, Icon }) => {
                                  const channelEnabled = isUserNotificationChannelEnabled(selectedRolePreferences, item.key, value);
                                  return (
                                    <label
                                      key={value}
                                      className={`flex h-9 cursor-pointer items-center justify-center gap-1.5 rounded-lg border px-2 text-xs font-semibold transition ${
                                        channelEnabled
                                          ? 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
                                          : 'border-gray-200 bg-gray-100 text-gray-400'
                                      } ${isEnabled && canEdit ? '' : 'cursor-not-allowed opacity-60'}`}
                                      title={`Канал: ${label}`}
                                    >
                                      <input
                                        type="checkbox"
                                        checked={channelEnabled}
                                        disabled={!canEdit || !isEnabled || !selectedRolePreference}
                                        onChange={(event) => {
                                          updateSelectedRolePreferences((current) => toggleUserNotificationChannelPreference(current, item.key, value, event.target.checked));
                                        }}
                                        className="sr-only"
                                      />
                                      <Icon className="h-3.5 w-3.5" />
                                      <span className="truncate">{label}</span>
                                    </label>
                                  );
                                })}
                              </div>
                            </div>
                          );
                        })}
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>

          <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div>
                <h4 className="text-base font-semibold text-gray-900">Шаблоны уведомлений</h4>
                <p className="mt-1 max-w-3xl text-sm text-gray-500">
                  Единое место для тем и текстов email/Telegram-уведомлений. Переменные вставляются в формате {'{{поле}}'}.
                </p>
              </div>
              {!canEdit && (
                <span className="rounded-full border border-amber-200 bg-amber-50 px-3 py-1 text-xs font-semibold text-amber-700">
                  Только просмотр
                </span>
              )}
            </div>

            {templates.length === 0 ? (
              <div className="mt-4 rounded-xl border border-dashed border-gray-200 px-4 py-6 text-sm text-gray-500">
                Шаблоны пока не загружены.
              </div>
            ) : (
              <div className="mt-5 grid gap-5 xl:grid-cols-[320px_minmax(0,1fr)]">
                <div className="space-y-2">
                  {templates.map((template) => (
                    <button
                      key={template.key}
                      type="button"
                      onClick={() => setSelectedTemplateKey(template.key)}
                      className={`w-full rounded-xl border px-3 py-3 text-left transition ${
                        selectedTemplate?.key === template.key
                          ? 'border-blue-200 bg-blue-50 text-blue-900'
                          : 'border-gray-200 bg-white text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      <span className="block text-sm font-semibold">{template.label}</span>
                      <span className="mt-1 block text-xs text-gray-500">{template.description}</span>
                    </button>
                  ))}
                </div>

                {selectedTemplate && templateDraft && (
                  <div className="rounded-xl border border-gray-200 p-4">
                    <div className="flex flex-wrap items-center justify-between gap-3">
                      <div>
                        <h5 className="text-sm font-semibold text-gray-900">{selectedTemplate.label}</h5>
                        <p className="mt-1 text-xs text-gray-500">{selectedTemplate.description}</p>
                      </div>
                      <label className="inline-flex items-center gap-2 text-sm font-semibold text-gray-700">
                        <input
                          type="checkbox"
                          className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                          checked={templateDraft.isEnabled}
                          disabled={!canEdit || templateSaving}
                          onChange={(event) => setTemplateDraft((current) => current ? { ...current, isEnabled: event.target.checked } : current)}
                        />
                        Использовать шаблон
                      </label>
                    </div>

                    <div className="mt-4 grid gap-4">
                      <label className="text-sm font-medium text-gray-700">
                        Тема
                        <input
                          value={templateDraft.subject}
                          disabled={!canEdit || templateSaving}
                          onChange={(event) => setTemplateDraft((current) => current ? { ...current, subject: event.target.value } : current)}
                          className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm disabled:bg-gray-50"
                        />
                      </label>
                      <label className="text-sm font-medium text-gray-700">
                        Текст
                        <textarea
                          value={templateDraft.body}
                          disabled={!canEdit || templateSaving}
                          onChange={(event) => setTemplateDraft((current) => current ? { ...current, body: event.target.value } : current)}
                          rows={8}
                          className="mt-1 w-full resize-y rounded-lg border border-gray-300 px-3 py-2 font-mono text-sm disabled:bg-gray-50"
                        />
                      </label>
                    </div>

                    <div className="mt-4 flex flex-wrap gap-2">
                      {selectedTemplate.variables.map((variable) => (
                        <button
                          key={variable.key}
                          type="button"
                          onClick={() => setTemplateDraft((current) => current ? { ...current, body: `${current.body}${current.body.endsWith(' ') || current.body.endsWith('\n') ? '' : ' '}{{${variable.key}}}` } : current)}
                          disabled={!canEdit || templateSaving}
                          className="rounded-lg border border-gray-200 px-2.5 py-1.5 text-xs font-semibold text-gray-600 hover:bg-gray-50 disabled:opacity-60"
                          title={variable.label}
                        >
                          {'{{'}{variable.key}{'}}'}
                        </button>
                      ))}
                    </div>

                    <div className="mt-5 flex flex-wrap justify-end gap-2">
                      <button
                        type="button"
                        onClick={() => void resetTemplate()}
                        disabled={!canEdit || templateSaving}
                        className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-semibold text-gray-700 hover:bg-gray-50 disabled:opacity-60"
                      >
                        <RefreshCw className="h-4 w-4" />
                        По умолчанию
                      </button>
                      <button
                        type="button"
                        onClick={() => void saveTemplate()}
                        disabled={!canEdit || templateSaving}
                        className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60"
                      >
                        <Save className="h-4 w-4" />
                        Сохранить шаблон
                      </button>
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>

          <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <h4 className="text-base font-semibold text-gray-900">Отправители уведомлений</h4>
                <p className="text-sm text-gray-500">Отдельные реестры для Email-ящиков и Telegram-ботов. Позже сюда можно добавить новые каналы.</p>
              </div>
              <button
                type="button"
                onClick={() => void loadSettings()}
                disabled={loading}
                className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-semibold text-gray-700 hover:bg-gray-50 disabled:opacity-60"
              >
                <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
                Обновить
              </button>
            </div>

            <div className="mt-5 grid gap-5 xl:grid-cols-2">
              <SenderTable
                channel="email"
                title="Email отправители"
                description="SMTP-ящики для регистрации, паролей, напоминаний и ручных уведомлений."
                senders={emailSenders}
                purposes={purposes}
                onCreate={openNewSender}
                onOpen={openEditSender}
                canEdit={canEdit}
              />
              <SenderTable
                channel="telegram"
                title="Telegram-боты"
                description="Боты для авторизации сотрудников, напоминаний, хука и polling."
                senders={telegramSenders}
                purposes={purposes}
                onCreate={openNewSender}
                onOpen={openEditSender}
                canEdit={canEdit}
              />
            </div>
          </div>

          <DeliveryLogPanel
            items={deliveries}
            total={deliveriesTotal}
            status={deliveryStatus}
            loading={deliveriesLoading}
            retryingId={retryingDeliveryId}
            canEdit={canEdit}
            onStatusChange={setDeliveryStatus}
            onRefresh={loadDeliveries}
            onRetry={retryDelivery}
          />
        </div>
      ) : (
        <div className="space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <a
              href={buildNotificationRouteHref({ mode: 'list' })}
              onClick={(event) => {
                event.preventDefault();
                openSenderList();
              }}
              className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-semibold text-gray-700 hover:bg-gray-50"
            >
              <ArrowLeft className="h-4 w-4" />
              К списку отправителей
            </a>
            <div className="flex flex-wrap gap-2">
              {form.id && (
                <button
                  type="button"
                  onClick={() => void deleteSender()}
                  disabled={!canEdit || saving}
                  className="inline-flex items-center gap-2 rounded-lg border border-rose-200 bg-white px-3 py-2 text-sm font-semibold text-rose-700 hover:bg-rose-50 disabled:opacity-60"
                >
                  <Trash2 className="h-4 w-4" />
                  Удалить
                </button>
              )}
              <button
                type="button"
                onClick={() => void saveSender()}
                disabled={!canEdit || saving}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60"
              >
                <Save className="h-4 w-4" />
                Сохранить
              </button>
            </div>
          </div>

          <div className="grid gap-6 xl:grid-cols-[minmax(0,1.35fr)_minmax(320px,0.65fr)]">
            <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
              <div className="mb-5">
                <h4 className="text-base font-semibold text-gray-900">{form.id ? 'Карточка отправителя' : 'Новая карточка отправителя'}</h4>
                <p className="text-sm text-gray-500">{channelLabel(form.channel)}{selectedSender ? ` · #${selectedSender.id}` : ''}</p>
              </div>

              <div className="grid gap-4 md:grid-cols-2">
                <label className="text-sm font-medium text-gray-700">
                  Канал
                  <select
                    value={form.channel}
                    onChange={(event) => openNewSender(event.target.value as NotificationChannel)}
                    disabled={!canEdit || Boolean(form.id)}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  >
                    <option value="email">Email</option>
                    <option value="telegram">Telegram</option>
                  </select>
                </label>
                <label className="text-sm font-medium text-gray-700">
                  Название
                  <input
                    value={form.name}
                    onChange={(event) => setForm((prev) => ({ ...prev, name: event.target.value }))}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
                <label className="text-sm font-medium text-gray-700">
                  Порядок
                  <input
                    type="number"
                    value={form.sortOrder}
                    onChange={(event) => setForm((prev) => ({ ...prev, sortOrder: Number(event.target.value) || 0 }))}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
                <label className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700">
                  <input
                    type="checkbox"
                    checked={form.isEnabled}
                    onChange={(event) => setForm((prev) => ({ ...prev, isEnabled: event.target.checked }))}
                    className="h-4 w-4 rounded border-gray-300 text-blue-600"
                  />
                  Включён
                </label>
              </div>

              <div className="mt-5">
                <div className="mb-2 text-sm font-semibold text-gray-800">Сценарии</div>
                <div className="grid gap-2 md:grid-cols-2">
                  {purposes.map((purpose) => (
                    <label key={purpose.key} className="flex items-start gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-700">
                      <input
                        type="checkbox"
                        checked={form.purposes.includes(purpose.key)}
                        onChange={() => togglePurpose(purpose.key)}
                        className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600"
                      />
                      <span>
                        <span className="block font-medium text-gray-900">{purpose.label}</span>
                        <span className="text-xs text-gray-500">{purpose.description}</span>
                      </span>
                    </label>
                  ))}
                </div>
              </div>

              {form.channel === 'email' ? (
                <EmailFields form={form} setForm={setForm} passwordSet={selectedSender?.email?.smtpPasswordSet} />
              ) : (
                <TelegramFields
                  form={form}
                  setForm={setForm}
                  sender={selectedSender}
                  busy={saving}
                  onSyncWebhook={syncTelegramWebhook}
                  onDeleteWebhook={deleteTelegramWebhook}
                  onSyncProfile={syncTelegramProfile}
                  onApplyProfile={applyTelegramProfile}
                />
              )}
            </div>

            <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
              <h4 className="text-base font-semibold text-gray-900">Тест</h4>
              <div className="mt-4 space-y-3">
                <label className="text-sm font-medium text-gray-700">
                  Получатель
                  <input
                    value={testRecipient}
                    onChange={(event) => setTestRecipient(event.target.value)}
                    placeholder={form.channel === 'telegram' ? 'Telegram chat id' : 'name@example.com'}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
                <label className="text-sm font-medium text-gray-700">
                  Тема
                  <input
                    value={testSubject}
                    onChange={(event) => setTestSubject(event.target.value)}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
                <label className="text-sm font-medium text-gray-700">
                  Сообщение
                  <textarea
                    value={testMessage}
                    onChange={(event) => setTestMessage(event.target.value)}
                    rows={4}
                    className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
                  />
                </label>
                <button
                  type="button"
                  onClick={() => void sendTest()}
                  disabled={!canEdit || testing || !form.id}
                  className="inline-flex w-full items-center justify-center gap-2 rounded-lg bg-emerald-600 px-3 py-2 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-60"
                >
                  <Send className="h-4 w-4" />
                  Отправить тест
                </button>
              </div>

              {message && (
                <div className="mt-4 rounded-lg border border-blue-100 bg-blue-50 px-3 py-2 text-sm text-blue-800">
                  {message}
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function SmsSettingsPanel({
  settings,
  form,
  saving,
  canEdit,
  setForm,
  onSave,
}: {
  settings: NotificationSettingsDto | null;
  form: SmsSettingsForm;
  saving: boolean;
  canEdit: boolean;
  setForm: Dispatch<SetStateAction<SmsSettingsForm>>;
  onSave: () => void | Promise<void>;
}) {
  const sms = settings?.sms;

  return (
    <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <Smartphone className="h-5 w-5 text-blue-600" />
            <h4 className="text-base font-semibold text-gray-900">SMS-уведомления</h4>
          </div>
          <p className="mt-1 max-w-3xl text-sm text-gray-500">
            Настройки будущего SMS-канала и диагностика готовности. Пока адаптер провайдера не подключен, сообщения не отправляются.
          </p>
        </div>
        <span className={`rounded-full border px-3 py-1 text-xs font-semibold ${statusClass(Boolean(sms?.ready), Boolean(sms?.enabled))}`}>
          {sms?.ready ? 'Готово' : sms?.enabled ? 'Требуется настройка' : 'Отключено'}
        </span>
      </div>

      {sms?.statusText ? (
        <div className="mt-4 rounded-xl border border-amber-100 bg-amber-50 px-3 py-2 text-sm text-amber-800">
          {sms.statusText}
        </div>
      ) : null}

      <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
          <div className="text-xs font-semibold uppercase text-gray-500">Провайдер</div>
          <div className="mt-2 text-sm text-gray-700">{sms?.providerKey || 'не выбран'}</div>
          <div className="mt-1 text-xs text-gray-500">
            {sms?.providerConfigured ? 'доступы заполнены' : 'нужны провайдер и токен'}
          </div>
        </div>
        <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
          <div className="text-xs font-semibold uppercase text-gray-500">Отправитель</div>
          <div className="mt-2 text-sm text-gray-700">{sms?.senderName || 'не указан'}</div>
          <div className="mt-1 text-xs text-gray-500">имя, которое увидит получатель</div>
        </div>
        <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
          <div className="text-xs font-semibold uppercase text-gray-500">Лимиты</div>
          <div className="mt-2 text-sm text-gray-700">{sms?.dailyLimit ?? form.dailyLimit} в день</div>
          <div className="mt-1 text-xs text-gray-500">до {sms?.maxMessageLength ?? form.maxMessageLength} символов</div>
        </div>
        <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
          <div className="text-xs font-semibold uppercase text-gray-500">Адаптер</div>
          <div className="mt-2 text-sm text-gray-700">{sms?.adapterConfigured ? 'подключен' : 'не подключен'}</div>
          <div className="mt-1 text-xs text-gray-500">токен: {sms?.apiTokenSet ? 'сохранен' : 'не указан'}</div>
        </div>
      </div>

      <div className="mt-5 grid gap-4 md:grid-cols-2">
        <label className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700">
          <input
            type="checkbox"
            checked={form.enabled}
            disabled={!canEdit || saving}
            onChange={(event) => setForm((prev) => ({ ...prev, enabled: event.target.checked }))}
            className="h-4 w-4 rounded border-gray-300 text-blue-600"
          />
          Включить SMS-канал после готовности провайдера
        </label>
        <label className="text-sm font-medium text-gray-700">
          Провайдер
          <select
            value={form.providerKey}
            disabled={!canEdit || saving}
            onChange={(event) => setForm((prev) => ({ ...prev, providerKey: event.target.value }))}
            className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
          >
            <option value="">Не выбран</option>
            <option value="sms-ru">SMS.RU</option>
            <option value="sms-aero">SMS Aero</option>
            <option value="custom">Другой провайдер</option>
          </select>
        </label>
        <label className="text-sm font-medium text-gray-700">
          Имя отправителя
          <input
            value={form.senderName}
            disabled={!canEdit || saving}
            onChange={(event) => setForm((prev) => ({ ...prev, senderName: event.target.value }))}
            placeholder="PayPlanner"
            className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
          />
        </label>
        <label className="text-sm font-medium text-gray-700">
          API-токен
          <input
            type="password"
            value={form.apiToken}
            disabled={!canEdit || saving}
            onChange={(event) => setForm((prev) => ({
              ...prev,
              apiToken: event.target.value,
              keepExistingApiToken: event.target.value.length === 0 ? prev.keepExistingApiToken : false,
            }))}
            placeholder={sms?.apiTokenSet ? 'Токен сохранен, оставьте пустым без изменений' : 'Токен провайдера'}
            className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
          />
        </label>
        <label className="text-sm font-medium text-gray-700">
          Дневной лимит
          <input
            type="number"
            min={1}
            max={100000}
            value={form.dailyLimit}
            disabled={!canEdit || saving}
            onChange={(event) => setForm((prev) => ({ ...prev, dailyLimit: Number(event.target.value) || 100 }))}
            className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
          />
        </label>
        <label className="text-sm font-medium text-gray-700">
          Максимальная длина сообщения
          <input
            type="number"
            min={70}
            max={1000}
            value={form.maxMessageLength}
            disabled={!canEdit || saving}
            onChange={(event) => setForm((prev) => ({ ...prev, maxMessageLength: Number(event.target.value) || 480 }))}
            className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
          />
        </label>
        <label className="text-sm font-medium text-gray-700 md:col-span-2">
          Заметка администратора
          <textarea
            value={form.notes}
            disabled={!canEdit || saving}
            onChange={(event) => setForm((prev) => ({ ...prev, notes: event.target.value }))}
            rows={3}
            maxLength={500}
            className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
          />
        </label>
      </div>

      <div className="mt-4 flex justify-end">
        <button
          type="button"
          onClick={() => void onSave()}
          disabled={!canEdit || saving}
          className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60"
        >
          <Save className={`h-4 w-4 ${saving ? 'animate-pulse' : ''}`} />
          Сохранить SMS
        </button>
      </div>
    </div>
  );
}

function MaxDiagnosticsPanel({ settings }: { settings: NotificationSettingsDto | null }) {
  const max = settings?.max;
  const pendingItems = [
    { label: 'API MAX', ready: Boolean(max?.apiConfirmed) },
    { label: 'Бот', ready: Boolean(max?.botConfigured) },
    { label: 'Сценарии', ready: Boolean(max?.scenariosConfirmed) },
  ];

  return (
    <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <Bot className="h-5 w-5 text-violet-600" />
            <h4 className="text-base font-semibold text-gray-900">MAX messenger</h4>
          </div>
          <p className="mt-1 max-w-3xl text-sm text-gray-500">
            Диагностика будущего канала MAX. Пока API и сценарии не подтверждены, CRM не отправляет сообщения в MAX.
          </p>
        </div>
        <span className={'rounded-full border px-3 py-1 text-xs font-semibold ' + statusClass(Boolean(max?.ready), Boolean(max?.enabled))}>
          {max?.ready ? 'Готово' : max?.enabled ? 'Требуется настройка' : 'Запланирован'}
        </span>
      </div>

      {max?.statusText ? (
        <div className="mt-4 rounded-xl border border-violet-100 bg-violet-50 px-3 py-2 text-sm text-violet-800">
          {max.statusText}
        </div>
      ) : null}

      <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
          <div className="text-xs font-semibold uppercase text-gray-500">Готовность</div>
          <div className="mt-2 space-y-1 text-sm text-gray-700">
            {pendingItems.map((item) => (
              <div key={item.label} className="flex items-center justify-between gap-2">
                <span>{item.label}</span>
                <span className={item.ready ? 'font-semibold text-emerald-700' : 'font-semibold text-amber-700'}>
                  {item.ready ? 'готово' : 'ждет решения'}
                </span>
              </div>
            ))}
          </div>
        </div>
        <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
          <div className="text-xs font-semibold uppercase text-gray-500">События</div>
          <div className="mt-2 text-sm text-gray-700">
            {max?.allowedEventTypes?.length ? String(max.allowedEventTypes.length) + ' разрешено' : 'пока не выбраны'}
          </div>
          <div className="mt-1 text-xs text-gray-500">доступные типы определятся после согласования сценариев</div>
        </div>
        <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
          <div className="text-xs font-semibold uppercase text-gray-500">Успешная доставка</div>
          <div className="mt-2 text-sm text-gray-700">{formatDateTime(max?.lastSuccessfulDeliveryAt)}</div>
          <div className="mt-1 text-xs text-gray-500">после подключения будет видна последняя доставка</div>
        </div>
        <div className="rounded-xl border border-gray-200 bg-gray-50 p-3">
          <div className="text-xs font-semibold uppercase text-gray-500">Ошибки</div>
          <div className="mt-2 text-sm text-gray-700">{formatDateTime(max?.lastFailedDeliveryAt)}</div>
          {max?.lastDeliveryError ? (
            <div className="mt-1 truncate text-xs text-red-600" title={max.lastDeliveryError}>
              {max.lastDeliveryError}
            </div>
          ) : (
            <div className="mt-1 text-xs text-gray-500">ошибок доставки пока нет</div>
          )}
        </div>
      </div>
    </div>
  );
}

function SummaryTile({ label, value, ready }: { label: string; value: number; ready: number }) {
  return (
    <div className="rounded-2xl border border-gray-200 bg-white p-4 shadow-sm">
      <div className="text-sm font-medium text-gray-500">{label}</div>
      <div className="mt-2 flex items-end justify-between gap-3">
        <div className="text-2xl font-semibold text-gray-900">{value}</div>
        <div className="inline-flex items-center gap-1 rounded-full border border-emerald-200 bg-emerald-50 px-2 py-1 text-xs font-semibold text-emerald-700">
          <CheckCircle2 className="h-3.5 w-3.5" />
          {ready}
        </div>
      </div>
    </div>
  );
}

function formatDeliveryDate(value?: string | null) {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
}

function deliveryStatusLabel(status: string) {
  switch (status) {
    case 'sent':
      return 'Отправлено';
    case 'failed':
      return 'Ошибка';
    case 'skipped':
      return 'Пропущено';
    case 'pending':
      return 'В обработке';
    default:
      return status || '—';
  }
}

function deliveryStatusClass(status: string) {
  switch (status) {
    case 'sent':
      return 'border-emerald-200 bg-emerald-50 text-emerald-700';
    case 'failed':
      return 'border-rose-200 bg-rose-50 text-rose-700';
    case 'skipped':
      return 'border-amber-200 bg-amber-50 text-amber-700';
    default:
      return 'border-slate-200 bg-slate-50 text-slate-700';
  }
}

function DeliveryLogPanel({
  items,
  total,
  status,
  loading,
  retryingId,
  canEdit,
  onStatusChange,
  onRefresh,
  onRetry,
}: {
  items: NotificationDeliveryDto[];
  total: number;
  status: string;
  loading: boolean;
  retryingId: number | null;
  canEdit: boolean;
  onStatusChange: (status: string) => void;
  onRefresh: () => void | Promise<void>;
  onRetry: (delivery: NotificationDeliveryDto) => void | Promise<void>;
}) {
  return (
    <section className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h4 className="text-base font-semibold text-gray-900">Журнал доставки</h4>
          <p className="text-sm text-gray-500">Последние попытки отправки email и Telegram. Ошибочные и пропущенные можно повторить вручную.</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <select
            value={status}
            onChange={(event) => onStatusChange(event.target.value)}
            className="h-10 rounded-lg border border-gray-200 bg-white px-3 text-sm"
          >
            <option value="failed">Ошибки</option>
            <option value="skipped">Пропущенные</option>
            <option value="sent">Отправленные</option>
            <option value="all">Все</option>
          </select>
          <button
            type="button"
            onClick={() => void onRefresh()}
            disabled={loading}
            className="inline-flex h-10 items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 text-sm font-semibold text-gray-700 hover:bg-gray-50 disabled:opacity-60"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            Обновить
          </button>
        </div>
      </div>

      <div className="mt-4 overflow-x-auto rounded-xl border border-gray-200">
        <table className="min-w-full divide-y divide-gray-200 text-sm">
          <thead className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
            <tr>
              <th className="px-4 py-3">Дата</th>
              <th className="px-4 py-3">Канал</th>
              <th className="px-4 py-3">Получатель</th>
              <th className="px-4 py-3">Тема</th>
              <th className="px-4 py-3">Статус</th>
              <th className="px-4 py-3 text-right">Действия</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-200 bg-white">
            {loading ? (
              <tr>
                <td colSpan={6} className="px-4 py-8 text-center text-sm text-gray-500">Загружаем журнал доставки...</td>
              </tr>
            ) : items.length === 0 ? (
              <tr>
                <td colSpan={6} className="px-4 py-8 text-center text-sm text-gray-500">Записей по выбранному фильтру нет.</td>
              </tr>
            ) : (
              items.map((item) => {
                const canRetry = item.status === 'failed' || item.status === 'skipped';
                return (
                  <tr key={item.id} className="align-top hover:bg-gray-50">
                    <td className="whitespace-nowrap px-4 py-3 text-gray-700">{formatDeliveryDate(item.createdAt)}</td>
                    <td className="px-4 py-3 text-gray-700">{channelLabel(item.channel as NotificationChannel)}</td>
                    <td className="max-w-[220px] px-4 py-3 text-gray-700">
                      <div className="truncate" title={item.recipientAddress}>{item.recipientAddress || '—'}</div>
                      {item.senderName ? <div className="text-xs text-gray-500">{item.senderName}</div> : null}
                    </td>
                    <td className="max-w-[320px] px-4 py-3 text-gray-700">
                      <div className="truncate" title={item.subject}>{item.subject || '—'}</div>
                      {item.error ? <div className="mt-1 line-clamp-2 text-xs text-rose-600">{item.error}</div> : null}
                    </td>
                    <td className="px-4 py-3">
                      <span className={`inline-flex rounded-full border px-2 py-0.5 text-xs font-semibold ${deliveryStatusClass(item.status)}`}>
                        {deliveryStatusLabel(item.status)}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-right">
                      {canRetry ? (
                        <button
                          type="button"
                          onClick={() => void onRetry(item)}
                          disabled={!canEdit || retryingId === item.id}
                          className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-xs font-semibold text-blue-700 hover:bg-blue-100 disabled:opacity-60"
                        >
                          <Send className="h-3.5 w-3.5" />
                          Повторить
                        </button>
                      ) : (
                        <span className="text-xs text-gray-400">—</span>
                      )}
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>

      <div className="mt-3 text-xs text-gray-500">Показано {items.length} из {total}</div>
    </section>
  );
}

function SenderTable({
  channel,
  title,
  description,
  senders,
  purposes,
  onCreate,
  onOpen,
  canEdit,
}: {
  channel: NotificationChannel;
  title: string;
  description: string;
  senders: NotificationSenderDto[];
  purposes: NotificationPurposeDto[];
  onCreate: (channel: NotificationChannel) => void;
  onOpen: (sender: NotificationSenderDto) => void;
  canEdit: boolean;
}) {
  const Icon = channel === 'telegram' ? Bot : Mail;

  return (
    <div className="rounded-xl border border-gray-200 bg-gray-50/60">
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-gray-200 bg-white px-4 py-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <Icon className={`h-4 w-4 ${channel === 'telegram' ? 'text-teal-600' : 'text-blue-600'}`} />
            <h5 className="font-semibold text-gray-900">{title}</h5>
          </div>
          <p className="mt-1 text-sm text-gray-500">{description}</p>
        </div>
        <a
          href={buildNotificationRouteHref({ mode: 'new', channel })}
          onClick={(event) => {
            event.preventDefault();
            if (!canEdit) return;
            onCreate(channel);
          }}
          className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-semibold ${
            !canEdit
              ? 'cursor-not-allowed border-gray-200 bg-gray-100 text-gray-400'
              : channel === 'telegram'
              ? 'border-teal-200 bg-teal-50 text-teal-700 hover:bg-teal-100'
              : 'border-blue-200 bg-blue-50 text-blue-700 hover:bg-blue-100'
          }`}
        >
          <Plus className="h-4 w-4" />
          Добавить
        </a>
      </div>

      {senders.length === 0 ? (
        <div className="m-4 rounded-xl border border-dashed border-gray-300 bg-white px-4 py-5 text-sm text-gray-600">
          {channel === 'telegram' ? 'Telegram-боты пока не заведены.' : 'Email отправители пока не заведены.'}
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-200 text-sm">
            <thead className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
              <tr>
                <th className="px-4 py-3">Название</th>
                <th className="px-4 py-3">Сценарии</th>
                <th className="px-4 py-3">{channel === 'telegram' ? 'Бот' : 'SMTP'}</th>
                <th className="px-4 py-3">Статус</th>
                <th className="px-4 py-3">Порядок</th>
                <th className="px-4 py-3 text-right">Действия</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200 bg-white">
              {senders.map((sender) => (
                <tr key={sender.id} className="hover:bg-gray-50">
                  <td className="px-4 py-3">
                    <div className="font-semibold text-gray-900">{sender.name}</div>
                    <div className="text-xs text-gray-500">#{sender.id}</div>
                  </td>
                  <td className="px-4 py-3">
                    <div className="flex max-w-[260px] flex-wrap gap-1.5">
                      {sender.purposes.map((purpose) => (
                        <span key={purpose} className="rounded-full border border-gray-200 bg-gray-50 px-2 py-0.5 text-xs text-gray-700">
                          {purposeLabel(purposes, purpose)}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-4 py-3 text-gray-700">
                    {channel === 'telegram' ? (
                      <>
                        <div>{sender.telegram?.botUsername ? `@${sender.telegram.botUsername}` : 'Username не задан'}</div>
                        <div className="text-xs text-gray-500">
                          {updateModeLabel(sender.telegram?.updateMode)} · подписок: {sender.telegram?.subscriptionsCount ?? 0}
                        </div>
                      </>
                    ) : (
                      <>
                        <div>{sender.email?.fromEmail || 'From email не задан'}</div>
                        <div className="text-xs text-gray-500">
                          {sender.email?.smtpHost || 'SMTP host не задан'}{sender.email?.smtpPort ? `:${sender.email.smtpPort}` : ''}
                        </div>
                      </>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    <span className={`inline-flex rounded-full border px-2 py-1 text-xs font-semibold ${statusClass(sender.ready, sender.isEnabled)}`}>
                      {senderStatusLabel(sender)}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-gray-700">{sender.sortOrder}</td>
                  <td className="px-4 py-3 text-right">
                    <a
                      href={buildNotificationRouteHref({ mode: 'edit', channel: sender.channel, id: sender.id })}
                      onClick={(event) => {
                        event.preventDefault();
                        onOpen(sender);
                      }}
                      className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-semibold text-gray-700 hover:border-blue-200 hover:bg-blue-50 hover:text-blue-700"
                    >
                      <Pencil className="h-4 w-4" />
                      Открыть
                    </a>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function senderStatusLabel(sender: NotificationSenderDto) {
  if (!sender.isEnabled) return 'Отключён';
  return sender.ready ? 'Готов' : 'Не готов';
}

function EmailFields({
  form,
  setForm,
  passwordSet,
}: {
  form: SenderForm;
  setForm: Dispatch<SetStateAction<SenderForm>>;
  passwordSet?: boolean;
}) {
  return (
    <div className="mt-5 grid gap-4 md:grid-cols-2">
      <label className="text-sm font-medium text-gray-700">
        SMTP host
        <input value={form.smtpHost} onChange={(event) => setForm((prev) => ({ ...prev, smtpHost: event.target.value }))} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
      </label>
      <label className="text-sm font-medium text-gray-700">
        SMTP port
        <input type="number" value={form.smtpPort} onChange={(event) => setForm((prev) => ({ ...prev, smtpPort: Number(event.target.value) || 587 }))} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
      </label>
      <label className="text-sm font-medium text-gray-700">
        SMTP username
        <input value={form.smtpUsername} onChange={(event) => setForm((prev) => ({ ...prev, smtpUsername: event.target.value }))} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
      </label>
      <label className="text-sm font-medium text-gray-700">
        SMTP password {passwordSet ? '(сохранён)' : ''}
        <input type="password" value={form.smtpPassword} onChange={(event) => setForm((prev) => ({ ...prev, smtpPassword: event.target.value, keepExistingPassword: false }))} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
      </label>
      <label className="text-sm font-medium text-gray-700">
        From email
        <input value={form.fromEmail} onChange={(event) => setForm((prev) => ({ ...prev, fromEmail: event.target.value }))} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
      </label>
      <label className="text-sm font-medium text-gray-700">
        From name
        <input value={form.fromName} onChange={(event) => setForm((prev) => ({ ...prev, fromName: event.target.value }))} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
      </label>
      <label className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700">
        <input type="checkbox" checked={form.enableSsl} onChange={(event) => setForm((prev) => ({ ...prev, enableSsl: event.target.checked }))} className="h-4 w-4 rounded border-gray-300 text-blue-600" />
        SSL
      </label>
      {passwordSet && (
        <label className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700">
          <input type="checkbox" checked={form.keepExistingPassword} onChange={(event) => setForm((prev) => ({ ...prev, keepExistingPassword: event.target.checked }))} className="h-4 w-4 rounded border-gray-300 text-blue-600" />
          Оставить сохранённый пароль
        </label>
      )}
    </div>
  );
}

function TelegramFields({
  form,
  setForm,
  sender,
  busy,
  onSyncWebhook,
  onDeleteWebhook,
  onSyncProfile,
  onApplyProfile,
}: {
  form: SenderForm;
  setForm: Dispatch<SetStateAction<SenderForm>>;
  sender?: NotificationSenderDto | null;
  busy: boolean;
  onSyncWebhook: () => void;
  onDeleteWebhook: () => void;
  onSyncProfile: () => void;
  onApplyProfile: () => void;
}) {
  const tokenSet = sender?.telegram?.botTokenSet;
  const webhookSecretSet = sender?.telegram?.webhookSecretSet;
  const proxyPasswordSet = sender?.telegram?.proxyPasswordSet;
  const savedMode = normalizeUpdateMode(sender?.telegram?.updateMode);
  const subscriptionsCount = sender?.telegram?.subscriptionsCount ?? 0;

  return (
    <div className="mt-5 grid gap-4 md:grid-cols-2">
      <label className="text-sm font-medium text-gray-700">
        Bot token {tokenSet ? '(сохранён)' : ''}
        <input type="password" value={form.botToken} onChange={(event) => setForm((prev) => ({ ...prev, botToken: event.target.value, keepExistingToken: false }))} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
      </label>
      <label className="text-sm font-medium text-gray-700">
        Bot username
        <input value={form.botUsername} onChange={(event) => setForm((prev) => ({ ...prev, botUsername: event.target.value }))} className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
      </label>
      {tokenSet && (
        <label className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700">
          <input type="checkbox" checked={form.keepExistingToken} onChange={(event) => setForm((prev) => ({ ...prev, keepExistingToken: event.target.checked }))} className="h-4 w-4 rounded border-gray-300 text-blue-600" />
          Оставить сохранённый token
        </label>
      )}
      <label className="text-sm font-medium text-gray-700">
        Режим входящих сообщений
        <select
          value={form.updateMode}
          onChange={(event) => setForm((prev) => ({ ...prev, updateMode: normalizeUpdateMode(event.target.value) }))}
          className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
        >
          <option value="disabled">Отключены</option>
          <option value="webhook">Webhook</option>
          <option value="polling">Polling</option>
        </select>
      </label>
      <label className="text-sm font-medium text-gray-700">
        Webhook secret {webhookSecretSet ? '(сохранён)' : ''}
        <input
          type="password"
          value={form.webhookSecret}
          onChange={(event) => setForm((prev) => ({ ...prev, webhookSecret: event.target.value, keepExistingWebhookSecret: false }))}
          placeholder="Сгенерируется автоматически"
          className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
        />
      </label>
      {webhookSecretSet && (
        <label className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700">
          <input
            type="checkbox"
            checked={form.keepExistingWebhookSecret}
            onChange={(event) => setForm((prev) => ({ ...prev, keepExistingWebhookSecret: event.target.checked }))}
            className="h-4 w-4 rounded border-gray-300 text-blue-600"
          />
          Оставить сохранённый webhook secret
        </label>
      )}
      <label className="text-sm font-medium text-gray-700">
        Proxy URL
        <input
          value={form.proxyUrl}
          onChange={(event) => setForm((prev) => ({ ...prev, proxyUrl: event.target.value }))}
          placeholder="http://127.0.0.1:8080"
          className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
        />
      </label>
      <label className="text-sm font-medium text-gray-700">
        Proxy username
        <input
          value={form.proxyUsername}
          onChange={(event) => setForm((prev) => ({ ...prev, proxyUsername: event.target.value }))}
          className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
        />
      </label>
      <label className="text-sm font-medium text-gray-700">
        Proxy password {proxyPasswordSet ? '(сохранён)' : ''}
        <input
          type="password"
          value={form.proxyPassword}
          onChange={(event) => setForm((prev) => ({ ...prev, proxyPassword: event.target.value, keepExistingProxyPassword: false }))}
          className="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
        />
      </label>
      {proxyPasswordSet && (
        <label className="flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700">
          <input
            type="checkbox"
            checked={form.keepExistingProxyPassword}
            onChange={(event) => setForm((prev) => ({ ...prev, keepExistingProxyPassword: event.target.checked }))}
            className="h-4 w-4 rounded border-gray-300 text-blue-600"
          />
          Оставить сохранённый пароль proxy
        </label>
      )}
      <div className="md:col-span-2 rounded-xl border border-blue-100 bg-blue-50 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <div className="text-sm font-semibold text-blue-950">Профиль Telegram-бота</div>
            <p className="mt-1 text-xs text-blue-700">
              Проверка подтягивает имя, username, описание, команды и фото. Применение меняет имя, описания и команды в Telegram.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void onSyncProfile()}
              disabled={busy || !form.id}
              title="Проверить и подтянуть данные бота"
              aria-label="Проверить и подтянуть данные бота"
              className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-blue-200 bg-white text-blue-700 hover:bg-blue-100 disabled:opacity-60"
            >
              <RefreshCw className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => void onApplyProfile()}
              disabled={busy || !form.id}
              title="Применить изменения в Telegram"
              aria-label="Применить изменения в Telegram"
              className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-60"
            >
              <Save className="h-4 w-4" />
            </button>
          </div>
        </div>

        <div className="mt-4 grid gap-4 md:grid-cols-[96px_minmax(0,1fr)]">
          <TelegramBotPhoto sender={sender} />
          <div className="grid gap-3 md:grid-cols-2">
            <ReadOnlyInfo label="ID" value={sender?.telegram?.botId ? String(sender.telegram.botId) : '—'} />
            <ReadOnlyInfo label="Username" value={sender?.telegram?.botUsername ? `@${sender.telegram.botUsername}` : '—'} />
            <ReadOnlyInfo label="First name" value={sender?.telegram?.botFirstName || '—'} />
            <ReadOnlyInfo label="Синхронизация" value={formatDateTime(sender?.telegram?.profileSyncedAt)} />
          </div>
        </div>

        <div className="mt-4 grid gap-4 md:grid-cols-2">
          <label className="text-sm font-medium text-blue-950">
            Имя в Telegram
            <input
              value={form.profileName}
              maxLength={64}
              onChange={(event) => setForm((prev) => ({ ...prev, profileName: event.target.value }))}
              className="mt-1 w-full rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm text-slate-900"
            />
            <span className="mt-1 block text-xs text-blue-700">{form.profileName.length}/64</span>
          </label>
          <label className="text-sm font-medium text-blue-950">
            Короткое описание
            <textarea
              value={form.profileShortDescription}
              maxLength={120}
              rows={3}
              onChange={(event) => setForm((prev) => ({ ...prev, profileShortDescription: event.target.value }))}
              className="mt-1 w-full rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm text-slate-900"
            />
            <span className="mt-1 block text-xs text-blue-700">{form.profileShortDescription.length}/120</span>
          </label>
          <label className="text-sm font-medium text-blue-950 md:col-span-2">
            Описание
            <textarea
              value={form.profileDescription}
              maxLength={512}
              rows={4}
              onChange={(event) => setForm((prev) => ({ ...prev, profileDescription: event.target.value }))}
              className="mt-1 w-full rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm text-slate-900"
            />
            <span className="mt-1 block text-xs text-blue-700">{form.profileDescription.length}/512</span>
          </label>
          <label className="text-sm font-medium text-blue-950 md:col-span-2">
            Команды
            <textarea
              value={form.profileCommandsText}
              rows={5}
              onChange={(event) => setForm((prev) => ({ ...prev, profileCommandsText: event.target.value }))}
              className="mt-1 w-full rounded-lg border border-blue-200 bg-white px-3 py-2 font-mono text-sm text-slate-900"
            />
            <span className="mt-1 block text-xs text-blue-700">
              Формат: /start - описание. До 100 команд, команда 1-32 символа, описание до 256.
            </span>
          </label>
        </div>

        <div className="mt-3 flex flex-wrap gap-2 text-xs">
          <span className="rounded-full border border-blue-200 bg-white px-2 py-1 text-blue-800">
            Группы: {sender?.telegram?.canJoinGroups ? 'да' : 'нет'}
          </span>
          <span className="rounded-full border border-blue-200 bg-white px-2 py-1 text-blue-800">
            Читать все сообщения: {sender?.telegram?.canReadAllGroupMessages ? 'да' : 'нет'}
          </span>
          <span className="rounded-full border border-blue-200 bg-white px-2 py-1 text-blue-800">
            Inline: {sender?.telegram?.supportsInlineQueries ? 'да' : 'нет'}
          </span>
        </div>
      </div>
      <div className="md:col-span-2 rounded-xl border border-slate-200 bg-slate-50 p-4 text-sm text-slate-700">
        <div className="grid gap-3 md:grid-cols-3">
          <div>
            <div className="text-xs font-semibold uppercase text-slate-500">Режим</div>
            <div className="mt-1 font-semibold text-slate-900">{updateModeLabel(savedMode)}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase text-slate-500">Подписки</div>
            <div className="mt-1 font-semibold text-slate-900">{subscriptionsCount}</div>
          </div>
          <div>
            <div className="text-xs font-semibold uppercase text-slate-500">Последний polling</div>
            <div className="mt-1 font-semibold text-slate-900">{formatDateTime(sender?.telegram?.lastPollingAt)}</div>
          </div>
        </div>

        {sender?.telegram?.webhookUrl && (
          <div className="mt-3">
            <div className="text-xs font-semibold uppercase text-slate-500">Webhook URL</div>
            <div className="mt-1 break-all rounded-lg border border-slate-200 bg-white px-3 py-2 font-mono text-xs text-slate-700">
              {sender.telegram.webhookUrl}
            </div>
            <div className="mt-1 text-xs text-slate-500">Синхронизирован: {formatDateTime(sender.telegram.webhookSyncedAt)}</div>
          </div>
        )}

        {form.updateMode === 'webhook' && (
          <div className="mt-4 flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void onSyncWebhook()}
              disabled={busy || !form.id}
              className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-100 disabled:opacity-60"
            >
              <Link2 className="h-4 w-4" />
              Синхронизировать webhook
            </button>
            <button
              type="button"
              onClick={() => void onDeleteWebhook()}
              disabled={busy || !form.id || !sender?.telegram?.webhookUrl}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-100 disabled:opacity-60"
            >
              <Unlink className="h-4 w-4" />
              Отключить webhook
            </button>
          </div>
        )}

        {form.updateMode === 'polling' && (
          <p className="mt-3 text-xs text-slate-500">
            Polling работает фоновым сервисом CRM. После сохранения бот сам будет забирать входящие команды /start, /status и /help.
          </p>
        )}

        {!form.id && (
          <p className="mt-3 text-xs text-slate-500">
            Сначала сохраните Telegram-бота, затем можно будет синхронизировать webhook.
          </p>
        )}
      </div>
    </div>
  );
}

function normalizeUpdateMode(value?: string | null): SenderForm['updateMode'] {
  return value === 'webhook' || value === 'polling' ? value : 'disabled';
}

function updateModeLabel(value?: string | null) {
  const mode = normalizeUpdateMode(value);
  if (mode === 'webhook') return 'Webhook';
  if (mode === 'polling') return 'Polling';
  return 'Отключены';
}

function formatDateTime(value?: string | null) {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function TelegramBotPhoto({ sender }: { sender?: NotificationSenderDto | null }) {
  const [photoUrl, setPhotoUrl] = useState<string | null>(null);
  const senderId = sender?.id;
  const hasPhoto = Boolean(sender?.telegram?.hasProfilePhoto);
  const syncedAt = sender?.telegram?.profileSyncedAt;

  useEffect(() => {
    let disposed = false;
    let objectUrl: string | null = null;

    if (!senderId || !hasPhoto) {
      setPhotoUrl(null);
      return () => undefined;
    }

    apiService.getTelegramBotProfilePhotoBlob(senderId)
      .then((blob) => {
        if (disposed) return;
        objectUrl = URL.createObjectURL(blob);
        setPhotoUrl(objectUrl);
      })
      .catch(() => {
        if (!disposed) {
          setPhotoUrl(null);
        }
      });

    return () => {
      disposed = true;
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
      }
    };
  }, [senderId, hasPhoto, syncedAt]);

  return (
    <div className="flex h-24 w-24 items-center justify-center overflow-hidden rounded-xl border border-blue-200 bg-white">
      {photoUrl ? (
        <img src={photoUrl} alt="Telegram bot" className="h-full w-full object-cover" />
      ) : (
        <Bot className="h-9 w-9 text-blue-400" />
      )}
    </div>
  );
}

function ReadOnlyInfo({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-blue-100 bg-white px-3 py-2">
      <div className="text-xs font-semibold uppercase text-blue-500">{label}</div>
      <div className="mt-1 truncate text-sm font-semibold text-slate-900">{value}</div>
    </div>
  );
}

function defaultCommandsText() {
  return [
    '/start - Привязать Telegram к сотруднику',
    '/status - Проверить привязку',
    '/help - Помощь по боту',
  ].join('\n');
}

function commandsToText(commands?: TelegramBotCommandDto[] | null) {
  if (!commands?.length) return defaultCommandsText();
  return commands.map((item) => `/${item.command} - ${item.description}`).join('\n');
}

function parseCommandsText(value: string): TelegramBotCommandDto[] {
  return value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => {
      const withoutSlash = line.replace(/^\//, '');
      const match = withoutSlash.match(/^([A-Za-z0-9_]{1,32})(?:\s*[-–—:]\s*|\s+)(.*)$/);
      if (!match) {
        return { command: withoutSlash.toLowerCase(), description: '' };
      }
      return {
        command: match[1].toLowerCase(),
        description: match[2].trim(),
      };
    });
}

function resolveNextOrder(senders: NotificationSenderDto[]) {
  return (senders.reduce((max, item) => Math.max(max, item.sortOrder), 0) || 0) + 10;
}

function purposeLabel(purposes: NotificationPurposeDto[], key: string) {
  return purposes.find((item) => item.key === key)?.label ?? key;
}
