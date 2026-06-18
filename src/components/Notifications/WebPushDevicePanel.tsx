import { useCallback, useEffect, useMemo, useState } from 'react';
import { Activity, AlertCircle, CheckCircle2, RefreshCw, Smartphone } from 'lucide-react';
import { apiService, type WebPushDiagnosticsDto } from '../../services/api';
import {
  getWebPushStatus,
  subscribeCurrentDeviceToWebPush,
  unsubscribeCurrentDeviceFromWebPush,
  type WebPushStatus,
} from '../../services/webPushNotifications';
import { useToast } from '../../hooks/useToast';
import {
  REALTIME_NOTIFICATION_DIAGNOSTICS_EVENT,
  getRealtimeNotificationDiagnostics,
  type RealtimeNotificationDiagnostics,
  type RealtimeNotificationConnectionStatus,
} from '../../utils/realtimeNotificationDiagnostics';
import { UserPushHelp } from './NotificationHelp';

type WebPushDevicePanelProps = {
  autoLoad?: boolean;
  showDevices?: boolean;
  title?: string;
  description?: string;
  className?: string;
};

function formatDateTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  try {
    return new Intl.DateTimeFormat('ru-RU', {
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    }).format(new Date(value));
  } catch {
    return value;
  }
}

function formatRealtimeStatus(status: RealtimeNotificationConnectionStatus) {
  switch (status) {
    case 'connected':
      return 'подключено';
    case 'connecting':
      return 'подключаемся';
    case 'fallback':
      return 'fallback-опрос';
    case 'reconnecting':
      return 'переподключение';
    case 'disconnected':
      return 'отключено';
    case 'unavailable':
      return 'недоступно';
    default:
      return 'ещё не проверено';
  }
}

export function WebPushDevicePanel({
  autoLoad = true,
  showDevices = true,
  title = 'Push-уведомления на устройстве',
  description,
  className = '',
}: WebPushDevicePanelProps) {
  const toast = useToast();
  const [webPushStatus, setWebPushStatus] = useState<WebPushStatus | null>(null);
  const [webPushDiagnostics, setWebPushDiagnostics] = useState<WebPushDiagnosticsDto | null>(null);
  const [realtimeDiagnostics, setRealtimeDiagnostics] = useState<RealtimeNotificationDiagnostics>(() => getRealtimeNotificationDiagnostics());
  const [webPushLoading, setWebPushLoading] = useState(false);
  const [webPushBusy, setWebPushBusy] = useState(false);
  const [webPushRevokingId, setWebPushRevokingId] = useState<number | null>(null);

  const activePushSubscriptionsCount = useMemo(
    () => webPushStatus?.subscriptions.filter((item) => item.isActive).length ?? 0,
    [webPushStatus],
  );
  const pushSubscriptions = useMemo(
    () => webPushStatus?.subscriptions.slice(0, 6) ?? [],
    [webPushStatus],
  );
  const canEnable = webPushStatus?.status !== 'unsupported'
    && webPushStatus?.status !== 'insecure-context'
    && webPushStatus?.status !== 'server-disabled'
    && webPushStatus?.status !== 'server-not-ready'
    && webPushStatus?.status !== 'permission-denied';

  const refreshWebPushStatus = useCallback(async () => {
    try {
      setWebPushLoading(true);
      const [nextStatus, diagnosticsResult] = await Promise.allSettled([
        getWebPushStatus(),
        apiService.getWebPushDiagnostics(),
      ]);
      if (nextStatus.status === 'fulfilled') {
        setWebPushStatus(nextStatus.value);
      } else {
        throw nextStatus.reason;
      }

      setWebPushDiagnostics(diagnosticsResult.status === 'fulfilled' ? diagnosticsResult.value : null);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось загрузить статус push-уведомлений.');
      setWebPushStatus(null);
      setWebPushDiagnostics(null);
    } finally {
      setWebPushLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    const handleDiagnosticsChanged = (event: Event) => {
      const detail = (event as CustomEvent<RealtimeNotificationDiagnostics>).detail;
      setRealtimeDiagnostics(detail ?? getRealtimeNotificationDiagnostics());
    };

    window.addEventListener(REALTIME_NOTIFICATION_DIAGNOSTICS_EVENT, handleDiagnosticsChanged);
    return () => window.removeEventListener(REALTIME_NOTIFICATION_DIAGNOSTICS_EVENT, handleDiagnosticsChanged);
  }, []);

  useEffect(() => {
    if (autoLoad) {
      void refreshWebPushStatus();
    }
  }, [autoLoad, refreshWebPushStatus]);

  const handleEnableWebPush = async () => {
    try {
      setWebPushBusy(true);
      await subscribeCurrentDeviceToWebPush();
      await refreshWebPushStatus();
      toast.success('Push-уведомления включены на этом устройстве.');
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось включить push-уведомления.');
    } finally {
      setWebPushBusy(false);
    }
  };

  const handleDisableWebPush = async () => {
    try {
      setWebPushBusy(true);
      await unsubscribeCurrentDeviceFromWebPush();
      await refreshWebPushStatus();
      toast.success('Push-уведомления отключены на этом устройстве.');
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось отключить push-уведомления.');
    } finally {
      setWebPushBusy(false);
    }
  };

  const handleRevokeWebPushSubscription = async (id: number) => {
    try {
      setWebPushRevokingId(id);
      await apiService.revokeUserPushSubscription(id);
      await refreshWebPushStatus();
      toast.success('Push-устройство отключено.');
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'Не удалось отключить push-устройство.');
    } finally {
      setWebPushRevokingId(null);
    }
  };

  return (
    <div className={`rounded-lg border border-slate-200 bg-slate-50 p-3 ${className}`}>
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="min-w-0">
          <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
            <Smartphone className="h-4 w-4 text-blue-600" />
            {title}
          </div>
          {description ? (
            <p className="mt-1 text-xs leading-5 text-slate-500">{description}</p>
          ) : null}
          <p className="mt-1 text-xs leading-5 text-slate-500">
            {webPushLoading
              ? 'Проверяем подключение устройства...'
              : webPushStatus?.message ?? 'Статус устройства ещё не проверен.'}
          </p>
          <div className="mt-2 flex flex-wrap gap-2 text-xs text-slate-500">
            <span className="rounded-full bg-white px-2 py-0.5">
              Активных устройств: {activePushSubscriptionsCount}
            </span>
            {webPushStatus?.permission ? (
              <span className="rounded-full bg-white px-2 py-0.5">
                Разрешение браузера: {webPushStatus.permission}
              </span>
            ) : null}
          </div>
          <div className="mt-3 grid gap-2 text-xs md:grid-cols-2 xl:grid-cols-4">
            <div className="rounded-lg border border-white bg-white p-2">
              <div className="flex items-center gap-1.5 font-medium text-slate-700">
                {realtimeDiagnostics.status === 'connected' ? (
                  <CheckCircle2 className="h-3.5 w-3.5 text-emerald-600" />
                ) : (
                  <Activity className="h-3.5 w-3.5 text-amber-600" />
                )}
                Моментальные
              </div>
              <div className="mt-1 text-slate-500">
                {formatRealtimeStatus(realtimeDiagnostics.status)}
                {realtimeDiagnostics.fallbackPollingActive ? ' · опрос 30 сек.' : ''}
              </div>
            </div>
            <div className="rounded-lg border border-white bg-white p-2">
              <div className="flex items-center gap-1.5 font-medium text-slate-700">
                {webPushDiagnostics?.ready || webPushStatus?.publicKeyReady ? (
                  <CheckCircle2 className="h-3.5 w-3.5 text-emerald-600" />
                ) : (
                  <AlertCircle className="h-3.5 w-3.5 text-amber-600" />
                )}
                Web Push сервер
              </div>
              <div className="mt-1 text-slate-500">
                {webPushDiagnostics
                  ? (webPushDiagnostics.ready ? 'готов' : 'требует настройки')
                  : (webPushStatus?.publicKeyReady ? 'ключ доступен' : 'диагностика недоступна')}
              </div>
            </div>
            <div className="rounded-lg border border-white bg-white p-2">
              <div className="font-medium text-slate-700">Подписки</div>
              <div className="mt-1 text-slate-500">
                {webPushDiagnostics
                  ? `${webPushDiagnostics.activeSubscriptionsCount} активных из ${webPushDiagnostics.totalSubscriptionsCount}`
                  : `${activePushSubscriptionsCount} активных`}
              </div>
            </div>
            <div className="rounded-lg border border-white bg-white p-2">
              <div className="font-medium text-slate-700">Последняя доставка</div>
              <div className="mt-1 text-slate-500">
                {webPushDiagnostics?.lastFailedDeliveryAt
                  ? `ошибка: ${formatDateTime(webPushDiagnostics.lastFailedDeliveryAt)}`
                  : formatDateTime(webPushDiagnostics?.lastSuccessfulDeliveryAt)}
              </div>
            </div>
          </div>
          {realtimeDiagnostics.lastError ? (
            <p className="mt-2 text-xs text-amber-700">Realtime: {realtimeDiagnostics.lastError}</p>
          ) : null}
          {webPushDiagnostics?.lastDeliveryError ? (
            <p className="mt-1 text-xs text-red-600">Web Push: {webPushDiagnostics.lastDeliveryError}</p>
          ) : null}
          <UserPushHelp />
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => void refreshWebPushStatus()}
            disabled={webPushLoading || webPushBusy}
            className="inline-flex h-9 items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 text-xs font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${webPushLoading ? 'animate-spin' : ''}`} />
            Проверить
          </button>
          {webPushStatus?.status === 'subscribed' ? (
            <button
              type="button"
              onClick={() => void handleDisableWebPush()}
              disabled={webPushLoading || webPushBusy}
              className="inline-flex h-9 items-center justify-center gap-2 rounded-lg border border-red-200 bg-white px-3 text-xs font-medium text-red-700 transition hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Отключить это устройство
            </button>
          ) : (
            <button
              type="button"
              onClick={() => void handleEnableWebPush()}
              disabled={webPushLoading || webPushBusy || !canEnable}
              className="inline-flex h-9 items-center justify-center gap-2 rounded-lg bg-blue-600 px-3 text-xs font-medium text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Включить на этом устройстве
            </button>
          )}
        </div>
      </div>
      {showDevices && pushSubscriptions.length > 0 ? (
        <div className="mt-3 divide-y divide-slate-200 rounded-lg border border-slate-200 bg-white">
          {pushSubscriptions.map((subscription) => {
            const isCurrentDevice = subscription.endpoint === webPushStatus?.currentEndpoint;
            return (
              <div key={subscription.id} className="flex flex-col gap-2 px-3 py-2 text-xs sm:flex-row sm:items-center sm:justify-between">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2 font-medium text-slate-800">
                    <span className="truncate">{subscription.deviceName || subscription.browser || 'Браузер'}</span>
                    {isCurrentDevice ? <span className="rounded-full bg-blue-50 px-2 py-0.5 text-blue-700">это устройство</span> : null}
                    <span className={`rounded-full px-2 py-0.5 ${subscription.isActive ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'}`}>
                      {subscription.isActive ? 'активно' : 'отключено'}
                    </span>
                  </div>
                  <div className="mt-1 text-slate-500">
                    {subscription.platform || 'Платформа не определена'} · последняя активность: {formatDateTime(subscription.lastSeenAt ?? subscription.updatedAt)}
                  </div>
                  {subscription.lastError ? <div className="mt-1 text-red-600">{subscription.lastError}</div> : null}
                </div>
                {subscription.isActive ? (
                  isCurrentDevice ? (
                    <button
                      type="button"
                      onClick={() => void handleDisableWebPush()}
                      disabled={webPushLoading || webPushBusy}
                      className="inline-flex h-8 shrink-0 items-center justify-center rounded-lg border border-red-200 px-3 font-medium text-red-700 transition hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      Отключить
                    </button>
                  ) : (
                    <button
                      type="button"
                      onClick={() => void handleRevokeWebPushSubscription(subscription.id)}
                      disabled={webPushLoading || webPushBusy || webPushRevokingId === subscription.id}
                      className="inline-flex h-8 shrink-0 items-center justify-center rounded-lg border border-slate-200 px-3 font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      {webPushRevokingId === subscription.id ? 'Отключаем...' : 'Отключить'}
                    </button>
                  )
                ) : null}
              </div>
            );
          })}
        </div>
      ) : null}
    </div>
  );
}
