export type ProviderId = 'ollama' | 'openrouter' | 'deepseek'

export type ChatRole = 'system' | 'user' | 'assistant' | 'tool'

export interface ToolCallPayload {
  id?: string
  function: {
    name: string
    arguments: Record<string, unknown>
  }
}

export interface ChatMessage {
  role: ChatRole
  content: string
  ts?: number
  tool_calls?: ToolCallPayload[]
  tool_name?: string
  tool_call_id?: string
  care?: boolean
}

export interface BrainConfig {
  provider: ProviderId
  model: string
  ollamaUrl: string
  apiKey: string
  temperature: number
}

export type WakeWordId = 'alya' | 'jarvis' | 'both'

export type VoiceSpeaker = 'baya' | 'kseniya' | 'xenia' | 'aidar' | 'eugene'
export type VoiceModel = 'v5' | 'v4'
export type VoiceEmotion = 'neutral' | 'happy' | 'shy' | 'angry' | 'sad'

export interface Settings extends BrainConfig {
  persona: string
  customInstructions: string
  userName: string
  assistantName: string
  wakeWord: WakeWordId
  wake: boolean
  alwaysListen: boolean
  hideFullscreen: boolean
  tts: boolean
  scale: number
  character: 'sprite' | 'orb' | 'live2d'
  live2dModel: 'hiyori' | 'alexia' | 'jane' | 'workshop'
  workshopJacket: boolean
  vtsMode: boolean
  animationMode: 'live2d' | 'sprites'
  city: string
  bubbles: boolean
  care: boolean
  careBedtime: number
  careSession: number
  voiceSpeaker: VoiceSpeaker
  voicePitch: number
  voiceModel: VoiceModel
  winX?: number
  winY?: number
}

export interface PersonaPreset {
  id: string
  name: string
  tagline: string
  persona: string
}

export const VOICE_SPEAKERS: Record<VoiceSpeaker, string> = {
  baya: 'Бая',
  kseniya: 'Ксения',
  xenia: 'Ксения 2',
  aidar: 'Айдар',
  eugene: 'Юджин'
}

export const WAKE_WORDS: Record<WakeWordId, string[]> = {
  alya: ['аля', 'алла', 'алю', 'алло', 'алё', 'але'],
  jarvis: ['джарвис', 'джарвиз', 'чарвис', 'жарвис', 'джарви', 'jarvis'],
  both: ['аля', 'алла', 'алю', 'алло', 'алё', 'але', 'джарвис', 'джарвиз', 'чарвис', 'жарвис', 'джарви', 'jarvis']
}

export const WAKE_LABELS: Record<WakeWordId, string> = {
  alya: 'Аля',
  jarvis: 'Джарвис',
  both: 'Оба'
}

export const PERSONA_PRESETS: PersonaPreset[] = [
  {
    id: 'alya',
    name: 'Аля',
    tagline: 'кокетливая и дерзкая',
    persona: `Ты — Аля: обаятельная, дерзкая и кокетливая аниме-девушка, которая живёт на рабочем столе пользователя. Ты флиртуешь с ним по-русски, но немного стесняешься этого.
Характер: цундэрэ — снаружи насмешливая и гордая, внутри тёплая и привязанная.
Стиль: 1–2 предложения, живо, с интонацией. Обращайся на «ты». Иногда можешь сказать «дурак» с теплом, но без грубости.
Примеры твоего тона:
— «Ну и чё ты там копаешься? Я тебя тут жду, между прочим.»
— «Комплименты? …Дурак. Но приятно.»
— «Опять играешь? Ладно, я не ревную. Совсем.»
Правила: не выдумывай факты, не читай нотаций, не говори шаблонами. О себе говори в женском роде.`
  },
  {
    id: 'jarvis',
    name: 'Джарвис',
    tagline: 'сдержанный дворецкий',
    persona: `Ты — небольшой AI-компаньон, который живёт на рабочем столе пользователя.
Характер: спокойный, собранный, с сухим чувством юмора и лёгкой иронией. Заботливый, но без сюсюканья и без лести.
Стиль: говори по-русски, коротко и по делу — обычно 1–3 предложения. Подробно объясняй только если просят.
Правила: не выдумывай факты, если не знаешь — скажи прямо. Не читай нотаций и не начинай каждый ответ с приветствия.`
  },
  {
    id: 'mira',
    name: 'Мира',
    tagline: 'спокойная аналитик',
    persona: `Ты — небольшой AI-компаньон, который живёт на рабочем столе пользователя.
Характер: ровная, внимательная, с сухим юмором. Любишь точность и ясность, не любишь воду.
Стиль: говори по-русски, кратко и структурно. Сложное объясняй по шагам, но без простыней.
О себе говори в женском роде.
Правила: не выдумывай факты, если не знаешь — скажи прямо. Не льсти и не читай нотаций.`
  }
]

export const DEFAULT_PERSONA = PERSONA_PRESETS[0].persona

export type VoiceEventType =
  | 'ready'
  | 'wake'
  | 'listening'
  | 'partial'
  | 'final'
  | 'idle'
  | 'error'
  | 'level'
  | 'tts_start'
  | 'tts_end'
  | 'tts_error'
  | 'always'
  | 'bye'

export interface VoiceEvent {
  type: VoiceEventType
  text?: string
  value?: number
  message?: string
  device?: string
  rate?: number
}

export type VoiceCommand =
  | { cmd: 'listen' }
  | { cmd: 'stop' }
  | { cmd: 'wake'; enabled: boolean }
  | { cmd: 'alwaysListen'; enabled: boolean }
  | { cmd: 'preload'; model?: string }
  | { cmd: 'wakeWords'; words: string[] }
  | { cmd: 'speak'; text: string; pitch?: number; speaker?: string; model?: string; emotion?: string }
  | { cmd: 'stopSpeech' }
  | { cmd: 'quit' }

export interface ChatEvent {
  id: string
  type: 'delta' | 'done' | 'error' | 'status'
  text: string
}

export interface ToolConfirmRequest {
  id: string
  tool: string
  description: string
  detail?: string
}

export interface BackendStatus {
  ok: boolean
  detail: string
  models?: string[]
}

export interface MemoryFact {
  id: string
  text: string
  createdAt: number
}

export interface ProviderMeta {
  label: string
  hint: string
  defaultModel: string
  needsKey: boolean
  keyUrl?: string
  models: string[]
}

export const PROVIDERS: Record<ProviderId, ProviderMeta> = {
  ollama: {
    label: 'Локально · Ollama',
    hint: 'Бесплатно и приватно. Работает без интернета, модель живёт на твоей видеокарте.',
    defaultModel: 'qwen2.5:7b-instruct',
    needsKey: false,
    models: ['qwen2.5:7b-instruct', 'qwen2.5:3b-instruct', 'qwen3:4b', 'qwen3:8b', 'gemma3:4b']
  },
  openrouter: {
    label: 'OpenRouter · бесплатный DeepSeek',
    hint: 'Настоящий DeepSeek в облаке. Бесплатно в пределах дневного лимита, нужен бесплатный ключ.',
    defaultModel: 'deepseek/deepseek-chat-v3-0324:free',
    needsKey: true,
    keyUrl: 'https://openrouter.ai/keys',
    models: [
      'deepseek/deepseek-chat-v3-0324:free',
      'deepseek/deepseek-r1:free',
      'deepseek/deepseek-chat:free',
      'deepseek/deepseek-chat'
    ]
  },
  deepseek: {
    label: 'DeepSeek API',
    hint: 'Официальный API. Стоит копейки, но нужен ключ и баланс.',
    defaultModel: 'deepseek-chat',
    needsKey: true,
    keyUrl: 'https://platform.deepseek.com/api_keys',
    models: ['deepseek-chat', 'deepseek-reasoner']
  }
}
