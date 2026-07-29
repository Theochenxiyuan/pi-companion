export const composerCommandNames = [
  'compact',
  'model',
  'new',
  'name',
  'session',
  'settings',
  'reload',
  'stop',
  'help',
] as const

export type ComposerCommandName = typeof composerCommandNames[number]

export interface ComposerSkillOption {
  name: string
  description: string
  location: string
  manualOnly: boolean
}

export interface ComposerCommandInvocation {
  kind: 'command'
  name: string
  args: string
}

export interface ComposerSkillInvocation {
  kind: 'skill'
  name: string
  args: string
}

export type ComposerInvocation = ComposerCommandInvocation | ComposerSkillInvocation

const commandNames = new Set<string>(composerCommandNames)

export function isComposerCommandName(value: string): value is ComposerCommandName {
  return commandNames.has(value)
}

export function parseComposerInvocation(source: string): ComposerInvocation | null {
  const value = source.trim()
  if (!value.startsWith('/') || value.startsWith('//')) return null

  const skill = /^\/skill:([a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?)(?:\s+([\s\S]*))?$/u.exec(value)
  if (skill) {
    return {
      kind: 'skill',
      name: skill[1]!,
      args: skill[2]?.trim() ?? '',
    }
  }

  const command = /^\/([a-z][a-z0-9-]*)(?:\s+([\s\S]*))?$/u.exec(value)
  if (!command) return null
  return {
    kind: 'command',
    name: command[1]!,
    args: command[2]?.trim() ?? '',
  }
}

export function literalComposerMessage(source: string) {
  const trimmed = source.trim()
  return trimmed.startsWith('//') ? trimmed.slice(1) : trimmed
}
