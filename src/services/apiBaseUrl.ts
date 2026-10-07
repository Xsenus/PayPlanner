import { parseRestoredRuntimeMode, resolveRestoredApiUrl } from '../utils/restoredRuntimeSafety';

const LOCAL_API_BASE_URL = 'http://localhost:5080/api';
const PRODUCTION_API_BASE_URL = 'https://api.elitpravo.com/api';

type ResolveApiBaseUrlOptions = {
  configuredUrl?: string;
  isProduction?: boolean;
  pageProtocol?: string;
  restoredRuntime?: boolean;
};

export function resolveApiBaseUrl(options: ResolveApiBaseUrlOptions = {}): string {
  const isProduction = options.isProduction ?? import.meta.env.PROD;
  const pageProtocol =
    options.pageProtocol ?? (typeof window === 'undefined' ? undefined : window.location.protocol);
  const fallbackUrl = isProduction ? PRODUCTION_API_BASE_URL : LOCAL_API_BASE_URL;
  const configuredUrl = (options.configuredUrl ?? import.meta.env.VITE_API_URL)?.trim();
  const restoredRuntime = options.restoredRuntime ?? parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION);
  if (restoredRuntime) return resolveRestoredApiUrl(configuredUrl, pageProtocol);
  const apiUrl = configuredUrl || fallbackUrl;

  if (isProduction && pageProtocol === 'https:' && apiUrl.startsWith('http://')) {
    return PRODUCTION_API_BASE_URL;
  }

  return apiUrl;
}

export const API_BASE_URL = resolveApiBaseUrl();
