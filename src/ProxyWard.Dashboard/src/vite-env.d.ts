/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_PROXYWARD_API_BASE_URL?: string
  readonly VITE_PROXYWARD_ADMIN_TOKEN?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
