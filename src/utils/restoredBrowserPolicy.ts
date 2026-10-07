import { assertRestoredBrowserHost, resolveRestoredApiUrl } from './restoredRuntimeSafety';

// Resource policy, not an OS network firewall or a restriction on user navigation.
export function createRestoredBrowserPolicy(apiUrl: string, options: { inlineScriptHashes?: string[]; devWebSocketUrl?: string } = {}): string {
  const origin = new URL(resolveRestoredApiUrl(apiUrl)).origin;
  const websocket = origin.replace(/^http/, 'ws');
  const hashes = options.inlineScriptHashes ?? [];
  if (hashes.some(hash => !/^[A-Za-z0-9+/]{43}=$/.test(hash))) throw new Error('Некорректная контрольная сумма служебного скрипта.');
  let devWebsocket = '';
  if (options.devWebSocketUrl) {
    let url: URL;
    try { url = new URL(options.devWebSocketUrl); }
    catch { throw new Error('Некорректный локальный адрес HMR.'); }
    assertRestoredBrowserHost(url.hostname.replace(/^\[|\]$/g, ''));
    if (!['ws:', 'wss:'].includes(url.protocol) || url.username || url.password || url.search || url.hash || url.pathname !== '/') {
      throw new Error('Некорректный локальный адрес HMR.');
    }
    devWebsocket = ` ${url.origin}`;
  }
  return [
    "default-src 'none'",
    `script-src 'self'${hashes.map(hash => ` 'sha256-${hash}'`).join('')}`,
    "style-src 'self' 'unsafe-inline'",
    "font-src 'self' data:",
    `img-src 'self' data: blob: ${origin}`,
    `connect-src 'self' ${origin} ${websocket}${devWebsocket}`,
    `media-src 'self' blob: ${origin}`,
    `frame-src 'self' blob: ${origin}`,
    "worker-src 'self' blob:",
    "manifest-src 'self'",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'self'",
  ].join('; ');
}
