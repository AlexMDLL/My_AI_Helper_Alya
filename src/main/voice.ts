import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { app } from 'electron'
import type { VoiceCommand, VoiceEvent } from '../shared/types'

/** Python-сайдкар: потоковое распознавание речи и wake-word. */
export class VoiceService {
  private proc: ChildProcessWithoutNullStreams | null = null
  private buffer = ''

  constructor(private readonly onEvent: (event: VoiceEvent) => void) {}

  private get python(): string {
    return join(app.getAppPath(), 'voice', '.venv', 'Scripts', 'python.exe')
  }

  private get script(): string {
    return join(app.getAppPath(), 'voice', 'voice.py')
  }

  start(): void {
    if (this.proc) return
    if (!existsSync(this.python) || !existsSync(this.script)) {
      this.onEvent({ type: 'error', message: 'Голосовой модуль не найден (voice/.venv)' })
      return
    }

    const proc = spawn(this.python, [this.script], {
      cwd: join(app.getAppPath(), 'voice'),
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'pipe']
    })

    proc.stdout.setEncoding('utf8')
    proc.stdout.on('data', (chunk: string) => {
      this.buffer += chunk
      let newline: number
      while ((newline = this.buffer.indexOf('\n')) >= 0) {
        const line = this.buffer.slice(0, newline).trim()
        this.buffer = this.buffer.slice(newline + 1)
        if (!line) continue
        try {
          this.onEvent(JSON.parse(line) as VoiceEvent)
        } catch {
          // мусор в stdout игнорируем
        }
      }
    })

    proc.stderr.setEncoding('utf8')
    proc.stderr.on('data', (chunk: string) => {
      const text = chunk.trim()
      if (text) console.warn('[voice]', text.slice(0, 400))
    })

    proc.on('error', (error) => this.onEvent({ type: 'error', message: String(error) }))
    proc.on('exit', () => {
      this.proc = null
    })

    this.proc = proc
  }

  send(command: VoiceCommand): void {
    if (!this.proc) return
    try {
      this.proc.stdin.write(JSON.stringify(command) + '\n')
    } catch {
      // процесс уже умер
    }
  }

  listen(): void {
    this.send({ cmd: 'listen' })
  }

  get isRunning(): boolean {
    return !!this.proc
  }

  speak(text: string, pitch: number, speaker: string, model: string, emotion = 'neutral'): void {
    this.send({ cmd: 'speak', text, pitch, speaker, model, emotion })
  }

  stopSpeech(): void {
    this.send({ cmd: 'stopSpeech' })
  }

  setWake(enabled: boolean): void {
    this.send({ cmd: 'wake', enabled })
  }

  setAlwaysListen(enabled: boolean): void {
    this.send({ cmd: 'alwaysListen', enabled })
  }

  preloadTts(model: string): void {
    this.send({ cmd: 'preload', model })
  }

  setWakeWords(words: string[]): void {
    this.send({ cmd: 'wakeWords', words })
  }

  stop(): void {
    this.send({ cmd: 'quit' })
    this.proc?.kill()
    this.proc = null
  }
}
