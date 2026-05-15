import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [sveltekit()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5000',
      '/openapi': 'http://localhost:5000',
      '/scalar': 'http://localhost:5000',
      '/metrics': 'http://localhost:5000',
      '/health': 'http://localhost:5000',
    },
  },
});
