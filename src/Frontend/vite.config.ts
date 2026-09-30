import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

const nm = (p: string) => fileURLToPath(new URL(`./node_modules/${p}`, import.meta.url));

// The build goes straight into the ASP.NET Core wwwroot so there is one deployable folder.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../Backend/wwwroot', emptyOutDir: true },
  server: {
    port: 5173,
    fs: { allow: ['../..'] }, // lets vitest load tests from tests/Frontend.Tests
    // Local development: the Vite dev server proxies API and sign-in calls to `dotnet run`.
    proxy: {
      '/api': 'http://localhost:5080',
      '/signin-oidc': 'http://localhost:5080',
      '/signout-callback-oidc': 'http://localhost:5080',
    },
  },
  resolve: {
    // Tests live in tests/Frontend.Tests, outside this folder, so point their imports at this node_modules.
    alias: {
      '@testing-library/react': nm('@testing-library/react'),
      '@testing-library/user-event': nm('@testing-library/user-event'),
      '@testing-library/jest-dom': nm('@testing-library/jest-dom'),
      'react-router-dom': nm('react-router-dom'),
      'react-dom': nm('react-dom'),
      react: nm('react'),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    include: ['../../tests/Frontend.Tests/**/*.test.{ts,tsx}'],
    setupFiles: ['./test-setup.ts'],
    css: false,
  },
});
