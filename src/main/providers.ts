import type { BackendStatus, BrainConfig, ChatMessage } from '../shared/types'

const OPENROUTER_BASE = 'https://openrouter.ai/api/v1'
const DEEPSEEK_BASE = 'https://api.deepseek.com'

export interface ToolCallRequest {
  id?: string
  name: string
  arguments: Record<string, unknown>
}

export interface StreamResult {
  text: string
  toolCalls: ToolCallRequest[]
}

interface StreamArgs {
  config: BrainConfig
  messages: ChatMessage[]
  onDelta: (text: string) => void
  signal?: AbortSignal
  tools?: unknown[]
}

interface OllamaChunk {
  message?: {
    content?: string
    tool_calls?: Array<{
      id?: string
      function?: { name?: string; arguments?: Record<string, unknown> | string }
    }>
  }
  error?: string
}

interface OpenAIToolDelta {
  index?: number
  id?: string
  function?: { name?: string; arguments?: string }
}

interface OpenAIChunk {
  choices?: Array<{
    delta?: { content?: string; tool_calls?: OpenAIToolDelta[] }
  }>
  error?: { message?: string }
}

export async function streamChat({
  config,
  messages,
  onDelta,
  signal,
  tools
}: StreamArgs): Promise<StreamResult> {
  if (config.provider === 'ollama') {
    return streamOllama(config, messages, onDelta, signal, tools)
  }
  return streamOpenAI(config, messages, onDelta, signal, tools)
}

function parseArguments(value: Record<string, unknown> | string | undefined): Record<string, unknown> {
  if (!value) return {}
  if (typeof value === 'string') {
    try {
      const parsed: unknown = JSON.parse(value)
      return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : {}
    } catch {
      return {}
    }
  }
  return value
}

async function streamOllama(
  config: BrainConfig,
  messages: ChatMessage[],
  onDelta: (text: string) => void,
  signal: AbortSignal | undefined,
  tools: unknown[] | undefined
): Promise<StreamResult> {
  const base = (config.ollamaUrl || 'http://127.0.0.1:11434').replace(/\/+$/, '')
  const body: Record<string, unknown> = {
    model: config.model || 'qwen2.5:3b-instruct',
    messages,
    stream: true,
    options: {
      temperature: config.temperature,
      num_ctx: 8192,
      num_predict: 700
    }
  }
  if (tools && tools.length > 0) body.tools = tools
  if (/qwen3|deepseek-r1/i.test(config.model)) body.think = false

  let response: Response
  try {
    response = await fetch(`${base}/api/chat`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
      signal
    })
  } catch {
    throw new Error(
      `Не могу достучаться до Ollama на ${base}. Проверь, что Ollama запущен (иконка в трее), или выбери другой мозг в настройках.`
    )
  }

  if (!response.ok) {
    const detail = (await response.text()).slice(0, 300)
    throw new Error(`Ollama ответил ${response.status}: ${detail}`)
  }
  if (!response.body) throw new Error('Ollama вернул пустой ответ')

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let full = ''
  const toolCalls: ToolCallRequest[] = []

  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    let newline: number
    while ((newline = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, newline).trim()
      buffer = buffer.slice(newline + 1)
      if (!line) continue

      let chunk: OllamaChunk
      try {
        chunk = JSON.parse(line) as OllamaChunk
      } catch {
        continue
      }
      if (chunk.error) throw new Error(String(chunk.error))
      const delta = chunk.message?.content ?? ''
      if (delta) {
        full += delta
        onDelta(delta)
      }
      for (const call of chunk.message?.tool_calls ?? []) {
        if (!call.function?.name) continue
        toolCalls.push({
          id: call.id,
          name: call.function.name,
          arguments: parseArguments(call.function.arguments)
        })
      }
    }
  }

  return { text: full, toolCalls }
}

function toOpenAIMessages(messages: ChatMessage[]): unknown[] {
  return messages.map((message) => {
    if (message.role === 'tool') {
      return {
        role: 'tool',
        tool_call_id: message.tool_call_id ?? message.tool_name ?? 'call',
        content: message.content
      }
    }
    if (message.role === 'assistant' && message.tool_calls && message.tool_calls.length > 0) {
      return {
        role: 'assistant',
        content: message.content || null,
        tool_calls: message.tool_calls.map((call, index) => ({
          id: call.id ?? `call_${index}`,
          type: 'function',
          function: {
            name: call.function.name,
            arguments: JSON.stringify(call.function.arguments)
          }
        }))
      }
    }
    return { role: message.role, content: message.content }
  })
}

async function streamOpenAI(
  config: BrainConfig,
  messages: ChatMessage[],
  onDelta: (text: string) => void,
  signal: AbortSignal | undefined,
  tools: unknown[] | undefined
): Promise<StreamResult> {
  if (!config.apiKey.trim()) {
    throw new Error('Нужен API-ключ. Открой настройки (шестерёнка) и вставь ключ.')
  }

  const isOpenRouter = config.provider === 'openrouter'
  const base = isOpenRouter ? OPENROUTER_BASE : DEEPSEEK_BASE
  const headers: Record<string, string> = {
    'content-type': 'application/json',
    authorization: `Bearer ${config.apiKey.trim()}`
  }
  if (isOpenRouter) {
    headers['HTTP-Referer'] = 'https://localhost'
    headers['X-Title'] = 'Jarvis Desktop'
  }

  const body: Record<string, unknown> = {
    model: config.model,
    messages: toOpenAIMessages(messages),
    stream: true,
    temperature: config.temperature
  }
  if (tools && tools.length > 0) body.tools = tools

  let response: Response
  try {
    response = await fetch(`${base}/chat/completions`, {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
      signal
    })
  } catch {
    throw new Error('Нет связи с сервером модели. Проверь интернет и попробуй ещё раз.')
  }

  if (!response.ok) {
    const detail = (await response.text()).slice(0, 300)
    throw new Error(`Сервер ответил ${response.status}: ${detail}`)
  }
  if (!response.body) throw new Error('Сервер вернул пустой ответ')

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let full = ''
  const pending = new Map<number, { id?: string; name: string; args: string }>()

  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    let boundary: number
    while ((boundary = buffer.indexOf('\n\n')) >= 0) {
      const event = buffer.slice(0, boundary)
      buffer = buffer.slice(boundary + 2)

      for (const line of event.split('\n')) {
        const trimmed = line.trim()
        if (!trimmed.startsWith('data:')) continue
        const payload = trimmed.slice(5).trim()
        if (!payload || payload === '[DONE]') continue

        let chunk: OpenAIChunk
        try {
          chunk = JSON.parse(payload) as OpenAIChunk
        } catch {
          continue
        }
        if (chunk.error?.message) throw new Error(chunk.error.message)

        const delta = chunk.choices?.[0]?.delta
        const text = delta?.content ?? ''
        if (text) {
          full += text
          onDelta(text)
        }
        for (const call of delta?.tool_calls ?? []) {
          const index = call.index ?? 0
          const entry = pending.get(index) ?? { id: call.id, name: '', args: '' }
          if (call.id) entry.id = call.id
          if (call.function?.name) entry.name += call.function.name
          if (call.function?.arguments) entry.args += call.function.arguments
          pending.set(index, entry)
        }
      }
    }
  }

  const toolCalls: ToolCallRequest[] = []
  for (const entry of pending.values()) {
    if (!entry.name) continue
    toolCalls.push({ id: entry.id, name: entry.name, arguments: parseArguments(entry.args) })
  }

  return { text: full, toolCalls }
}

export async function backendStatus(config: BrainConfig): Promise<BackendStatus> {
  if (config.provider === 'ollama') {
    const base = (config.ollamaUrl || 'http://127.0.0.1:11434').replace(/\/+$/, '')
    try {
      const response = await fetch(`${base}/api/tags`, { signal: AbortSignal.timeout(2500) })
      if (!response.ok) return { ok: false, detail: `Ollama ответил ${response.status}` }
      const data = (await response.json()) as { models?: Array<{ name?: string }> }
      const models = (data.models ?? []).map((m) => m.name ?? '').filter(Boolean)
      if (models.length === 0) {
        return { ok: true, detail: 'Ollama на связи, но моделей нет — скачай модель', models }
      }
      return { ok: true, detail: `Ollama на связи · моделей: ${models.length}`, models }
    } catch {
      return { ok: false, detail: `Ollama не отвечает на ${base}` }
    }
  }

  if (!config.apiKey.trim()) {
    return { ok: false, detail: 'Ключ не указан' }
  }

  try {
    const url = config.provider === 'openrouter' ? `${OPENROUTER_BASE}/key` : 'https://api.deepseek.com/user/balance'
    const response = await fetch(url, {
      headers: { authorization: `Bearer ${config.apiKey.trim()}` },
      signal: AbortSignal.timeout(6000)
    })
    if (response.ok) return { ok: true, detail: 'Ключ принят, сервер на связи' }
    if (response.status === 401 || response.status === 403) {
      return { ok: false, detail: 'Ключ не принят — проверь, что скопирован целиком' }
    }
    return { ok: false, detail: `Сервер ответил ${response.status}` }
  } catch {
    return { ok: false, detail: 'Нет связи с сервером модели' }
  }
}
