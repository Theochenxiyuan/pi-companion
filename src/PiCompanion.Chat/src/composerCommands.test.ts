import { describe, expect, it } from 'vitest'
import {
  isComposerCommandName,
  literalComposerMessage,
  parseComposerInvocation,
} from './composerCommands'

describe('composer commands', () => {
  it('parses app commands and preserves optional arguments', () => {
    expect(parseComposerInvocation('/compact 保留关键决策')).toEqual({
      kind: 'command',
      name: 'compact',
      args: '保留关键决策',
    })
    expect(isComposerCommandName('compact')).toBe(true)
    expect(isComposerCommandName('unknown')).toBe(false)
  })

  it('parses one explicit skill invocation and rejects incomplete syntax', () => {
    expect(parseComposerInvocation('/skill:find-skills 查找前端技能')).toEqual({
      kind: 'skill',
      name: 'find-skills',
      args: '查找前端技能',
    })
    expect(parseComposerInvocation('/skill:')).toBeNull()
  })

  it('uses a double slash to escape regular text', () => {
    expect(parseComposerInvocation('//compact')).toBeNull()
    expect(literalComposerMessage('//compact')).toBe('/compact')
    expect(literalComposerMessage('普通消息')).toBe('普通消息')
  })
})
