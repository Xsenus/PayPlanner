import { apiService, type UpsertPushSubscriptionRequest, type UserPushSubscriptionDto } from './api';

const WORKER_PATH = '/payplanner-push-worker.js';

export type WebPushDeviceStatus =
  | 'unsupported'
  | 'server-disabled'
  | 'server-not-ready'
  | 'permission-denied'
  | 'not-subscribed'
  | 'subscribed';

export interface WebPushStatus {
  status: WebPushDeviceStatus;
  permission: NotificationPermission | 'unsupported';
  subscriptions: UserPushSubscriptionDto[];
  currentEndpoint?: string;
  publicKeyReady: boolean;
  message: string;
}

export function isWebPushSupported() {
  return typeof window !== 'undefined'
    && Boolean(window.Notification)
    && Boolean(navigator.serviceWorker)
    && Boolean(window.PushManager);
}

export async function getWebPushStatus(): Promise<WebPushStatus> {
  if (!isWebPushSupported()) {
    return {
      status: 'unsupported',
      permission: 'unsupported',
      subscriptions: [],
      publicKeyReady: false,
      message: 'Этот браузер не поддерживает push-уведомления.',
    };
  }

  const [publicKey, subscriptions] = await Promise.all([
    apiService.getWebPushPublicKey(),
    apiService.getUserPushSubscriptions(),
  ]);
  const registration = await registerPayPlannerPushWorker();
  const browserSubscription = await registration.pushManager.getSubscription();
  const permission = Notification.permission;

  if (!publicKey.enabled) {
    return {
      status: 'server-disabled',
      permission,
      subscriptions,
      currentEndpoint: browserSubscription?.endpoint,
      publicKeyReady: false,
      message: 'Push-уведомления отключены на сервере.',
    };
  }

  if (!publicKey.ready || !publicKey.publicKey) {
    return {
      status: 'server-not-ready',
      permission,
      subscriptions,
      currentEndpoint: browserSubscription?.endpoint,
      publicKeyReady: false,
      message: 'На сервере не настроены ключи push-уведомлений.',
    };
  }

  if (permission === 'denied') {
    return {
      status: 'permission-denied',
      permission,
      subscriptions,
      currentEndpoint: browserSubscription?.endpoint,
      publicKeyReady: true,
      message: 'Браузер запретил push-уведомления для CRM.',
    };
  }

  const hasCurrentActiveSubscription = Boolean(
    browserSubscription
      && subscriptions.some((item) => item.isActive && item.endpoint === browserSubscription.endpoint),
  );

  return {
    status: hasCurrentActiveSubscription ? 'subscribed' : 'not-subscribed',
    permission,
    subscriptions,
    currentEndpoint: browserSubscription?.endpoint,
    publicKeyReady: true,
    message: hasCurrentActiveSubscription
      ? 'Push-уведомления включены на этом устройстве.'
      : 'Это устройство пока не получает push-уведомления.',
  };
}

export async function subscribeCurrentDeviceToWebPush() {
  if (!isWebPushSupported()) {
    throw new Error('Этот браузер не поддерживает push-уведомления.');
  }

  const publicKey = await apiService.getWebPushPublicKey();
  if (!publicKey.enabled) {
    throw new Error('Push-уведомления отключены на сервере.');
  }

  if (!publicKey.ready || !publicKey.publicKey) {
    throw new Error('На сервере не настроены ключи push-уведомлений.');
  }

  const permission = await Notification.requestPermission();
  if (permission !== 'granted') {
    throw new Error(permission === 'denied'
      ? 'Браузер запретил push-уведомления для CRM.'
      : 'Разрешение на push-уведомления не выдано.');
  }

  const registration = await registerPayPlannerPushWorker();
  const existing = await registration.pushManager.getSubscription();
  const subscription = existing ?? await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: urlBase64ToUint8Array(publicKey.publicKey),
  });
  const payload = buildSubscriptionPayload(subscription);
  return apiService.upsertUserPushSubscription(payload);
}

export async function unsubscribeCurrentDeviceFromWebPush() {
  if (!isWebPushSupported()) {
    return;
  }

  const registration = await registerPayPlannerPushWorker();
  const subscription = await registration.pushManager.getSubscription();
  if (!subscription) {
    return;
  }

  await apiService.revokeUserPushSubscriptionByEndpoint(subscription.endpoint);
  await subscription.unsubscribe();
}

export async function registerPayPlannerPushWorker() {
  return navigator.serviceWorker.register(WORKER_PATH, { scope: '/' });
}

export function urlBase64ToUint8Array(value: string) {
  const padding = '='.repeat((4 - (value.length % 4)) % 4);
  const base64 = `${value}${padding}`.replace(/-/g, '+').replace(/_/g, '/');
  const rawData = window.atob(base64);
  const outputArray = new Uint8Array(rawData.length);

  for (let i = 0; i < rawData.length; i += 1) {
    outputArray[i] = rawData.charCodeAt(i);
  }

  return outputArray;
}

function buildSubscriptionPayload(subscription: PushSubscription): UpsertPushSubscriptionRequest {
  const json = subscription.toJSON();
  const keys = json.keys ?? {};
  const browser = detectBrowser(navigator.userAgent);
  const nav = navigator as Navigator & { userAgentData?: { platform?: string } };
  const platform = nav.userAgentData?.platform || navigator.platform || '';

  if (!json.endpoint || !keys.p256dh || !keys.auth) {
    throw new Error('Браузер не вернул данные push-подписки.');
  }

  return {
    endpoint: json.endpoint,
    keys: {
      p256dh: keys.p256dh,
      auth: keys.auth,
    },
    browser,
    platform,
    deviceName: [browser, platform].filter(Boolean).join(' / '),
    userAgent: navigator.userAgent,
  };
}

function detectBrowser(userAgent: string) {
  if (/Edg\//.test(userAgent)) {
    return 'Microsoft Edge';
  }

  if (/OPR\//.test(userAgent)) {
    return 'Opera';
  }

  if (/Chrome\//.test(userAgent)) {
    return 'Google Chrome';
  }

  if (/Firefox\//.test(userAgent)) {
    return 'Firefox';
  }

  if (/Safari\//.test(userAgent)) {
    return 'Safari';
  }

  return 'Browser';
}
