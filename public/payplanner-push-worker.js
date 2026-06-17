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
  const options = {
    body: payload.body || 'Новое уведомление в CRM',
    icon: '/logo.png',
    badge: '/logo.png',
    tag: payload.eventKey || payload.notificationId || undefined,
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

self.addEventListener('notificationclick', (event) => {
  event.notification.close();

  const targetUrl = new URL(event.notification.data?.url || '/notifications', self.location.origin).href;

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
