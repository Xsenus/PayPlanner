import { apiService, type UpsertPushSubscriptionRequest, type UserPushSubscriptionDto } from './api';

export const PAYPLANNER_PUSH_WORKER_PATH = '/payplanner-push-worker.js';
export const PAYPLANNER_PUSH_WORKER_SCOPE = '/';
const PAYPLANNER_PUSH_WORKER_READY_TIMEOUT_MS = 4_000;

export type WebPushDeviceStatus =
  | 'unsupported'
  | 'insecure-context'
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

export function isWebPushSecureContext() {
  if (typeof window === 'undefined') {
    return false;
  }

  if (typeof window.isSecureContext === 'boolean') {
    return window.isSecureContext;
  }

  const hostname = window.location.hostname;
  return hostname === 'localhost'
    || hostname === '127.0.0.1'
    || hostname === '::1';
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

  if (!isWebPushSecureContext()) {
    return {
      status: 'insecure-context',
      permission: Notification.permission,
      subscriptions: [],
      publicKeyReady: false,
      message: 'Push-уведомления работают только на защищённом HTTPS-адресе CRM.',
    };
  }

  const [publicKey, subscriptions] = await Promise.all([
    apiService.getWebPushPublicKey(),
    apiService.getUserPushSubscriptions(),
  ]);
  const registration = await registerPayPlannerPushWorker();
  const browserSubscription = await registration.pushManager.getSubscription();
  const permission = Notification.permission;
  const hasCurrentActiveSubscription = Boolean(
    browserSubscription
      && subscriptions.some((item) => (
        item.isActive
        && item.endpoint === browserSubscription.endpoint
        && !hasInvalidWebPushSubscriptionError(item)
      )),
  );

  if (hasCurrentActiveSubscription && permission !== 'denied') {
    return {
      status: 'subscribed',
      permission,
      subscriptions,
      currentEndpoint: browserSubscription?.endpoint,
      publicKeyReady: Boolean(publicKey.enabled && publicKey.ready && publicKey.publicKey),
      message: 'Push-уведомления включены на этом устройстве.',
    };
  }

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

  if (!isWebPushSecureContext()) {
    throw new Error('Push-уведомления работают только на защищённом HTTPS-адресе CRM.');
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
  if (existing && !doesSubscriptionUseApplicationServerKey(existing, publicKey.publicKey)) {
    await existing.unsubscribe();
  }

  const current = await registration.pushManager.getSubscription();
  const subscription = current ?? await createBrowserPushSubscription(registration, publicKey.publicKey);
  const payload = buildSubscriptionPayload(subscription);
  return apiService.upsertUserPushSubscription(payload);
}

export async function unsubscribeCurrentDeviceFromWebPush() {
  if (!isWebPushSupported()) {
    return;
  }

  if (!isWebPushSecureContext()) {
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
  if (!isWebPushSecureContext()) {
    throw new Error('Push-уведомления работают только на защищённом HTTPS-адресе CRM.');
  }

  const registration = await navigator.serviceWorker.register(PAYPLANNER_PUSH_WORKER_PATH, { scope: PAYPLANNER_PUSH_WORKER_SCOPE });
  if (registration.active) {
    return registration;
  }

  const readyRegistration = await waitForReadyPayPlannerPushWorker(registration);
  return readyRegistration ?? registration;
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

function hasInvalidWebPushSubscriptionError(subscription: UserPushSubscriptionDto) {
  const error = subscription.lastError ?? '';
  return /VAPID credentials/i.test(error)
    || /push subscription has unsubscribed or expired/i.test(error)
    || /subscription no longer valid/i.test(error);
}

function doesSubscriptionUseApplicationServerKey(subscription: PushSubscription, publicKey: string) {
  const actualKey = subscription.options?.applicationServerKey;
  if (!actualKey) {
    return true;
  }

  return areByteArraysEqual(
    new Uint8Array(actualKey),
    urlBase64ToUint8Array(publicKey),
  );
}

function areByteArraysEqual(left: Uint8Array, right: Uint8Array) {
  if (left.length !== right.length) {
    return false;
  }

  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) {
      return false;
    }
  }

  return true;
}

async function createBrowserPushSubscription(registration: ServiceWorkerRegistration, publicKey: string) {
  try {
    return await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: urlBase64ToUint8Array(publicKey),
    });
  } catch (error) {
    if (isBrowserPushPermissionError(error)) {
      throw new Error(buildWebPushPermissionHelpMessage());
    }

    throw error;
  }
}

async function waitForReadyPayPlannerPushWorker(registration: ServiceWorkerRegistration) {
  const ready = navigator.serviceWorker.ready;
  if (!ready || typeof ready.then !== 'function') {
    return null;
  }

  try {
    return await Promise.race([
      ready.then((readyRegistration) => (
        readyRegistration.scope === registration.scope ? readyRegistration : registration
      )),
      new Promise<ServiceWorkerRegistration | null>((resolve) => {
        window.setTimeout(() => resolve(null), PAYPLANNER_PUSH_WORKER_READY_TIMEOUT_MS);
      }),
    ]);
  } catch {
    return null;
  }
}

function buildWebPushPermissionHelpMessage() {
  const host = window.location.hostname || 'CRM';
  return [
    'Браузер не дал CRM создать Push-подписку.',
    `Проверьте разрешение уведомлений для ${host} и убедитесь,`,
    'что уведомления для этого браузера не отключены в Windows,',
    'затем обновите страницу и включите устройство заново.',
  ].join(' ');
}

function isBrowserPushPermissionError(error: unknown) {
  const name = error instanceof DOMException ? error.name : '';
  const message = error instanceof Error ? error.message : String(error ?? '');

  return name === 'NotAllowedError'
    || /permission\s+denied/i.test(message)
    || /not\s+allowed/i.test(message);
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
