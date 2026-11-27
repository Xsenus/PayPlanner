import assert from 'node:assert/strict';
import { prepareVerifiedBrowsers } from '../prepare-ci-browsers.mjs';

assert.equal(process.env.RUNNER_ENVIRONMENT, 'github-hosted');
assert.equal(process.platform, 'win32');
assert.equal(process.env.BackgroundServices__Enabled, 'false');
assert.equal(process.env.Email__Enabled, 'false');
console.log(JSON.stringify(await prepareVerifiedBrowsers()));
