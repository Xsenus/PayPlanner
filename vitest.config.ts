import { configDefaults, defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'jsdom',
    exclude: [...configDefaults.exclude, '.codex-build/**', 'dist/**'],
    setupFiles: './src/test/setup.ts',
    testTimeout: 15000,
    hookTimeout: 15000,
  },
});
