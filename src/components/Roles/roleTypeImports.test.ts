// @vitest-environment node

import { describe, expect, it } from 'vitest';

const roleModuleSources = import.meta.glob('./*.tsx', {
  eager: true,
  import: 'default',
  query: '?raw',
}) as Record<string, string>;

describe('Roles type imports', () => {
  it('keeps modal role types outside of the screen component', () => {
    const modalImportsFromRoles = Object.entries(roleModuleSources)
      .filter(([filePath]) => /Role(Modal|PermissionsModal)\.tsx$/.test(filePath))
      .filter(([, source]) => /from\s+['"]\.\/Roles['"]/.test(source))
      .map(([filePath]) => filePath);

    expect(modalImportsFromRoles).toEqual([]);
  });
});
