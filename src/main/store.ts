import { app } from 'electron'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

/**
 * Минимальное JSON-хранилище. Пишем с задержкой, чтобы не дёргать диск на каждый чанк.
 */
export class JsonStore<T extends object> {
  private readonly file: string
  private value: T
  private timer: NodeJS.Timeout | null = null

  constructor(fileName: string, defaults: T) {
    this.file = join(app.getPath('userData'), fileName)
    mkdirSync(dirname(this.file), { recursive: true })
    let loaded: Partial<T> | null = null
    try {
      loaded = existsSync(this.file) ? (JSON.parse(readFileSync(this.file, 'utf8')) as Partial<T>) : null
    } catch {
      loaded = null
    }
    this.value = loaded ? { ...defaults, ...loaded } : defaults
  }

  get(): T {
    return this.value
  }

  set(patch: Partial<T>): T {
    this.value = { ...this.value, ...patch }
    this.scheduleFlush()
    return this.value
  }

  private scheduleFlush(): void {
    if (this.timer) clearTimeout(this.timer)
    this.timer = setTimeout(() => this.flush(), 300)
  }

  flush(): void {
    if (this.timer) {
      clearTimeout(this.timer)
      this.timer = null
    }
    try {
      writeFileSync(this.file, JSON.stringify(this.value, null, 2), 'utf8')
    } catch (error) {
      console.error('[store] не удалось сохранить', this.file, error)
    }
  }
}
