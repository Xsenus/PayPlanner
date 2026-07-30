import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('payplanner-worker', () => {
  it('keeps push and application cache in separate modules under one root worker', () => {
    const script = readFileSync(resolve(process.cwd(), 'public/payplanner-worker.js'), 'utf8');

    expect(script).toContain("'/payplanner-push-worker.js'");
    expect(script).toContain("'/payplanner-app-cache.js'");
    expect(script.match(/importScripts/g)).toHaveLength(1);
  });
});
