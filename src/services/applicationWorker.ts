import { parseRestoredRuntimeMode } from '../utils/restoredRuntimeSafety';

export const PAYPLANNER_APPLICATION_WORKER_PATH = '/payplanner-worker.js';
export const PAYPLANNER_APPLICATION_WORKER_SCOPE = '/';
const PAYPLANNER_APPLICATION_WORKER_READY_TIMEOUT_MS = 4_000;

export function isApplicationWorkerSupported() {
  return typeof window !== 'undefined' && Boolean(navigator.serviceWorker);
}

export async function registerPayPlannerApplicationWorker() {
  if (parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION)) return null;
  if (!isApplicationWorkerSupported()) {
    return null;
  }

  const registration = await navigator.serviceWorker.register(
    PAYPLANNER_APPLICATION_WORKER_PATH,
    { scope: PAYPLANNER_APPLICATION_WORKER_SCOPE },
  );

  if (registration.active) {
    return registration;
  }

  return (await waitForReadyApplicationWorker(registration)) ?? registration;
}

async function waitForReadyApplicationWorker(registration: ServiceWorkerRegistration) {
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
        window.setTimeout(() => resolve(null), PAYPLANNER_APPLICATION_WORKER_READY_TIMEOUT_MS);
      }),
    ]);
  } catch {
    return null;
  }
}
