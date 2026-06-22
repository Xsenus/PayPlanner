self.addEventListener('push', (event) => {
  let payload = {};

  try {
    payload = event.data ? event.data.json() : {};
  } catch {
    payload = {
      title: 'PayPlanner CRM',
      body: event.data ? event.data.text() : '',
    };
  }

  const title = payload.title || 'PayPlanner CRM';
  const notificationTag = payload.notificationId
    ? `payplanner-notification-${payload.notificationId}`
    : payload.eventKey || undefined;
  const notificationTimestamp = Date.parse(payload.createdAt || '');
  const options = {
    body: payload.body || 'Новое уведомление в CRM',
    icon: '/logo.png',
    badge: '/logo.png',
    tag: notificationTag,
    renotify: Boolean(notificationTag),
    timestamp: Number.isFinite(notificationTimestamp) ? notificationTimestamp : Date.now(),
    data: {
      url: payload.url || '/notifications',
      notificationId: payload.notificationId,
      objectType: payload.objectType,
      objectId: payload.objectId,
      taskJournalItemId: payload.taskJournalItemId,
    },
  };

  event.waitUntil(self.registration.showNotification(title, options));
});

function resolveNotificationTargetUrl(rawUrl) {
  const fallbackUrl = new URL('/notifications', self.location.origin);
  const targetUrl = new URL(rawUrl || fallbackUrl.pathname, self.location.origin);

  return targetUrl.origin === self.location.origin ? targetUrl.href : fallbackUrl.href;
}

self.addEventListener('notificationclick', (event) => {
  event.notification.close();

  const targetUrl = resolveNotificationTargetUrl(event.notification.data?.url);

  event.waitUntil((async () => {
    const clientsList = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const sameOriginClient = clientsList.find((client) => new URL(client.url).origin === self.location.origin);

    if (sameOriginClient) {
      if ('navigate' in sameOriginClient) {
        await sameOriginClient.navigate(targetUrl);
      }
      await sameOriginClient.focus();
      return;
    }

    await self.clients.openWindow(targetUrl);
  })());
});
