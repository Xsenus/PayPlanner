const PAYPLANNER_APP_CACHE_VERSION = 'v1';
const PAYPLANNER_APP_CACHE_PREFIX = 'payplanner-app-shell-';
const PAYPLANNER_APP_CACHE_NAME = `${PAYPLANNER_APP_CACHE_PREFIX}${PAYPLANNER_APP_CACHE_VERSION}`;
const PAYPLANNER_APP_SHELL = [
  '/',
  '/index.html',
  '/manifest.webmanifest',
  '/logo.ico',
  '/logo.png',
];

function isSafeStaticAsset(requestUrl) {
  return requestUrl.origin === self.location.origin
    && requestUrl.pathname.startsWith('/assets/');
}

function isNavigationRequest(request) {
  return request.mode === 'navigate';
}

async function cacheSuccessfulResponse(request, response) {
  if (!response || !response.ok || response.type === 'opaque') {
    return response;
  }

  const cache = await caches.open(PAYPLANNER_APP_CACHE_NAME);
  await cache.put(request, response.clone());
  return response;
}

async function readStaticAsset(request) {
  const cached = await caches.match(request);
  if (cached) {
    return cached;
  }

  return cacheSuccessfulResponse(request, await fetch(request));
}

async function readNavigation(request) {
  try {
    return await cacheSuccessfulResponse('/index.html', await fetch(request));
  } catch {
    return (await caches.match('/index.html')) || Response.error();
  }
}

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(PAYPLANNER_APP_CACHE_NAME)
      .then((cache) => cache.addAll(PAYPLANNER_APP_SHELL)),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const cacheNames = await caches.keys();
    await Promise.all(
      cacheNames
        .filter((name) => (
          name.startsWith(PAYPLANNER_APP_CACHE_PREFIX)
          && name !== PAYPLANNER_APP_CACHE_NAME
        ))
        .map((name) => caches.delete(name)),
    );
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const { request } = event;
  if (request.method !== 'GET') {
    return;
  }

  const requestUrl = new URL(request.url);
  if (requestUrl.origin !== self.location.origin) {
    return;
  }

  if (isNavigationRequest(request)) {
    event.respondWith(readNavigation(request));
    return;
  }

  if (isSafeStaticAsset(requestUrl)) {
    event.respondWith(readStaticAsset(request));
  }
});
