import { resolveConfig } from 'vite';
import { describe, expect, it } from 'vitest';
import { configureRestoredBrowserPolicy, createPayPlannerViteConfig, isKnownSignalRAnnotationWarning, validateRestoredBrowserConfig } from './vite.config';
import type { ResolvedConfig } from 'vite';

describe('isKnownSignalRAnnotationWarning', () => {
  it.each(['Rollup', 'Rolldown'])(
    'recognizes the SignalR pure annotation warning emitted by %s',
    (bundler) => {
      expect(isKnownSignalRAnnotationWarning({
        code: 'INVALID_ANNOTATION',
        id: 'C:\\repo\\node_modules\\@microsoft\\signalr\\dist\\esm\\Utils.js',
        message: `A comment contains an annotation that ${bundler} cannot interpret due to the position of the comment.`,
      })).toBe(true);
    },
  );

  it('does not hide annotation warnings from application code', () => {
    expect(isKnownSignalRAnnotationWarning({
      code: 'INVALID_ANNOTATION',
      id: 'C:/repo/src/App.tsx',
      message: 'A comment contains an annotation that Rolldown cannot interpret.',
    })).toBe(false);
  });
});

describe('restored browser configuration', () => {
  const env = { VITE_RESTORED_RUNTIME_ISOLATION: 'true', VITE_API_URL: 'http://127.0.0.1:5080/api', VITE_RESTORED_RUNTIME_INSTANCE_ID: 'cefe9b7e3bb64c51b3c80a9e96bb57ea' };
  const resolved = () => ({ server: { host: '127.0.0.1', allowedHosts: [] }, preview: { host: '127.0.0.1', allowedHosts: [] }, env }) as unknown as Pick<ResolvedConfig, 'server' | 'preview' | 'env'>;

  it.each([{}, env])('pre-bundles the full icon export graph without changing runtime isolation', settings => {
    const config = createPayPlannerViteConfig(settings);
    expect(config.optimizeDeps?.include).toContain('lucide-react');
    expect(config.optimizeDeps?.exclude ?? []).not.toContain('lucide-react');
  });

  it('binds both dev and preview locally only in the opt-in mode', () => {
    const config = createPayPlannerViteConfig(env);
    expect(config.server?.host).toBe('127.0.0.1');
    expect(config.preview?.host).toBe('127.0.0.1');
    expect(createPayPlannerViteConfig({}).server?.host).toBe(true);
    expect(createPayPlannerViteConfig({}).preview).toBeUndefined();
    expect(() => validateRestoredBrowserConfig(resolved())).not.toThrow();
  });
  it('fails configuration before a build when the local API is missing or remote', () => {
    expect(() => createPayPlannerViteConfig({ ...env, VITE_API_URL: undefined })).toThrow();
    expect(() => createPayPlannerViteConfig({ ...env, VITE_API_URL: 'https://api.elitpravo.com/api' })).toThrow();
  });
  it.each([undefined, '', 'invalid', '00000000000000000000000000000000'])('rejects a missing or invalid probe ID: %s', id => {
    expect(() => createPayPlannerViteConfig({ ...env, VITE_RESTORED_RUNTIME_INSTANCE_ID: id })).toThrow();
  });
  it.each([true, '0.0.0.0', 'localhost', 'fixture.invalid'])('rejects a merged public host override: %s', host => {
    const config = resolved();
    config.server.host = host;
    expect(() => validateRestoredBrowserConfig(config)).toThrow();
  });
  it('rejects preview host, arbitrary allowedHosts, proxy and remote HMR overrides', () => {
    let config = resolved();
    config.preview.host = '0.0.0.0';
    expect(() => validateRestoredBrowserConfig(config)).toThrow();
    config = resolved();
    config.server.allowedHosts = true;
    expect(() => validateRestoredBrowserConfig(config)).toThrow();
    config = resolved();
    config.preview.proxy = { '/api': 'https://fixture.invalid' };
    expect(() => validateRestoredBrowserConfig(config)).toThrow();
    config = resolved();
    config.server.hmr = { host: 'fixture.invalid' };
    expect(() => validateRestoredBrowserConfig(config)).toThrow();
  });
  it.each(['base', 'strictPort', 'port', 'protocol'])('rejects unsafe dev policy settings: %s', field => {
    const config = { ...resolved(), command: 'serve', base: '/',
      server: { ...resolved().server, strictPort: true, port: 5173 },
    } as ResolvedConfig;
    if (field === 'base') config.base = '/";fixture';
    if (field === 'strictPort') config.server.strictPort = false;
    if (field === 'port') config.server.hmr = { clientPort: 0 };
    if (field === 'protocol') config.server.hmr = { protocol: 'http' };
    expect(() => configureRestoredBrowserPolicy(config)).toThrow();
  });
  it('keeps preview scripts restricted to self without the dev preamble hash', () => {
    const config = { ...resolved(), command: 'serve' } as ResolvedConfig;
    const policy = configureRestoredBrowserPolicy(config, true);
    expect(policy.split(';').find(part => part.trim().startsWith('script-src'))?.trim()).toBe("script-src 'self'");
  });
});

describe('production browsers from early 2022', () => {
  it('resolves the actual plugin chain to old browser syntax and CSS targets', async () => {
    const config = await resolveConfig({ ...createPayPlannerViteConfig({}), configFile: false }, 'build');
    expect(config.build.target).toEqual(expect.arrayContaining(['chrome97', 'edge97', 'firefox96', 'safari15.2', 'ios15.2']));
    expect(config.build.cssTarget).toEqual(['chrome97', 'edge97', 'firefox96', 'safari15.2', 'ios15.2']);
  });
});
