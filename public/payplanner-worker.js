// The root scope has exactly one service worker. Push delivery and the
// versioned application shell live in separate scripts so their lifecycles
// can evolve independently without competing registrations.
importScripts(
  '/payplanner-push-worker.js',
  '/payplanner-app-cache.js',
);
