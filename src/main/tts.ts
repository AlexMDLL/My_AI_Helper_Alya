import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { existsSync, unlinkSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { app } from 'electron'

/** Озвучка через Windows SAPI (голос ru-RU). Живёт отдельным процессом PowerShell. */
export class TtsService {
  private proc: ChildProcessWithoutNullStreams | null = null
  private buffer = ''

  constructor(private readonly onState: (speaking: boolean) => void) {}

  private ensure(): ChildProcessWithoutNullStreams | null {
    if (this.proc) return this.proc
    const script = join(app.getAppPath(), 'resources', 'tts.ps1')
    if (!existsSync(script)) return null

    const proc = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script], {
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
        if (line === 'START') this.onState(true)
        if (line === 'END') this.onState(false)
      }
    })
    proc.stderr.on('data', () => undefined)
    proc.on('exit', () => {
      this.proc = null
      this.onState(false)
    })

    this.proc = proc
    return proc
  }

  speak(text: string): void {
    const clean = sanitize(text)
    if (!clean) return
    const proc = this.ensure()
    if (!proc) return

    const file = join(app.getPath('temp'), `jarvis-tts-${Date.now()}.txt`)
    try {
      writeFileSync(file, clean, 'utf8')
      proc.stdin.write(file + '\n')
      setTimeout(() => {
        try {
          unlinkSync(file)
        } catch {
          // файл уже удалён
        }
      }, 60000)
    } catch (error) {
      console.warn('[tts] не удалось озвучить', error)
    }
  }

  stop(): void {
    this.proc?.kill()
    this.proc = null
  }
}

function sanitize(text: string): string {
  return text
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/https?:\/\/\S+/g, ' ')
    .replace(/[*_#>]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 600)
}
