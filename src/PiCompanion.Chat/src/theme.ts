export type ThemePreference = 'dark' | 'light' | 'system'
export type ResolvedTheme = 'dark' | 'light'

export const systemThemeQuery = '(prefers-color-scheme: light)'

export function resolveTheme(
  preference: ThemePreference,
  systemPrefersLight: boolean,
): ResolvedTheme {
  if (preference === 'system') return systemPrefersLight ? 'light' : 'dark'
  return preference
}

export function applyTheme(theme: ResolvedTheme, root: HTMLElement = document.documentElement) {
  root.dataset.theme = theme
  root.style.colorScheme = theme
}

export function clearTheme(root: HTMLElement = document.documentElement) {
  delete root.dataset.theme
  root.style.removeProperty('color-scheme')
}
