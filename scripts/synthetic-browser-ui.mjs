import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { fingerprintBrowserInputs, getOrBuildBrowserArtifact } from './browser-ui-artifact-cache.mjs';

export const browserRuntimePath = '/__payplanner_browser_runtime__.js';
const runtimeGlobal = '__PAYPLANNER_SYNTHETIC_BROWSER_RUNTIME__';

export function validateSyntheticApi(value) {
  const api = new URL(value);
  assert.equal(api.protocol, 'http:');
  assert.equal(api.hostname, '127.0.0.1');
  assert.ok(api.port && Number(api.port) > 0 && Number(api.port) < 65536, 'A concrete loopback API port is required.');
  assert.ok(!api.username && !api.password && !api.search && !api.hash && ['/', '/api'].includes(api.pathname), 'Use an owned API origin without credentials or extra paths.');
  return new URL('/api', api).href;
}

export function createBrowserRuntimePlugin({ api, port }) {
  const apiUrl = validateSyntheticApi(api);
  assert.ok(Number.isInteger(port) && port > 0 && port < 65536);
  const origin = `http://127.0.0.1:${port}`;
  const code = `if(location.origin!==${JSON.stringify(origin)})throw new Error('Synthetic browser runtime requires its owned loopback origin');\n`
    + `Object.defineProperty(globalThis,${JSON.stringify(runtimeGlobal)},{value:Object.freeze({apiUrl:${JSON.stringify(apiUrl)}})});\n`;
  return {
    name: 'payplanner-synthetic-browser-runtime',
    transformIndexHtml: { order: 'post', handler: () => [
      { tag: 'script', attrs: { src: browserRuntimePath }, injectTo: 'head-prepend' },
    ] },
    configurePreviewServer(server) {
      server.middlewares.use((request, response, next) => {
        if (request.url?.split('?')[0] !== browserRuntimePath) return next();
        response.setHeader('Cache-Control', 'no-store');
        response.setHeader('X-Content-Type-Options', 'nosniff');
        if (request.headers.host !== `127.0.0.1:${port}` || !['GET', 'HEAD'].includes(request.method)) {
          response.writeHead(403).end(); return;
        }
        response.setHeader('Content-Type', 'application/javascript; charset=utf-8');
        response.writeHead(200).end(request.method === 'HEAD' ? undefined : code);
      });
    },
  };
}

/** Only synthetic writable fixtures use this cache. Restored gates retain their
 * independent strict CSP/instance identity and never use this runtime override. */
export async function prepareSyntheticBrowserUi({ api, port, root = process.cwd(), cacheDir = process.env.PAYPLANNER_BROWSER_UI_CACHE }) {
  validateSyntheticApi(api);
  assert.equal(process.env.VITE_RESTORED_RUNTIME_ISOLATION, 'false', 'Synthetic UI caching must be explicitly selected by an owned fixture.');
  root = resolve(root);
  const { build, preview } = await import('vite');
  const runtimePlugin = createBrowserRuntimePlugin({ api, port });
  const previousApi = process.env.VITE_API_URL;
  const previousNodeEnv = process.env.NODE_ENV;
  // Vite's dynamic import.meta.env object must also receive this inert value;
  // the API resolver's static property is replaced by the fail-closed expression.
  process.env.VITE_API_URL = 'http://127.0.0.1:1/api';
  process.env.NODE_ENV = 'production';
  const config = {
    root, mode: 'production', logLevel: 'error',
    define: { 'import.meta.env.VITE_API_URL': `globalThis.${runtimeGlobal}.apiUrl` },
    plugins: [runtimePlugin],
  };
  try {
    const artifact = await getOrBuildBrowserArtifact({
      cacheDir: cacheDir ?? resolve(root, 'node_modules/.cache/payplanner-browser-ui'),
      qualify: () => fingerprintBrowserInputs(root),
      build: async ({ directory, outDir }) => {
        const result = await build({ ...config, envDir: directory, cacheDir: resolve(directory, 'vite-cache'),
          build: { outDir, emptyOutDir: false, rollupOptions: {
            input: { index: resolve(root, 'index.html'), 'case-retirement-fixture': resolve(root, 'scripts/case-browser-fixture-entry.mjs') },
            preserveEntrySignatures: 'strict',
          } } });
        const chunks = (Array.isArray(result) ? result : [result]).flatMap(output => output.output ?? [])
          .filter(chunk => chunk.type === 'chunk').map(chunk => ({ name: chunk.name, fileName: chunk.fileName,
            isEntry: chunk.isEntry, exports: chunk.exports, codeLength: chunk.code.length, moduleIds: chunk.moduleIds }));
        assert.ok(chunks.some(chunk => chunk.isEntry && chunk.name === 'index' && chunk.codeLength > 0), 'The real production UI entry must compile.');
        const fixture = chunks.filter(chunk => chunk.isEntry && chunk.name === 'case-retirement-fixture');
        assert.equal(fixture.length, 1, 'The real upload helper must have one compiled fixture entry.');
        assert.ok(['beginCaseDocumentUpload', 'getAuthToken'].every(name => fixture[0].exports.includes(name)), 'Preserve the real upload and authentication helpers.');
        return { chunks };
      },
    });
    console.log(`PP_BROWSER_UI_ARTIFACT cache=${artifact.cacheHit ? 'hit' : 'miss'} fingerprint=${artifact.manifest.fingerprint} files=${artifact.manifest.inputFileCount}`);
    return {
      ...artifact,
      chunks: artifact.manifest.metadata.chunks,
      startPreview: async () => {
        const priorNodeEnv = process.env.NODE_ENV;
        process.env.NODE_ENV = 'production';
        try {
          const server = await preview({ ...config, envDir: artifact.directory,
            build: { outDir: artifact.outDir }, preview: { host: '127.0.0.1', port, strictPort: true } });
          assert.equal(server.config.isProduction, true, 'The browser must qualify production React behavior.');
          assert.equal(server.config.env.DEV, false, 'The browser must not use development-only mount effects.');
          return server;
        } finally {
          if (priorNodeEnv === undefined) delete process.env.NODE_ENV; else process.env.NODE_ENV = priorNodeEnv;
        }
      },
    };
  } finally {
    if (previousApi === undefined) delete process.env.VITE_API_URL; else process.env.VITE_API_URL = previousApi;
    if (previousNodeEnv === undefined) delete process.env.NODE_ENV; else process.env.NODE_ENV = previousNodeEnv;
  }
}
