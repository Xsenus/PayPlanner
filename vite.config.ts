import legacy from '@vitejs/plugin-legacy';
import { defineConfig, loadEnv, type ConfigEnv, type ResolvedConfig, type UserConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { createHash } from 'node:crypto';
import { assertRestoredBrowserHost, parseRestoredRuntimeMode, requireRestoredInstanceId, resolveRestoredApiUrl } from './src/utils/restoredRuntimeSafety';
import { createRestoredBrowserPolicy } from './src/utils/restoredBrowserPolicy';

function splitVendorChunk(id: string): string | undefined {
  const normalizedId = id.replace(/\\/g, '/');

  if (!normalizedId.includes('/node_modules/')) {
    return undefined;
  }

  if (normalizedId.includes('/@microsoft/signalr/')) {
    return 'vendor-signalr';
  }

  if (normalizedId.includes('/recharts/')) {
    return 'vendor-recharts';
  }

  if (normalizedId.includes('/d3-')) {
    return 'vendor-d3';
  }

  if (
    normalizedId.includes('/framer-motion/') ||
    normalizedId.includes('/motion-dom/') ||
    normalizedId.includes('/motion-utils/')
  ) {
    return 'vendor-motion';
  }

  if (normalizedId.includes('/lucide-react/')) {
    return 'vendor-icons';
  }

  if (
    normalizedId.includes('/react/') ||
    normalizedId.includes('/react-dom/') ||
    normalizedId.includes('/scheduler/')
  ) {
    return 'vendor-react';
  }

  return undefined;
}

export function isKnownSignalRAnnotationWarning(warning: unknown): boolean {
  if (!warning || typeof warning !== 'object') {
    return false;
  }

  const rollupWarning = warning as { code?: string; id?: string; message?: string };
  const normalizedId = rollupWarning.id?.replace(/\\/g, '/') ?? '';

  return (
    rollupWarning.code === 'INVALID_ANNOTATION' &&
    normalizedId.includes('/@microsoft/signalr/dist/esm/Utils.js') &&
    Boolean(
      rollupWarning.message?.includes('contains an annotation') &&
      rollupWarning.message.includes('cannot interpret'),
    )
  );
}

export function validateRestoredBrowserConfig(config: Pick<ResolvedConfig, 'server' | 'preview' | 'env'>): void {
  if (!parseRestoredRuntimeMode(config.env.VITE_RESTORED_RUNTIME_ISOLATION)) {
    throw new Error('Изоляция пробного интерфейса должна оставаться включённой.');
  }
  resolveRestoredApiUrl(config.env.VITE_API_URL);
  requireRestoredInstanceId(config.env.VITE_RESTORED_RUNTIME_INSTANCE_ID);
  assertRestoredBrowserHost(config.server.host);
  assertRestoredBrowserHost(config.preview.host);
  for (const server of [config.server, config.preview]) {
    if (server.allowedHosts === true || Object.keys(server.proxy ?? {}).length > 0) {
      throw new Error('Пробный интерфейс не допускает произвольные hosts и proxy.');
    }
    if (Array.isArray(server.allowedHosts)) server.allowedHosts.forEach(assertRestoredBrowserHost);
  }
  if (typeof config.server.hmr === 'object' && config.server.hmr.host !== undefined) {
    assertRestoredBrowserHost(config.server.hmr.host);
  }
}

export function createPayPlannerViteConfig(env: Record<string, string | undefined>): UserConfig {
  const isolated = parseRestoredRuntimeMode(env.VITE_RESTORED_RUNTIME_ISOLATION);
  if (isolated) {
    resolveRestoredApiUrl(env.VITE_API_URL);
    requireRestoredInstanceId(env.VITE_RESTORED_RUNTIME_INSTANCE_ID);
  }
  let policy = isolated ? createRestoredBrowserPolicy(env.VITE_API_URL!) : undefined;
  let isPreview = false;
  return {
  plugins: [react(), legacy({ renderLegacyChunks: false, modernPolyfills: true,
    modernTargets: ['Chrome >= 97', 'Edge >= 97', 'Firefox >= 96', 'Safari >= 15.2', 'iOS >= 15.2'],
  }), ...(isolated ? [{
    name: 'payplanner-restored-runtime-guard',
    config(_config: UserConfig, configEnv: ConfigEnv) {
      isPreview = configEnv.isPreview === true;
    },
    configResolved(config: ResolvedConfig) {
      validateRestoredBrowserConfig(config);
      policy = configureRestoredBrowserPolicy(config, isPreview);
    },
    transformIndexHtml: {
      order: 'post' as const,
      handler: () => [{ tag: 'meta', attrs: { 'http-equiv': 'Content-Security-Policy', content: policy! }, injectTo: 'head-prepend' as const }],
    },
  }] : [])],
  server: {
    host: isolated ? '127.0.0.1' : true,
    port: 5173,
    strictPort: true,
    headers: policy ? { 'Content-Security-Policy': `${policy}; frame-ancestors 'none'` } : undefined,
  },
  preview: isolated ? {
    host: '127.0.0.1', strictPort: true,
    headers: { 'Content-Security-Policy': `${policy}; frame-ancestors 'none'` },
  } : undefined,
  optimizeDeps: {
    // The icon package exports over 1500 modules. Pre-bundle it instead of
    // making the browser request the entire export graph on a cold dev start.
    include: ['lucide-react'],
  },
  build: {
    cssTarget: ['chrome97', 'edge97', 'firefox96', 'safari15.2', 'ios15.2'],
    rollupOptions: {
      onwarn(warning, warn) {
        if (isKnownSignalRAnnotationWarning(warning)) {
          return;
        }

        warn(warning);
      },
      output: {
        manualChunks: splitVendorChunk,
      },
    },
  },
  };
}

export function configureRestoredBrowserPolicy(config: ResolvedConfig, isPreview = false): string {
  const apiUrl = resolveRestoredApiUrl(config.env.VITE_API_URL);
  let policy = createRestoredBrowserPolicy(apiUrl);
  if (config.command === 'serve' && !isPreview) {
    if (!/^\/[A-Za-z0-9_/-]*$/.test(config.base) || config.server.strictPort !== true) {
      throw new Error('Пробный dev-сервер требует локальный base path и фиксированный порт.');
    }
    const hmr = typeof config.server.hmr === 'object' ? config.server.hmr : {};
    const host = hmr.host ?? config.server.host;
    assertRestoredBrowserHost(host);
    const port = hmr.clientPort ?? hmr.port ?? config.server.port;
    if (!Number.isInteger(port) || !port || port < 1 || port > 65535) throw new Error('Некорректный порт локального HMR.');
    const protocol = hmr.protocol ?? (config.server.https ? 'wss' : 'ws');
    if (protocol !== 'ws' && protocol !== 'wss') throw new Error('Некорректный протокол локального HMR.');
    const address = typeof host === 'string' && host.includes(':') ? `[${host}]` : host;
    const hash = createHash('sha256').update(react.preambleCode.replace('__BASE__', config.base)).digest('base64');
    policy = createRestoredBrowserPolicy(apiUrl, { inlineScriptHashes: [hash], devWebSocketUrl: `${protocol}://${address}:${port}` });
  }
  config.server.headers = { ...config.server.headers, 'Content-Security-Policy': `${policy}; frame-ancestors 'none'` };
  config.preview.headers = { ...config.preview.headers, 'Content-Security-Policy': `${policy}; frame-ancestors 'none'` };
  return policy;
}

export default defineConfig(({ mode }) => createPayPlannerViteConfig(loadEnv(mode, process.cwd(), 'VITE_')));
