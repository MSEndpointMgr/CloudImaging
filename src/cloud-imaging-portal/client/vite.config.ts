import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import path from 'path';

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    fs: {
      allow: [path.resolve(__dirname, '../../..')],
    },
    proxy: {
      '/api': {
        target: process.env.VITE_API_BASE_URL ?? 'http://localhost:3000',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
  test: {
    globals: true,
    environment: 'jsdom',
    include: ['../../../tests/cloud-imaging-portal/client/**/*.{test,spec}.?(c|m)[jt]s?(x)'],
    // Tests live outside this package, so a bare specifier in a test file resolves to a
    // different module id than the same specifier inside src/, and vi.mock() then silently
    // fails to bind. Pinning them to this package's copy lets a test mock a component's
    // dependencies and assert real behaviour. Test-only: the production build is unaffected.
    // `@testing-library/react` is here for the plainer reason that Node resolution walks up
    // from the *test* file, which never reaches this package's node_modules at all.
    alias: {
      '@azure/msal-react': path.resolve(__dirname, './node_modules/@azure/msal-react'),
      '@azure/msal-browser': path.resolve(__dirname, './node_modules/@azure/msal-browser'),
      '@testing-library/react': path.resolve(__dirname, './node_modules/@testing-library/react'),
    },
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html'],
      thresholds: { lines: 80 },
    },
  },
});
