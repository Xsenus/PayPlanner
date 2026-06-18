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
