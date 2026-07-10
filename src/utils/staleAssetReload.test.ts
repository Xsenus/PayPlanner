import { describe, expect, it } from 'vitest';

import { isStaleAssetLoadError } from './staleAssetReload';

describe('isStaleAssetLoadError', () => {
  it('detects stale dynamic import errors from deployed chunks', () => {
    expect(isStaleAssetLoadError(new Error('Failed to fetch dynamically imported module: /assets/TaskJournal-old.js'))).toBe(true);
    expect(isStaleAssetLoadError('Importing a module script failed.')).toBe(true);
    expect(isStaleAssetLoadError({ message: 'error loading dynamically imported module' })).toBe(true);
  });

  it('ignores ordinary application errors', () => {
    expect(isStaleAssetLoadError(new Error('Validation failed'))).toBe(false);
    expect(isStaleAssetLoadError({ message: 'HTTP 403 Forbidden' })).toBe(false);
    expect(isStaleAssetLoadError(null)).toBe(false);
  });
});
