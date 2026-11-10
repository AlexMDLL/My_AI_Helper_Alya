import { randomUUID } from 'node:crypto'
import type { BrainConfig, ChatMessage, MemoryFact } from '../shared/types'
import { streamChat } from './providers'
import { JsonStore } from './store'

interface MemoryData {
  facts: MemoryFact[]
  sinceConsolidation: number
}

const CONSOLIDATION_PROMPT = `Ты — модуль долговременной памяти AI-компаньона.
Прочитай фрагмент диалога и выдели факты, которые стоит помнить о пользователе: имя, город, профессия, проекты, привычки, предпочтения, важные договорённости и даты.
Верни ТОЛЬКО JSON-массив коротких строк (до 140 символов каждая) на русском языке. Без markdown и пояснений.
Если запоминать нечего — верни [].
Пример: ["Пользователя зовут Алексей", "Живёт в Москве", "Работает над дипломом по ГОСТ"]`

export class MemoryStore {
  private readonly store: JsonStore<MemoryData>

  constructor() {
    this.store = new JsonStore<MemoryData>('memory.json', { facts: [], sinceConsolidation: 0 })
  }

  facts(): MemoryFact[] {
    return this.store.get().facts
  }

  add(raw: string): MemoryFact | null {
    let text = raw.trim().replace(/\s+/g, ' ')
    text = text
      .replace(/^(я\s+)?(запомнила|запомнил|запомни|запомнить|запомню|помни)[,:\s]+(что\s+)?/i, '')
      .trim()
    if (text) text = text.charAt(0).toUpperCase() + text.slice(1)
    if (text.length < 3 || text.length > 240) return null
    const data = this.store.get()
    const key = text.toLowerCase()
    if (data.facts.some((fact) => fact.text.toLowerCase() === key)) return null
    const fact: MemoryFact = { id: randomUUID(), text, createdAt: Date.now() }
    this.store.set({ facts: [...data.facts, fact].slice(-120) })
    return fact
  }

  remove(id: string): MemoryFact[] {
    const facts = this.store.get().facts.filter((fact) => fact.id !== id)
    this.store.set({ facts })
    return facts
  }

  clear(): void {
    this.store.set({ facts: [], sinceConsolidation: 0 })
  }

  flush(): void {
    this.store.flush()
  }

  bump(): void {
    this.store.set({ sinceConsolidation: this.store.get().sinceConsolidation + 1 })
  }

  get pending(): number {
    return this.store.get().sinceConsolidation
  }

  async consolidate(config: BrainConfig, history: ChatMessage[]): Promise<number> {
    try {
      const transcript = history
        .slice(-16)
        .map((message) => `${message.role === 'user' ? 'Пользователь' : 'Джарвис'}: ${message.content}`)
        .join('\n')

      const messages: ChatMessage[] = [
        { role: 'system', content: CONSOLIDATION_PROMPT },
        { role: 'user', content: transcript }
      ]

      let output = ''
      await streamChat({ config, messages, onDelta: (delta) => (output += delta) })

      let added = 0
      for (const fact of parseFacts(output)) {
        if (this.add(fact)) added += 1
      }
      this.store.set({ sinceConsolidation: 0 })
      if (added > 0) console.log(`[memory] запомнил фактов: ${added}`)
      return added
    } catch (error) {
      console.warn('[memory] консолидация не удалась', error)
      return 0
    }
  }

  async rebuild(config: BrainConfig, history: ChatMessage[]): Promise<number> {
    let added = 0
    const chunk = 16
    const start = Math.max(0, history.length - 80)
    for (let index = start; index < history.length; index += chunk) {
      added += await this.consolidate(config, history.slice(index, index + chunk))
    }
    return added
  }
}

function parseFacts(raw: string): string[] {
  const start = raw.indexOf('[')
  const end = raw.lastIndexOf(']')
  if (start < 0 || end <= start) return []
  try {
    const parsed: unknown = JSON.parse(raw.slice(start, end + 1))
    if (!Array.isArray(parsed)) return []
    return parsed
      .filter((item): item is string => typeof item === 'string')
      .map((item) => item.trim())
      .filter(Boolean)
  } catch {
    return []
  }
}
