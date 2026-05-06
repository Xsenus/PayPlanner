import { useCallback, useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react';
import { Bot, CheckCircle2, Link2, Mail, Plus, RefreshCw, Save, Send, Trash2, Unlink } from 'lucide-react';
import {
  apiService,
  type NotificationChannel,
  type TelegramBotCommandDto,
  type NotificationPurposeDto,
  type NotificationSenderDto,
  type NotificationSettingsDto,
  type UpsertNotificationSenderRequest,
} from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

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

const DEFAULT_PURPOSES = ['manualTest'];

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
  return channel === 'telegram' ? 'Telegram' : 'Email';
}

function statusClass(ready: boolean, enabled: boolean) {
  if (!enabled) return 'bg-slate-100 text-slate-700 border-slate-200';
  return ready ? 'bg-emerald-50 text-emerald-700 border-emerald-200' : 'bg-amber-50 text-amber-700 border-amber-200';
}

export function NotificationSettings() {
  const toast = useToast();
  const { confirm } = useDialog();
  const [settings, setSettings] = useState<NotificationSettingsDto | null>(null);
  const [form, setForm] = useState<SenderForm>(() => createBlankForm());
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testRecipient, setTestRecipient] = useState('');
  const [testSubject, setTestSubject] = useState('Тестовое уведомление PayPlanner');
  const [testMessage, setTestMessage] = useState('Проверка отправителя уведомлений.');
  const [message, setMessage] = useState<string | null>(null);

  const loadSettings = useCallback(async () => {
    setLoading(true);
    setMessage(null);
    try {
      const next = await apiService.getNotificationSettings();
      setSettings(next);
      setForm((prev) => {
        if (prev.id) {
          const actual = next.senders.find((item) => item.id === prev.id);
          return actual ? formFromSender(actual) : createBlankForm('email', resolveNextOrder(next.senders));
        }
        return prev;
      });
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

  const senders = useMemo(() => settings?.senders ?? [], [settings?.senders]);
  const purposes = useMemo(() => settings?.purposes ?? [], [settings?.purposes]);
  const emailSenders = useMemo(() => senders.filter((item) => item.channel === 'email'), [senders]);
  const telegramSenders = useMemo(() => senders.filter((item) => item.channel === 'telegram'), [senders]);
  const selectedSender = form.id ? senders.find((item) => item.id === form.id) : null;

  const selectNewSender = (channel: NotificationChannel) => {
    const nextOrder = resolveNextOrder(senders.filter((item) => item.channel === channel));
    setForm(createBlankForm(channel, nextOrder));
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
    if (!form.id) return;
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
    } catch (error) {
      const text = extractDisplayErrorMessage(error, 'Не удалось отправить тестовое уведомление');
      setMessage(text);
      toast.error(text, 'Уведомления');
    } finally {
      setTesting(false);
    }
  };

  const syncTelegramWebhook = async () => {
    if (!form.id) return;
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
    if (!form.id) return;
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
    if (!form.id) return;
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
    if (!form.id) return;
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

      <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h4 className="text-base font-semibold text-gray-900">Отправители</h4>
            <p className="text-sm text-gray-500">Email-ящики и Telegram-боты для системных сообщений.</p>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => selectNewSender('email')}
              className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-100"
            >
              <Plus className="h-4 w-4" />
              Email
            </button>
            <button
              type="button"
              onClick={() => selectNewSender('telegram')}
              className="inline-flex items-center gap-2 rounded-lg border border-teal-200 bg-teal-50 px-3 py-2 text-sm font-semibold text-teal-700 hover:bg-teal-100"
            >
              <Plus className="h-4 w-4" />
              Telegram
            </button>
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
        </div>

        <div className="mt-4 grid gap-3 lg:grid-cols-2">
          {senders.map((sender) => (
            <button
              key={sender.id}
              type="button"
              onClick={() => setForm(formFromSender(sender))}
              className={`rounded-xl border p-4 text-left transition ${
                form.id === sender.id
                  ? 'border-blue-300 bg-blue-50'
                  : 'border-gray-200 bg-white hover:border-blue-200 hover:bg-blue-50/50'
              }`}
            >
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    {sender.channel === 'telegram' ? <Bot className="h-4 w-4 text-teal-600" /> : <Mail className="h-4 w-4 text-blue-600" />}
                    <span className="truncate font-semibold text-gray-900">{sender.name}</span>
                  </div>
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {sender.purposes.map((purpose) => (
                      <span key={purpose} className="rounded-full border border-gray-200 bg-gray-50 px-2 py-0.5 text-xs text-gray-700">
                        {purposeLabel(purposes, purpose)}
                      </span>
                    ))}
                  </div>
                </div>
                <span className={`shrink-0 rounded-full border px-2 py-1 text-xs font-semibold ${statusClass(sender.ready, sender.isEnabled)}`}>
                  {!sender.isEnabled ? 'Отключён' : sender.ready ? 'Готов' : 'Не готов'}
                </span>
              </div>
            </button>
          ))}
          {senders.length === 0 && (
            <div className="rounded-xl border border-dashed border-gray-300 bg-gray-50 p-5 text-sm text-gray-600">
              Отправители пока не заведены.
            </div>
          )}
        </div>
      </div>

      <div className="grid gap-6 xl:grid-cols-[minmax(0,1.35fr)_minmax(320px,0.65fr)]">
        <div className="rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
          <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
            <div>
              <h4 className="text-base font-semibold text-gray-900">{form.id ? 'Редактирование отправителя' : 'Новый отправитель'}</h4>
              {selectedSender && (
                <p className="text-sm text-gray-500">{channelLabel(selectedSender.channel)} · #{selectedSender.id}</p>
              )}
            </div>
            <div className="flex flex-wrap gap-2">
              {form.id && (
                <button
                  type="button"
                  onClick={() => void deleteSender()}
                  disabled={saving}
                  className="inline-flex items-center gap-2 rounded-lg border border-rose-200 bg-white px-3 py-2 text-sm font-semibold text-rose-700 hover:bg-rose-50 disabled:opacity-60"
                >
                  <Trash2 className="h-4 w-4" />
                  Удалить
                </button>
              )}
              <button
                type="button"
                onClick={() => void saveSender()}
                disabled={saving}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60"
              >
                <Save className="h-4 w-4" />
                Сохранить
              </button>
            </div>
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <label className="text-sm font-medium text-gray-700">
              Канал
              <select
                value={form.channel}
                onChange={(event) => setForm(createBlankForm(event.target.value as NotificationChannel, form.sortOrder))}
                disabled={Boolean(form.id)}
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
              disabled={testing || !form.id}
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
