// Minimal compatibility surface used by pi-web-search. Provider credentials
// resolved by Pi's model registry remain authoritative; this mirrors Pi AI's
// environment fallback only for the official providers enabled by Companion.
export function getEnvApiKey(
  provider: string,
  environment: Record<string, string | undefined> = process.env,
) {
  const names = provider === 'anthropic'
    ? ['ANTHROPIC_OAUTH_TOKEN', 'ANTHROPIC_API_KEY']
    : provider === 'openai'
      ? ['OPENAI_API_KEY']
      : provider === 'google'
        ? ['GEMINI_API_KEY']
        : []

  for (const name of names) {
    const value = environment[name]?.trim()
    if (value) return value
  }
  return undefined
}
