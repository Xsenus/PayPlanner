import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  PAYPLANNER_PUSH_WORKER_PATH,
  PAYPLANNER_PUSH_WORKER_SCOPE,
  getWebPushStatus,
  registerPayPlannerPushWorker,
  subscribeCurrentDeviceToWebPush,
  urlBase64ToUint8Array,
} from './webPushNotifications';

const apiMock = vi.hoisted(() => ({
  getWebPushPublicKey: vi.fn(),
  getUserPushSubscriptions: vi.fn(),
  upsertUserPushSubscription: vi.fn(),
  revokeUserPushSubscriptionByEndpoint: vi.fn(),
}));

vi.mock('./api', () => ({
  apiService: apiMock,
}));

describe('webPushNotifications', () => {
  beforeEach(() => {
    Object.values(apiMock).forEach((mock) => mock.mockReset());
  });

  it('decodes VAPID public key from URL-safe base64', () => {
    const decoded = urlBase64ToUint8Array('SGVsbG8td29ybGQ');

    expect(Array.from(decoded)).toEqual(Array.from(new TextEncoder().encode('Hello-world')));
  });

  it('returns unsupported status when browser APIs are unavailable', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalPushManager = window.PushManager;
    const originalNotification = window.Notification;

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: undefined,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: undefined,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: undefined,
    });

    await expect(getWebPushStatus()).resolves.toMatchObject({
      status: 'unsupported',
      permission: 'unsupported',
      subscriptions: [],
      publicKeyReady: false,
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: originalPushManager,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: originalNotification,
    });
  });

  it('returns insecure-context status on non-HTTPS origins before contacting the server', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalPushManager = window.PushManager;
    const originalNotification = window.Notification;
    const originalIsSecureContext = window.isSecureContext;

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { register: vi.fn() },
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: { permission: 'default' },
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: false,
    });

    await expect(getWebPushStatus()).resolves.toMatchObject({
      status: 'insecure-context',
      permission: 'default',
      subscriptions: [],
      publicKeyReady: false,
    });
    expect(apiMock.getWebPushPublicKey).not.toHaveBeenCalled();
    expect(apiMock.getUserPushSubscriptions).not.toHaveBeenCalled();

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: originalPushManager,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: originalNotification,
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: originalIsSecureContext,
    });
  });

  it('blocks subscription attempts on non-HTTPS origins before requesting permissions', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalPushManager = window.PushManager;
    const originalNotification = window.Notification;
    const originalIsSecureContext = window.isSecureContext;
    const requestPermission = vi.fn();

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { register: vi.fn() },
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: { permission: 'default', requestPermission },
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: false,
    });

    await expect(subscribeCurrentDeviceToWebPush()).rejects.toThrow(
      'Push-уведомления работают только на защищённом HTTPS-адресе CRM.',
    );
    expect(requestPermission).not.toHaveBeenCalled();
    expect(apiMock.getWebPushPublicKey).not.toHaveBeenCalled();

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: originalPushManager,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: originalNotification,
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: originalIsSecureContext,
    });
  });

  it('explains browser-level push permission failures without saving an empty subscription', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalPushManager = window.PushManager;
    const originalNotification = window.Notification;
    const originalIsSecureContext = window.isSecureContext;
    const requestPermission = vi.fn().mockResolvedValue('granted');
    const subscribe = vi.fn().mockRejectedValue(new Error('Registration failed - permission denied'));
    const register = vi.fn().mockResolvedValue({
      active: {},
      pushManager: {
        getSubscription: vi.fn().mockResolvedValue(null),
        subscribe,
      },
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { register },
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: { permission: 'granted', requestPermission },
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: true,
    });
    apiMock.getWebPushPublicKey.mockResolvedValue({
      enabled: true,
      ready: true,
      publicKey: 'SGVsbG8td29ybGQ',
    });

    await expect(subscribeCurrentDeviceToWebPush()).rejects.toThrow(
      'Браузер не дал CRM создать Push-подписку.',
    );
    expect(subscribe).toHaveBeenCalledTimes(1);
    expect(apiMock.upsertUserPushSubscription).not.toHaveBeenCalled();

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: originalPushManager,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: originalNotification,
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: originalIsSecureContext,
    });
  });

  it('recreates an existing browser subscription when the server VAPID key changed', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalPushManager = window.PushManager;
    const originalNotification = window.Notification;
    const originalIsSecureContext = window.isSecureContext;
    const requestPermission = vi.fn().mockResolvedValue('granted');
    const unsubscribe = vi.fn().mockResolvedValue(true);
    const oldSubscription = {
      endpoint: 'https://push.example/old',
      options: {
        applicationServerKey: new TextEncoder().encode('old-key').buffer,
      },
      unsubscribe,
      toJSON: vi.fn(),
    } as unknown as PushSubscription;
    const newSubscription = {
      endpoint: 'https://push.example/new',
      options: {
        applicationServerKey: new TextEncoder().encode('Hello-world').buffer,
      },
      toJSON: vi.fn().mockReturnValue({
        endpoint: 'https://push.example/new',
        keys: {
          p256dh: 'new-p256dh',
          auth: 'new-auth',
        },
      }),
    } as unknown as PushSubscription;
    const getSubscription = vi.fn()
      .mockResolvedValueOnce(oldSubscription)
      .mockResolvedValueOnce(null);
    const subscribe = vi.fn().mockResolvedValue(newSubscription);
    const register = vi.fn().mockResolvedValue({
      active: {},
      pushManager: {
        getSubscription,
        subscribe,
      },
    });
    apiMock.getWebPushPublicKey.mockResolvedValue({
      enabled: true,
      ready: true,
      publicKey: 'SGVsbG8td29ybGQ',
    });
    apiMock.upsertUserPushSubscription.mockResolvedValue({
      id: 8,
      endpoint: newSubscription.endpoint,
      browser: 'Google Chrome',
      platform: 'Windows',
      deviceName: 'Google Chrome / Windows',
      isActive: true,
      createdAt: '2026-07-05T09:00:00Z',
      updatedAt: '2026-07-05T09:00:00Z',
      lastSeenAt: '2026-07-05T09:00:00Z',
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { register },
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: { permission: 'granted', requestPermission },
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: true,
    });

    await expect(subscribeCurrentDeviceToWebPush()).resolves.toMatchObject({
      endpoint: newSubscription.endpoint,
    });

    expect(unsubscribe).toHaveBeenCalledTimes(1);
    expect(subscribe).toHaveBeenCalledWith({
      userVisibleOnly: true,
      applicationServerKey: urlBase64ToUint8Array('SGVsbG8td29ybGQ'),
    });
    expect(apiMock.upsertUserPushSubscription).toHaveBeenCalledWith(expect.objectContaining({
      endpoint: newSubscription.endpoint,
      keys: {
        p256dh: 'new-p256dh',
        auth: 'new-auth',
      },
    }));

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: originalPushManager,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: originalNotification,
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: originalIsSecureContext,
    });
  });

  it('keeps current active subscription enabled when server keys are temporarily not ready', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalPushManager = window.PushManager;
    const originalNotification = window.Notification;
    const originalIsSecureContext = window.isSecureContext;
    const browserSubscription = { endpoint: 'https://push.example/current' };
    const register = vi.fn().mockResolvedValue({
      pushManager: {
        getSubscription: vi.fn().mockResolvedValue(browserSubscription),
      },
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { register },
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: { permission: 'granted' },
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: true,
    });
    apiMock.getWebPushPublicKey.mockResolvedValue({
      enabled: true,
      ready: false,
      publicKey: '',
    });
    apiMock.getUserPushSubscriptions.mockResolvedValue([
      {
        id: 7,
        endpoint: browserSubscription.endpoint,
        browser: 'Google Chrome',
        platform: 'Windows',
        deviceName: 'Google Chrome / Windows',
        isActive: true,
        createdAt: '2026-06-22T09:00:00Z',
        updatedAt: '2026-06-22T09:00:00Z',
        lastSeenAt: '2026-06-22T09:00:00Z',
      },
    ]);

    await expect(getWebPushStatus()).resolves.toMatchObject({
      status: 'subscribed',
      permission: 'granted',
      currentEndpoint: browserSubscription.endpoint,
      publicKeyReady: false,
      message: 'Push-уведомления включены на этом устройстве.',
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: originalPushManager,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: originalNotification,
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: originalIsSecureContext,
    });
  });

  it('treats active subscriptions with invalid VAPID errors as not subscribed', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalPushManager = window.PushManager;
    const originalNotification = window.Notification;
    const originalIsSecureContext = window.isSecureContext;
    const browserSubscription = { endpoint: 'https://push.example/stale' };
    const register = vi.fn().mockResolvedValue({
      active: {},
      pushManager: {
        getSubscription: vi.fn().mockResolvedValue(browserSubscription),
      },
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { register },
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: { permission: 'granted' },
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: true,
    });
    apiMock.getWebPushPublicKey.mockResolvedValue({
      enabled: true,
      ready: true,
      publicKey: 'SGVsbG8td29ybGQ',
    });
    apiMock.getUserPushSubscriptions.mockResolvedValue([
      {
        id: 9,
        endpoint: browserSubscription.endpoint,
        browser: 'Google Chrome',
        platform: 'Windows',
        deviceName: 'Google Chrome / Windows',
        isActive: true,
        createdAt: '2026-06-22T09:00:00Z',
        updatedAt: '2026-07-05T09:00:00Z',
        lastSeenAt: '2026-06-22T09:00:00Z',
        lastError: 'Received unexpected response code: 403. Details: the VAPID credentials in the authorization header do not correspond to the credentials used to create the subscriptions.',
      },
    ]);

    await expect(getWebPushStatus()).resolves.toMatchObject({
      status: 'not-subscribed',
      permission: 'granted',
      currentEndpoint: browserSubscription.endpoint,
      publicKeyReady: true,
      message: 'Это устройство пока не получает push-уведомления.',
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: originalPushManager,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: originalNotification,
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: originalIsSecureContext,
    });
  });

  it('registers PayPlanner push worker at the root scope', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalIsSecureContext = window.isSecureContext;
    const register = vi.fn().mockResolvedValue({} as ServiceWorkerRegistration);

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { register },
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: true,
    });

    await expect(registerPayPlannerPushWorker()).resolves.toEqual({});
    expect(register).toHaveBeenCalledWith(PAYPLANNER_PUSH_WORKER_PATH, {
      scope: PAYPLANNER_PUSH_WORKER_SCOPE,
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'isSecureContext', {
      configurable: true,
      value: originalIsSecureContext,
    });
  });
});
