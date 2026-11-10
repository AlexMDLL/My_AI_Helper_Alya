import { app, BrowserWindow, desktopCapturer, globalShortcut, ipcMain, Menu, nativeImage, net, protocol, screen, shell, Tray } from 'electron'
import { spawn, execFileSync } from 'node:child_process'
import type { ChildProcessWithoutNullStreams } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { existsSync, readFileSync, readdirSync, writeFileSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { DEFAULT_PERSONA, PERSONA_PRESETS, PROVIDERS, WAKE_WORDS } from '../shared/types'
import type { ChatMessage, Settings, ToolConfirmRequest, VoiceEvent, WakeWordId } from '../shared/types'
import { backendStatus, streamChat } from './providers'
import { MemoryStore } from './memory'
import { JsonStore } from './store'
import { describeTool, executeTool, getWeatherForCity, TOOL_DEFS } from './tools'
import { TtsService } from './tts'
import { VoiceService } from './voice'
import { VtsClient } from './vts'

const isDev = !app.isPackaged
const shouldCapture = !!process.env.PET_CAPTURE

let win: BrowserWindow | null = null
let tray: Tray | null = null
let settings: JsonStore<Settings>
let history: JsonStore<{ messages: ChatMessage[] }>
let memory: MemoryStore
let vts: VtsClient
let voice: VoiceService
let tts: TtsService
let lastVoiceEvent: VoiceEvent | null = null
let lastSpoken = ''
let topmost = true
let dragState: { cursor: { x: number; y: number }; origin: { x: number; y: number } } | null = null
const inflight = new Map<string, AbortController>()
const pendingConfirms = new Map<string, (allow: boolean) => void>()
let backdropTimer: NodeJS.Timeout | null = null

const DEFAULT_SETTINGS: Settings = {
  provider: 'ollama',
  model: 'qwen2.5:7b-instruct',
  ollamaUrl: 'http://127.0.0.1:11434',
  apiKey: '',
  temperature: 0.85,
  persona: DEFAULT_PERSONA,
  customInstructions: '',
  userName: '',
  assistantName: PERSONA_PRESETS[0].name,
  wakeWord: 'alya',
  wake: true,
  alwaysListen: false,
  hideFullscreen: true,
  tts: true,
  scale: 1.0,
  character: 'live2d',
  live2dModel: 'workshop',
  workshopJacket: true,
  vtsMode: false,
  animationMode: 'live2d',
  city: 'Москва',
  bubbles: true,
  care: true,
  careBedtime: 23,
  careSession: 120,
  voiceSpeaker: 'xenia',
  voicePitch: 0.97,
  voiceModel: 'v5'
}

const WINDOW_WIDTH = 400
const WINDOW_HEIGHT = 720
const WINDOW_WIDTH_CHAT = 780

function createWindow(): void {
  const { workArea } = screen.getPrimaryDisplay()
  const saved = settings.get()
  const fallbackX = workArea.x + workArea.width - WINDOW_WIDTH - 24
  const fallbackY = workArea.y + workArea.height - WINDOW_HEIGHT - 12

  let x = typeof saved.winX === 'number' ? saved.winX : fallbackX
  let y = typeof saved.winY === 'number' ? saved.winY : fallbackY
  const visible = screen.getAllDisplays().some((display) => {
    const area = display.workArea
    return x + WINDOW_WIDTH > area.x && x < area.x + area.width && y + WINDOW_HEIGHT > area.y && y < area.y + area.height
  })
  if (!visible) {
    x = fallbackX
    y = fallbackY
  }

  win = new BrowserWindow({
    width: WINDOW_WIDTH,
    height: WINDOW_HEIGHT,
    x,
    y,
    show: false,
    frame: false,
    thickFrame: false,
    transparent: true,
    backgroundColor: '#00000000',
    resizable: false,
    movable: true,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    hasShadow: false,
    alwaysOnTop: true,
    icon: nativeImage.createFromPath(join(app.getAppPath(), 'resources', 'icon.png')),
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      spellcheck: false,
      backgroundThrottling: false
    }
  })

  Menu.setApplicationMenu(null)
  win.setMenuBarVisibility(false)
  win.setAlwaysOnTop(true, 'floating')
  win.webContents.setBackgroundThrottling(false)
  try {
    win.webContents.setFrameRate(60)
  } catch {
    // метод может быть недоступен для обычного окна
  }
  win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true })
  win.setIgnoreMouseEvents(true, { forward: true })

  win.on('ready-to-show', () => win?.showInactive())
  win.webContents.on('console-message', (event) => {
    const details = event as unknown as { message?: string }
    const message = details.message ?? ''
    if (message.includes('live2d') || message.includes('pet://') || message.includes('Uncaught')) {
      console.log('[renderer]', message)
    }
  })
  win.webContents.on('render-process-gone', (_event, details) => {
    console.log('[renderer] process gone:', details.reason)
  })
  win.on('closed', () => {
    win = null
    if (backdropTimer) {
      clearInterval(backdropTimer)
      backdropTimer = null
    }
  })

  if (isDev && process.env['ELECTRON_RENDERER_URL']) {
    void win.loadURL(process.env['ELECTRON_RENDERER_URL'])
  } else {
    void win.loadFile(join(__dirname, '../renderer/index.html'))
  }

  if (shouldCapture) setupCapture()
  startBackdropSampling()
}

/**
 * Раз в несколько секунд смотрит на экран за спиной персонажа и отправляет
 * средний цвет фона, чтобы клетка могла от него отстроиться.
 */
function startBackdropSampling(): void {
  if (backdropTimer) clearInterval(backdropTimer)
  backdropTimer = setInterval(() => {
    void sampleBackdrop()
  }, 4500)
  setTimeout(() => void sampleBackdrop(), 1200)
}

async function sampleBackdrop(): Promise<void> {
  if (!win || !win.isVisible()) return
  try {
    const bounds = win.getBounds()
    const display = screen.getDisplayMatching(bounds)
    const sources = await desktopCapturer.getSources({
      types: ['screen'],
      thumbnailSize: { width: display.size.width, height: display.size.height }
    })
    const source = sources.find((item) => item.display_id === String(display.id)) ?? sources[0]
    if (!source) return

    const image = source.thumbnail
    const size = image.getSize()
    const scaleX = size.width / display.bounds.width
    const scaleY = size.height / display.bounds.height
    const crop = image.crop({
      x: Math.max(0, Math.round((bounds.x - display.bounds.x) * scaleX)),
      y: Math.max(0, Math.round((bounds.y - display.bounds.y) * scaleY)),
      width: Math.max(1, Math.round(bounds.width * scaleX)),
      height: Math.max(1, Math.round(bounds.height * scaleY))
    })

    const width = crop.getSize().width
    const height = crop.getSize().height
    const bitmap = crop.toBitmap()
    let red = 0
    let green = 0
    let blue = 0
    let count = 0

    for (let y = 0; y < height; y += 6) {
      for (let x = 0; x < width; x += 6) {
        const top = y < height * 0.32
        const side = x < width * 0.1 || x > width * 0.9
        if (!top && !side) continue
        const index = (y * width + x) * 4
        if (bitmap[index + 3] === 0) continue
        blue += bitmap[index]
        green += bitmap[index + 1]
        red += bitmap[index + 2]
        count += 1
      }
    }

    if (count === 0) return
    win.webContents.send('ui:backdrop', {
      r: Math.round(red / count),
      g: Math.round(green / count),
      b: Math.round(blue / count)
    })
  } catch {
    // не критично: если захват экрана недоступен, просто не адаптируемся
  }
}

function setupCapture(): void {
  const target = win
  if (!target) return
  target.webContents.once('did-finish-load', () => {
    setTimeout(() => {
      void (async () => {
        if (!win) return
        await new Promise((resolve) => setTimeout(resolve, 1200))
        try {
          const base = app.getPath('userData')
          const open = await win.webContents.capturePage()
          writeFileSync(join(base, 'capture.png'), open.toPNG())

          if (process.env.PET_VOICE) {
            win.webContents.send('voice:event', { type: 'wake' })
            await new Promise((resolve) => setTimeout(resolve, 400))
            win.webContents.send('voice:event', { type: 'partial', text: 'который' })
            await new Promise((resolve) => setTimeout(resolve, 500))
            win.webContents.send('voice:event', { type: 'partial', text: 'который час' })
            await new Promise((resolve) => setTimeout(resolve, 500))
            win.webContents.send('voice:event', { type: 'final', text: 'Который час?' })
            await new Promise((resolve) => setTimeout(resolve, 32000))
            const voiceShot = await win.webContents.capturePage()
            writeFileSync(join(base, 'capture-voice.png'), voiceShot.toPNG())
          }

          if (process.env.PET_ASK) {
            win.webContents.send('ui:ask', process.env.PET_ASK)
            await new Promise((resolve) => setTimeout(resolve, 30000))
            const chat = await win.webContents.capturePage()
            writeFileSync(join(base, 'capture-chat.png'), chat.toPNG())
          }

          if (process.env.PET_CONFIRM) {
            win.webContents.send('tool:confirm', {
              id: 'demo-confirm',
              tool: 'run_powershell',
              description: 'Проверить свободное место на диске C',
              detail: 'Get-PSDrive C | Select-Object Used,Free'
            })
            await new Promise((resolve) => setTimeout(resolve, 700))
            const confirmShot = await win.webContents.capturePage()
            writeFileSync(join(base, 'capture-confirm.png'), confirmShot.toPNG())
          }

          win.webContents.send('ui:toggle-chat')
          await new Promise((resolve) => setTimeout(resolve, 700))
          const closed = await win.webContents.capturePage()
          writeFileSync(join(base, 'capture-orb.png'), closed.toPNG())

          win.webContents.send('ui:toggle-chat')
          win.webContents.send('ui:open-settings')
          await new Promise((resolve) => setTimeout(resolve, 700))
          const settingsTop = await win.webContents.capturePage()
          writeFileSync(join(base, 'capture-settings-top.png'), settingsTop.toPNG())
          try {
            await win.webContents.executeJavaScript("document.querySelector('.settings-body')?.scrollTo(0, 99999)")
          } catch {
            // окно уже закрылось
          }
          await new Promise((resolve) => setTimeout(resolve, 500))
          const settingsShot = await win.webContents.capturePage()
          writeFileSync(join(base, 'capture-settings.png'), settingsShot.toPNG())

          if (process.env.PET_BUBBLE) {
            win.webContents.send('ui:toggle-chat')
            await new Promise((resolve) => setTimeout(resolve, 500))
            win.webContents.send('chat:event', {
              id: 'demo-bubble',
              type: 'done',
              text: 'Ну и чё ты там завис, а? Я всё ещё тут, между прочим.'
            })
            await new Promise((resolve) => setTimeout(resolve, 900))
            const bubbleShot = await win.webContents.capturePage()
            writeFileSync(join(base, 'capture-bubble.png'), bubbleShot.toPNG())
          }

          console.log('CAPTURE=' + join(base, 'capture.png'))
        } catch (error) {
          console.error('capture failed', error)
        }
        app.quit()
      })()
    }, 800)
  })
}

function toggleChat(): void {
  if (!win) return
  if (!win.isVisible()) win.showInactive()
  win.webContents.send('ui:toggle-chat')
}

function requestConfirmation(
  sender: Electron.WebContents,
  description: string,
  detail?: string
): Promise<boolean> {
  return new Promise((resolve) => {
    const id = randomUUID()
    const request: ToolConfirmRequest = { id, tool: 'run_powershell', description, detail }
    pendingConfirms.set(id, resolve)
    if (!sender.isDestroyed()) sender.send('tool:confirm', request)
    setTimeout(() => {
      if (pendingConfirms.has(id)) {
        pendingConfirms.delete(id)
        resolve(false)
      }
    }, 120000)
  })
}

async function detectCity(): Promise<string | null> {
  const services: Array<{ url: string; pick: (data: Record<string, unknown>) => unknown }> = [
    { url: 'https://ipwho.is/', pick: (data) => data.city },
    { url: 'https://ipinfo.io/json', pick: (data) => data.city },
    { url: 'http://ip-api.com/json/', pick: (data) => data.city }
  ]
  for (const service of services) {
    try {
      const response = await fetch(service.url, { signal: AbortSignal.timeout(4000) })
      if (!response.ok) continue
      const data = (await response.json()) as Record<string, unknown>
      const city = service.pick(data)
      if (typeof city === 'string' && city.trim()) return city.trim()
    } catch {
      // пробуем следующий сервис
    }
  }
  return null
}

let lastActionText: string | null = null

const FEMININE_FIXES: Array<[RegExp, string]> = [
  [/\bя сказал\b/gi, 'я сказала'],
  [/\bя был\b/gi, 'я была'],
  [/\bя сам\b/gi, 'я сама'],
  [/\bя готов\b/gi, 'я готова'],
  [/\bя должен\b/gi, 'я должна'],
  [/\bя рад\b/gi, 'я рада'],
  [/\bя уверен\b/gi, 'я уверена'],
  [/\bя понял\b/gi, 'я поняла'],
  [/\bя сделал\b/gi, 'я сделала'],
  [/\bя устал\b/gi, 'я устала'],
  [/\bя знал\b/gi, 'я знала'],
  [/\bя хотел\b/gi, 'я хотела'],
  [/\bя пришёл\b/gi, 'я пришла'],
  [/\bя пришел\b/gi, 'я пришла'],
  [/\bя нашёл\b/gi, 'я нашла'],
  [/\bя нашел\b/gi, 'я нашла'],
  [/\bя забыл\b/gi, 'я забыла'],
  [/\bя увидел\b/gi, 'я увидела'],
  [/\bя подумал\b/gi, 'я подумала'],
  [/\bя решил\b/gi, 'я решила'],
  [/\bя начал\b/gi, 'я начала'],
  [/\bя закончил\b/gi, 'я закончила'],
  [/\bя включил\b/gi, 'я включила'],
  [/\bя открыл\b/gi, 'я открыла'],
  [/\bя проверил\b/gi, 'я проверила'],
  [/\bя поставил\b/gi, 'я поставила'],
  [/\bя выбрал\b/gi, 'я выбрала'],
  [/\bя попробовал\b/gi, 'я попробовала'],
  [/\bя смог\b/gi, 'я смогла'],
  [/\bя мог\b/gi, 'я могла'],
  [/\bя один\b/gi, 'я одна']
]

function fixFeminine(text: string): string {
  let result = text
  for (const [pattern, replacement] of FEMININE_FIXES) {
    result = result.replace(pattern, (match) => {
      const fixed = replacement
      return match.charAt(0) === 'Я' ? fixed.charAt(0).toUpperCase() + fixed.slice(1) : fixed
    })
  }
  return result
}

function humanize(text: string): string {
  let result = text
  result = result.replace(/[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uac00-\ud7af]+/g, ' ')
  result = result.replace(/[ \t]{2,}/g, ' ')
  result = result.replace(/^(?:извините|извиняюсь|прошу прощения)[^.!?]*[.!?]\s*/gi, '')
  result = result.replace(/\bпозвольте\b/gi, 'дай-ка')
  result = result.replace(/\bпожалуйста\b[,:]?\s*/gi, '')
  result = result.replace(/\bукажите\b/gi, 'скажи')
  result = result.replace(/\bсообщите\b/gi, 'скажи')
  result = result.replace(/\bподскажите\b/gi, 'скажи')
  const map: Array<[RegExp, string]> = [
    [/\bвас\b/gi, 'тебя'],
    [/\bвам\b/gi, 'тебе'],
    [/\bваш\b/gi, 'твой'],
    [/\bваша\b/gi, 'твоя'],
    [/\bваше\b/gi, 'твоё'],
    [/\bваши\b/gi, 'твои'],
    [/\bвы\b/gi, 'ты']
  ]
  for (const [pattern, replacement] of map) {
    result = result.replace(pattern, (match) =>
      match.charAt(0) === match.charAt(0).toUpperCase()
        ? replacement.charAt(0).toUpperCase() + replacement.slice(1)
        : replacement
    )
  }
  result = result.replace(/\s{2,}/g, ' ').replace(/\s+([,.!?])/g, '$1').trim()
  if (result) result = result.charAt(0).toUpperCase() + result.slice(1)
  return result
}

let pendingLaunch: { folder: string; files: Array<{ n: number; name: string }>; at: number } | null = null

function findZapretFolder(): string | null {
  const candidates: string[] = ['C:\\zapret-discord-youtube-1.10.4']
  try {
    for (const name of readdirSync('C:\\')) {
      if (/zapret/i.test(name)) candidates.push(join('C:\\', name))
    }
  } catch {
    // нет доступа к корню — обойдёмся известным путём
  }
  return (
    candidates.find((folder) => {
      try {
        return existsSync(folder) && readdirSync(folder).some((file) => /\.(bat|cmd)$/i.test(file))
      } catch {
        return false
      }
    }) ?? null
  )
}

let isElevated = false
let ttsPreloaded = false

async function detectElevation(): Promise<void> {
  try {
    const result = await runPowerShellOut(
      '([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)'
    )
    isElevated = /true/i.test(result)
  } catch {
    isElevated = false
  }
  console.log('[admin] elevated:', isElevated)
  try {
    writeFileSync(join(tmpdir(), 'jarvis-elevated.txt'), String(isElevated))
  } catch {
    // диагностический файл не критичен
  }
}

function relaunchAsAdmin(): void {
  const exe = process.execPath.replace(/'/g, "''")
  const cwd = process.cwd().replace(/'/g, "''")
  const args = process.argv.slice(1).filter((arg) => arg && !arg.startsWith('--'))
  const argList = args.length > 0 ? args.map((arg) => `'${arg.replace(/'/g, "''")}'`).join(',') : "'.'"
  const log = join(tmpdir(), 'jarvis-relaunch.log').replace(/'/g, "''")
  const script =
    `Start-Sleep -Milliseconds 3000; ` +
    `try { Start-Process -FilePath '${exe}' -ArgumentList ${argList} ` +
    `-WorkingDirectory '${cwd}' -Verb RunAs -ErrorAction Stop; ` +
    `'OK' | Out-File -FilePath '${log}' -Encoding utf8 } ` +
    `catch { ('ERR: ' + $_.Exception.Message) | Out-File -FilePath '${log}' -Encoding utf8 }`
  console.log('[admin] relaunch:', script)
  spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script], {
    windowsHide: false,
    detached: true,
    stdio: 'ignore'
  }).unref()
  setTimeout(() => app.exit(0), 600)
}

async function launchAsAdmin(file: string, folder: string): Promise<{ ok: boolean; text: string }> {
  const full = join(folder, file)
  if (isElevated) {
    const child = spawn('cmd.exe', ['/c', full], { cwd: folder, detached: true, stdio: 'ignore', windowsHide: false })
    child.unref()
    return { ok: true, text: `Запускаю ${file}. Приложение уже с правами администратора.` }
  }
  const safeFull = full.replace(/'/g, "''")
  const safeFolder = folder.replace(/'/g, "''")
  const log = join(tmpdir(), 'jarvis-admin.log')
  try {
    if (existsSync(log)) writeFileSync(log, '')
  } catch {
    // лог не критичен
  }
  const script =
    `try { Start-Process -FilePath "$env:ComSpec" -ArgumentList '/c', '"${safeFull}"' ` +
    `-Verb RunAs -WorkingDirectory '${safeFolder}' -ErrorAction Stop; ` +
    `'OK' | Out-File -FilePath '${log.replace(/'/g, "''")}' -Encoding utf8 } ` +
    `catch { ('ERR: ' + $_.Exception.Message) | Out-File -FilePath '${log.replace(/'/g, "''")}' -Encoding utf8 }`
  console.log('[admin]', script)
  spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script], {
    windowsHide: true,
    detached: true,
    stdio: 'ignore'
  }).unref()

  await new Promise((resolve) => setTimeout(resolve, 2500))
  let content = ''
  try {
    content = readFileSync(log, 'utf8').trim()
  } catch {
    content = ''
  }
  console.log('[admin] result:', content || '(нет ответа — вероятно, ждёт UAC)')
  if (content.startsWith('ERR')) {
    return { ok: false, text: `Windows не дала запустить ${file}: ${content.slice(4).trim()}` }
  }
  return { ok: true, text: `Запускаю ${file} от имени администратора — подтверди запрос UAC.` }
}

const RU_NUMBERS: Record<string, number> = {
  первый: 1,
  первая: 1,
  второй: 2,
  вторая: 2,
  третий: 3,
  третья: 3,
  четвёртый: 4,
  четвертый: 4,
  пятый: 5,
  шестой: 6,
  седьмой: 7,
  восьмой: 8,
  девятый: 9,
  десятый: 10,
  одиннадцатый: 11,
  двенадцатый: 12,
  тринадцатый: 13,
  четырнадцатый: 14,
  пятнадцатый: 15,
  шестнадцатый: 16,
  семнадцатый: 17,
  восемнадцатый: 18,
  девятнадцатый: 19,
  двадцатый: 20,
  одиннадцать: 11,
  двенадцать: 12,
  тринадцать: 13,
  четырнадцать: 14,
  пятнадцать: 15
}

function parseNumberFromText(text: string): number | null {
  const digits = /(?:alt|альт|вариант|номер)?\s*(\d{1,3})(?!\d)/i.exec(text)
  if (digits) return Number(digits[1])
  const lower = text.toLowerCase()
  for (const [word, value] of Object.entries(RU_NUMBERS)) {
    if (lower.includes(word)) return value
  }
  return null
}

function zapretOptions(folder: string): Array<{ n: number; name: string }> {
  const files = readdirSync(folder).filter((file) => /\.(bat|cmd)$/i.test(file))
  const options: Array<{ n: number; name: string }> = []
  for (const file of files) {
    const alt = /ALT\s*(\d+)/i.exec(file)
    if (alt) {
      options.push({ n: Number(alt[1]), name: file })
    } else if (/ALT/i.test(file)) {
      options.push({ n: 1, name: file })
    }
  }
  return options.sort((a, b) => a.n - b.n)
}

function autoLearn(text: string): void {
  const value = text.trim()
  if (!value) return

  const rules: Array<{ pattern: RegExp; make: (match: RegExpMatchArray) => string }> = [
    { pattern: /^(?:меня зовут|моё имя|мое имя)\s+(.+)$/i, make: (m) => `Пользователя зовут ${m[1].trim()}` },
    { pattern: /^я живу в\s+(.+)$/i, make: (m) => `Живёт в ${m[1].trim()}` },
    { pattern: /^я (?:работаю|учусь)\s+(.+)$/i, make: (m) => `Работает или учится: ${m[1].trim()}` },
    { pattern: /^я люблю\s+(.+)$/i, make: (m) => `Любит ${m[1].trim()}` },
    { pattern: /^я не люблю\s+(.+)$/i, make: (m) => `Не любит ${m[1].trim()}` },
    { pattern: /^(?:называй меня|зови меня)\s+(.+)$/i, make: (m) => `Называть пользователя: ${m[1].trim()}` },
    { pattern: /^(?:всегда|впредь)\s+(.+)$/i, make: (m) => `Правило: всегда ${m[1].trim()}` },
    { pattern: /^никогда не\s+(.+)$/i, make: (m) => `Правило: никогда не ${m[1].trim()}` }
  ]

  for (const rule of rules) {
    const match = value.match(rule.pattern)
    if (match) {
      memory.add(rule.make(match))
      return
    }
  }

  if (/^(?:называй меня|зови меня)\s+(.+)$/i.test(value)) {
    const match = value.match(/^(?:называй меня|зови меня)\s+(.+)$/i)
    if (match) memory.add(`Называть пользователя: ${match[1].trim()}`)
    return
  }

  const correction = /^(?:не так|неправильно|неверно)[,:]?\s*(.+)$/i.exec(value)
  if (correction) {
    memory.add(`Правило: ${correction[1].trim()}`)
    return
  }

  const stopDoing = /^больше не\s+(.+)$/i.exec(value)
  if (stopDoing) {
    memory.add(`Правило: больше не ${stopDoing[1].trim()}`)
    return
  }

  if (/не так|неправильно|надо было|сделай по-другому|я же говорил/i.test(value)) {
    memory.add(`Пользователь поправлял: «${value.slice(0, 160)}»`)
  }
}

const CLOSE_ALIASES: Record<string, string[]> = {
  дискорд: ['Discord'],
  дискорде: ['Discord'],
  стим: ['steam'],
  телеграм: ['Telegram'],
  телеграмма: ['Telegram'],
  телега: ['Telegram'],
  хром: ['chrome'],
  хроме: ['chrome'],
  браузер: ['chrome', 'msedge', 'firefox', 'opera'],
  опера: ['opera'],
  файрфокс: ['firefox'],
  спотифай: ['Spotify'],
  спотифая: ['Spotify'],
  код: ['Code'],
  вскод: ['Code'],
  зум: ['Zoom'],
  обс: ['obs64'],
  обсе: ['obs64'],
  втс: ['VTube Studio'],
  вектор: ['VTube Studio'],
  'диспетчер задач': ['Taskmgr', 'Диспетчер задач'],
  диспетчер: ['Taskmgr', 'Диспетчер задач'],
  taskmgr: ['Taskmgr'],
  блокнот: ['notepad', 'Блокнот'],
  калькулятор: ['CalculatorApp', 'Calculator', 'Калькулятор'],
  контра: ['cs2', 'Counter-Strike'],
  контру: ['cs2', 'Counter-Strike'],
  кс: ['cs2', 'Counter-Strike'],
  кс2: ['cs2', 'Counter-Strike'],
  ксго: ['cs2', 'Counter-Strike'],
  дбд: ['DeadByDaylight', 'Dead by Daylight'],
  варфейс: ['Warface'],
  саундпад: ['Soundpad'],
  валпапер: ['wallpaper32', 'wallpaper64', 'Wallpaper Engine'],
  витуб: ['VTube Studio']
}

function closeAppByName(target: string): string {
  const key = target.toLowerCase().replace(/[.!?]/g, '').trim()
  const wanted = [...(CLOSE_ALIASES[key] ?? [key])]
  const steamGame = findInstalledSteamGame(key)
  if (steamGame) wanted.push(steamGame.name)
  const quote = (value: string): string => `'${value.replace(/'/g, "''")}'`
  const conditions = wanted
    .map(
      (name) =>
        `($_.ProcessName -like ${quote(`*${name}*`)} -or $_.MainWindowTitle -like ${quote(`*${name}*`)})`
    )
    .join(' -or ')
  const script = `$ErrorActionPreference = 'SilentlyContinue'
$p = Get-Process | Where-Object { (${conditions}) -and $_.Id -ne $PID }
if ($p) {
  $names = ($p | ForEach-Object { $_.ProcessName } | Sort-Object -Unique) -join ', '
  $p | Stop-Process -Force
  Write-Output $names
} else {
  Write-Output ''
}
`
  try {
    const result = execFileSync(
      'powershell.exe',
      ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script],
      { encoding: 'utf8' }
    ).trim()
    if (!result) return `«${target}» не запущено — закрывать нечего.`
    return `Закрыла: ${result}.`
  } catch {
    return 'Не получилось закрыть процесс.'
  }
}

function parseTelegramCommand(text: string): { contact: string | null; text: string } | null {
  const normalized = text
    .replace(/\bв\s+тг\b/gi, 'в телеграм')
    .replace(/\bв\s+телеге\b/gi, 'в телеграм')
    .replace(/\bв\s+телегу\b/gi, 'в телеграм')
    .replace(/\bв\s+телеграмме\b/gi, 'в телеграм')
    .replace(/\bтг\b/gi, 'телеграм')
    .replace(/\bтелега\b/gi, 'телеграм')
    .replace(/\bтелеге\b/gi, 'телеграм')
  const cleanContact = (value: string): string | null => {
    const contact = value.trim()
    if (/^(ему|ей|им|этому|этой|этот|эта)$/i.test(contact)) return null
    return contact || null
  }

  let match = /^(?:напиши|отправь|скинь)\s+в\s+телеграм\s+(.+?)\s*:\s*(.+)$/i.exec(normalized)
  if (match) return { contact: cleanContact(match[1]), text: match[2].trim() }
  match = /^(?:напиши|отправь|скинь)\s+(.+?)\s+в\s+телеграм\s+(.+)$/i.exec(normalized)
  if (match) return { contact: cleanContact(match[1]), text: match[2].trim() }
  match = /^(?:напиши|отправь|скинь)\s+(?:сообщение\s+)?в\s+телеграм\s+(.+)$/i.exec(normalized)
  if (match) return { contact: null, text: match[1].trim() }
  match = /^(?:напиши|отправь|скинь)\s+телеграм\s+(.+)$/i.exec(normalized)
  if (match) return { contact: null, text: match[1].trim() }
  if (/(?:напиши|отправь|скинь)/i.test(normalized) && /телеграм/i.test(normalized)) {
    const index = normalized.search(/телеграм/i)
    const rest = normalized.slice(index + 'телеграм'.length).replace(/^[\s,:;-]+/, '').trim()
    if (rest) return { contact: null, text: rest }
  }
  return null
}

function sendTelegramMessage(contact: string | null, message: string): string {
  const quote = (value: string): string => `'${value.replace(/'/g, "''")}'`
  const search = contact
    ? `Set-Clipboard -Value ${quote(contact)}
Start-Sleep -Milliseconds 500
$ws.SendKeys('^f')
Start-Sleep -Milliseconds 500
$ws.SendKeys('^v')
Start-Sleep -Milliseconds 900
$ws.SendKeys('{ENTER}')
Start-Sleep -Milliseconds 900`
    : ''
  const script = `$ErrorActionPreference = 'Stop'
$ws = New-Object -ComObject WScript.Shell
$proc = Get-Process Telegram -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw 'Telegram window not found' }
$null = $ws.AppActivate($proc.Id)
Start-Sleep -Milliseconds 800
${search}
Set-Clipboard -Value ${quote(message)}
Start-Sleep -Milliseconds 700
$ws.SendKeys('^v')
Start-Sleep -Milliseconds 700
$ws.SendKeys('{ENTER}')
Start-Sleep -Milliseconds 400
`
  try {
    const hasTelegram = execFileSync('tasklist', ['/fo', 'csv', '/nh'], { encoding: 'utf8' })
      .toLowerCase()
      .includes('telegram.exe')
    if (!hasTelegram) {
      void shell.openExternal('tg://')
      return 'Telegram не запущен — открываю его. Повтори через несколько секунд.'
    }
    // запускаем помощник через explorer: он стартует с обычными правами,
    // поэтому SendKeys работает даже если само приложение запущено от админа
    const scriptFile = join(app.getPath('temp'), `alya-telegram-${Date.now()}.ps1`)
    writeFileSync(scriptFile, script, 'utf8')
    try {
      execFileSync(
        'powershell.exe',
        ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', scriptFile],
        { stdio: 'ignore', timeout: 20000 }
      )
    } catch {
      // если приложение запущено от админа, SendKeys блокируется: пробуем
      // запустить помощник с обычными правами через explorer
      const batFile = join(app.getPath('temp'), `alya-telegram-${Date.now()}.bat`)
      writeFileSync(
        batFile,
        `@echo off\r\n"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "${scriptFile}"\r\n`,
        'utf8'
      )
      spawn('explorer.exe', [batFile], { detached: true, stdio: 'ignore' }).unref()
    }
    return contact ? `Написала в Telegram: ${contact}.` : 'Написала в активный чат Telegram.'
  } catch {
    return 'Не смогла отправить: убедись, что Telegram открыт и чат доступен.'
  }
}

interface SteamGame {
  appid: string
  name: string
}

let steamGamesCache: SteamGame[] | null = null

function steamInstallPath(): string | null {
  try {
    const output = execFileSync('reg', ['query', 'HKCU\\Software\\Valve\\Steam', '/v', 'SteamPath'], {
      encoding: 'utf8'
    })
    const match = /SteamPath\s+REG_SZ\s+(.+)/i.exec(output)
    return match ? match[1].trim() : null
  } catch {
    return null
  }
}

function installedSteamGames(): SteamGame[] {
  if (steamGamesCache) return steamGamesCache
  const root = steamInstallPath()
  if (!root) return []
  const libraries = new Set<string>([join(root, 'steamapps')])
  const foldersFile = join(root, 'steamapps', 'libraryfolders.vdf')
  try {
    if (existsSync(foldersFile)) {
      const text = readFileSync(foldersFile, 'utf8')
      for (const match of text.matchAll(/"path"\s+"([^"]+)"/g)) {
        libraries.add(join(match[1].replace(/\\\\/g, '\\'), 'steamapps'))
      }
    }
  } catch {
    // нет файла библиотек
  }
  const games: SteamGame[] = []
  for (const library of libraries) {
    try {
      for (const file of readdirSync(library)) {
        if (!file.startsWith('appmanifest_') || !file.endsWith('.acf')) continue
        const text = readFileSync(join(library, file), 'utf8')
        const appid = /"appid"\s+"(\d+)"/.exec(text)?.[1]
        const name = /"name"\s+"([^"]+)"/.exec(text)?.[1]
        if (appid && name) games.push({ appid, name })
      }
    } catch {
      // библиотека недоступна
    }
  }
  steamGamesCache = games
  return games
}

const STEAM_ALIASES: Record<string, string> = {
  кс: 'Counter-Strike',
  кс2: 'Counter-Strike',
  ксго: 'Counter-Strike',
  контра: 'Counter-Strike',
  контру: 'Counter-Strike',
  контры: 'Counter-Strike',
  дбд: 'Dead by Daylight',
  'дед бай': 'Dead by Daylight',
  'дед бай дейлайт': 'Dead by Daylight',
  варфейс: 'Warface',
  саундпад: 'Soundpad',
  валпапер: 'Wallpaper Engine',
  обои: 'Wallpaper Engine',
  витуб: 'VTube Studio',
  втс: 'VTube Studio',
  'витуб студио': 'VTube Studio'
}

function findInstalledSteamGame(query: string): SteamGame | null {
  const normalized = query.toLowerCase().trim()
  const alias = STEAM_ALIASES[normalized]
  const target = (alias ?? normalized).toLowerCase()
  const games = installedSteamGames()
  return (
    games.find((game) => game.name.toLowerCase() === target) ??
    games.find((game) => game.name.toLowerCase().includes(target)) ??
    games.find((game) => target.includes(game.name.toLowerCase())) ??
    null
  )
}

async function resolveSteamAppId(query: string): Promise<{ appid: string; name: string } | null> {
  const installed = findInstalledSteamGame(query)
  if (installed) return installed
  try {
    const response = await fetch(
      `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(query)}&l=ru&cc=RU`
    )
    const data = (await response.json()) as { items?: Array<{ id: number; name: string }> }
    const first = data.items?.[0]
    if (first) return { appid: String(first.id), name: first.name }
  } catch {
    // сеть недоступна
  }
  return null
}

async function launchSteamGame(query: string): Promise<string> {
  const game = await resolveSteamAppId(query)
  if (!game) return `Не нашла игру «${query}» в Steam.`
  const installed = Boolean(findInstalledSteamGame(query))
  if (installed) {
    void shell.openExternal(`steam://rungameid/${game.appid}`)
    return `Запускаю ${game.name}.`
  }
  void shell.openExternal(`steam://install/${game.appid}`)
  return `${game.name} не установлена — открыла установку в Steam.`
}

async function installSteamGame(query: string): Promise<string> {
  const game = await resolveSteamAppId(query)
  if (!game) return `Не нашла игру «${query}» в Steam.`
  if (findInstalledSteamGame(query)) {
    void shell.openExternal(`steam://rungameid/${game.appid}`)
    return `${game.name} уже установлена — запускаю.`
  }
  void shell.openExternal(`steam://install/${game.appid}`)
  return `Открываю установку ${game.name} в Steam.`
}

async function cancelSteamDownload(query: string): Promise<string> {
  const game = await resolveSteamAppId(query)
  if (!game) return `Не нашла игру «${query}» в Steam.`
  void shell.openExternal(`steam://pause/${game.appid}`)
  setTimeout(() => {
    void shell.openExternal('steam://open/downloads')
  }, 800)
  return `Открыла загрузки Steam — ${game.name} можно поставить на паузу или отменить там.`
}

async function runQuickCommand(text: string): Promise<string | null> {
  const cleaned = text
    .trim()
    .replace(/^(?:ал|аля|алю|але|аля,)\s+/i, '')
    .replace(/[.!?]+$/, '')
  const lower = cleaned.toLowerCase()
  const quickCtx = {
    confirm: async () => false,
    remember: () => undefined,
    defaultCity: settings.get().city || 'Москва'
  }

  if (
    /^(повтори|повторить|ещё раз|еще раз|снова|то же самое|сделай это снова|запусти снова|включи снова|запусти опять|включи опять)$/i.test(
      cleaned
    ) ||
    /^(повтори|сделай)\s+(это|то же( самое)?)/i.test(cleaned) ||
    /^(повтори|сделай)\s+(предыдущ|прошл|последн)/i.test(cleaned)
  ) {
    if (!lastActionText) return 'Пока нечего повторять — я ещё ничего не делала.'
    const previous = lastActionText
    lastActionText = null
    const reply = await runQuickCommand(previous)
    lastActionText = previous
    return reply ? `Повторила: ${reply}` : `Не получилось повторить «${previous}».`
  }

  const done = (reply: string): string => {
    lastActionText = cleaned
    return reply
  }

  if (pendingLaunch && Date.now() - pendingLaunch.at < 10 * 60_000) {
    const pick = /^(?:вариант|номер|альт|alt)?\s*(\d{1,3})$/i.exec(cleaned)
    if (pick) {
      const number = Number(pick[1])
      const option = pendingLaunch.files.find((item) => item.n === number)
      if (!option) {
        const list = pendingLaunch.files.map((item) => `${item.n} — ${item.name}`).join('; ')
        return done(`Такого варианта нет. Есть: ${list}`)
      }
      const launch = await launchAsAdmin(option.name, pendingLaunch.folder)
      pendingLaunch = null
      return done(launch.text)
    }
    if (/^(отмена|не надо|стоп|нет)$/i.test(cleaned)) {
      pendingLaunch = null
      return done('Ок, отменила.')
    }
  }

  if (
    /(?:запусти|запустить|открой|открыть|включи|включить|старт)/i.test(cleaned) &&
    /(?:запрет|zapret)/i.test(cleaned)
  ) {
    const folder = findZapretFolder()
    if (!folder) return done('Не нашла папку с запретом на диске C.')
    const options = zapretOptions(folder)
    if (options.length === 0) return done('В папке запрета не нашла .bat-файлов.')
    const requested = parseNumberFromText(cleaned)
    const option = requested ? options.find((item) => item.n === requested) : undefined
    if (option) {
      const launch = await launchAsAdmin(option.name, folder)
      pendingLaunch = null
      return done(launch.text)
    }
    pendingLaunch = { folder, files: options, at: Date.now() }
    return done(`Какой запускать? ${options.map((item) => `${item.n} — ${item.name}`).join('; ')}`)
  }

  if (/^(сделай\s+)?(скриншот|снимок экрана)/i.test(cleaned)) {
    const outcome = await executeTool({ id: 'quick', name: 'screenshot', arguments: {} }, quickCtx)
    return done(outcome.ok ? 'Сделала скриншот.' : `Не получилось сделать скриншот: ${outcome.text}`)
  }

  const cancelDownload = /^(?:отмени|останови|приостанови|пауза)\s+(?:загрузку|скачивание|установку)\s+(.+)$/i.exec(cleaned)
  if (cancelDownload) {
    return done(await cancelSteamDownload(cancelDownload[1].trim()))
  }
  const installGame = /^(?:скачай|установи|загрузи|поставь)\s+(.+)$/i.exec(cleaned)
  if (installGame && (await resolveSteamAppId(installGame[1].trim()))) {
    return done(await installSteamGame(installGame[1].trim()))
  }
  const launchGame = /^(?:запусти|включи|открой|поиграем в|играть в)\s+(.+)$/i.exec(cleaned)
  if (launchGame && findInstalledSteamGame(launchGame[1].trim())) {
    return done(await launchSteamGame(launchGame[1].trim()))
  }

  const compound = /^(открой|запусти|включи)\s+(.+?)\s+и\s+(открой|запусти|включи)\s+(.+)$/i.exec(cleaned)
  if (compound) {
    const first = await runQuickCommand(`${compound[1]} ${compound[2]}`)
    const second = await runQuickCommand(`${compound[3]} ${compound[4]}`)
    return done([first, second].filter(Boolean).join(' '))
  }

  const opened = /^(открой|открыть|запусти|запустить|включи|включить)\s+(?!музык|трек|песн|плейлист|снова|опять|звук|громкост|следующ|предыдущ)(.+)$/i.exec(cleaned)
  if (opened) {
    const target = opened[2].trim()
    const sites: Record<string, string> = {
      ютуб: 'https://youtube.com',
      youtube: 'https://youtube.com',
      вк: 'https://vk.com',
      'в контакте': 'https://vk.com',
      гугл: 'https://google.com',
      google: 'https://google.com',
      яндекс: 'https://ya.ru',
      телемост: 'https://telemost.yandex.ru',
      телеост: 'https://telemost.yandex.ru',
      'яндекс телемост': 'https://telemost.yandex.ru',
      почту: 'https://mail.google.com',
      почта: 'https://mail.google.com',
      инстаграм: 'https://instagram.com',
      instagram: 'https://instagram.com',
      тикток: 'https://tiktok.com',
      twitch: 'https://twitch.tv',
      твич: 'https://twitch.tv'
    }
    const site = sites[target.toLowerCase()]
    if (site) {
      const outcome = await executeTool({ id: 'quick', name: 'open_url', arguments: { url: site } }, quickCtx)
      return done(outcome.ok ? `Открываю ${target}.` : `Не получилось открыть ${target}: ${outcome.text}`)
    }
    const isUrl = /^(https?:\/\/|www\.)|\.[a-z]{2,}(\/|$)/i.test(target)
    const outcome = isUrl
      ? await executeTool(
          { id: 'quick', name: 'open_url', arguments: { url: target.startsWith('http') ? target : `https://${target}` } },
          quickCtx
        )
      : await executeTool({ id: 'quick', name: 'open_app', arguments: { name: target } }, quickCtx)
    return done(outcome.ok ? `Открываю ${target}.` : `Не получилось открыть ${target}: ${outcome.text}`)
  }

  if (/^(громче|прибавь (звук|громкость)|сделай громче|погромче)/i.test(cleaned)) {
    await sendVk(VK.volup)
    await sendVk(VK.volup)
    await sendVk(VK.volup)
    return done('Сделала громче.')
  }

  if (/^(тише|убавь (звук|громкость)|сделай тише|потише)/i.test(cleaned)) {
    await sendVk(VK.voldown)
    await sendVk(VK.voldown)
    await sendVk(VK.voldown)
    return done('Сделала тише.')
  }

  if (/^(выключи звук|без звука|мьют|заглуши)/i.test(cleaned)) {
    await sendVk(VK.mute)
    return done('Звук выключен.')
  }

  const wantsPlay =
    /(включи|запусти|поставь|продолжи)\s+(музыку|трек|песню|плейлист)/i.test(cleaned) ||
    /(запусти|включи|продолжи)\s+опять/i.test(cleaned) ||
    /^продолжи( музыку)?$/i.test(cleaned) ||
    /^(играй|дальше играй)$/i.test(cleaned)
  const wantsPause =
    /^(пауза|поставь на паузу|останови музыку|выключи музыку|стоп музыка)/i.test(cleaned) ||
    /(поставь|включи)?\s*(видео\s+)?на паузу/i.test(cleaned) ||
    /^(останови|стоп) видео/i.test(cleaned)
  const wantsNext = /(следующий трек|следующую песню|дальше трек|переключи трек|скип)/i.test(cleaned)
  const wantsPrev = /(предыдущий трек|верни трек|назад трек)/i.test(cleaned)
  const wantsUnmute = /^(включи|верни)\s+(звук|громкость)/i.test(cleaned)

  if (wantsUnmute) {
    await sendVk(VK.mute)
    return done('Включаю звук.')
  }

  if (wantsPlay) {
    const count = (await runPowerShellOut('(Get-Process Spotify -ErrorAction SilentlyContinue).Count')).trim()
    if (!count || count === '0') {
      await executeTool({ id: 'quick', name: 'open_app', arguments: { name: 'spotify' } }, quickCtx)
      await new Promise((resolve) => setTimeout(resolve, 7000))
    }
    await sendVk(VK.playpause)
    return done('Включаю музыку.')
  }

  if (wantsPause) {
    await sendVk(VK.playpause)
    return done('Пауза.')
  }

  if (wantsNext) {
    await sendVk(VK.next)
    return done('Следующий трек.')
  }

  if (wantsPrev) {
    await sendVk(VK.prev)
    return done('Предыдущий трек.')
  }

  if (/(следующая вкладка|следующую вкладку|вкладку вперёд|вкладка дальше|дальше вкладка)/i.test(cleaned)) {
    await sendHotkey([VK.ctrl, VK.tab])
    return done('Следующая вкладка.')
  }

  if (/(предыдущая вкладка|предыдущую вкладку|вкладку назад|вкладка назад)/i.test(cleaned)) {
    await sendHotkey([VK.ctrl, VK.shift, VK.tab])
    return done('Предыдущая вкладка.')
  }

  if (/(новая вкладка|новую вкладку)/i.test(cleaned)) {
    await sendHotkey([VK.ctrl, 0x54])
    return done('Открываю новую вкладку.')
  }

  if (/(закрой вкладку|закрыть вкладку)/i.test(cleaned)) {
    await sendHotkey([VK.ctrl, 0x57])
    return done('Закрываю вкладку.')
  }

  const tabNumber = /(?:переключись на|открой|перейди на)\s+вкладку\s+(\d)/i.exec(cleaned)
  if (tabNumber) {
    const number = Number(tabNumber[1])
    if (number >= 1 && number <= 9) {
      await sendHotkey([VK.ctrl, 0x30 + number])
      return done(`Вкладка ${number}.`)
    }
  }

  if (/(адресная строка|перейди на адрес|введи адрес)/i.test(cleaned)) {
    await sendHotkey([VK.ctrl, 0x4c])
    return done('Открываю адресную строку.')
  }

  if (/^(сверни всё|сверни окна|на рабочий стол|покажи рабочий стол)$/i.test(cleaned)) {
    await sendHotkey([VK.win, 0x44])
    return done('Свернула всё.')
  }

  if (/^(выключи монитор|погаси экран)/i.test(cleaned)) {
    const source =
      'using System; using System.Runtime.InteropServices; public class Mon { [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }'
    await runPowerShell(
      `Add-Type -TypeDefinition '${source}'; [Mon]::SendMessage([IntPtr]0xffff, 0x0112, [IntPtr]0xF170, [IntPtr]2) | Out-Null`
    )
    return done('Выключаю монитор.')
  }

  if (/^(заблокируй|заблокируй компьютер|блокировка)/i.test(cleaned)) {
    await runPowerShell('rundll32.exe user32.dll,LockWorkStation')
    return done('Блокирую компьютер.')
  }

  const closeMatch = /^(закрой|закрыть|заверши|убей|выключи|прибей)\s+(.+)$/i.exec(cleaned)
  if (closeMatch) {
    return done(closeAppByName(closeMatch[2].trim()))
  }

  const telegram = parseTelegramCommand(cleaned)
  if (telegram) {
    return done(sendTelegramMessage(telegram.contact, telegram.text))
  }

  const gestures: Array<[RegExp, string, string]> = [
    [/^(поклонись|поклон|сделай поклон)$/i, 'bow', 'Кланяюсь.'],
    [/^(кивни|кивок)$/i, 'nod', 'Киваю.'],
    [/^(подмигни|подмигивание)$/i, 'wink', 'Подмигиваю.'],
    [/^(улыбнись|улыбка)$/i, 'smile', 'Улыбаюсь.'],
    [/^(засмейся|посмейся|рассмейся|смех)$/i, 'laugh', 'Хе-хе.'],
    [/^(станцуй|потанцуй|танец)$/i, 'dance', 'Танцую.'],
    [/^(потянись|потягушки|разомнись)$/i, 'stretch', 'Потягиваюсь.'],
    [/^(смутись|смущайся)$/i, 'shy', 'Смущаюсь.'],
    [/^(помаши|помаши рукой|помаши руками|помахай|маши рукой|махай)$/i, 'wave', 'Машу.'],
    [/^(удивись|удивление|покажи удивление)$/i, 'surprise', 'Ого.'],
    [/^(отрицай|помотай головой|скажи нет)$/i, 'deny', 'Нет.']
  ]
  for (const [pattern, name, reply] of gestures) {
    if (pattern.test(cleaned)) {
      win?.webContents.send('pet:gesture', name)
      return done(reply)
    }
  }

  const searchMatch = /^(?:найди|загугли|поищи|погугли)\s+(?:в\s+(?:интернете|гугле|яндексе)\s+)?(.+)$/i.exec(cleaned)
  if (searchMatch) {
    const query = searchMatch[1].trim()
    await executeTool(
      { id: 'quick', name: 'open_url', arguments: { url: `https://www.google.com/search?q=${encodeURIComponent(query)}` } },
      quickCtx
    )
    return done(`Ищу «${query}».`)
  }

  const youtube = /^(?:включи|найди|поставь|включить)\s+на\s+(?:ютьюбе|ютубе|youtube)\s+(.+)$/i.exec(cleaned)
  if (youtube) {
    const query = youtube[1].trim()
    await executeTool(
      {
        id: 'quick',
        name: 'open_url',
        arguments: { url: `https://www.youtube.com/results?search_query=${encodeURIComponent(query)}` }
      },
      quickCtx
    )
    return done(`Ищу на YouTube «${query}».`)
  }

  const timer =
    /^(?:поставь\s+)?таймер\s+на\s+(\d+)\s*(минут[аы]?|мин|час[а]?|часов|секунд[аы]?|сек)/i.exec(cleaned) ||
    /^напомни\s+через\s+(\d+)\s*(минут[аы]?|мин|час[а]?|часов|секунд[аы]?|сек)\s*(?:что\s+)?(.+)$/i.exec(cleaned)
  if (timer) {
    const amount = Number(timer[1])
    const unit = timer[2].toLowerCase()
    const note = timer[3]?.trim()
    const ms = unit.startsWith('час') ? amount * 3_600_000 : unit.startsWith('сек') ? amount * 1000 : amount * 60_000
    const label = unit.startsWith('час')
      ? `${amount} ч`
      : unit.startsWith('сек')
        ? `${amount} сек`
        : `${amount} мин`
    const delay = Math.max(1000, Math.min(ms, 24 * 3_600_000))
    setTimeout(() => deliverCare(note ? `Напоминаю: ${note}` : `Таймер на ${label} сработал.`), delay)
    return done(`Ок, напомню через ${label}.`)
  }

  if (/погод|температур|дожд|снег|ветер|жарко|холодно|градус/i.test(cleaned)) {
    const outcome = await executeTool({ id: 'quick', name: 'get_weather', arguments: {} }, quickCtx)
    return done(outcome.ok ? `Погода в городе ${outcome.text}` : `Не получилось узнать погоду: ${outcome.text}`)
  }

  if (/^(который час|сколько (сейчас )?врем|время сейчас|скажи время|текущее время|сколько время)/i.test(cleaned) || /^время$/i.test(cleaned)) {
    const time = new Date().toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })
    const date = new Date().toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', weekday: 'long' })
    return done(`Сейчас ${time}, ${date}.`)
  }

  return null
}

function runPowerShell(script: string): Promise<void> {
  return new Promise((resolve) => {
    const child = spawn('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], {
      windowsHide: true,
      stdio: 'ignore'
    })
    child.on('close', () => resolve())
    child.on('error', () => resolve())
  })
}

function runPowerShellOut(script: string): Promise<string> {
  return new Promise((resolve) => {
    const child = spawn('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], {
      windowsHide: true
    })
    let out = ''
    child.stdout.on('data', (data) => {
      out += String(data)
    })
    child.on('close', () => resolve(out))
    child.on('error', () => resolve(''))
  })
}

let careTimer: NodeJS.Timeout | null = null
let careStartedAt = Date.now()
let lastNightCare = 0
let lastLongCare = 0
let pendingCare: string | null = null

const GAME_NAMES = [
  'deadbydaylight-win64-shipping',
  'dbd',
  'cs2',
  'csgo',
  'dota2',
  'gta5',
  'rdr2',
  'cyberpunk2077',
  'witcher3',
  'eldenring',
  'pubg',
  'valorant',
  'league of legends',
  'genshinimpact',
  'minecraft',
  'rust',
  'r5apex',
  'overwatch',
  'fortnite',
  'escapefromtarkov',
  'dayz',
  'arma3',
  'destiny2',
  'warframe',
  'ffxiv',
  'wow',
  'rocketleague',
  'beamng',
  'ets2'
]

function probeForeground(): Promise<{ name: string; idle: number; fullscreen: boolean; title: string }> {
  const script = [
    'Add-Type -AssemblyName System.Windows.Forms',
    "Add-Type @'",
    'using System; using System.Runtime.InteropServices;',
    'public class CareProbe {',
    ' [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();',
    ' [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);',
    ' [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);',
    ' [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);',
    ' public struct RECT { public int L, T, R, B; }',
    ' public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }',
    '}',
    "'@",
    '$h=[CareProbe]::GetForegroundWindow(); $pid2=0; [CareProbe]::GetWindowThreadProcessId($h,[ref]$pid2)|Out-Null',
    '$p=Get-Process -Id $pid2 -ErrorAction SilentlyContinue',
    '$li=New-Object CareProbe+LASTINPUTINFO; $li.cbSize=8; [CareProbe]::GetLastInputInfo([ref]$li)|Out-Null',
    '$idle=[int]([Environment]::TickCount - $li.dwTime)',
    '$r=New-Object CareProbe+RECT; [CareProbe]::GetWindowRect($h,[ref]$r)|Out-Null',
    '$s=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds',
    '$full = (($r.R-$r.L) -ge $s.Width -and ($r.B-$r.T) -ge $s.Height)',
    "$name = if ($p) { $p.ProcessName } else { '' }",
    "$title = if ($p) { $p.MainWindowTitle } else { '' }",
    '"$name|$idle|$full|$title"'
  ].join('\n')

  return runPowerShellOut(script).then((out) => {
    const [name = '', idle = '0', full = 'False', title = ''] = out.trim().split('|')
    return { name: name.trim(), idle: Number(idle) || 0, fullscreen: /true/i.test(full), title: title.trim() }
  })
}

function deliverCare(text: string): void {
  console.log('[care] deliver', text)
  if (!win || win.isDestroyed()) return
  const messages = history.get().messages
  messages.push({ role: 'assistant', content: text, ts: Date.now(), care: true })
  history.set({ messages: messages.slice(-200) })
  win.webContents.send('pet:care', text)
}

function nightMessage(): string {
  const options = [
    'Уже поздняя ночь. Тебе пора спать — компьютер не убежит, а сон важнее.',
    'Смотри, сколько времени. Давай закругляться и ложиться спать?',
    'Ночь на дворе. Закрывай свои дела и отправляйся отдыхать, я серьёзно.'
  ]
  return options[Math.floor(Math.random() * options.length)]
}

async function careTick(): Promise<void> {
  const config = settings.get()
  if (config.care === false) return
  let info: { name: string; idle: number; fullscreen: boolean; title: string }
  try {
    info = await probeForeground()
    console.log('[care] probe', JSON.stringify(info))
  } catch (error) {
    console.log('[care] probe failed', String(error))
    return
  }

  const now = new Date()
  const hour = now.getHours()
  const night = hour >= (config.careBedtime ?? 23) || hour < 6
  const sessionMin = (Date.now() - careStartedAt) / 60000
  const game = info.fullscreen || GAME_NAMES.includes(info.name.toLowerCase())

  if (info.idle > 15 * 60) {
    careStartedAt = Date.now()
    pendingCare = null
    return
  }

  let message: string | null = null
  if (night && Date.now() - lastNightCare > 90 * 60000) {
    message = nightMessage()
  } else if (sessionMin >= (config.careSession ?? 120) && Date.now() - lastLongCare > 60 * 60000) {
    message = `Ты уже ${Math.round(sessionMin)} минут за компьютером. Встань, разомнись и попей воды — глазам и спине нужен перерыв.`
  }

  if (!message) {
    if (pendingCare && !game && info.idle < 120) {
      deliverCare(pendingCare)
      pendingCare = null
    }
    return
  }

  if (game) {
    pendingCare = message
    return
  }

  deliverCare(message)
  if (night) lastNightCare = Date.now()
  else {
    lastLongCare = Date.now()
    careStartedAt = Date.now()
  }
}

const VK = {
  playpause: 0xb3,
  next: 0xb0,
  prev: 0xb1,
  stop: 0xb2,
  mute: 0xad,
  voldown: 0xae,
  volup: 0xaf,
  ctrl: 0x11,
  shift: 0x10,
  tab: 0x09,
  win: 0x5b
}

function sendHotkey(keys: number[]): Promise<void> {
  const source =
    'using System; using System.Runtime.InteropServices; public class Kbd { [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo); }'
  const down = keys.map((vk) => `[Kbd]::keybd_event(${vk},0,0,[UIntPtr]::Zero)`).join('; ')
  const up = [...keys]
    .reverse()
    .map((vk) => `[Kbd]::keybd_event(${vk},0,2,[UIntPtr]::Zero)`)
    .join('; ')
  const script = `Add-Type -TypeDefinition '${source}'; ${down}; Start-Sleep -Milliseconds 40; ${up}`
  return runPowerShell(script)
}

function sendVk(vk: number): Promise<void> {
  return sendHotkey([vk])
}

let fullscreenWatcher: ChildProcessWithoutNullStreams | null = null
let fullscreenActive = false
let fullscreenHidden = false

function startFullscreenWatcher(): void {
  const script = [
    'Add-Type -AssemblyName System.Windows.Forms',
    "Add-Type @'",
    'using System; using System.Runtime.InteropServices;',
    'public class FsProbe {',
    ' [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();',
    ' [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);',
    ' [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);',
    ' public struct RECT { public int L, T, R, B; }',
    '}',
    "'@",
    '$s=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds',
    'while ($true) {',
    `  if (-not (Get-Process -Id ${process.pid} -ErrorAction SilentlyContinue)) { break }`,
    '  $h=[FsProbe]::GetForegroundWindow(); $pid2=0; [FsProbe]::GetWindowThreadProcessId($h,[ref]$pid2)|Out-Null',
    '  $p=Get-Process -Id $pid2 -ErrorAction SilentlyContinue',
    '  $r=New-Object FsProbe+RECT; [FsProbe]::GetWindowRect($h,[ref]$r)|Out-Null',
    '  $full = (($r.R-$r.L) -ge $s.Width -and ($r.B-$r.T) -ge $s.Height)',
    '  Write-Output ("{0}|{1}" -f [int]$full, $(if ($p) { $p.ProcessName } else { "" }))',
    '  [Console]::Out.Flush()',
    '  Start-Sleep -Milliseconds 1500',
    '}'
  ].join('\n')

  fullscreenWatcher = spawn('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], {
    windowsHide: true
  })

  let buffer = ''
  let seenNormalWindow = false
  fullscreenWatcher.stdout.on('data', (chunk) => {
    buffer += String(chunk)
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    for (const raw of lines) {
      const line = raw.trim()
      if (!line) continue
      const active = line.startsWith('1|')
      if (active && !fullscreenActive) {
        fullscreenActive = true
        // прячемся только если до этого видели обычное окно, иначе при
        // запуске из полноэкранного YouTube окно сразу бы исчезало
        if (!seenNormalWindow) continue
        if (win && !win.isDestroyed() && win.isVisible() && settings.get().hideFullscreen !== false) {
          win.hide()
          fullscreenHidden = true
        }
      } else if (!active && fullscreenActive) {
        seenNormalWindow = true
        fullscreenActive = false
        if (fullscreenHidden) {
          fullscreenHidden = false
          if (win && !win.isDestroyed()) win.showInactive()
        }
      }
    }
  })
  fullscreenWatcher.stderr.on('data', () => undefined)
}

function createTray(): void {
  const icon = nativeImage.createFromPath(join(app.getAppPath(), 'resources', 'icon.png'))
  tray = new Tray(icon.isEmpty() ? nativeImage.createEmpty() : icon)
  tray.on('click', () => toggleChat())
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: 'Открыть чат', accelerator: 'CommandOrControl+Alt+J', click: () => toggleChat() },
      { label: 'Слушать команду', accelerator: 'CommandOrControl+Alt+Space', click: () => voice?.listen() },
      { label: 'Скрыть', click: () => win?.hide() },
      { type: 'separator' },
      {
        label: 'Перезапустить от администратора',
        click: () => relaunchAsAdmin()
      },
      {
        label: 'Всегда поверх',
        type: 'checkbox',
        checked: topmost,
        click: (item) => {
          topmost = item.checked
          win?.setAlwaysOnTop(topmost, 'floating')
        }
      },
      {
        label: 'Настройки',
        click: () => {
          if (!win) return
          win.showInactive()
          win.webContents.send('ui:open-settings')
        }
      },
      {
        label: 'Перезапустить',
        click: () => {
          app.relaunch()
          app.exit()
        }
      },
      { type: 'separator' },
      { label: 'Выход', click: () => app.quit() }
    ])
  )
}

function registerShortcuts(): void {
  globalShortcut.register('CommandOrControl+Alt+J', () => toggleChat())
  globalShortcut.register('CommandOrControl+Alt+Space', () => voice?.listen())
  globalShortcut.register('CommandOrControl+Alt+H', () => {
    if (!win) return
    if (win.isVisible()) win.hide()
    else win.showInactive()
  })
}

function registerIpc(): void {
  ipcMain.handle('settings:get', () => settings.get())

  ipcMain.handle('settings:set', (_event, patch: Partial<Settings>) => {
    const next: Partial<Settings> = {}
    if (patch.provider && patch.provider in PROVIDERS) next.provider = patch.provider
    if (typeof patch.model === 'string') next.model = patch.model
    if (typeof patch.ollamaUrl === 'string') next.ollamaUrl = patch.ollamaUrl
    if (typeof patch.apiKey === 'string') next.apiKey = patch.apiKey
    if (typeof patch.temperature === 'number') next.temperature = patch.temperature
    if (typeof patch.persona === 'string') next.persona = patch.persona
    if (typeof patch.userName === 'string') next.userName = patch.userName
    if (typeof patch.assistantName === 'string' && patch.assistantName.trim()) {
      next.assistantName = patch.assistantName.trim()
    }
    if (patch.wakeWord && patch.wakeWord in WAKE_WORDS) next.wakeWord = patch.wakeWord
    if (typeof patch.wake === 'boolean') next.wake = patch.wake
    if (typeof patch.alwaysListen === 'boolean') next.alwaysListen = patch.alwaysListen
    if (typeof patch.hideFullscreen === 'boolean') next.hideFullscreen = patch.hideFullscreen
    if (typeof patch.tts === 'boolean') next.tts = patch.tts
    if (typeof patch.scale === 'number') next.scale = Math.max(0.5, Math.min(1.3, patch.scale))
    if (patch.character === 'sprite' || patch.character === 'orb' || patch.character === 'live2d') {
      next.character = patch.character
    }
    if (
      patch.live2dModel === 'hiyori' ||
      patch.live2dModel === 'alexia' ||
      patch.live2dModel === 'jane' ||
      patch.live2dModel === 'workshop'
    ) {
      next.live2dModel = patch.live2dModel
    }
    if (typeof patch.workshopJacket === 'boolean') next.workshopJacket = patch.workshopJacket
    if (typeof patch.vtsMode === 'boolean') next.vtsMode = patch.vtsMode
    if (patch.animationMode === 'live2d' || patch.animationMode === 'sprites') next.animationMode = patch.animationMode
    if (typeof patch.customInstructions === 'string') next.customInstructions = patch.customInstructions.slice(0, 4000)
    if (typeof patch.city === 'string') next.city = patch.city.trim()
    if (typeof patch.bubbles === 'boolean') next.bubbles = patch.bubbles
    if (typeof patch.care === 'boolean') next.care = patch.care
    if (typeof patch.careBedtime === 'number') next.careBedtime = Math.max(0, Math.min(23, Math.round(patch.careBedtime)))
    if (typeof patch.careSession === 'number') next.careSession = Math.max(30, Math.min(360, Math.round(patch.careSession)))
    if (patch.voiceSpeaker === 'baya' || patch.voiceSpeaker === 'kseniya' || patch.voiceSpeaker === 'xenia') {
      next.voiceSpeaker = patch.voiceSpeaker
    }
    if (typeof patch.voicePitch === 'number') next.voicePitch = Math.max(0.85, Math.min(1.3, patch.voicePitch))
    if (patch.voiceModel === 'v5' || patch.voiceModel === 'v4') next.voiceModel = patch.voiceModel
    if (typeof patch.winX === 'number') next.winX = patch.winX
    if (typeof patch.winY === 'number') next.winY = patch.winY
    const merged = settings.set(next)
    if (typeof next.wake === 'boolean') voice?.setWake(next.wake)
    if (typeof next.alwaysListen === 'boolean') voice?.setAlwaysListen(next.alwaysListen)
    if (next.wakeWord) voice?.setWakeWords(WAKE_WORDS[merged.wakeWord])
    if (next.tts === false) tts?.stop()
    return merged
  })

  ipcMain.handle('chat:history', () => history.get().messages)

  ipcMain.handle('chat:clear', () => {
    history.set({ messages: [] })
    return true
  })

  ipcMain.handle('voice:snapshot', () => lastVoiceEvent)

  ipcMain.on('ui:chat-size', (_event, open: boolean) => {
    if (!win || win.isDestroyed()) return
    const bounds = win.getBounds()
    const wide = open
    if (wide && bounds.width !== WINDOW_WIDTH_CHAT) {
      const nextX = Math.max(0, bounds.x - (WINDOW_WIDTH_CHAT - WINDOW_WIDTH))
      win.setBounds({ x: nextX, y: bounds.y, width: WINDOW_WIDTH_CHAT, height: bounds.height })
    } else if (!wide && bounds.width !== WINDOW_WIDTH) {
      const nextX = Math.max(0, bounds.x + (bounds.width - WINDOW_WIDTH))
      win.setBounds({ x: nextX, y: bounds.y, width: WINDOW_WIDTH, height: bounds.height })
    }
  })

  ipcMain.on('anim:test', () => {
    win?.webContents.send('pet:gesture', 'dance')
  })

  ipcMain.on('voice:listen', () => voice?.listen())

  ipcMain.on('tts:speak', (_event, text: string, emotion?: string) => {
    lastSpoken = text
    const config = settings.get()
    if (voice?.isRunning) {
      voice.speak(
        text,
        config.voicePitch ?? 0.97,
        config.voiceSpeaker ?? 'xenia',
        config.voiceModel ?? 'v5',
        emotion ?? 'neutral'
      )
    } else {
      tts?.speak(text)
    }
  })

  ipcMain.on('tts:stop', () => {
    voice?.stopSpeech()
    tts?.stop()
  })

  ipcMain.on('tts:preview', (_event, payload: {
    text: string
    speaker?: string
    model?: string
    pitch?: number
  }) => {
    const config = settings.get()
    if (voice?.isRunning) {
      voice.speak(
        payload.text,
        payload.pitch ?? config.voicePitch ?? 0.97,
        payload.speaker ?? config.voiceSpeaker ?? 'baya',
        payload.model ?? config.voiceModel ?? 'v5'
      )
    } else {
      tts?.speak(payload.text)
    }
  })

  ipcMain.on('tool:answer', (_event, payload: { id: string; allow: boolean }) => {
    const resolve = pendingConfirms.get(payload.id)
    if (!resolve) return
    pendingConfirms.delete(payload.id)
    resolve(!!payload.allow)
  })

  ipcMain.handle('memory:list', () => memory.facts())

  ipcMain.handle('memory:add', (_event, text: string) => memory.add(text))

  ipcMain.handle('memory:delete', (_event, id: string) => memory.remove(id))

  ipcMain.handle('memory:clear', () => memory.clear())

  ipcMain.handle('memory:rebuild', async () => {
    const added = await memory.rebuild(settings.get(), history.get().messages)
    return { added, facts: memory.facts() }
  })

  ipcMain.handle('vts:status', () => vts.status())

  ipcMain.handle('vts:connect', async () => vts.connect())

  ipcMain.handle('vts:expression', async (_event, file: string, active: boolean) => {
    await vts.activateExpression(String(file), Boolean(active))
    return true
  })

  ipcMain.handle('vts:hotkey', async (_event, name: string) => vts.triggerHotkey(String(name)))

  ipcMain.handle('vts:hotkeys', async () => vts.listHotkeys())

  ipcMain.handle('anim:bake', () => bakeAnimations())

  async function bakeAnimations(): Promise<boolean> {
    const animDir = join(app.getAppPath(), 'resources', 'anim')
    const scenes: Array<{ name: string; fps: number; duration: number }> = [
      { name: 'Idle', fps: 60, duration: 3.4 },
      { name: 'Wink', fps: 60, duration: 1.2 },
      { name: 'Bow', fps: 60, duration: 1.5 },
      { name: 'Nod', fps: 60, duration: 1.1 },
      { name: 'Dance', fps: 60, duration: 3.2 },
      { name: 'Laugh', fps: 60, duration: 1.6 },
      { name: 'Smile', fps: 60, duration: 1.6 },
      { name: 'Shy', fps: 60, duration: 1.9 },
      { name: 'Stretch', fps: 60, duration: 1.7 },
      { name: 'Wave', fps: 60, duration: 1.6 },
      { name: 'Angry', fps: 60, duration: 1.2 },
      { name: 'Sad', fps: 60, duration: 1.9 },
      { name: 'LookAround', fps: 60, duration: 2.4 },
      { name: 'Hum', fps: 60, duration: 2.0 },
      { name: 'Yawn', fps: 60, duration: 2.2 },
      { name: 'Giggle', fps: 60, duration: 1.8 },
      { name: 'Curtsy', fps: 60, duration: 1.8 },
      { name: 'Shrug', fps: 60, duration: 1.6 }
    ]
    if (!win) throw new Error('окно не готово')
    const total = scenes.length
    for (let index = 0; index < total; index += 1) {
      const scene = scenes[index]
      win.webContents.send('anim:bake-progress', { scene: scene.name, index, total })
      const frames = (await win.webContents.executeJavaScript(
        `window.__alyaBake && window.__alyaBake(${JSON.stringify(scene.name)}, ${scene.fps}, ${scene.duration})`
      )) as string[]
      const dir = join(animDir, scene.name)
      mkdirSync(dir, { recursive: true })
      frames.forEach((dataUrl, frameIndex) => {
        const base64 = dataUrl.split(',')[1] ?? ''
        if (!base64) return
        const file = join(dir, `${String(frameIndex).padStart(4, '0')}.png`)
        writeFileSync(file, Buffer.from(base64, 'base64'))
      })
      const actualFps = Math.max(1, Math.round((frames.length / scene.duration) * 10) / 10)
      writeFileSync(join(dir, 'meta.json'), JSON.stringify({ fps: actualFps }), 'utf8')
    }
    win.webContents.send('anim:bake-progress', { scene: 'готово', index: total, total })
    return true
  }
  ;(globalThis as unknown as { __bakeAnimations?: () => Promise<boolean> }).__bakeAnimations = bakeAnimations

  ipcMain.handle('anim:scenes', () => {
    const animDir = join(app.getAppPath(), 'resources', 'anim')
    if (!existsSync(animDir)) return [] as Array<{ name: string; count: number }>
    return readdirSync(animDir, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => {
        const frames = readdirSync(join(animDir, entry.name)).filter((file) => file.endsWith('.png') || file.endsWith('.webp'))
        return { name: entry.name, count: frames.length }
      })
      .filter((scene) => scene.count > 0)
  })

  ipcMain.handle('vts:record', async (_event, seconds: number, fps: number) => {
    const duration = Math.max(1, Math.min(30, Number(seconds) || 6))
    const rate = Math.max(5, Math.min(60, Number(fps) || 30))
    const scene = `VTS_${Date.now().toString().slice(-6)}`
    const dir = join(app.getAppPath(), 'resources', 'anim', scene)
    mkdirSync(dir, { recursive: true })
    const started = Date.now()
    const interval = 1000 / rate
    let count = 0
    while (Date.now() - started < duration * 1000) {
      const frameStarted = Date.now()
      try {
        const base64 = await vts.takeScreenshot()
        if (base64) {
          writeFileSync(join(dir, `${String(count).padStart(4, '0')}.png`), Buffer.from(base64, 'base64'))
          count += 1
        }
      } catch {
        // пропускаем неудачный кадр
      }
      const wait = interval - (Date.now() - frameStarted)
      if (wait > 0) await new Promise((resolve) => setTimeout(resolve, wait))
    }
    writeFileSync(join(dir, 'meta.json'), JSON.stringify({ fps: Math.max(1, Math.round((count / duration) * 10) / 10) }), 'utf8')
    return { scene, frames: count }
  })

  ipcMain.handle('anim:meta', () => {
    const animDir = join(app.getAppPath(), 'resources', 'anim')
    const result: Record<string, number> = {}
    if (!existsSync(animDir)) return result
    for (const entry of readdirSync(animDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue
      const metaFile = join(animDir, entry.name, 'meta.json')
      if (!existsSync(metaFile)) continue
      try {
        const meta = JSON.parse(readFileSync(metaFile, 'utf8')) as { fps?: number }
        if (typeof meta.fps === 'number') result[entry.name] = meta.fps
      } catch {
        // пропускаем битую мету
      }
    }
    return result
  })

  ipcMain.handle('anim:frames', (_event, scene: string) => {
    const safe = String(scene ?? '').replace(/[^a-zA-Z0-9_]/g, '')
    const dir = join(app.getAppPath(), 'resources', 'anim', safe)
    if (!existsSync(dir)) return [] as string[]
    return readdirSync(dir)
      .filter((file) => file.endsWith('.png') || file.endsWith('.webp'))
      .sort()
      .map((file) => `pet://anim/${safe}/${encodeURIComponent(file)}`)
  })

  ipcMain.handle('geo:detect', () => detectCity())

  ipcMain.handle('character:sprite', (_event, name: string) => {    const safe = String(name ?? '').replace(/[^a-z0-9_-]/gi, '')
    if (!safe) return null
    const file = join(app.getAppPath(), 'resources', 'characters', 'alya', `${safe}.png`)
    if (!existsSync(file)) return null
    try {
      return `data:image/png;base64,${readFileSync(file).toString('base64')}`
    } catch {
      return null
    }
  })

  ipcMain.handle('backend:status', (_event, config?: Partial<Settings>) =>
    backendStatus({ ...settings.get(), ...config })
  )

  ipcMain.handle('shell:open', async (_event, url: string) => {
    if (/^https?:\/\//i.test(url)) await shell.openExternal(url)
  })

  ipcMain.on('chat:send', (event, payload: { id: string; text: string }) => {
    const sender = event.sender
    const config = settings.get()
    const messages = history.get().messages
    messages.push({ role: 'user', content: payload.text, ts: Date.now() })
    autoLearn(payload.text)

    const now = new Date().toLocaleString('ru-RU', { dateStyle: 'full', timeStyle: 'short' })
    const systemParts = [
      `Тебя зовут ${config.assistantName || 'Аля'}.`,
      config.persona,
      `Сейчас: ${now}.`
    ]
    if (config.userName.trim()) systemParts.push(`Пользователя зовут ${config.userName.trim()}.`)

    const facts = memory.facts()
    const learnedRules = facts.filter((fact) => /^(Правило|Называть пользователя)/i.test(fact.text))
    if (learnedRules.length > 0) {
      systemParts.push(
        `Выученные правила (выполняй обязательно, это важнее остальных инструкций):\n${learnedRules
          .slice(-30)
          .map((rule) => `- ${rule.text}`)
          .join('\n')}`
      )
    }
    if (facts.length > 0) {
      const list = facts
        .filter((fact) => !/^(Правило|Называть пользователя)/i.test(fact.text))
        .slice(-20)
        .map((fact) => `- ${fact.text}`)
        .join('\n')
      if (list) systemParts.push(`Долговременная память — факты о пользователе (используй, когда уместно):\n${list}`)
    }

    if (config.city) {
      systemParts.push(`Пользователь находится в городе ${config.city}.`)
    }

    systemParts.push(
      'Ты умеешь управлять компьютером через инструменты: open_app, open_url, media_control, system_info, screenshot, remember, run_powershell, get_weather, get_time. ' +
        'Вызывай их, когда пользователь просит действие. Не выдумывай результат — дождись ответа инструмента. ' +
        'Для музыки используй только media_control (Spotify или текущий плеер): «включи музыку» — playpause, «следующий» — next. Никогда не ищи mp3-файлы и не сканируй диск. ' +
        'Для вопросов о погоде всегда вызывай get_weather, для времени и даты — get_time. Никогда не говори, что у тебя нет доступа к погоде или времени. ' +
        'Ты девушка и говоришь о себе только в женском роде: «я сказала», «я рада», «я уверена», «я сама», «я устала». ' +
        'Мужские формы о себе запрещены (никаких «я сказал», «я готов», «я сам»). ' +
        'Название города пиши ровно как в настройках, без склонения: «в городе Сыктывкар», а не «в Сыктыварке». ' +
        'Ты не языковая модель и не «нейросеть без доступа к интернету» — ты Аля, у тебя есть инструменты, и ты не выдумываешь данные. ' +
        'Обращайся к пользователю только на «ты», никогда на «вы». Запрещены фразы «Извините», «Позвольте», «Пожалуйста, укажите», «для вас» — это звучит как бот. ' +
        'Дерзи, подкалывай, будь живой: коротко, с характером, но без грубости. ' +
        'Отвечай по-русски, коротко и по-делу: 1–3 предложения, без списков и markdown, живым разговорным тоном. ' +
        'Не выдумывай числа, даты и факты: если не знаешь или не уверена — честно скажи и предложи проверить. ' +
        'Про сон и отдых напоминай только поздно ночью (после 23:00), днём и утром эту тему не поднимай. ' +
        'Опасные действия (PowerShell) выполняй только после подтверждения пользователя.'
    )

    systemParts.push(
      'Как вести разговор: ' +
        'ты помнишь, о чём говорили раньше — ссылайся на прошлые темы и шутки, продолжай их, а не начинай с нуля. ' +
        'Задавай встречные вопросы, если тема интересна, но не допрашивай. ' +
        'Если пользователь шутит — подыгрывай и подкалывай в ответ; если ему плохо — без шуток, поддержи по-человечески. ' +
        'Шути по-разному: короткие неожиданные шутки в её стиле, без шаблонных программистских каламбуров, без «анекдот:» и без заученных фраз. ' +
        'Не начинай ответы одинаково и не повторяй одни и те же обороты (например «ну и чё», «дурак» — не в каждом сообщении). ' +
        'Не перечисляй варианты и не читай лекций: живой диалог, как с близким человеком. ' +
        'Если не поняла — переспроси коротко, по-своему, а не «уточните запрос».'
    )
    systemParts.push('Пиши только по-русски кириллицей. Никаких иероглифов, китайских слов и английских вставок.')

    if (config.customInstructions?.trim()) {
      systemParts.push(`Правила от пользователя (выполняй обязательно):\n${config.customInstructions.trim()}`)
    }

    systemParts.push(
      'Примеры: «открой хром» → open_app {name:"chrome"}; «включи музыку» → media_control {action:"playpause"}; «следующий трек» → media_control {action:"next"}; «следующая вкладка» → нажатие Ctrl+Tab; «открой visual studio» → open_app {name:"visual studio"}; «какая погода» → get_weather; «который час» → get_time; «сделай скриншот» → screenshot.'
    )

    const working: ChatMessage[] = []

    const controller = new AbortController()
    inflight.set(payload.id, controller)
    let partial = ''

    void (async () => {
      try {
        const quick = await runQuickCommand(payload.text)
        if (quick) {
          if (!sender.isDestroyed()) {
            sender.send('chat:event', { id: payload.id, type: 'delta', text: quick })
            sender.send('chat:event', { id: payload.id, type: 'done', text: quick })
          }
          messages.push({ role: 'assistant', content: quick, ts: Date.now() })
          history.set({ messages: messages.slice(-200) })
          return
        }

        if (/погод|температур|дожд|снег|ветер|жарко|холодно|градус/i.test(payload.text)) {
          const weather = await getWeatherForCity(config.city || 'Москва')
          systemParts.push(
            weather.ok
              ? `Актуальные данные о погоде (получены только что, используй их в ответе): ${weather.text}`
              : `Погоду получить не удалось: ${weather.text}. Скажи об этом честно, не выдумывай.`
          )
        }

        working.push(
          { role: 'system', content: systemParts.join('\n') },
          ...messages.filter((message) => !message.care).slice(-40)
        )

        let finalText = ''
        for (let step = 0; step < 4; step += 1) {
          const result = await streamChat({
            config,
            messages: working,
            tools: TOOL_DEFS,
            onDelta: (delta) => {
              partial += delta
              if (!sender.isDestroyed()) {
                sender.send('chat:event', { id: payload.id, type: 'delta', text: delta })
              }
            },
            signal: controller.signal
          })

          finalText = result.text
          if (result.toolCalls.length === 0) break

          working.push({
            role: 'assistant',
            content: result.text,
            tool_calls: result.toolCalls.map((call) => ({
              id: call.id,
              function: { name: call.name, arguments: call.arguments }
            }))
          })

          for (const call of result.toolCalls) {
            if (!sender.isDestroyed()) {
              sender.send('chat:event', { id: payload.id, type: 'status', text: describeTool(call) })
            }
            const outcome = await executeTool(call, {
              confirm: (request) => requestConfirmation(sender, request.description, request.detail),
              remember: (fact) => {
                memory.add(fact)
              },
              defaultCity: config.city
            })
            console.log('[tool]', call.name, JSON.stringify(call.arguments), '->', outcome.ok, outcome.text.slice(0, 160))
            working.push({
              role: 'tool',
              content: outcome.ok ? outcome.text : `Ошибка: ${outcome.text}`,
              tool_name: call.name,
              tool_call_id: call.id
            })
          }
        }

        const text = humanize(fixFeminine(finalText.trim()))
        if (text) messages.push({ role: 'assistant', content: text, ts: Date.now() })
        history.set({ messages: messages.slice(-200) })

        memory.bump()
        const trigger = /запомни|не забудь|remember/i.test(payload.text) ? 1 : 8
        if (memory.pending >= trigger) {
          void memory.consolidate(config, messages.slice(-16))
        }

        if (!sender.isDestroyed()) {
          sender.send('chat:event', { id: payload.id, type: 'done', text })
        }
      } catch (error) {
        const aborted = controller.signal.aborted
        if (aborted) {
          const text = partial.trim()
          if (text) messages.push({ role: 'assistant', content: text, ts: Date.now() })
          history.set({ messages: messages.slice(-200) })
          if (!sender.isDestroyed()) {
            sender.send('chat:event', { id: payload.id, type: 'done', text })
          }
        } else {
          history.set({ messages: messages.slice(-200) })
          const message = error instanceof Error ? error.message : 'Неизвестная ошибка'
          if (!sender.isDestroyed()) {
            sender.send('chat:event', { id: payload.id, type: 'error', text: message })
          }
        }
      } finally {
        inflight.delete(payload.id)
      }
    })()
  })

  ipcMain.on('chat:abort', (_event, id: string) => {
    inflight.get(id)?.abort()
  })

  ipcMain.on('window:interactive', (_event, interactive: boolean) => {
    win?.setIgnoreMouseEvents(!interactive, { forward: true })
  })

  ipcMain.on('window:hide', () => win?.hide())
  ipcMain.on('app:quit', () => app.quit())

  ipcMain.on('drag:start', (_event, point: { x: number; y: number }) => {
    if (!win) return
    const [x, y] = win.getPosition()
    dragState = { cursor: point, origin: { x, y } }
  })

  ipcMain.on('drag:move', (_event, point: { x: number; y: number }) => {
    if (!win || !dragState) return
    win.setPosition(
      Math.round(dragState.origin.x + point.x - dragState.cursor.x),
      Math.round(dragState.origin.y + point.y - dragState.cursor.y)
    )
  })

  ipcMain.on('drag:end', () => {
    dragState = null
    if (win) {
      const [x, y] = win.getPosition()
      settings.set({ winX: x, winY: y })
    }
  })
}

if (!app.requestSingleInstanceLock()) {
  app.quit()
} else {
  app.on('second-instance', () => {
    if (!win) return
    win.showInactive()
    win.webContents.send('ui:toggle-chat')
  })

  app.commandLine.appendSwitch('disable-frame-rate-limit')
  app.commandLine.appendSwitch('disable-gpu-vsync')
  app.commandLine.appendSwitch('ignore-gpu-blocklist')
  app.commandLine.appendSwitch('enable-gpu-rasterization')
  app.commandLine.appendSwitch('enable-zero-copy')
  app.commandLine.appendSwitch('enable-accelerated-2d-canvas')
  app.commandLine.appendSwitch('enable-features', 'CanvasOopRasterization')
  app.commandLine.appendSwitch('use-angle', 'd3d11')
  app.commandLine.appendSwitch('disable-gpu-sandbox')
  app.commandLine.appendSwitch('enable-gpu')
  app.commandLine.appendSwitch('enable-unsafe-webgpu')

protocol.registerSchemesAsPrivileged([
  {
    scheme: 'pet',
    privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true, stream: true }
  }
])

app.whenReady().then(() => {
    app.setAppUserModelId('ru.foxif.jarvis')
    try {
      console.log('[gpu]', JSON.stringify(app.getGPUFeatureStatus()))
      setTimeout(() => {
        try {
          console.log('[gpu-late]', JSON.stringify(app.getGPUFeatureStatus()))
        } catch {
          // ignore
        }
      }, 6000)
    } catch {
      // статус недоступен
    }
    protocol.handle('pet', (request) => {
      const url = new URL(request.url)
      const relative = decodeURIComponent(url.pathname).replace(/^\/+/, '')
      const base = url.hostname === 'anim' ? 'anim' : 'live2d'
      const filePath = join(app.getAppPath(), 'resources', base, relative)
      return net.fetch(pathToFileURL(filePath).toString())
    })
    settings = new JsonStore<Settings>('settings.json', DEFAULT_SETTINGS)
    history = new JsonStore<{ messages: ChatMessage[] }>('history.json', { messages: [] })
    memory = new MemoryStore()
    vts = new VtsClient()
    tts = new TtsService((speaking) => {
      win?.webContents.send('tts:state', speaking)
    })
    voice = new VoiceService((event) => {
      if (event.type === 'ready' || event.type === 'error') lastVoiceEvent = event
      if (event.type === 'ready' && !ttsPreloaded) {
        ttsPreloaded = true
        // грузим голос только после инициализации микрофона, иначе загрузка
        // torch и ASR одновременно может подвесить сайдкар
        setTimeout(() => voice?.preloadTts(settings.get().voiceModel ?? 'v5'), 1500)
      }
      if (event.type === 'tts_error' && lastSpoken) {
        tts?.speak(lastSpoken)
      }
      if (event.type === 'always' && typeof (event as { enabled?: boolean }).enabled === 'boolean') {
        const enabled = Boolean((event as { enabled?: boolean }).enabled)
        const current = settings.get()
        settings.set({ alwaysListen: enabled })
        win?.webContents.send('settings:sync', { ...current, alwaysListen: enabled })
      }
      win?.webContents.send('voice:event', event)
    })

    registerIpc()
    void detectElevation()
    createWindow()
    createTray()
    registerShortcuts()
    careStartedAt = Date.now()
    careTimer = setInterval(() => void careTick(), 60_000)
    startFullscreenWatcher()
    if (memory.facts().length === 0 && history.get().messages.length >= 4) {
      void memory.rebuild(settings.get(), history.get().messages).then((added) => {
        console.log('[memory] авто-обновление, добавлено фактов:', added)
      })
    }
    voice.start()
    console.log('[voice] alwaysListen =', settings.get().alwaysListen)
    voice.setWake(settings.get().wake)
    voice.setAlwaysListen(settings.get().alwaysListen)
    voice.setWakeWords(WAKE_WORDS[settings.get().wakeWord])

    if (process.env.PET_GESTURE) {
      const delay = Number(process.env.PET_GESTURE_DELAY ?? 18000)
      setTimeout(() => {
        win?.webContents.send('pet:gesture', process.env.PET_GESTURE)
      }, delay)
    }
    if (process.env.PET_BAKE) {
      setTimeout(() => {
        void (globalThis as unknown as { __bakeAnimations?: () => Promise<boolean> }).__bakeAnimations?.()
      }, Number(process.env.PET_BAKE_DELAY ?? 20000))
    }
    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow()
    })
  })

  app.on('window-all-closed', () => app.quit())

  app.on('before-quit', () => {
    if (careTimer) {
      clearInterval(careTimer)
      careTimer = null
    }
    if (fullscreenWatcher) {
      fullscreenWatcher.kill()
      fullscreenWatcher = null
    }
  })

  app.on('will-quit', () => {
    globalShortcut.unregisterAll()
    settings?.flush()
    history?.flush()
    memory?.flush()
    voice?.stop()
    tts?.stop()
  })
}
