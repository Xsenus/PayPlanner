// @vitest-environment node
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createServer, request } from 'node:http';
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import { afterEach, describe, it } from 'vitest';
import { fingerprintBrowserInputs, getOrBuildBrowserArtifact, hashFileTree } from './browser-ui-artifact-cache.mjs';
import { browserRuntimePath, createBrowserRuntimePlugin, prepareSyntheticBrowserUi, validateSyntheticApi } from './synthetic-browser-ui.mjs';

const directories = [];
const servers = [];
const initialEnv = { ...process.env };
afterEach(async () => {
  for (const server of servers.splice(0)) await new Promise(resolve => { server.closeAllConnections?.(); server.close(resolve); });
  for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
  for (const key of ['VITE_API_URL', 'VITE_RESTORED_RUNTIME_ISOLATION', 'VITE_CACHE_TEST_FLAG', 'CASE_RETIREMENT_PASSWORD', 'NODE_ENV']) {
    if (initialEnv[key] === undefined) delete process.env[key]; else process.env[key] = initialEnv[key];
  }
});

async function temporary() {
  const root = await mkdtemp(join(tmpdir(), 'pp-browser-artifact-test-'));
  directories.push(root);
  return root;
}

const fingerprint = value => createHash('sha256').update(value).digest('hex');
async function artifactFixture() {
  const cacheDir = await temporary();
  let version = 'source-1';
  let builds = 0;
  const qualify = async () => ({ fingerprint: fingerprint(version), fileCount: 1 });
  const build = async ({ outDir }) => {
    builds++;
    await writeFile(join(outDir, 'index.html'), 'production-' + version);
    await writeFile(join(outDir, 'entry.js'), 'real-entry-' + version);
    return { entry: 'entry.js', production: true };
  };
  return { cacheDir, qualify, build, builds: () => builds, change: () => { version += '-changed'; } };
}

describe('qualified browser artifact cache', () => {
  it('builds once across concurrent users and reuses an intact complete artifact', async () => {
    const options = await artifactFixture();
    const artifacts = await Promise.all(Array.from({ length: 6 }, () => getOrBuildBrowserArtifact({ ...options, pollMs: 5 })));
    assert.equal(options.builds(), 1);
    assert.equal(artifacts.filter(value => !value.cacheHit).length, 1);
    assert.equal(new Set(artifacts.map(value => value.outDir)).size, 1);
    assert.equal((await getOrBuildBrowserArtifact(options)).cacheHit, true);
    assert.deepEqual(await readdir(options.cacheDir), [artifacts[0].manifest.fingerprint]);
  });

  it('invalidates changed inputs and corrupted, missing, or additional output files', async () => {
    const options = await artifactFixture();
    let artifact = await getOrBuildBrowserArtifact(options);
    await writeFile(join(artifact.outDir, 'entry.js'), 'corrupted');
    artifact = await getOrBuildBrowserArtifact(options);
    assert.equal(artifact.cacheHit, false);
    await rm(join(artifact.outDir, 'entry.js'));
    artifact = await getOrBuildBrowserArtifact(options);
    assert.equal(artifact.cacheHit, false);
    await writeFile(join(artifact.outDir, 'unqualified.js'), 'unexpected');
    artifact = await getOrBuildBrowserArtifact(options);
    assert.equal(artifact.cacheHit, false);
    options.change();
    const next = await getOrBuildBrowserArtifact(options);
    assert.equal(next.cacheHit, false);
    assert.notEqual(next.directory, artifact.directory);
    assert.equal(options.builds(), 5);
  });

  it('checks manifest metadata and never publishes a failed or changing-input build', async () => {
    const options = await artifactFixture();
    let artifact = await getOrBuildBrowserArtifact(options);
    const manifest = JSON.parse(await readFile(join(artifact.directory, 'manifest.json'), 'utf8'));
    manifest.metadata.production = false;
    await writeFile(join(artifact.directory, 'manifest.json'), JSON.stringify(manifest));
    artifact = await getOrBuildBrowserArtifact(options);
    assert.equal(artifact.cacheHit, false);
    assert.equal(artifact.manifest.metadata.production, true);
    const other = await artifactFixture();
    await assert.rejects(getOrBuildBrowserArtifact({ ...other, build: async args => { await other.build(args); throw new Error('compile failed'); } }), /compile failed/);
    assert.deepEqual(await readdir(other.cacheDir), []);
    await assert.rejects(getOrBuildBrowserArtifact({ ...other, build: async args => { const result = await other.build(args); other.change(); return result; } }), /inputs changed/);
    assert.deepEqual(await readdir(other.cacheDir), []);
    assert.equal((await getOrBuildBrowserArtifact(other)).cacheHit, false);
  });

  it('waits for a live lock without stealing it or trusting partial output', async () => {
    const options = await artifactFixture();
    const { fingerprint: key } = await options.qualify();
    await mkdir(join(options.cacheDir, key, 'dist'), { recursive: true });
    await writeFile(join(options.cacheDir, key, 'dist/index.html'), 'partial');
    const lock = join(options.cacheDir, key + '.lock');
    await writeFile(lock, JSON.stringify({ pid: process.pid }));
    await assert.rejects(getOrBuildBrowserArtifact({ ...options, lockTimeoutMs: 25, pollMs: 5 }), /Timed out waiting/);
    assert.equal(options.builds(), 0);
    assert.equal(JSON.parse(await readFile(lock, 'utf8')).pid, process.pid);
    await rm(lock);
    assert.equal((await getOrBuildBrowserArtifact(options)).cacheHit, false);
  });

  it('fingerprints source, public assets, config, helper, environment and actual dependency bytes', async () => {
    const root = await temporary();
    const inputs = ['src/main.ts', 'public/icon.svg', 'vite.config.ts', 'tailwind.config.js', 'tsconfig.app.json',
      'package-lock.json', '.env.production', 'scripts/synthetic-browser-ui.mjs', 'scripts/browser-ui-artifact-cache.mjs',
      'scripts/case-browser-fixture-entry.mjs', 'node_modules/vite/dist/index.js', 'vendor/local.tgz'];
    for (const path of inputs) {
      await mkdir(join(root, path, '..'), { recursive: true });
      await writeFile(join(root, path), 'original');
    }
    let previous = await fingerprintBrowserInputs(root);
    for (const path of inputs) {
      await writeFile(join(root, path), 'changed');
      const next = await fingerprintBrowserInputs(root);
      assert.notEqual(next.fingerprint, previous.fingerprint, path);
      previous = next;
    }
    process.env.VITE_CACHE_TEST_FLAG = 'changed';
    const changed = await fingerprintBrowserInputs(root);
    assert.notEqual(changed.fingerprint, previous.fingerprint);
    process.env.VITE_API_URL = 'http://127.0.0.1:32123/api';
    process.env.CASE_RETIREMENT_PASSWORD = 'must-not-qualify-or-persist';
    await mkdir(join(root, 'node_modules/.cache'), { recursive: true });
    await writeFile(join(root, 'node_modules/.cache/generated'), 'artifact');
    assert.equal((await fingerprintBrowserInputs(root)).fingerprint, changed.fingerprint);
    assert.ok(!JSON.stringify(changed).includes('must-not-qualify-or-persist'));
    assert.equal((await hashFileTree(root, ['public'])).length, 1);
  });
});

describe('per-preview loopback runtime', () => {
  it.each(['https://127.0.0.1:5080', 'http://localhost:5080', 'http://127.0.0.2:5080', 'http://example.com:5080',
    'http://127.0.0.1', 'http://user:secret@127.0.0.1:5080', 'http://127.0.0.1:5080/api?token=private',
    'http://127.0.0.1:5080/api#fragment', 'http://127.0.0.1:5080/other'])('rejects unowned API endpoint %s', value => {
    assert.throws(() => validateSyntheticApi(value));
  });

  it('serves only the exact runtime route to its owned Host, without caching or accepting writes', async () => {
    let middleware;
    createBrowserRuntimePlugin({ api: 'http://127.0.0.1:43210', port: 43211 })
      .configurePreviewServer({ middlewares: { use: handler => { middleware = handler; } } });
    const run = (url, host, method = 'GET') => {
      let code, body, passed = false;
      const headers = {};
      const response = { setHeader: (name, value) => { headers[name] = value; }, writeHead: status => { code = status; return response; }, end: data => { body = data; } };
      middleware({ url, headers: { host }, method }, response, () => { passed = true; });
      return { code, body, passed, headers };
    };
    assert.equal(run('/some-page', '127.0.0.1:43211').passed, true);
    for (const [host, method] of [['outside.invalid', 'GET'], ['127.0.0.1:43212', 'GET'], ['127.0.0.1:43211', 'POST']])
      assert.equal(run(browserRuntimePath, host, method).code, 403);
    const result = run(browserRuntimePath, '127.0.0.1:43211');
    assert.equal(result.code, 200);
    assert.equal(result.headers['Cache-Control'], 'no-store');
    assert.throws(() => runInNewContext(result.body, { location: { origin: 'https://outside.invalid' } }), /owned loopback origin/);
    const context = { location: { origin: 'http://127.0.0.1:43211' } };
    runInNewContext(result.body, context);
    assert.equal(context.__PAYPLANNER_SYNTHETIC_BROWSER_RUNTIME__.apiUrl, 'http://127.0.0.1:43210/api');
    assert.equal(run(browserRuntimePath, '127.0.0.1:43211', 'HEAD').body, undefined);
  });

  it('builds a real production artifact once, then binds two previews to different real loopback APIs', async () => {
    const root = await temporary();
    const cacheDir = join(root, '.test-cache');
    await mkdir(join(root, 'scripts'), { recursive: true });
    await mkdir(join(root, 'src'), { recursive: true });
    await writeFile(join(root, 'package.json'), '{"type":"module"}');
    await writeFile(join(root, 'package-lock.json'), '{}');
    await writeFile(join(root, 'index.html'), '<html><head></head><body><script type="module" src="/src/entry.js"></script></body></html>');
    await writeFile(join(root, 'src/entry.js'), "globalThis.readApi = () => fetch(import.meta.env.VITE_API_URL + '/identity').then(r => r.text()); globalThis.production = import.meta.env.PROD; globalThis.development = import.meta.env.DEV;");
    await writeFile(join(root, 'scripts/case-browser-fixture-entry.mjs'), 'export const beginCaseDocumentUpload = () => {}; export const getAuthToken = () => {};');
    const listen = async server => {
      servers.push(server);
      await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
      return server.address().port;
    };
    const reservePort = async () => {
      const server = createServer();
      await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
      const port = server.address().port;
      await new Promise(resolve => server.close(resolve));
      return port;
    };
    process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
    let firstDirectory;
    for (const identity of ['first-api', 'second-api']) {
      const apiPort = await listen(createServer((req, res) => res.end(identity + ':' + req.url)));
      const port = await reservePort();
      const artifact = await prepareSyntheticBrowserUi({ api: `http://127.0.0.1:${apiPort}`, port, root, cacheDir });
      assert.equal(artifact.cacheHit, identity === 'second-api');
      if (firstDirectory) assert.equal(artifact.directory, firstDirectory); else firstDirectory = artifact.directory;
      const preview = await artifact.startPreview();
      servers.push(preview.httpServer);
      const origin = `http://127.0.0.1:${port}`;
      const html = await (await fetch(origin)).text();
      assert.ok(html.indexOf(browserRuntimePath) < html.indexOf('type="module"'), 'Runtime configuration must execute before the production entry.');
      const entry = artifact.chunks.find(chunk => chunk.name === 'index' && chunk.isEntry);
      const entryCode = await (await fetch(origin + '/' + entry.fileName)).text();
      const runtimeCode = await (await fetch(origin + browserRuntimePath)).text();
      const context = { fetch, location: { origin }, document: { createElement: () => ({ relList: { supports: () => true } }) } };
      runInNewContext(entryCode, context);
      await assert.rejects(async () => context.readApi(), /apiUrl|undefined/);
      runInNewContext(runtimeCode, context);
      assert.equal(context.production, true);
      assert.equal(context.development, false);
      assert.equal(await context.readApi(), identity + ':/api/identity');
      const forbidden = await new Promise((resolve, reject) => {
        const call = request(origin + browserRuntimePath, { headers: { host: 'outside.invalid' } }, res => { res.resume(); resolve(res.statusCode); });
        call.on('error', reject); call.end();
      });
      assert.equal(forbidden, 403);
    }
  }, 20_000);
});
