import { SystemCheckbox } from '../ui/SystemCheckbox';
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Copy, ExternalLink, Loader2, RefreshCw, Save } from 'lucide-react';
import {
  authService,
  type AuthProviderPublicSettings,
  type AuthProviderSettings as AuthProviderSettingsDto,
} from '../../services/authService';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

type SectionId =
  | 'overview'
  | 'google'
  | 'vk'
  | 'yandex'
  | 'telegramBot'
  | 'telegramWidget';
type ActionKey = 'google' | 'vk' | 'yandex' | 'telegramBot' | 'telegramWidget';
type StatusTone = 'success' | 'warning' | 'muted' | 'info';

const SECTION_OPTIONS: Array<{ id: SectionId; label: string }> = [
  { id: 'overview', label: 'Обзор' },
  { id: 'google', label: 'Google' },
  { id: 'vk', label: 'VK ID' },
  { id: 'yandex', label: 'Яндекс ID' },
  { id: 'telegramWidget', label: 'Telegram Widget' },
];

const DOCS = {
  google: 'https://developers.google.com/identity/protocols/oauth2/web-server',
  vk: 'https://id.vk.com/about/business/go/docs/ru/vkid/latest/vk-id/connection/create-application',
  yandex: 'https://yandex.ru/dev/id/doc/en/register-client.html',
  telegramWidget: 'https://core.telegram.org/widgets/login',
  telegramWebhook: 'https://core.telegram.org/bots/api#setwebhook',
};

const EMPTY_SETTINGS: AuthProviderSettingsDto = {
  googleEnabled: false,
  googleClientId: '',
  googleClientSecret: '',
  googleReady: false,
  googleCallbackUrl: '',
  vkEnabled: false,
  vkClientId: '',
  vkClientSecret: '',
  vkReady: false,
  vkCallbackUrl: '',
  yandexEnabled: false,
  yandexClientId: '',
  yandexClientSecret: '',
  yandexReady: false,
  yandexCallbackUrl: '',
  telegramEnabled: false,
  telegramWidgetEnabled: false,
  telegramBotToken: '',
  telegramBotUsername: '',
  telegramWebhookSecret: '',
  telegramWebhookUrl: '',
  telegramWebhookConfigured: false,
  telegramBotReady: false,
  telegramWidgetReady: false,
  telegramBotInfoSyncedAt: null,
  telegramWebhookSyncedAt: null,
};

const EMPTY_PUBLIC: AuthProviderPublicSettings = {
  googleEnabled: false,
  vkEnabled: false,
  yandexEnabled: false,
  telegramEnabled: false,
  telegramWidgetEnabled: false,
  telegramBotUsername: null,
};

function formatDateTime(value?: string | null, timeZoneId?: string | null) {
  if (!value) {
    return 'Не синхронизировано';
  }

  try {
    return formatDateTimeInTimeZone(value, timeZoneId) || value;
  } catch {
    return value;
  }
}

function tryGetHost(value?: string | null) {
  if (!value) {
    return null;
  }

  try {
    return new URL(value).hostname;
  } catch {
    return null;
  }
}

function isIpHost(host?: string | null) {
  if (!host) {
    return false;
  }

  return /^\d{1,3}(\.\d{1,3}){3}$/.test(host);
}

function isLocalHost(host?: string | null) {
  if (!host) {
    return false;
  }

  return host === 'localhost' || host.endsWith('.local') || isIpHost(host);
}

function isHttps(value?: string | null) {
  if (!value) {
    return false;
  }

  try {
    return new URL(value).protocol === 'https:';
  } catch {
    return false;
  }
}

function getMessage(error: unknown, fallback: string) {
  return extractDisplayErrorMessage(error, fallback);
}

async function copyValue(value?: string | null) {
  if (!value) {
    return;
  }

  await navigator.clipboard.writeText(value);
}

function pillClass(tone: StatusTone) {
  if (tone === 'success') return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  if (tone === 'warning') return 'border-amber-200 bg-amber-50 text-amber-700';
  if (tone === 'info') return 'border-blue-200 bg-blue-50 text-blue-700';
  return 'border-slate-200 bg-slate-50 text-slate-600';
}

function StatusPill({ label, tone }: { label: string; tone: StatusTone }) {
  return (
    <span className={`inline-flex items-center rounded-full border px-3 py-1 text-xs font-semibold ${pillClass(tone)}`}>
      {label}
    </span>
  );
}

function SectionButton({
  active,
  label,
  onClick,
}: {
  active: boolean;
  label: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`w-full rounded-2xl border px-4 py-3 text-left text-sm font-semibold transition ${
        active
          ? 'border-blue-300 bg-blue-50 text-blue-700 shadow-sm'
          : 'border-transparent bg-white text-slate-700 hover:border-slate-200 hover:bg-slate-50'
      }`}
    >
      {label}
    </button>
  );
}

function Card({
  title,
  children,
  actions,
  headerClassName = '',
  titleClassName = '',
  bodyClassName = '',
}: {
  title: string;
  children: ReactNode;
  actions?: ReactNode;
  headerClassName?: string;
  titleClassName?: string;
  bodyClassName?: string;
}) {
  return (
    <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
      <div className={`flex flex-wrap items-start justify-between gap-3 ${headerClassName}`.trim()}>
        <h3 className={`text-lg font-semibold text-slate-900 ${titleClassName}`.trim()}>{title}</h3>
        {actions}
      </div>
      <div className={`mt-4 space-y-4 ${bodyClassName}`.trim()}>{children}</div>
    </div>
  );
}

function ReadonlyField({
  label,
  value,
  allowCopy = true,
}: {
  label: string;
  value?: string | null;
  allowCopy?: boolean;
}) {
  const displayValue = value?.trim() ? value : '—';

  return (
    <div className="space-y-2">
      <div className="text-sm font-medium text-slate-700">{label}</div>
      <div className="flex items-center gap-2 rounded-2xl border border-slate-200 bg-slate-50 px-3 py-3">
        <div className="min-w-0 flex-1 break-all text-sm text-slate-700">{displayValue}</div>
        {allowCopy && value ? (
          <button
            type="button"
            onClick={() => void copyValue(value)}
            className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-slate-200 bg-white text-slate-500 hover:text-slate-700"
            title="Скопировать"
          >
            <Copy size={16} />
          </button>
        ) : null}
      </div>
    </div>
  );
}

function AutofillGuard() {
  return (
    <div className="hidden" aria-hidden="true">
      <input type="text" name="pp_fake_username" autoComplete="username" tabIndex={-1} />
      <input type="password" name="pp_fake_password" autoComplete="current-password" tabIndex={-1} />
    </div>
  );
}

function StatusRow({
  label,
  value,
  tone,
}: {
  label: string;
  value: string;
  tone: StatusTone;
}) {
  return (
    <div className="flex items-start justify-between gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
      <div className="text-sm font-medium text-slate-700">{label}</div>
      <StatusPill label={value} tone={tone} />
    </div>
  );
}

function ActionButton({
  children,
  onClick,
  busy,
  icon,
  variant = 'primary',
}: {
  children: ReactNode;
  onClick: () => void;
  busy?: boolean;
  icon?: ReactNode;
  variant?: 'primary' | 'secondary';
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={busy}
      className={`inline-flex items-center gap-2 rounded-2xl px-4 py-2.5 text-sm font-semibold transition disabled:cursor-not-allowed disabled:opacity-60 ${
        variant === 'primary'
          ? 'bg-blue-600 text-white hover:bg-blue-700'
          : 'border border-slate-300 bg-white text-slate-700 hover:bg-slate-50'
      }`}
    >
      {busy ? <Loader2 size={16} className="animate-spin" /> : icon}
      {children}
    </button>
  );
}

export function AuthProviderSettings() {
  const toast = useToast();
  const timeZoneId = useUserTimeZone();
  const [activeSection, setActiveSection] = useState<SectionId>('overview');
  const [settings, setSettings] = useState<AuthProviderSettingsDto>(EMPTY_SETTINGS);
  const [publicSettings, setPublicSettings] = useState<AuthProviderPublicSettings>(EMPTY_PUBLIC);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [saving, setSaving] = useState<Record<ActionKey, boolean>>({
    google: false,
    vk: false,
    yandex: false,
    telegramBot: false,
    telegramWidget: false,
  });
  const [checking, setChecking] = useState<Record<ActionKey, boolean>>({
    google: false,
    vk: false,
    yandex: false,
    telegramBot: false,
    telegramWidget: false,
  });
  const [messages, setMessages] = useState<Record<ActionKey, string | null>>({
    google: null,
    vk: null,
    yandex: null,
    telegramBot: null,
    telegramWidget: null,
  });

  const currentHost = typeof window === 'undefined' ? '' : window.location.hostname;

  const setSectionMessage = (key: ActionKey, value: string | null) =>
    setMessages((current) => ({ ...current, [key]: value }));

  const refreshAll = useCallback(async (silent = false) => {
    if (silent) {
      setRefreshing(true);
    } else {
      setLoading(true);
    }

    try {
      const [adminSettings, providerSettings] = await Promise.all([
        authService.getAuthProviderSettings(),
        authService.getAuthProviders(),
      ]);
      setSettings(adminSettings);
      setPublicSettings(providerSettings);
    } catch (error) {
      const message = getMessage(error, 'Не удалось загрузить настройки внешней авторизации.');
      toast.error(message, 'Авторизация');
    } finally {
      if (silent) {
        setRefreshing(false);
      } else {
        setLoading(false);
      }
    }
  }, [toast]);

  useEffect(() => {
    void refreshAll();
  }, [refreshAll]);

  const applySaveResult = async (key: ActionKey, message: string, nextSettings: AuthProviderSettingsDto) => {
    setSettings(nextSettings);
    const nextPublicSettings = await authService.getAuthProviders();
    setPublicSettings(nextPublicSettings);
    setSectionMessage(key, message);
    toast.success(message, 'Авторизация');
  };

  const saveGoogle = async () => {
    setSaving((current) => ({ ...current, google: true }));
    try {
      const result = await authService.updateGoogleAuthProviderSettings({
        enabled: settings.googleEnabled,
        clientId: settings.googleClientId,
        clientSecret: settings.googleClientSecret,
      });
      await applySaveResult('google', result.message, result.settings);
    } catch (error) {
      const message = getMessage(error, 'Не удалось сохранить настройки Google.');
      setSectionMessage('google', message);
      toast.error(message, 'Google');
    } finally {
      setSaving((current) => ({ ...current, google: false }));
    }
  };

  const saveVk = async () => {
    setSaving((current) => ({ ...current, vk: true }));
    try {
      const result = await authService.updateVkAuthProviderSettings({
        enabled: settings.vkEnabled,
        clientId: settings.vkClientId,
        clientSecret: settings.vkClientSecret,
      });
      await applySaveResult('vk', result.message, result.settings);
    } catch (error) {
      const message = getMessage(error, 'Не удалось сохранить настройки VK ID.');
      setSectionMessage('vk', message);
      toast.error(message, 'VK ID');
    } finally {
      setSaving((current) => ({ ...current, vk: false }));
    }
  };

  const saveYandex = async () => {
    setSaving((current) => ({ ...current, yandex: true }));
    try {
      const result = await authService.updateYandexAuthProviderSettings({
        enabled: settings.yandexEnabled,
        clientId: settings.yandexClientId,
        clientSecret: settings.yandexClientSecret,
      });
      await applySaveResult('yandex', result.message, result.settings);
    } catch (error) {
      const message = getMessage(error, 'Не удалось сохранить настройки Яндекс ID.');
      setSectionMessage('yandex', message);
      toast.error(message, 'Яндекс ID');
    } finally {
      setSaving((current) => ({ ...current, yandex: false }));
    }
  };

  const saveTelegramBot = async () => {
    setSaving((current) => ({ ...current, telegramBot: true }));
    try {
      const result = await authService.updateTelegramLoginBotSettings({
        enabled: settings.telegramEnabled,
        botToken: settings.telegramBotToken,
      });
      await applySaveResult('telegramBot', result.message, result.settings);
    } catch (error) {
      const message = getMessage(error, 'Не удалось сохранить настройки Telegram Bot.');
      setSectionMessage('telegramBot', message);
      toast.error(message, 'Telegram Bot');
    } finally {
      setSaving((current) => ({ ...current, telegramBot: false }));
    }
  };

  const saveTelegramWidget = async () => {
    setSaving((current) => ({ ...current, telegramWidget: true }));
    try {
      const result = await authService.updateTelegramWidgetSettings({
        enabled: settings.telegramWidgetEnabled,
      });
      await applySaveResult('telegramWidget', result.message, result.settings);
    } catch (error) {
      const message = getMessage(error, 'Не удалось сохранить настройки Telegram Widget.');
      setSectionMessage('telegramWidget', message);
      toast.error(message, 'Telegram Widget');
    } finally {
      setSaving((current) => ({ ...current, telegramWidget: false }));
    }
  };

  const runOAuthCheck = async (provider: 'google' | 'vk' | 'yandex') => {
    setChecking((current) => ({ ...current, [provider]: true }));
    try {
      const result = await authService.startExternalAuth(provider, '/auth');
      const message = `Backend сформировал auth URL для ${provider}: ${result.authUrl}`;
      setSectionMessage(provider, message);
      toast.success(`Проверка ${provider} прошла: backend формирует redirect.`, 'Авторизация');
    } catch (error) {
      const message = getMessage(error, `Не удалось проверить запуск ${provider}.`);
      setSectionMessage(provider, message);
      toast.error(message, 'Авторизация');
    } finally {
      setChecking((current) => ({ ...current, [provider]: false }));
    }
  };

  const runTelegramBotCheck = async () => {
    setChecking((current) => ({ ...current, telegramBot: true }));
    try {
      const result = await authService.startTelegramBotAuth('/auth');
      const message = `Deep-link для входа сформирован: ${result.authUrl}`;
      setSectionMessage('telegramBot', message);
      toast.success('Telegram Bot отвечает и формирует deep-link для входа.', 'Telegram Bot');
    } catch (error) {
      const message = getMessage(error, 'Не удалось проверить Telegram Bot.');
      setSectionMessage('telegramBot', message);
      toast.error(message, 'Telegram Bot');
    } finally {
      setChecking((current) => ({ ...current, telegramBot: false }));
    }
  };

  const runTelegramWidgetCheck = async () => {
    setChecking((current) => ({ ...current, telegramWidget: true }));
    try {
      await refreshAll(true);
      const message = settings.telegramWidgetReady
        ? 'Widget считается готовым: бот известен, настройка включена и блок виден на входе.'
        : 'Widget еще не готов. Проверьте bot username, /setdomain и реальный домен без localhost.';
      setSectionMessage('telegramWidget', message);
    } finally {
      setChecking((current) => ({ ...current, telegramWidget: false }));
    }
  };

  const syncTelegramWebhook = async () => {
    setChecking((current) => ({ ...current, telegramBot: true }));
    try {
      const result = await authService.syncTelegramWebhook();
      await refreshAll(true);
      setSectionMessage('telegramBot', result.message);
      toast.success(result.message, 'Telegram Bot');
    } catch (error) {
      const message = getMessage(error, 'Не удалось синхронизировать Telegram webhook.');
      setSectionMessage('telegramBot', message);
      toast.error(message, 'Telegram Bot');
    } finally {
      setChecking((current) => ({ ...current, telegramBot: false }));
    }
  };

  const summaryCards = useMemo(
    () => [
      { label: 'Google', ready: settings.googleReady, visible: publicSettings.googleEnabled },
      { label: 'VK ID', ready: settings.vkReady, visible: publicSettings.vkEnabled },
      { label: 'Яндекс ID', ready: settings.yandexReady, visible: publicSettings.yandexEnabled },
      { label: 'Telegram Bot', ready: settings.telegramBotReady, visible: publicSettings.telegramEnabled },
      { label: 'Telegram Widget', ready: settings.telegramWidgetReady, visible: publicSettings.telegramWidgetEnabled },
    ],
    [publicSettings, settings],
  );

  const googleCallbackHost = tryGetHost(settings.googleCallbackUrl);
  const vkCallbackHost = tryGetHost(settings.vkCallbackUrl);
  const yandexCallbackHost = tryGetHost(settings.yandexCallbackUrl);
  const widgetHostSupported = Boolean(currentHost) && !isLocalHost(currentHost);
  const telegramBotUsername = settings.telegramBotUsername || publicSettings.telegramBotUsername || '';

  if (loading) {
    return (
      <div className="rounded-3xl border border-slate-200 bg-white p-6 text-sm text-slate-600 shadow-sm">
        Загружаем настройки внешней авторизации...
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="rounded-3xl border border-blue-200 bg-blue-50 px-5 py-4 text-sm text-blue-900">
        Новый пользователь, зарегистрированный через внешний провайдер, все равно попадает в ожидание подтверждения
        администратора. Внешний вход не обходит процесс ручного одобрения.
      </div>

      <div className="grid gap-6 xl:grid-cols-[280px,minmax(0,1fr)]">
        <aside className="space-y-4">
          <div className="rounded-3xl border border-slate-200 bg-white p-5 text-center shadow-sm">
            <div className="text-xs font-semibold uppercase tracking-[0.28em] text-slate-400">Секция доступа</div>
            <div className="mt-3 text-2xl font-semibold text-slate-900">Внешняя авторизация</div>
            <p className="mt-3 text-sm leading-6 text-slate-600">
              Каждый блок сохраняется отдельно. Проверки показывают только готовность backend и публичной части. Совпадение
              callback URL с кабинетом провайдера нужно сверять вручную.
            </p>
            <div className="mt-4 flex justify-center">
              <ActionButton busy={refreshing} onClick={() => void refreshAll(true)} icon={<RefreshCw size={16} />} variant="secondary">
                Обновить статусы
              </ActionButton>
            </div>
          </div>

          <div className="rounded-3xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="space-y-2">
              {SECTION_OPTIONS.map((section) => (
                <SectionButton
                  key={section.id}
                  active={activeSection === section.id}
                  label={section.label}
                  onClick={() => setActiveSection(section.id)}
                />
              ))}
            </div>
          </div>
        </aside>

        <div className="space-y-6">
          {activeSection === 'overview' ? (
            <>
              <Card
                title="Текущая сводка"
                headerClassName="justify-center"
                titleClassName="w-full text-center"
              >
                <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5">
                  {summaryCards.map((item) => (
                    <div
                      key={item.label}
                      className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-4 text-center"
                    >
                      <div className="text-sm font-semibold text-slate-900">{item.label}</div>
                      <div className="mt-4 space-y-3">
                        <div className="space-y-1.5">
                          <div className="text-[11px] font-medium uppercase tracking-[0.14em] text-slate-400">
                            Backend
                          </div>
                          <div className="flex justify-center">
                            <StatusPill
                              label={item.ready ? 'Готов' : 'Не готов'}
                              tone={item.ready ? 'success' : 'warning'}
                            />
                          </div>
                        </div>
                        <div className="space-y-1.5">
                          <div className="text-[11px] font-medium uppercase tracking-[0.14em] text-slate-400">
                            Вход
                          </div>
                          <div className="flex justify-center">
                            <StatusPill
                              label={item.visible ? 'Виден' : 'Скрыт'}
                              tone={item.visible ? 'success' : 'muted'}
                            />
                          </div>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              </Card>

              <div className="grid gap-6 lg:grid-cols-2">
                <Card title="Как работает общий сценарий">
                  <ol className="space-y-3 text-sm leading-6 text-slate-700">
                    <li>1. Администратор настраивает и сохраняет только нужный блок.</li>
                    <li>2. Backend публикует провайдера на странице входа только если конфигурация рабочая.</li>
                    <li>3. Backend формирует auth URL, deep-link или widget-сценарий.</li>
                    <li>4. Провайдер возвращает пользователя в нашу систему на callback или в webhook-поток.</li>
                    <li>5. Новый аккаунт остается в ожидании подтверждения администратора.</li>
                  </ol>
                </Card>

                <Card title="Что здесь считается проверкой">
                  <ol className="space-y-3 text-sm leading-6 text-slate-700">
                    <li>1. Сохранение подтверждает, что настройки блока записались отдельно от остальных.</li>
                    <li>2. Проверка запуска показывает, что backend реально умеет сформировать auth URL или deep-link.</li>
                    <li>3. Статус «виден на входе» означает, что публичный API уже отдает провайдера фронтенду логина.</li>
                    <li>4. Callback URL и домены видны на странице, но их совпадение с кабинетом провайдера проверяется вручную.</li>
                  </ol>
                </Card>
              </div>

              <div className="grid gap-6 lg:grid-cols-2">
                <Card title="Официальные требования">
                  <ul className="space-y-3 text-sm leading-6 text-slate-700">
                    <li>Google: `redirect_uri` должен точно совпадать с Authorized redirect URI.</li>
                    <li>VK ID: redirect URL должен быть добавлен в приложение VK ID.</li>
                    <li>Яндекс ID: локальные адреса для Redirect URI использовать нельзя.</li>
                    <li>Telegram Widget: домен нужно привязать к боту через `/setdomain`.</li>
                    <li>Telegram webhook: webhook должен быть HTTPS, secret приходит заголовком.</li>
                  </ul>
                </Card>

                <Card title="Официальная документация">
                  <div className="space-y-3 text-sm">
                    {[
                      { label: 'Google OAuth', href: DOCS.google },
                      { label: 'VK ID', href: DOCS.vk },
                      { label: 'Яндекс ID', href: DOCS.yandex },
                      { label: 'Telegram Login Widget', href: DOCS.telegramWidget },
                      { label: 'Telegram Bot API / setWebhook', href: DOCS.telegramWebhook },
                    ].map((item) => (
                      <a
                        key={item.href}
                        href={item.href}
                        target="_blank"
                        rel="noreferrer"
                        className="flex items-center gap-2 text-blue-700 hover:text-blue-800"
                      >
                        <ExternalLink size={15} />
                        <span>{item.label}</span>
                      </a>
                    ))}
                  </div>
                </Card>
              </div>
            </>
          ) : null}

          {activeSection === 'google' ? (
            <>
              <Card
                title="Google OAuth"
                actions={
                  <div className="flex flex-wrap gap-2">
                    <ActionButton busy={checking.google} onClick={() => void runOAuthCheck('google')} icon={<RefreshCw size={16} />} variant="secondary">
                      Проверить запуск
                    </ActionButton>
                    <ActionButton busy={saving.google} onClick={() => void saveGoogle()} icon={<Save size={16} />}>
                      Сохранить
                    </ActionButton>
                  </div>
                }
              >
                <AutofillGuard />
                <div className="grid gap-4 lg:grid-cols-2">
                  <label className="space-y-2 text-sm">
                    <span className="font-medium text-slate-700">Client ID</span>
                    <input
                      type="text"
                      value={settings.googleClientId}
                      onChange={(event) => setSettings((current) => ({ ...current, googleClientId: event.target.value }))}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                      name="google-oauth-client-id"
                      autoComplete="new-password"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      data-lpignore="true"
                      data-1p-ignore="true"
                      placeholder="Google OAuth Client ID"
                    />
                  </label>
                  <label className="space-y-2 text-sm">
                    <span className="font-medium text-slate-700">Client Secret</span>
                    <input
                      type="password"
                      value={settings.googleClientSecret}
                      onChange={(event) => setSettings((current) => ({ ...current, googleClientSecret: event.target.value }))}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                      name="google-oauth-client-secret"
                      autoComplete="new-password"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      data-lpignore="true"
                      data-1p-ignore="true"
                      placeholder="Google OAuth Client Secret"
                    />
                  </label>
                </div>
                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                  <SystemCheckbox
                    type="checkbox"
                    checked={settings.googleEnabled}
                    onChange={(event) => setSettings((current) => ({ ...current, googleEnabled: event.target.checked }))}
                    className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                  />
                  Включить Google на странице входа
                </label>
                <ReadonlyField label="Callback URL" value={settings.googleCallbackUrl} />
              </Card>

              <div className="grid gap-6 lg:grid-cols-2">
                <Card title="Статус готовности">
                  <div className="space-y-3">
                    <StatusRow label="Backend конфигурация" value={settings.googleReady ? 'Готов' : 'Не готов'} tone={settings.googleReady ? 'success' : 'warning'} />
                    <StatusRow label="Виден на входе" value={publicSettings.googleEnabled ? 'Да' : 'Нет'} tone={publicSettings.googleEnabled ? 'success' : 'muted'} />
                    <StatusRow label="Callback URL сформирован" value={settings.googleCallbackUrl ? 'Да' : 'Нет'} tone={settings.googleCallbackUrl ? 'success' : 'warning'} />
                    <StatusRow label="Текущий callback host" value={googleCallbackHost || 'Не определен'} tone={googleCallbackHost ? 'info' : 'warning'} />
                  </div>
                  {messages.google ? (
                    <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">{messages.google}</div>
                  ) : null}
                </Card>

                <Card title="Как настраивать и проверять">
                  <div className="space-y-4 text-sm leading-6 text-slate-700">
                    <div>
                      <div className="font-semibold text-slate-900">Как работает</div>
                      <p>Backend формирует Google auth URL, Google возвращает пользователя строго на этот callback URL.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как настроить</div>
                      <p>Создайте Web application в Google Cloud и внесите этот callback в Authorized redirect URIs без расхождений.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как проверить</div>
                      <p>Сохраните блок, нажмите «Проверить запуск» и вручную сравните callback URL в кабинете Google один в один.</p>
                    </div>
                    <a href={DOCS.google} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 text-blue-700 hover:text-blue-800">
                      <ExternalLink size={15} />
                      Официальная документация Google OAuth
                    </a>
                  </div>
                </Card>
              </div>
            </>
          ) : null}

          {activeSection === 'vk' ? (
            <>
              <Card
                title="VK ID"
                actions={
                  <div className="flex flex-wrap gap-2">
                    <ActionButton busy={checking.vk} onClick={() => void runOAuthCheck('vk')} icon={<RefreshCw size={16} />} variant="secondary">
                      Проверить запуск
                    </ActionButton>
                    <ActionButton busy={saving.vk} onClick={() => void saveVk()} icon={<Save size={16} />}>
                      Сохранить
                    </ActionButton>
                  </div>
                }
              >
                <AutofillGuard />
                <div className="grid gap-4 lg:grid-cols-2">
                  <label className="space-y-2 text-sm">
                    <span className="font-medium text-slate-700">App ID / Client ID</span>
                    <input
                      type="text"
                      value={settings.vkClientId}
                      onChange={(event) => setSettings((current) => ({ ...current, vkClientId: event.target.value }))}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                      name="vk-oauth-client-id"
                      autoComplete="new-password"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      data-lpignore="true"
                      data-1p-ignore="true"
                      placeholder="VK Client ID"
                    />
                  </label>
                  <label className="space-y-2 text-sm">
                    <span className="font-medium text-slate-700">Client Secret</span>
                    <input
                      type="password"
                      value={settings.vkClientSecret}
                      onChange={(event) => setSettings((current) => ({ ...current, vkClientSecret: event.target.value }))}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                      name="vk-oauth-client-secret"
                      autoComplete="new-password"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      data-lpignore="true"
                      data-1p-ignore="true"
                      placeholder="VK Client Secret"
                    />
                  </label>
                </div>
                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                  <SystemCheckbox
                    type="checkbox"
                    checked={settings.vkEnabled}
                    onChange={(event) => setSettings((current) => ({ ...current, vkEnabled: event.target.checked }))}
                    className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                  />
                  Включить VK ID на странице входа
                </label>
                <ReadonlyField label="Redirect URL" value={settings.vkCallbackUrl} />
              </Card>

              <div className="grid gap-6 lg:grid-cols-2">
                <Card title="Статус готовности">
                  <div className="space-y-3">
                    <StatusRow label="Backend конфигурация" value={settings.vkReady ? 'Готов' : 'Не готов'} tone={settings.vkReady ? 'success' : 'warning'} />
                    <StatusRow label="Виден на входе" value={publicSettings.vkEnabled ? 'Да' : 'Нет'} tone={publicSettings.vkEnabled ? 'success' : 'muted'} />
                    <StatusRow label="Redirect URL сформирован" value={settings.vkCallbackUrl ? 'Да' : 'Нет'} tone={settings.vkCallbackUrl ? 'success' : 'warning'} />
                    <StatusRow label="Текущий callback host" value={vkCallbackHost || 'Не определен'} tone={vkCallbackHost ? 'info' : 'warning'} />
                  </div>
                  {messages.vk ? (
                    <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">{messages.vk}</div>
                  ) : null}
                </Card>

                <Card title="Как настраивать и проверять">
                  <div className="space-y-4 text-sm leading-6 text-slate-700">
                    <div>
                      <div className="font-semibold text-slate-900">Как работает</div>
                      <p>Backend запускает поток VK ID и принимает возврат пользователя на redirect URL, заданный для приложения.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как настроить</div>
                      <p>Создайте приложение VK ID, заполните ключи и внесите redirect URL из этого блока в настройки приложения.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как проверить</div>
                      <p>Сохраните блок, запустите проверку и отдельно убедитесь, что redirect URL в кабинете VK ID совпадает без расхождений.</p>
                    </div>
                    <a href={DOCS.vk} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 text-blue-700 hover:text-blue-800">
                      <ExternalLink size={15} />
                      Официальная документация VK ID
                    </a>
                  </div>
                </Card>
              </div>
            </>
          ) : null}

          {activeSection === 'yandex' ? (
            <>
              <Card
                title="Яндекс ID"
                actions={
                  <div className="flex flex-wrap gap-2">
                    <ActionButton busy={checking.yandex} onClick={() => void runOAuthCheck('yandex')} icon={<RefreshCw size={16} />} variant="secondary">
                      Проверить запуск
                    </ActionButton>
                    <ActionButton busy={saving.yandex} onClick={() => void saveYandex()} icon={<Save size={16} />}>
                      Сохранить
                    </ActionButton>
                  </div>
                }
              >
                <AutofillGuard />
                <div className="grid gap-4 lg:grid-cols-2">
                  <label className="space-y-2 text-sm">
                    <span className="font-medium text-slate-700">Client ID</span>
                    <input
                      type="text"
                      value={settings.yandexClientId}
                      onChange={(event) => setSettings((current) => ({ ...current, yandexClientId: event.target.value }))}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                      name="yandex-oauth-client-id"
                      autoComplete="new-password"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      data-lpignore="true"
                      data-1p-ignore="true"
                      placeholder="Yandex Client ID"
                    />
                  </label>
                  <label className="space-y-2 text-sm">
                    <span className="font-medium text-slate-700">Client Secret</span>
                    <input
                      type="password"
                      value={settings.yandexClientSecret}
                      onChange={(event) => setSettings((current) => ({ ...current, yandexClientSecret: event.target.value }))}
                      className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                      name="yandex-oauth-client-secret"
                      autoComplete="new-password"
                      autoCorrect="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      data-lpignore="true"
                      data-1p-ignore="true"
                      placeholder="Yandex Client Secret"
                    />
                  </label>
                </div>
                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                  <SystemCheckbox
                    type="checkbox"
                    checked={settings.yandexEnabled}
                    onChange={(event) => setSettings((current) => ({ ...current, yandexEnabled: event.target.checked }))}
                    className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                  />
                  Включить Яндекс ID на странице входа
                </label>
                <ReadonlyField label="Redirect URI" value={settings.yandexCallbackUrl} />
              </Card>

              <div className="grid gap-6 lg:grid-cols-2">
                <Card title="Статус готовности">
                  <div className="space-y-3">
                    <StatusRow label="Backend конфигурация" value={settings.yandexReady ? 'Готов' : 'Не готов'} tone={settings.yandexReady ? 'success' : 'warning'} />
                    <StatusRow label="Виден на входе" value={publicSettings.yandexEnabled ? 'Да' : 'Нет'} tone={publicSettings.yandexEnabled ? 'success' : 'muted'} />
                    <StatusRow label="Redirect URI сформирован" value={settings.yandexCallbackUrl ? 'Да' : 'Нет'} tone={settings.yandexCallbackUrl ? 'success' : 'warning'} />
                    <StatusRow
                      label="Host для Redirect URI"
                      value={yandexCallbackHost || 'Не определен'}
                      tone={!yandexCallbackHost ? 'warning' : isLocalHost(yandexCallbackHost) ? 'warning' : 'success'}
                    />
                  </div>
                  {messages.yandex ? (
                    <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">{messages.yandex}</div>
                  ) : null}
                </Card>

                <Card title="Как настраивать и проверять">
                  <div className="space-y-4 text-sm leading-6 text-slate-700">
                    <div>
                      <div className="font-semibold text-slate-900">Как работает</div>
                      <p>Backend запускает Яндекс OAuth и принимает возврат пользователя только на зарегистрированный Redirect URI.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как настроить</div>
                      <p>Создайте приложение Яндекс ID, заполните Client ID/Secret и внесите Redirect URI из этого блока в настройки клиента.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как проверить</div>
                      <p>Сохраните блок, запустите проверку и отдельно проверьте, что Redirect URI не указывает на localhost или IP.</p>
                    </div>
                    <a href={DOCS.yandex} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 text-blue-700 hover:text-blue-800">
                      <ExternalLink size={15} />
                      Официальная документация Яндекс ID
                    </a>
                  </div>
                </Card>
              </div>
            </>
          ) : null}

          {activeSection === 'telegramBot' ? (
            <>
              <Card
                title="Telegram Login Bot"
                actions={
                  <div className="flex flex-wrap gap-2">
                    <ActionButton busy={checking.telegramBot} onClick={() => void runTelegramBotCheck()} icon={<RefreshCw size={16} />} variant="secondary">
                      Проверить deep-link
                    </ActionButton>
                    <ActionButton busy={checking.telegramBot} onClick={() => void syncTelegramWebhook()} icon={<RefreshCw size={16} />} variant="secondary">
                      Синхронизировать webhook
                    </ActionButton>
                    <ActionButton busy={saving.telegramBot} onClick={() => void saveTelegramBot()} icon={<Save size={16} />}>
                      Сохранить
                    </ActionButton>
                  </div>
                }
              >
                <label className="space-y-2 text-sm">
                  <span className="font-medium text-slate-700">Bot Token</span>
                  <input
                    type="password"
                    value={settings.telegramBotToken}
                    onChange={(event) => setSettings((current) => ({ ...current, telegramBotToken: event.target.value }))}
                    className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                    placeholder="Telegram Bot Token"
                  />
                </label>
                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                  <SystemCheckbox
                    type="checkbox"
                    checked={settings.telegramEnabled}
                    onChange={(event) => setSettings((current) => ({ ...current, telegramEnabled: event.target.checked }))}
                    className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                  />
                  Включить Telegram Bot для входа
                </label>
                <div className="grid gap-4 lg:grid-cols-2">
                  <ReadonlyField label="Bot Username" value={telegramBotUsername} />
                  <ReadonlyField label="Webhook URL" value={settings.telegramWebhookUrl} />
                </div>
                <div className="grid gap-4 lg:grid-cols-2">
                  <ReadonlyField label="Webhook Secret" value={settings.telegramWebhookSecret} />
                  <ReadonlyField label="Синхронизация бота" value={formatDateTime(settings.telegramBotInfoSyncedAt, timeZoneId)} allowCopy={false} />
                </div>
                <ReadonlyField label="Синхронизация webhook" value={formatDateTime(settings.telegramWebhookSyncedAt, timeZoneId)} allowCopy={false} />
              </Card>

              <div className="grid gap-6 lg:grid-cols-2">
                <Card title="Статус готовности">
                  <div className="space-y-3">
                    <StatusRow label="Backend конфигурация" value={settings.telegramBotReady ? 'Готов' : 'Не готов'} tone={settings.telegramBotReady ? 'success' : 'warning'} />
                    <StatusRow label="Виден на входе" value={publicSettings.telegramEnabled ? 'Да' : 'Нет'} tone={publicSettings.telegramEnabled ? 'success' : 'muted'} />
                    <StatusRow label="Username синхронизирован" value={telegramBotUsername ? 'Да' : 'Нет'} tone={telegramBotUsername ? 'success' : 'warning'} />
                    <StatusRow label="Webhook настроен" value={settings.telegramWebhookConfigured ? 'Да' : 'Нет'} tone={settings.telegramWebhookConfigured ? 'success' : 'warning'} />
                    <StatusRow label="Webhook использует HTTPS" value={isHttps(settings.telegramWebhookUrl) ? 'Да' : 'Нет'} tone={isHttps(settings.telegramWebhookUrl) ? 'success' : 'warning'} />
                  </div>
                  {messages.telegramBot ? (
                    <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">{messages.telegramBot}</div>
                  ) : null}
                </Card>

                <Card title="Как настраивать и проверять">
                  <div className="space-y-4 text-sm leading-6 text-slate-700">
                    <div>
                      <div className="font-semibold text-slate-900">Как работает</div>
                      <p>Этот блок настраивает только login-бота: backend формирует deep-link, Telegram присылает `/start`, а webhook завершает вход.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как настроить</div>
                      <p>Сохраните bot token, включите блок, затем синхронизируйте webhook. Система сама подставляет webhook URL и secret.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как проверить</div>
                      <p>Нажмите «Проверить deep-link», затем «Синхронизировать webhook». После этого проверьте, что бот отвечает на start-ссылку входа.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Граница текущей реализации</div>
                      <p>Несколько ботов, автоответы, команды и шаблоны из `clothing_store` нужно переносить отдельным модулем, а не внутрь login-настроек.</p>
                    </div>
                    <a href={DOCS.telegramWebhook} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 text-blue-700 hover:text-blue-800">
                      <ExternalLink size={15} />
                      Официальная документация Telegram Bot API / setWebhook
                    </a>
                  </div>
                </Card>
              </div>
            </>
          ) : null}

          {activeSection === 'telegramWidget' ? (
            <>
              <Card
                title="Telegram Login Widget"
                actions={
                  <div className="flex flex-wrap gap-2">
                    <ActionButton busy={checking.telegramWidget} onClick={() => void runTelegramWidgetCheck()} icon={<RefreshCw size={16} />} variant="secondary">
                      Проверить статус
                    </ActionButton>
                    <ActionButton busy={saving.telegramWidget} onClick={() => void saveTelegramWidget()} icon={<Save size={16} />}>
                      Сохранить
                    </ActionButton>
                  </div>
                }
              >
                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700">
                  <SystemCheckbox
                    type="checkbox"
                    checked={settings.telegramWidgetEnabled}
                    onChange={(event) => setSettings((current) => ({ ...current, telegramWidgetEnabled: event.target.checked }))}
                    className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                  />
                  Включить Telegram Widget на странице входа
                </label>
                <div className="grid gap-4 lg:grid-cols-2">
                  <ReadonlyField label="Bot Username" value={telegramBotUsername} />
                  <ReadonlyField label="Текущий домен" value={currentHost || 'Не определен'} allowCopy={false} />
                </div>
              </Card>

              <div className="grid gap-6 lg:grid-cols-2">
                <Card title="Статус готовности">
                  <div className="space-y-3">
                    <StatusRow label="Login-бот готов" value={settings.telegramBotReady ? 'Да' : 'Нет'} tone={settings.telegramBotReady ? 'success' : 'warning'} />
                    <StatusRow label="Widget backend готов" value={settings.telegramWidgetReady ? 'Да' : 'Нет'} tone={settings.telegramWidgetReady ? 'success' : 'warning'} />
                    <StatusRow label="Виден на входе" value={publicSettings.telegramWidgetEnabled ? 'Да' : 'Нет'} tone={publicSettings.telegramWidgetEnabled ? 'success' : 'muted'} />
                    <StatusRow label="Домен подходит для widget" value={widgetHostSupported ? 'Да' : 'Нет'} tone={widgetHostSupported ? 'success' : 'warning'} />
                  </div>
                  {messages.telegramWidget ? (
                    <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">{messages.telegramWidget}</div>
                  ) : null}
                </Card>

                <Card title="Как настраивать и проверять">
                  <div className="space-y-4 text-sm leading-6 text-slate-700">
                    <div>
                      <div className="font-semibold text-slate-900">Как работает</div>
                      <p>Widget использует username login-бота и передает подписанный payload напрямую в backend.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как настроить</div>
                      <p>Сначала настройте login-бота, затем включите widget и задайте домен через BotFather командой `/setdomain`.</p>
                    </div>
                    <div>
                      <div className="font-semibold text-slate-900">Как проверить</div>
                      <p>Сохраните блок, проверьте статус и убедитесь, что вход открывается на реальном домене, а не на localhost или IP.</p>
                    </div>
                    <a href={DOCS.telegramWidget} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 text-blue-700 hover:text-blue-800">
                      <ExternalLink size={15} />
                      Официальная документация Telegram Login Widget
                    </a>
                  </div>
                </Card>
              </div>
            </>
          ) : null}

        </div>
      </div>
    </div>
  );
}
