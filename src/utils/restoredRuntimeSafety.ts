export function parseRestoredRuntimeMode(value?: string): boolean {
  if (value === undefined || value === '' || value === 'false') return false;
  if (value === 'true') return true;
  throw new Error('Некорректная настройка изолированного пробного режима.');
}

export function requireRestoredInstanceId(value?: string): string {
  if (!value || !/^[0-9a-f]{32}$/.test(value) || /^0+$/.test(value)) {
    throw new Error('Требуется идентификатор подготовленной восстановленной среды.');
  }
  return value;
}

export function assertRestoredBrowserHost(host: string | boolean | undefined): void {
  const parts = typeof host === 'string' ? host.split('.') : [];
  if (host === '::1' || (parts.length === 4 && parts[0] === '127' &&
    parts.every(part => /^\d+$/.test(part) && Number(part) <= 255))) return;
  throw new Error('Пробный интерфейс должен слушать только явный loopback IP.');
}

export function resolveRestoredApiUrl(value?: string, pageProtocol?: string): string {
  let url: URL;
  try {
    if (!value?.trim()) throw new Error();
    url = new URL(value.trim());
  } catch {
    throw new Error('Для пробного режима требуется явный локальный адрес API.');
  }
  const ipv4 = url.hostname.split('.');
  const loopback = url.hostname === '[::1]' ||
    (ipv4.length === 4 && ipv4[0] === '127' && ipv4.every(part => /^\d+$/.test(part) && Number(part) <= 255));
  if (!loopback || !['http:', 'https:'].includes(url.protocol) || url.username || url.password ||
    url.search || url.hash || !['/api', '/api/'].includes(url.pathname)) {
    throw new Error('Пробный режим допускает только HTTP/HTTPS API на loopback IP без дополнительных параметров.');
  }
  if (pageProtocol === 'https:' && url.protocol === 'http:') {
    throw new Error('Для HTTPS-страницы пробной CRM требуется локальный HTTPS API.');
  }
  return `${url.origin}/api`;
}
