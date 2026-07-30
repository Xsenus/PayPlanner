import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('manifest.webmanifest', () => {
  it('defines an installable root-scoped application with explicit branding', () => {
    const manifest = JSON.parse(
      readFileSync(resolve(process.cwd(), 'public/manifest.webmanifest'), 'utf8'),
    );

    expect(manifest).toMatchObject({
      id: '/',
      start_url: '/',
      scope: '/',
      display: 'standalone',
      lang: 'ru',
      theme_color: '#0f172a',
    });
    expect(manifest.icons).toEqual(expect.arrayContaining([
      expect.objectContaining({
        src: '/logo.png',
        sizes: '640x640',
        type: 'image/png',
      }),
    ]));
  });
});
