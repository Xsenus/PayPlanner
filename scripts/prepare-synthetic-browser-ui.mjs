import { prepareSyntheticBrowserUi } from './synthetic-browser-ui.mjs';

// Match the build environment of the synthetic test subprocess. No API or
// preview server is needed to qualify the content-addressed production build.
for (const name of Object.keys(process.env)) {
  if (/^(?:VITE_|BROWSERSLIST(?:_ENV|_CONFIG)?$|BABEL_ENV$)/i.test(name)) delete process.env[name];
}
delete process.env.PAYPLANNER_BROWSER_UI_CACHE;
process.env.VITE_RESTORED_RUNTIME_ISOLATION = 'false';
await prepareSyntheticBrowserUi({ api: 'http://127.0.0.1:1', port: 1 });
