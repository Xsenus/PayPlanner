import { useEffect, useState, type ReactNode } from 'react';
import {
  authService,
  type AuthProviderSettings as AuthProviderSettingsDto,
} from '../../services/authService';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';

const EMPTY_SETTINGS: AuthProviderSettingsDto = {
  googleEnabled: false,
  googleClientId: '',
  googleClientSecret: '',
  vkEnabled: false,
  vkClientId: '',
  vkClientSecret: '',
  yandexEnabled: false,
  yandexClientId: '',
  yandexClientSecret: '',
  telegramEnabled: false,
  telegramWidgetEnabled: false,
  telegramBotToken: '',
  telegramBotUsername: '',
  telegramWebhookSecret: '',
  telegramWebhookUrl: '',
  telegramWebhookConfigured: false,
  telegramBotInfoSyncedAt: null,
  telegramWebhookSyncedAt: null,
};

function formatDateTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  try {
    return new Date(value).toLocaleString('ru-RU');
  } catch {
    return value;
  }
}

interface ProviderCardProps {
  title: string;
  description: string;
  enabled: boolean;
  onEnabledChange: (checked: boolean) => void;
  children: ReactNode;
}

function ProviderCard({ title, description, enabled, onEnabledChange, children }: ProviderCardProps) {
  return (
    <div className="rounded-2xl border border-gray-200 p-5 space-y-4">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h4 className="text-base font-semibold text-gray-900">{title}</h4>
          <p className="mt-1 text-sm text-gray-600">{description}</p>
        </div>
        <label className="inline-flex items-center gap-2 text-sm font-medium text-gray-700">
          <input
            type="checkbox"
            checked={enabled}
            onChange={(event) => onEnabledChange(event.target.checked)}
            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          <span>Включено</span>
        </label>
      </div>
      {children}
    </div>
  );
}

export function AuthProviderSettings() {
  const { confirm } = useDialog();
  const toast = useToast();
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [syncingTelegram, setSyncingTelegram] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [settings, setSettings] = useState<AuthProviderSettingsDto>(EMPTY_SETTINGS);

  const loadSettings = async () => {
    setLoading(true);
    setMessage(null);
    try {
      const nextSettings = await authService.getAuthProviderSettings();
      setSettings(nextSettings);
    } catch (error) {
      const nextMessage = error instanceof Error ? error.message : 'Не удалось загрузить настройки внешней авторизации.';
      setMessage(nextMessage);
      toast.error(nextMessage, 'Авторизация');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadSettings();
  }, []);

  const saveSettings = async () => {
    const approved = await confirm({
      title: 'Сохранить настройки авторизации',
      description: 'Провайдеры входа будут доступны только после сохранения и проверки заполненных ключей.',
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (!approved) {
      return;
    }

    setSaving(true);
    setMessage(null);
    try {
      const result = await authService.updateAuthProviderSettings({
        googleEnabled: settings.googleEnabled,
        googleClientId: settings.googleClientId,
        googleClientSecret: settings.googleClientSecret,
        vkEnabled: settings.vkEnabled,
        vkClientId: settings.vkClientId,
        vkClientSecret: settings.vkClientSecret,
        yandexEnabled: settings.yandexEnabled,
        yandexClientId: settings.yandexClientId,
        yandexClientSecret: settings.yandexClientSecret,
        telegramEnabled: settings.telegramEnabled,
        telegramWidgetEnabled: settings.telegramWidgetEnabled,
        telegramBotToken: settings.telegramBotToken,
      });

      setSettings(result.settings);
      setMessage(result.message);
      toast.success(result.message, 'Авторизация');
    } catch (error) {
      const nextMessage = error instanceof Error ? error.message : 'Не удалось сохранить настройки внешней авторизации.';
      setMessage(nextMessage);
      toast.error(nextMessage, 'Авторизация');
    } finally {
      setSaving(false);
    }
  };

  const syncTelegram = async () => {
    const approved = await confirm({
      title: 'Синхронизировать Telegram',
      description: 'Будет обновлена информация о боте и настроен webhook для входа через deep-link.',
      confirmLabel: 'Синхронизировать',
      cancelLabel: 'Отмена',
    });
    if (!approved) {
      return;
    }

    setSyncingTelegram(true);
    setMessage(null);
    try {
      const result = await authService.syncTelegramWebhook();
      await loadSettings();
      setMessage(result.message);
      toast.success(result.message, 'Telegram');
    } catch (error) {
      const nextMessage = error instanceof Error ? error.message : 'Не удалось синхронизировать Telegram.';
      setMessage(nextMessage);
      toast.error(nextMessage, 'Telegram');
    } finally {
      setSyncingTelegram(false);
    }
  };

  if (loading) {
    return (
      <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 text-sm text-gray-600">
        Загружаем настройки внешней авторизации…
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="bg-white border border-gray-200 rounded-2xl shadow-sm p-6 space-y-4">
        <div className="rounded-xl border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-900">
          Новые пользователи, зарегистрированные через внешние провайдеры, все равно создаются как ожидающие одобрения администратора.
        </div>

        <div className="grid gap-5">
          <ProviderCard
            title="Google"
            description="OAuth-вход через аккаунт Google."
            enabled={settings.googleEnabled}
            onEnabledChange={(checked) => setSettings((current) => ({ ...current, googleEnabled: checked }))}
          >
            <div className="grid gap-4 md:grid-cols-2">
              <label className="text-sm text-gray-700">
                <span className="mb-1 block">Client ID</span>
                <input
                  type="text"
                  value={settings.googleClientId}
                  onChange={(event) => setSettings((current) => ({ ...current, googleClientId: event.target.value }))}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2"
                  placeholder="Google OAuth Client ID"
                />
              </label>
              <label className="text-sm text-gray-700">
                <span className="mb-1 block">Client Secret</span>
                <input
                  type="password"
                  value={settings.googleClientSecret}
                  onChange={(event) => setSettings((current) => ({ ...current, googleClientSecret: event.target.value }))}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2"
                  placeholder="Google OAuth Client Secret"
                />
              </label>
            </div>
          </ProviderCard>

          <ProviderCard
            title="VK"
            description="Вход через VK ID."
            enabled={settings.vkEnabled}
            onEnabledChange={(checked) => setSettings((current) => ({ ...current, vkEnabled: checked }))}
          >
            <div className="grid gap-4 md:grid-cols-2">
              <label className="text-sm text-gray-700">
                <span className="mb-1 block">Client ID</span>
                <input
                  type="text"
                  value={settings.vkClientId}
                  onChange={(event) => setSettings((current) => ({ ...current, vkClientId: event.target.value }))}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2"
                  placeholder="VK Client ID"
                />
              </label>
              <label className="text-sm text-gray-700">
                <span className="mb-1 block">Client Secret</span>
                <input
                  type="password"
                  value={settings.vkClientSecret}
                  onChange={(event) => setSettings((current) => ({ ...current, vkClientSecret: event.target.value }))}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2"
                  placeholder="VK Client Secret"
                />
              </label>
            </div>
          </ProviderCard>

          <ProviderCard
            title="Yandex"
            description="OAuth-вход через Яндекс."
            enabled={settings.yandexEnabled}
            onEnabledChange={(checked) => setSettings((current) => ({ ...current, yandexEnabled: checked }))}
          >
            <div className="grid gap-4 md:grid-cols-2">
              <label className="text-sm text-gray-700">
                <span className="mb-1 block">Client ID</span>
                <input
                  type="text"
                  value={settings.yandexClientId}
                  onChange={(event) => setSettings((current) => ({ ...current, yandexClientId: event.target.value }))}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2"
                  placeholder="Yandex Client ID"
                />
              </label>
              <label className="text-sm text-gray-700">
                <span className="mb-1 block">Client Secret</span>
                <input
                  type="password"
                  value={settings.yandexClientSecret}
                  onChange={(event) => setSettings((current) => ({ ...current, yandexClientSecret: event.target.value }))}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2"
                  placeholder="Yandex Client Secret"
                />
              </label>
            </div>
          </ProviderCard>

          <ProviderCard
            title="Telegram"
            description="Вход через Telegram-бот и Telegram Widget."
            enabled={settings.telegramEnabled}
            onEnabledChange={(checked) => setSettings((current) => ({ ...current, telegramEnabled: checked }))}
          >
            <div className="space-y-4">
              <label className="flex items-center gap-3 text-sm text-gray-700">
                <input
                  type="checkbox"
                  checked={settings.telegramWidgetEnabled}
                  onChange={(event) => setSettings((current) => ({ ...current, telegramWidgetEnabled: event.target.checked }))}
                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                />
                <span>Разрешить Telegram Widget</span>
              </label>

              <label className="text-sm text-gray-700">
                <span className="mb-1 block">Bot Token</span>
                <input
                  type="password"
                  value={settings.telegramBotToken}
                  onChange={(event) => setSettings((current) => ({ ...current, telegramBotToken: event.target.value }))}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2"
                  placeholder="Telegram Bot Token"
                />
              </label>

              <div className="grid gap-4 md:grid-cols-2">
                <div className="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-700">
                  <div className="font-medium text-gray-900">Bot Username</div>
                  <div className="mt-1">{settings.telegramBotUsername || '—'}</div>
                </div>
                <div className="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-700">
                  <div className="font-medium text-gray-900">Webhook</div>
                  <div className="mt-1 break-all">{settings.telegramWebhookUrl || '—'}</div>
                </div>
              </div>

              <div className="grid gap-4 md:grid-cols-3">
                <div className="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-700">
                  <div className="font-medium text-gray-900">Webhook secret</div>
                  <div className="mt-1 break-all">{settings.telegramWebhookSecret || '—'}</div>
                </div>
                <div className="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-700">
                  <div className="font-medium text-gray-900">Bot synced</div>
                  <div className="mt-1">{formatDateTime(settings.telegramBotInfoSyncedAt)}</div>
                </div>
                <div className="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-700">
                  <div className="font-medium text-gray-900">Webhook synced</div>
                  <div className="mt-1">{formatDateTime(settings.telegramWebhookSyncedAt)}</div>
                </div>
              </div>

              <div className="flex flex-wrap gap-3">
                <button
                  type="button"
                  onClick={syncTelegram}
                  disabled={syncingTelegram || saving}
                  className="px-4 py-2 rounded-lg border border-gray-300 text-sm font-medium hover:bg-gray-50 disabled:opacity-60"
                >
                  {syncingTelegram ? 'Синхронизируем…' : 'Синхронизировать Telegram'}
                </button>
                <div className="self-center text-sm text-gray-600">
                  {settings.telegramWebhookConfigured ? 'Webhook настроен' : 'Webhook еще не настроен'}
                </div>
              </div>
            </div>
          </ProviderCard>
        </div>

        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={saveSettings}
            disabled={saving || syncingTelegram}
            className="px-4 py-2 rounded-lg bg-slate-900 text-white text-sm font-medium hover:bg-slate-800 disabled:opacity-60"
          >
            {saving ? 'Сохраняем…' : 'Сохранить настройки'}
          </button>
          <button
            type="button"
            onClick={() => void loadSettings()}
            disabled={saving || syncingTelegram}
            className="px-4 py-2 rounded-lg border border-gray-300 text-sm font-medium hover:bg-gray-50 disabled:opacity-60"
          >
            Обновить
          </button>
        </div>

        {message && (
          <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">
            {message}
          </div>
        )}
      </div>
    </div>
  );
}
