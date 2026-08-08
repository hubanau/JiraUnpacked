import { defineConfig, type UserConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// The repo resolves two distinct 'vite' package instances: the root
// devDependency, and the one nested under vitest's own node_modules (vitest
// pins an older vite major as its peer). Vite's ambient `declare module
// 'vite' { interface UserConfig { test?: ... } }` augmentation — the one a
// plain `/// <reference types="vitest/config" />` pulls in — is physically
// declared inside vitest's package, so it resolves 'vite' from THERE and
// attaches to the nested instance, not the root one `defineConfig` below
// uses. Importing `defineConfig` from 'vitest/config' instead "fixes" that
// but reintroduces the same dual-instance mismatch one level up: `plugins`
// then expects the nested vite's `Plugin` type, which `react()`/`tailwindcss()`
// (typed against the root vite) don't structurally satisfy.
// Narrowest fix: keep root vite's `defineConfig` (so `plugins` typechecks
// against the same vite everything else here uses) and borrow only the
// `test` field's type via an indexed type import — that field doesn't
// itself reference `Plugin`, so it doesn't drag the cross-instance mismatch
// back in.
type VitestTestConfig = import('vitest/config').UserConfig['test'];

// Typed as a standalone const (not inline in the defineConfig(...) call) so
// this object literal is checked against the widened intersection type
// below rather than against defineConfig's own (root-vite-only) parameter
// type — TypeScript's excess-property check only fires for literals
// assigned/passed directly against a narrower type, not for an
// already-typed identifier passed on afterward.
const config: UserConfig & { test?: VitestTestConfig } = {
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      // Same-origin from the browser's point of view => no CORS anywhere.
      '/api': {
        target: 'https://localhost:7191',
        changeOrigin: true,
        secure: false, // the ASP.NET dev certificate is self-signed
      },
    },
  },
  build: {
    outDir: '../JiraUnpacked.Api/wwwroot',
    emptyOutDir: true,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
};

export default defineConfig(config);
