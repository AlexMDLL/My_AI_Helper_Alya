import { app } from 'electron'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

type Pending = {
  resolve: (value: Record<string, unknown>) => void
  reject: (error: Error) => void
  timer: ReturnType<typeof setTimeout>
}

export interface VtsStatus {
  connected: boolean
  authenticated: boolean
  message: string
}

const PLUGIN_NAME = 'Alya Desktop'
const PLUGIN_DEVELOPER = 'foxif'
const PLUGIN_ICON = ''

export class VtsClient {
  private socket: WebSocket | null = null
  private pending = new Map<string, Pending>()
  private token = ''
  private counter = 0
  private authenticated = false
  private lastMessage = 'не подключено'

  constructor() {
    try {
      const file = join(app.getPath('userData'), 'vts-token.json')
      if (existsSync(file)) {
        const saved = JSON.parse(readFileSync(file, 'utf8')) as { token?: string }
        if (saved.token) this.token = saved.token
      }
    } catch {
      // токен не критичен
    }
  }

  status(): VtsStatus {
    return { connected: Boolean(this.socket?.readyState === 1), authenticated: this.authenticated, message: this.lastMessage }
  }

  async connect(): Promise<VtsStatus> {
    if (this.socket && this.socket.readyState === 1) {
      return this.status()
    }
    this.lastMessage = 'подключаюсь к ws://127.0.0.1:8001'
    await new Promise<void>((resolve, reject) => {
      let settled = false
      const socket = new WebSocket('ws://127.0.0.1:8001')
      this.socket = socket
      const fail = (message: string): void => {
        if (settled) return
        settled = true
        this.lastMessage = message
        reject(new Error(message))
      }
      const timer = setTimeout(() => fail('VTubeStudio не отвечает. Включи API: Settings → API → Allow API access'), 5000)
      socket.onopen = (): void => {
        clearTimeout(timer)
        settled = true
        this.lastMessage = 'соединение установлено'
        resolve()
      }
      socket.onerror = (): void => {
        clearTimeout(timer)
        fail('не удалось подключиться к VTubeStudio (порт 8001 закрыт)')
      }
      socket.onclose = (): void => {
        this.authenticated = false
        this.socket = null
        this.lastMessage = 'соединение с VTubeStudio закрыто'
      }
      socket.onmessage = (event: MessageEvent): void => {
        this.handleMessage(String(event.data))
      }
    })

    await this.authenticate()
    return this.status()
  }

  private handleMessage(raw: string): void {
    let message: Record<string, unknown>
    try {
      message = JSON.parse(raw) as Record<string, unknown>
    } catch {
      return
    }
    const requestId = typeof message.requestID === 'string' ? message.requestID : ''
    const pending = this.pending.get(requestId)
    if (!pending) return
    this.pending.delete(requestId)
    clearTimeout(pending.timer)
    if (message.messageType === 'APIError') {
      const data = (message.data ?? {}) as { message?: string; errorID?: number }
      pending.reject(new Error(data.message ?? `ошибка API ${data.errorID ?? ''}`))
      return
    }
    pending.resolve((message.data ?? {}) as Record<string, unknown>)
  }

  private send(messageType: string, data: Record<string, unknown>, timeoutMs = 20000): Promise<Record<string, unknown>> {
    const socket = this.socket
    if (!socket || socket.readyState !== 1) {
      return Promise.reject(new Error('нет соединения с VTubeStudio'))
    }
    const requestID = `alya-${Date.now()}-${(this.counter += 1)}`
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestID)
        reject(new Error(`VTubeStudio не ответил на ${messageType}`))
      }, timeoutMs)
      this.pending.set(requestID, { resolve, reject, timer })
      socket.send(
        JSON.stringify({
          apiName: 'VTubeStudioPublicAPI',
          apiVersion: '1.0',
          requestID,
          messageType,
          data
        })
      )
    })
  }

  private async authenticate(): Promise<void> {
    this.lastMessage = 'проверяю авторизацию'
    const state = await this.send('APIStateRequest', {})
    if (state.active === false) {
      this.lastMessage = 'VTubeStudio неактивен'
    }
    if (state.authenticated) {
      this.authenticated = true
      this.lastMessage = 'подключено'
      return
    }

    if (!this.token) {
      this.lastMessage = 'жду подтверждения в VTubeStudio (окно «Allow Plugin»)...'
      const tokenResponse = await this.send(
        'AuthenticationTokenRequest',
        { pluginName: PLUGIN_NAME, pluginDeveloper: PLUGIN_DEVELOPER, pluginIcon: PLUGIN_ICON },
        60000
      )
      if (typeof tokenResponse.authenticationToken === 'string') {
        this.token = tokenResponse.authenticationToken
        try {
          writeFileSync(join(app.getPath('userData'), 'vts-token.json'), JSON.stringify({ token: this.token }), 'utf8')
        } catch {
          // токен можно не сохранять
        }
      }
    }

    const auth = await this.send(
      'AuthenticationRequest',
      { pluginName: PLUGIN_NAME, pluginDeveloper: PLUGIN_DEVELOPER, authenticationToken: this.token },
      30000
    )
    this.authenticated = auth.authenticated === true
    this.lastMessage = this.authenticated ? 'подключено' : 'авторизация отклонена'
    if (!this.authenticated) throw new Error('VTubeStudio отклонил авторизацию')
  }

  async activateExpression(file: string, active: boolean): Promise<void> {
    await this.connect().catch((error) => {
      throw error
    })
    await this.send('ExpressionActivationRequest', { expressionFile: file, active })
  }

  async triggerHotkey(name: string): Promise<boolean> {
    await this.connect()
    const list = await this.send('HotkeysInCurrentModelRequest', {})
    const hotkeys = Array.isArray(list.availableHotkeys) ? (list.availableHotkeys as Array<Record<string, unknown>>) : []
    const found = hotkeys.find((hotkey) => String(hotkey.name ?? hotkey.hotkeyName ?? '') === name)
    if (!found) return false
    const id = String(found.hotkeyID ?? found.id ?? '')
    if (!id) return false
    await this.send('HotkeyTriggerRequest', { hotkeyID: id })
    return true
  }

  async listHotkeys(): Promise<string[]> {
    await this.connect()
    const list = await this.send('HotkeysInCurrentModelRequest', {})
    const hotkeys = Array.isArray(list.availableHotkeys) ? (list.availableHotkeys as Array<Record<string, unknown>>) : []
    return hotkeys.map((hotkey) => String(hotkey.name ?? hotkey.hotkeyName ?? ''))
  }

  async takeScreenshot(): Promise<string> {
    await this.connect()
    const response = await this.send('TakeScreenshotRequest', { artMeshIdFilter: '' }, 10000)
    return String(response.imageData ?? '')
  }

  disconnect(): void {
    try {
      this.socket?.close()
    } catch {
      // ignore
    }
    this.socket = null
    this.authenticated = false
  }
}
