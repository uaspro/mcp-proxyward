import { useEffect, useState } from 'react'

export type Theme = 'light' | 'dark'

const storageKey = 'proxyward.dashboard.theme'

export function usePersistedTheme() {
  const [theme, setTheme] = useState<Theme>(() => readStoredTheme())

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme)
    try {
      window.localStorage.setItem(storageKey, theme)
    } catch {
      return
    }
  }, [theme])

  return [theme, setTheme] as const
}

function readStoredTheme(): Theme {
  try {
    const stored = window.localStorage.getItem(storageKey)
    return stored === 'dark' || stored === 'light' ? stored : 'light'
  } catch {
    return 'light'
  }
}
