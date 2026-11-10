import { contextBridge, ipcRenderer } from 'electron'
import type {
  BackendStatus,
  ChatEvent,
  ChatMessage,
  MemoryFact,
  Settings,
  ToolConfirmRequest,
  VoiceEvent
} from '../shared/types'

const api = {
  getSettings: (): Promise<Settings> => ipcRenderer.invoke('settings:get'),
  saveSettings: (patch: Partial<Settings>): Promise<Settings> => ipcRenderer.invoke('settings:set', patch),
  getHistory: (): Promise<ChatMessage[]> => ipcRenderer.invoke('chat:history'),
  clearHistory: (): Promise<boolean> => ipcRenderer.invoke('chat:clear'),

  memoryList: (): Promise<MemoryFact[]> => ipcRenderer.invoke('memory:list'),
  memoryAdd: (text: string): Promise<MemoryFact | null> => ipcRenderer.invoke('memory:add', text),
  memoryDelete: (id: string): Promise<MemoryFact[]> => ipcRenderer.invoke('memory:delete', id),
  memoryClear: (): Promise<void> => ipcRenderer.invoke('memory:clear'),
  memoryRebuild: (): Promise<{ added: number; facts: MemoryFact[] }> => ipcRenderer.invoke('memory:rebuild'),

  getSprite: (name: string): Promise<string | null> => ipcRenderer.invoke('character:sprite', name),
  detectCity: (): Promise<string | null> => ipcRenderer.invoke('geo:detect'),
  sendMessage: (id: string, text: string): void => ipcRenderer.send('chat:send', { id, text }),
  abort: (id: string): void => ipcRenderer.send('chat:abort', id),

  onChatEvent: (callback: (event: ChatEvent) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, payload: ChatEvent): void => callback(payload)
    ipcRenderer.on('chat:event', listener)
    return () => ipcRenderer.removeListener('chat:event', listener)
  },

  onToggleChat: (callback: () => void): (() => void) => {
    const listener = (): void => callback()
    ipcRenderer.on('ui:toggle-chat', listener)
    return () => ipcRenderer.removeListener('ui:toggle-chat', listener)
  },

  onOpenSettings: (callback: () => void): (() => void) => {
    const listener = (): void => callback()
    ipcRenderer.on('ui:open-settings', listener)
    return () => ipcRenderer.removeListener('ui:open-settings', listener)
  },

  onAsk: (callback: (text: string) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, text: string): void => callback(text)
    ipcRenderer.on('ui:ask', listener)
    return () => ipcRenderer.removeListener('ui:ask', listener)
  },

  onCare: (callback: (text: string) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, text: string): void => callback(text)
    ipcRenderer.on('pet:care', listener)
    return () => ipcRenderer.removeListener('pet:care', listener)
  },

  onSettingsSync: (callback: (settings: Settings) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, payload: Settings): void => callback(payload)
    ipcRenderer.on('settings:sync', listener)
    return () => ipcRenderer.removeListener('settings:sync', listener)
  },

  onGesture: (callback: (name: string) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, name: string): void => callback(name)
    ipcRenderer.on('pet:gesture', listener)
    return () => ipcRenderer.removeListener('pet:gesture', listener)
  },

  onVoiceEvent: (callback: (event: VoiceEvent) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, payload: VoiceEvent): void => callback(payload)
    ipcRenderer.on('voice:event', listener)
    return () => ipcRenderer.removeListener('voice:event', listener)
  },

  voiceSnapshot: (): Promise<VoiceEvent | null> => ipcRenderer.invoke('voice:snapshot'),
  listen: (): void => ipcRenderer.send('voice:listen'),

  onTtsState: (callback: (speaking: boolean) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, speaking: boolean): void => callback(speaking)
    ipcRenderer.on('tts:state', listener)
    return () => ipcRenderer.removeListener('tts:state', listener)
  },

  speak: (text: string, emotion?: string): void => ipcRenderer.send('tts:speak', text, emotion),
  stopSpeech: (): void => ipcRenderer.send('tts:stop'),
  previewVoice: (payload: { text: string; speaker?: string; model?: string; pitch?: number }): void =>
    ipcRenderer.send('tts:preview', payload),
  setChatSize: (open: boolean): void => ipcRenderer.send('ui:chat-size', open),
  testAnimation: (): void => ipcRenderer.send('anim:test'),
  vtsStatus: (): Promise<{ connected: boolean; authenticated: boolean; message: string }> =>
    ipcRenderer.invoke('vts:status'),
  vtsConnect: (): Promise<{ connected: boolean; authenticated: boolean; message: string }> =>
    ipcRenderer.invoke('vts:connect'),
  vtsExpression: (file: string, active: boolean): Promise<boolean> => ipcRenderer.invoke('vts:expression', file, active),
  vtsHotkey: (name: string): Promise<boolean> => ipcRenderer.invoke('vts:hotkey', name),
  vtsHotkeys: (): Promise<string[]> => ipcRenderer.invoke('vts:hotkeys'),
  bakeAnimations: (): Promise<boolean> => ipcRenderer.invoke('anim:bake'),
  animationScenes: (): Promise<Array<{ name: string; count: number }>> => ipcRenderer.invoke('anim:scenes'),
  animationFrames: (scene: string): Promise<string[]> => ipcRenderer.invoke('anim:frames', scene),
  animationMeta: (): Promise<Record<string, number>> => ipcRenderer.invoke('anim:meta'),
  recordFromVts: (seconds: number, fps: number): Promise<{ scene: string; frames: number }> =>
    ipcRenderer.invoke('vts:record', seconds, fps),
  onBakeProgress: (callback: (payload: { scene: string; index: number; total: number }) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, payload: { scene: string; index: number; total: number }): void =>
      callback(payload)
    ipcRenderer.on('anim:bake-progress', listener)
    return () => ipcRenderer.removeListener('anim:bake-progress', listener)
  },

  onToolConfirm: (callback: (request: ToolConfirmRequest) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, payload: ToolConfirmRequest): void => callback(payload)
    ipcRenderer.on('tool:confirm', listener)
    return () => ipcRenderer.removeListener('tool:confirm', listener)
  },

  answerTool: (id: string, allow: boolean): void => ipcRenderer.send('tool:answer', { id, allow }),

  setInteractive: (interactive: boolean): void => ipcRenderer.send('window:interactive', interactive),

  onBackdrop: (callback: (color: { r: number; g: number; b: number }) => void): (() => void) => {
    const listener = (_event: Electron.IpcRendererEvent, color: { r: number; g: number; b: number }): void =>
      callback(color)
    ipcRenderer.on('ui:backdrop', listener)
    return () => ipcRenderer.removeListener('ui:backdrop', listener)
  },  dragStart: (x: number, y: number): void => ipcRenderer.send('drag:start', { x, y }),
  dragMove: (x: number, y: number): void => ipcRenderer.send('drag:move', { x, y }),
  dragEnd: (): void => ipcRenderer.send('drag:end'),

  quit: (): void => ipcRenderer.send('app:quit'),
  hide: (): void => ipcRenderer.send('window:hide'),
  checkBackend: (config: Settings): Promise<BackendStatus> => ipcRenderer.invoke('backend:status', config),
  openExternal: (url: string): Promise<void> => ipcRenderer.invoke('shell:open', url)
}

contextBridge.exposeInMainWorld('pet', api)
