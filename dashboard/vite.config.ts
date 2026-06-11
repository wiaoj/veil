import { defineConfig } from 'vite'
import { devtools } from '@tanstack/devtools-vite'

import { tanstackStart } from '@tanstack/react-start/plugin/vite'

import viteReact from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

const config = defineConfig({
  resolve: { tsconfigPaths: true },
  plugins: [devtools(), tailwindcss(), tanstackStart(), viteReact()],
  server: {
    // Same-origin in dev: the dashboard calls /v1/* and Vite forwards to
    // the control plane, so the API needs no CORS setup.
    proxy: {
      '/v1': 'http://localhost:5210',
    },
  },
})

export default config
