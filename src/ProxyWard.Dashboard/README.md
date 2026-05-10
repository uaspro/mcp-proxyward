# ProxyWard Dashboard

React, TypeScript, and Vite operator console for `ProxyWard.Management.Api`.

## Scripts

```bash
npm run dev
npm run typecheck
npm run build
npm run test:e2e
npm run preview
```

Set `VITE_PROXYWARD_API_BASE_URL` to point the dashboard at a management API instance. The default is `http://localhost:8081`.
Set `VITE_PROXYWARD_PROXY_BASE_URL` to control the MCP proxy URL shown in policy snippets. The default is `http://127.0.0.1:8080`.
