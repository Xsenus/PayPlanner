const PUBLIC_MESSENGER_DOCUMENT_PATH = /^\/public\/document\/([0-9a-f-]{36})\/?$/i;

/** Возвращает публичный идентификатор документа только для точного безопасного маршрута. */
export function getPublicMessengerDocumentId(pathname = window.location.pathname) {
  return pathname.match(PUBLIC_MESSENGER_DOCUMENT_PATH)?.[1] ?? '';
}

/** Проверяет, что текущая страница предназначена для одноразового документа. */
export function isPublicMessengerDocumentPath(pathname = window.location.pathname) {
  return Boolean(getPublicMessengerDocumentId(pathname));
}
