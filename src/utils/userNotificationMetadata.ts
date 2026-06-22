type NotificationMetadataSource = {
  metadataJson?: string | null;
};

function readPositiveInteger(value: unknown): number | null {
  if (typeof value === 'number' && Number.isInteger(value) && value > 0) {
    return value;
  }

  if (typeof value === 'string') {
    const parsed = Number.parseInt(value, 10);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
  }

  return null;
}

export function parseUserNotificationMetadata(metadataJson?: string | null): Record<string, unknown> {
  if (!metadataJson?.trim()) {
    return {};
  }

  try {
    const parsed = JSON.parse(metadataJson) as unknown;
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? parsed as Record<string, unknown>
      : {};
  } catch {
    return {};
  }
}

export function getClientCaseDocumentIdFromNotification(notification: NotificationMetadataSource): number | null {
  const metadata = parseUserNotificationMetadata(notification.metadataJson);
  return readPositiveInteger(metadata.clientCaseDocumentId);
}
