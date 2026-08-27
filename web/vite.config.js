import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The API serves the built files in production, so build straight into its
// wwwroot. In development Vite serves the app and proxies the API calls, which
// keeps everything same-origin and avoids CORS entirely.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/terms': 'http://localhost:5099',
      '/courses': 'http://localhost:5099',
    },
  },
})
