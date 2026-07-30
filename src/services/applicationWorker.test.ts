import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  PAYPLANNER_APPLICATION_WORKER_PATH,
  PAYPLANNER_APPLICATION_WORKER_SCOPE,
  isApplicationWorkerSupported,
  registerPayPlannerApplicationWorker,
} from './applicationWorker';

describe('applicationWorker', () => {
  const originalServiceWorker = navigator.serviceWorker;

  afterEach(() => {
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
  });

  it('does nothing in browsers without service worker support', async () => {
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: undefined,
    });

    expect(isApplicationWorkerSupported()).toBe(false);
    await expect(registerPayPlannerApplicationWorker()).resolves.toBeNull();
  });

  it('registers the shared application worker once at the root scope', async () => {
    const registration = { active: {} } as ServiceWorkerRegistration;
    const register = vi.fn().mockResolvedValue(registration);
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { register },
    });

    await expect(registerPayPlannerApplicationWorker()).resolves.toBe(registration);
    expect(register).toHaveBeenCalledWith(PAYPLANNER_APPLICATION_WORKER_PATH, {
      scope: PAYPLANNER_APPLICATION_WORKER_SCOPE,
    });
  });
});
