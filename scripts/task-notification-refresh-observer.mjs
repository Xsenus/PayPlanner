// Runs inside the browser. Observe causal order without comparing browser and Node clocks.
export function installTaskNotificationRefreshObserver(apiOrigin) {
  window.__taskUploadNotificationRevision = 0;
  window.__taskUploadUnreadCountStartedRevision = 0;
  window.__taskUploadUnreadCountRefreshRevision = 0;
  window.addEventListener('payplanner:user-notifications-changed', () => {
    window.__taskUploadNotificationRevision++;
  });
  const originalFetch = window.fetch.bind(window);
  window.fetch = async (...args) => {
    const revision = window.__taskUploadNotificationRevision;
    let isUnreadCount = false;
    try {
      const input = args[0];
      const url = new URL(input && typeof input === 'object' && 'url' in input ? input.url : String(input), window.location.href);
      isUnreadCount = url.origin === apiOrigin && url.pathname === '/api/notifications/inbox/unread-count'
        && (args[1]?.method ?? input?.method ?? 'GET').toUpperCase() === 'GET';
    } catch {
      // Unsupported fetch inputs still go through the original browser implementation.
    }
    if (isUnreadCount) {
      window.__taskUploadUnreadCountStartedRevision = Math.max(window.__taskUploadUnreadCountStartedRevision, revision);
    }
    const response = await originalFetch(...args);
    if (isUnreadCount && response.status === 200) {
      try {
        await response.clone().arrayBuffer();
        window.__taskUploadUnreadCountRefreshRevision = Math.max(window.__taskUploadUnreadCountRefreshRevision, revision);
      } catch {
        // A failed body is not a completed refresh. Return the original response for normal app handling.
      }
    }
    return response;
  };
}
