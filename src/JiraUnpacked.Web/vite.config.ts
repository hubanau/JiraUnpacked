/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
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
});
