import { type ReactNode, useEffect, useMemo, useRef, useState } from 'react';
import { Loader2 } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import {
  authService,
  type AuthProviderPublicSettings,
  type ExternalAuthStartResponse,
  type ExternalAuthStatusResponse,
} from '../../services/authService';

type ProviderName = 'google' | 'vk' | 'yandex' | 'telegram';

interface ExternalAuthPanelProps {
  disabled?: boolean;
  onPendingApproval?: () => void;
  onInactive?: (message: string) => void;
  onError?: (message: string) => void;
}

type AuthSession = Pick<ExternalAuthStartResponse, 'provider' | 'state' | 'expiresAt' | 'pollIntervalMs'>;

const TELEGRAM_WIDGET_CALLBACK = '__payPlannerTelegramWidgetAuth';

const TELEGRAM_WIDGET_ERRORS = [
  'bot domain invalid',
  'bot username invalid',
  'domain invalid',
  'username invalid',
];

function getProviderLabel(provider: string) {
  switch (provider) {
    case 'google':
      return 'Google';
    case 'vk':
      return 'VK';
    case 'yandex':
      return 'Yandex';
    default:
      return 'Telegram';
  }
}

function getErrorMessage(error: unknown, fallback: string) {
  if (error instanceof Error && error.message.trim()) {
    return error.message.trim();
  }

  return fallback;
}

function buildPendingMessage(provider: string) {
  return provider === 'telegram'
    ? 'Ожидаем подтверждение входа в Telegram. Ссылка действует 10 минут.'
    : `Ожидаем завершение входа через ${getProviderLabel(provider)}.`;
}

function isProviderEnabled(settings: AuthProviderPublicSettings | null, provider: ProviderName) {
  if (!settings) {
    return false;
  }

  switch (provider) {
    case 'google':
      return settings.googleEnabled;
    case 'vk':
      return settings.vkEnabled;
    case 'yandex':
      return settings.yandexEnabled;
    default:
      return settings.telegramEnabled;
  }
}

function normalizeTelegramBotUsername(value?: string | null) {
  return String(value ?? '').trim().replace(/^@+/, '');
}

function QuickAuthTile({
  label,
  title,
  active = false,
  disabled = false,
  onClick,
  children,
}: {
  label: string;
  title?: string;
  active?: boolean;
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      title={title || label}
      aria-label={label}
      onClick={onClick}
      disabled={disabled}
      className={[
        'flex h-14 w-20 items-center justify-center rounded-2xl border bg-white px-0 shadow-sm transition-all',
        active
          ? 'border-slate-900/60 shadow-md'
          : 'border-slate-200 hover:-translate-y-0.5 hover:border-slate-300 hover:shadow-md',
        disabled ? 'cursor-not-allowed opacity-60' : '',
      ].join(' ')}
    >
      <span className="sr-only">{label}</span>
      {children}
    </button>
  );
}

function GoogleBrandIcon() {
  return (
    <span
      aria-hidden="true"
      className="text-[28px] font-semibold leading-none"
      style={{
        backgroundImage:
          'linear-gradient(135deg, #4285F4 0%, #4285F4 34%, #EA4335 34%, #EA4335 57%, #FBBC05 57%, #FBBC05 74%, #34A853 74%, #34A853 100%)',
        WebkitBackgroundClip: 'text',
        color: 'transparent',
      }}
    >
      G
    </span>
  );
}

function VkBrandIcon() {
  return (
    <span aria-hidden="true" className="text-lg font-black uppercase tracking-tight text-[#0077FF]">
      VK
    </span>
  );
}

function YandexBrandIcon() {
  return (
    <span aria-hidden="true" className="text-[28px] font-black leading-none text-[#FC3F1D]">
      Я
    </span>
  );
}

function TelegramBrandIcon() {
  return (
    <svg aria-hidden="true" width="28" height="28" viewBox="0 0 24 24" fill="none">
      <path
        d="M20.67 4.34L3.95 10.79C2.81 11.24 2.82 11.87 3.74 12.15L8.03 13.49L9.68 18.67C9.89 19.31 10.06 19.55 10.46 19.55C10.77 19.55 10.9 19.41 11.08 19.1L13.16 15.72L17.49 18.91C18.29 19.35 18.86 19.13 19.06 18.17L21.91 4.73C22.2 3.55 21.47 3.01 20.67 4.34Z"
        fill="#229ED9"
      />
    </svg>
  );
}

export function ExternalAuthPanel({
  disabled,
  onPendingApproval,
  onInactive,
  onError,
}: ExternalAuthPanelProps) {
  const { signInWithToken } = useAuth();
  const [settings, setSettings] = useState<AuthProviderPublicSettings | null>(null);
  const [loadingSettings, setLoadingSettings] = useState(true);
  const [busyProvider, setBusyProvider] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [hint, setHint] = useState<string | null>(null);
  const [externalSession, setExternalSession] = useState<AuthSession | null>(null);
  const [telegramSession, setTelegramSession] = useState<AuthSession | null>(null);
  const [widgetStatus, setWidgetStatus] = useState<'idle' | 'loading' | 'ready' | 'unavailable'>('idle');
  const [widgetError, setWidgetError] = useState<string | null>(null);
  const widgetContainerRef = useRef<HTMLDivElement | null>(null);
  const popupRef = useRef<Window | null>(null);

  const telegramBotUsername = useMemo(
    () => normalizeTelegramBotUsername(settings?.telegramBotUsername),
    [settings?.telegramBotUsername],
  );

  const providerButtons = useMemo(
    () =>
      [
        {
          id: 'google',
          enabled: settings?.googleEnabled,
          label: 'Google',
          title: 'Войти через Google',
          icon: <GoogleBrandIcon />,
        },
        {
          id: 'vk',
          enabled: settings?.vkEnabled,
          label: 'VK',
          title: 'Войти через VK',
          icon: <VkBrandIcon />,
        },
        {
          id: 'yandex',
          enabled: settings?.yandexEnabled,
          label: 'Яндекс',
          title: 'Войти через Яндекс',
          icon: <YandexBrandIcon />,
        },
        {
          id: 'telegram',
          enabled: settings?.telegramEnabled,
          label: 'Telegram',
          title: 'Войти через Telegram',
          icon: <TelegramBrandIcon />,
        },
      ].filter((item) => item.enabled),
    [settings],
  );

  const closePopup = () => {
    if (popupRef.current && !popupRef.current.closed) {
      popupRef.current.close();
    }

    popupRef.current = null;
  };

  const applyAuthStatus = async (status: ExternalAuthStatusResponse, fallbackProvider: string) => {
    if (!status.completed) {
      return false;
    }

    const provider = status.provider || fallbackProvider;
    if (status.requiresApproval) {
      onPendingApproval?.();
      return true;
    }

    if (status.isInactive) {
      const message = status.detail || 'Ваш аккаунт отключен. Обратитесь к администратору.';
      setError(message);
      onInactive?.(message);
      onError?.(message);
      return true;
    }

    if (status.token) {
      await signInWithToken(status.token, status.user ?? undefined, provider);
      return true;
    }

    return false;
  };

  useEffect(() => {
    let cancelled = false;

    const loadSettings = async () => {
      setLoadingSettings(true);
      try {
        const nextSettings = await authService.getAuthProviders();
        if (!cancelled) {
          setSettings(nextSettings);
        }
      } catch (nextError) {
        if (!cancelled) {
          const message = getErrorMessage(nextError, 'Не удалось загрузить доступные способы входа.');
          setError(message);
          onError?.(message);
        }
      } finally {
        if (!cancelled) {
          setLoadingSettings(false);
        }
      }
    };

    void loadSettings();
    return () => {
      cancelled = true;
    };
  }, [onError]);

  useEffect(() => {
    const url = new URL(window.location.href);
    const state = url.searchParams.get('externalAuthState');
    const provider = url.searchParams.get('externalAuthProvider');
    if (!state || !provider) {
      return;
    }

    setExternalSession((current) => current ?? {
      provider,
      state,
      expiresAt: '',
      pollIntervalMs: 2000,
    });
    setHint(buildPendingMessage(provider));

    url.searchParams.delete('externalAuthState');
    url.searchParams.delete('externalAuthProvider');
    window.history.replaceState({}, '', `${url.pathname}${url.search}${url.hash}`);
  }, []);

  useEffect(() => () => closePopup(), []);

  const startExternalAuth = async (provider: 'google' | 'vk' | 'yandex') => {
    setBusyProvider(provider);
    setError(null);

    try {
      const started = await authService.startExternalAuth(provider, '/');
      setExternalSession({
        provider,
        state: started.state,
        expiresAt: started.expiresAt,
        pollIntervalMs: started.pollIntervalMs,
      });
      setHint(buildPendingMessage(provider));

      popupRef.current = window.open(
        started.authUrl,
        'pp-external-auth',
        'popup=yes,width=540,height=720',
      );

      if (!popupRef.current) {
        window.location.assign(started.authUrl);
      }
    } catch (nextError) {
      const message = getErrorMessage(nextError, `Не удалось начать вход через ${getProviderLabel(provider)}.`);
      setError(message);
      onError?.(message);
    } finally {
      setBusyProvider(null);
    }
  };

  const startTelegramBotAuth = async () => {
    setBusyProvider('telegram-bot');
    setError(null);

    try {
      const started = await authService.startTelegramBotAuth('/');
      setTelegramSession({
        provider: 'telegram',
        state: started.state,
        expiresAt: started.expiresAt,
        pollIntervalMs: started.pollIntervalMs,
      });
      setHint(buildPendingMessage('telegram'));

      const popup = window.open(started.authUrl, '_blank', 'noopener,noreferrer');
      if (!popup) {
        window.location.assign(started.authUrl);
      }
    } catch (nextError) {
      const message = getErrorMessage(nextError, 'Не удалось начать вход через Telegram.');
      setError(message);
      onError?.(message);
    } finally {
      setBusyProvider(null);
    }
  };

  useEffect(() => {
    if (!externalSession?.state) {
      return undefined;
    }

    let cancelled = false;
    const timer = window.setInterval(async () => {
      try {
        const status = await authService.getExternalAuthStatus(externalSession.state);
        if (cancelled) {
          return;
        }

        if (await applyAuthStatus(status, externalSession.provider)) {
          closePopup();
          setExternalSession(null);
          setHint(null);
          return;
        }

        if (status.status === 'failed' || status.status === 'expired' || status.status === 'consumed') {
          closePopup();
          setExternalSession(null);
          const message = status.detail
            || (status.status === 'expired'
              ? `Сессия входа через ${getProviderLabel(externalSession.provider)} истекла.`
              : `Не удалось выполнить вход через ${getProviderLabel(externalSession.provider)}.`);
          setHint(null);
          setError(message);
          onError?.(message);
        }
      } catch (nextError) {
        if (cancelled) {
          return;
        }

        closePopup();
        setExternalSession(null);
        setHint(null);
        const message = getErrorMessage(nextError, `Ошибка проверки входа через ${getProviderLabel(externalSession.provider)}.`);
        setError(message);
        onError?.(message);
      }
    }, Math.max(1000, externalSession.pollIntervalMs || 2000));

    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [externalSession, onError, signInWithToken]);

  useEffect(() => {
    if (!telegramSession?.state) {
      return undefined;
    }

    let cancelled = false;
    const timer = window.setInterval(async () => {
      try {
        const status = await authService.getTelegramBotAuthStatus(telegramSession.state);
        if (cancelled) {
          return;
        }

        if (await applyAuthStatus(status, 'telegram')) {
          setTelegramSession(null);
          setHint(null);
          return;
        }

        if (status.status === 'failed' || status.status === 'expired' || status.status === 'consumed') {
          setTelegramSession(null);
          const message = status.detail
            || (status.status === 'expired'
              ? 'Сессия входа через Telegram истекла.'
              : 'Не удалось выполнить вход через Telegram.');
          setHint(null);
          setError(message);
          onError?.(message);
        }
      } catch (nextError) {
        if (cancelled) {
          return;
        }

        setTelegramSession(null);
        setHint(null);
        const message = getErrorMessage(nextError, 'Ошибка проверки входа через Telegram.');
        setError(message);
        onError?.(message);
      }
    }, Math.max(1000, telegramSession.pollIntervalMs || 2000));

    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [onError, signInWithToken, telegramSession]);

  useEffect(() => {
    if (!settings?.telegramWidgetEnabled || !telegramBotUsername || !widgetContainerRef.current) {
      setWidgetStatus('idle');
      setWidgetError(null);
      return undefined;
    }

    const container = widgetContainerRef.current;
    const widgetWindow = window as Window & {
      [TELEGRAM_WIDGET_CALLBACK]?: (payload: Record<string, unknown>) => void;
    };

    setWidgetStatus('loading');
    setWidgetError(null);
    container.innerHTML = '';

    const resolveWidgetState = () => {
      const normalizedText = String(container.textContent || '').trim().toLowerCase();
      if (TELEGRAM_WIDGET_ERRORS.some((pattern) => normalizedText.includes(pattern))) {
        setWidgetStatus('unavailable');
        setWidgetError('Telegram Widget недоступен для текущего домена или username бота.');
        return;
      }

      const hasRenderableContent = container.childElementCount > 0 || normalizedText.length > 0;
      if (hasRenderableContent) {
        setWidgetStatus('ready');
      }
    };

    widgetWindow[TELEGRAM_WIDGET_CALLBACK] = async (payload) => {
      setBusyProvider('telegram-widget');
      setError(null);
      try {
        const status = await authService.completeTelegramWidget(payload);
        const handled = await applyAuthStatus(status, 'telegram');
        if (!handled && status.detail) {
          setError(status.detail);
          onError?.(status.detail);
        }
      } catch (nextError) {
        const message = getErrorMessage(nextError, 'Не удалось выполнить вход через Telegram Widget.');
        setError(message);
        onError?.(message);
      } finally {
        setBusyProvider(null);
      }
    };

    const observer = new MutationObserver(() => resolveWidgetState());
    observer.observe(container, { childList: true, subtree: true, characterData: true });

    const script = document.createElement('script');
    script.src = 'https://telegram.org/js/telegram-widget.js?22';
    script.async = true;
    script.setAttribute('data-telegram-login', telegramBotUsername);
    script.setAttribute('data-size', 'large');
    script.setAttribute('data-userpic', 'false');
    script.setAttribute('data-radius', '10');
    script.setAttribute('data-request-access', 'write');
    script.setAttribute('data-onauth', `${TELEGRAM_WIDGET_CALLBACK}(user)`);
    script.onload = () => window.setTimeout(resolveWidgetState, 500);
    script.onerror = () => {
      setWidgetStatus('unavailable');
      setWidgetError('Не удалось загрузить Telegram Widget.');
    };
    container.appendChild(script);

    return () => {
      observer.disconnect();
      delete widgetWindow[TELEGRAM_WIDGET_CALLBACK];
      container.innerHTML = '';
    };
  }, [onError, settings?.telegramWidgetEnabled, telegramBotUsername, signInWithToken]);

  if (loadingSettings) {
    return (
      <div className="mt-6 rounded-xl border border-slate-200 bg-slate-50 p-4 text-sm text-slate-600">
        Доступные способы входа загружаются…
      </div>
    );
  }

  if (!providerButtons.length && !settings?.telegramWidgetEnabled) {
    return null;
  }

  return (
    <div className="mt-6 space-y-4">
      <div className="relative">
        <div className="absolute inset-0 flex items-center" aria-hidden="true">
          <div className="w-full border-t border-slate-200" />
        </div>
        <div className="relative flex justify-center">
          <span className="bg-white px-3 text-xs font-medium text-slate-400">
            или быстрый вход
          </span>
        </div>
      </div>

      <div className="flex flex-wrap items-center justify-center gap-3">
        {providerButtons.map((provider) => {
          const isBusy = busyProvider === provider.id;
          const isTelegram = provider.id === 'telegram';

          return (
            <QuickAuthTile
              key={provider.id}
              label={provider.label}
              title={provider.title}
              active={isBusy}
              disabled={disabled || Boolean(busyProvider)}
              onClick={() => (isTelegram ? void startTelegramBotAuth() : void startExternalAuth(provider.id as 'google' | 'vk' | 'yandex'))}
            >
              {isBusy ? <Loader2 className="h-5 w-5 animate-spin text-slate-500" /> : provider.icon}
            </QuickAuthTile>
          );
        })}
      </div>

      {settings?.telegramWidgetEnabled && telegramBotUsername && (
        <div className="space-y-2 rounded-xl border border-slate-200 bg-slate-50 p-4">
          <div className="text-sm font-medium text-slate-700">Telegram Widget</div>
          <div ref={widgetContainerRef} className={widgetStatus === 'unavailable' ? 'hidden' : 'flex min-h-[50px] justify-center'} />
          {widgetStatus === 'loading' && (
            <div className="flex items-center justify-center gap-2 text-sm text-slate-500">
              <Loader2 className="h-4 w-4 animate-spin" />
              <span>Подготавливаем Telegram Widget…</span>
            </div>
          )}
          {widgetStatus === 'unavailable' && widgetError && (
            <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
              {widgetError}
            </div>
          )}
        </div>
      )}

      {(hint || externalSession || telegramSession) && (
        <div className="rounded-xl border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-900">
          {hint || buildPendingMessage(externalSession?.provider || telegramSession?.provider || 'telegram')}
        </div>
      )}

      {error && (
        <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}
    </div>
  );
}
