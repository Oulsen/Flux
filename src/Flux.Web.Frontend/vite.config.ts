import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [svelte()],

  build: {
    // Step one of the two-step build: Vite writes the SPA into the backend's
    // wwwroot, then `dotnet build` copies it beside the executable. The folder is
    // gitignored - it is output, not source.
    outDir: '../Flux.Web/wwwroot',
    emptyOutDir: true,
  },

  server: {
    proxy: {
      // So `npm run dev` gets hot reload and a real beat feed at the same time.
      // changeOrigin is deliberately left off: rewriting the Host header while
      // leaving Origin alone is exactly the mismatch the backend's origin check
      // rejects.
      '/ws': { target: 'ws://localhost:5050', ws: true },
    },
  },
});
