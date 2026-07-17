// @vitest-environment node

import { describe, expect, it } from 'vitest';
import { getLegalEntityImageFieldState } from './legalEntityImageFieldUtils';

describe('getLegalEntityImageFieldState', () => {
  it('hides upload controls when an image already exists', () => {
    expect(
      getLegalEntityImageFieldState({
        value: 'data:image/png;base64,abc',
        persistedValue: 'data:image/png;base64,abc',
        changed: false,
        readOnly: false,
      }),
    ).toEqual({
      hasImage: true,
      removalPending: false,
      canUpload: false,
      canRemove: true,
      showReplaceHint: true,
      showMissingPlaceholder: false,
    });
  });

  it('shows pending-removal state until the card is saved', () => {
    expect(
      getLegalEntityImageFieldState({
        value: '',
        persistedValue: 'data:image/png;base64,abc',
        changed: true,
        readOnly: false,
      }),
    ).toEqual({
      hasImage: false,
      removalPending: true,
      canUpload: true,
      canRemove: false,
      showReplaceHint: false,
      showMissingPlaceholder: false,
    });
  });

  it('shows read-only placeholder when no image exists', () => {
    expect(
      getLegalEntityImageFieldState({
        value: '',
        persistedValue: '',
        changed: false,
        readOnly: true,
      }),
    ).toEqual({
      hasImage: false,
      removalPending: false,
      canUpload: false,
      canRemove: false,
      showReplaceHint: false,
      showMissingPlaceholder: true,
    });
  });
});
