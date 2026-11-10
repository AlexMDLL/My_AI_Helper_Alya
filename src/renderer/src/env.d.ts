import type {
  BackendStatus,
  ChatEvent,
  ChatMessage,
  MemoryFact,
  Settings,
  ToolConfirmRequest,
  VoiceEvent
} from '../../shared/types'

export interface PetApi {
  getSettings(): Promise<Settings>
  saveSettings(patch: Partial<Settings>): Promise<Settings>
  getHistory(): Promise<ChatMessage[]>
  clearHistory(): Promise<boolean>
  memoryList(): Promise<MemoryFact[]>
  memoryAdd(text: string): Promise<MemoryFact | null>
  memoryDelete(id: string): Promise<MemoryFact[]>
  memoryClear(): Promise<void>
  memoryRebuild(): Promise<{ added: number; facts: MemoryFact[] }>
  getSprite(name: string): Promise<string | null>
  detectCity(): Promise<string | null>
  sendMessage(id: string, text: string): void
  abort(id: string): void
  onChatEvent(callback: (event: ChatEvent) => void): () => void
  onToggleChat(callback: () => void): () => void
  onOpenSettings(callback: () => void): () => void
  onAsk(callback: (text: string) => void): () => void
  onCare(callback: (text: string) => void): () => void
  onSettingsSync(callback: (settings: Settings) => void): () => void
  onGesture(callback: (name: string) => void): () => void
  onVoiceEvent(callback: (event: VoiceEvent) => void): () => void
  voiceSnapshot(): Promise<VoiceEvent | null>
  listen(): void
  onTtsState(callback: (speaking: boolean) => void): () => void
  speak(text: string, emotion?: string): void
  stopSpeech(): void
  previewVoice(payload: { text: string; speaker?: string; model?: string; pitch?: number }): void
  setChatSize(open: boolean): void
  testAnimation(): void
  vtsStatus(): Promise<{ connected: boolean; authenticated: boolean; message: string }>
  vtsConnect(): Promise<{ connected: boolean; authenticated: boolean; message: string }>
  vtsExpression(file: string, active: boolean): Promise<boolean>
  vtsHotkey(name: string): Promise<boolean>
  vtsHotkeys(): Promise<string[]>
  bakeAnimations(): Promise<boolean>
  animationScenes(): Promise<Array<{ name: string; count: number }>>
  animationFrames(scene: string): Promise<string[]>
  animationMeta(): Promise<Record<string, number>>
  recordFromVts(seconds: number, fps: number): Promise<{ scene: string; frames: number }>
  onBakeProgress(callback: (payload: { scene: string; index: number; total: number }) => void): () => void
  onToolConfirm(callback: (request: ToolConfirmRequest) => void): () => void
  answerTool(id: string, allow: boolean): void
  onBackdrop(callback: (color: { r: number; g: number; b: number }) => void): () => void
  setInteractive(interactive: boolean): void
  dragStart(x: number, y: number): void
  dragMove(x: number, y: number): void
  dragEnd(): void
  quit(): void
  hide(): void
  checkBackend(config: Settings): Promise<BackendStatus>
  openExternal(url: string): Promise<void>
}

declare global {
  interface Window {
    pet: PetApi
  }
}

export {}
