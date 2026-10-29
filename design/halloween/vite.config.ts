import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { viteSingleFile } from 'vite-plugin-singlefile';
export default defineConfig({ plugins: [react(), tailwindcss(), viteSingleFile()], base: './', optimizeDeps: { entries: ['index.html'] }, build: { assetsInlineLimit: 100000000, cssCodeSplit: false }, server: { port: 5175, strictPort: true } });
