import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

function splitVendorChunk(id: string): string | undefined {
  const normalizedId = id.replace(/\\/g, '/');

  if (!normalizedId.includes('/node_modules/')) {
    return undefined;
  }

  if (normalizedId.includes('/@microsoft/signalr/')) {
    return 'vendor-signalr';
  }

  if (normalizedId.includes('/recharts/')) {
    return 'vendor-recharts';
  }

  if (normalizedId.includes('/d3-')) {
    return 'vendor-d3';
  }

  if (
    normalizedId.includes('/framer-motion/') ||
    normalizedId.includes('/motion-dom/') ||
    normalizedId.includes('/motion-utils/')
  ) {
    return 'vendor-motion';
  }

  if (normalizedId.includes('/lucide-react/')) {
    return 'vendor-icons';
  }

  if (
    normalizedId.includes('/react/') ||
    normalizedId.includes('/react-dom/') ||
    normalizedId.includes('/scheduler/')
  ) {
    return 'vendor-react';
  }

  return undefined;
}

function isKnownSignalRAnnotationWarning(warning: unknown): boolean {
  if (!warning || typeof warning !== 'object') {
    return false;
  }

  const rollupWarning = warning as { code?: string; id?: string; message?: string };
  const normalizedId = rollupWarning.id?.replace(/\\/g, '/') ?? '';

  return (
    rollupWarning.code === 'INVALID_ANNOTATION' &&
    normalizedId.includes('/@microsoft/signalr/dist/esm/Utils.js') &&
    Boolean(rollupWarning.message?.includes('contains an annotation that Rollup cannot interpret'))
  );
}

export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
    port: 5173,
    strictPort: true,
  },
  optimizeDeps: {
    exclude: ['lucide-react'],
  },
  build: {
    rollupOptions: {
      onwarn(warning, warn) {
        if (isKnownSignalRAnnotationWarning(warning)) {
          return;
        }

        warn(warning);
      },
      output: {
        manualChunks: splitVendorChunk,
      },
    },
  },
});
