/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { VitePWA } from 'vite-plugin-pwa'

// The API serves the built app, so there is one address to share. Building writes into the API's wwwroot.
// While developing (npm run dev) the API is reached through the proxy below, so nothing needs CORS.
const apiTarget = process.env.FLOWCAPTURE_API ?? 'http://localhost:5199'

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['icons/*.png'],
      manifest: {
        name: 'bbflowbuild',
        short_name: 'bbflowbuild',
        description: 'Capture department workflows and turn them into requirement documents.',
        theme_color: '#1b2430',
        background_color: '#ffffff',
        display: 'standalone',
        start_url: '/',
        scope: '/',
        icons: [
          { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png' },
          { src: '/icons/icon-512-maskable.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        // the app itself works from the cache; data always comes from the server
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [/^\/api\//, /^\/health/, /^\/swagger/, /^\/hangfire/],
        // The app itself is saved when it is installed. The diagram library is split into dozens of lazy pieces (one per diagram type), most of
        // which are never used, so those are saved only when they are first needed (the runtime rule below).
        globPatterns: ['index.html', 'assets/index-*.{js,css}', 'icons/*.png'],
        runtimeCaching: [{
          urlPattern: ({ url }) => url.pathname.startsWith('/assets/'),
          handler: 'CacheFirst',
          options: { cacheName: 'lazy-assets', expiration: { maxEntries: 120 } },
        }],
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024,
      },
    }),
  ],
  build: {
    outDir: '../src/FlowCapture.Api/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 800,
  },
  server: {
    host: true,
    port: 5173,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
      '/health': { target: apiTarget, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    exclude: ['e2e/**', 'node_modules/**'],
    // screens are typed into like a person would: on a busy machine a few of them need more than the default 5 seconds
    testTimeout: 20_000,
  },
})
