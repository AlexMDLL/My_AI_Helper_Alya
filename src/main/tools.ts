import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdir } from 'node:fs/promises'
import { join } from 'node:path'
import os from 'node:os'
import { app, shell } from 'electron'
import type { ToolCallRequest } from './providers'

export interface ToolOutcome {
  ok: boolean
  text: string
}

export interface ToolContext {
  confirm(request: { description: string; detail?: string }): Promise<boolean>
  remember(fact: string): void
  defaultCity: string
}

export const TOOL_DEFS = [
  {
    type: 'function',
    function: {
      name: 'open_app',
      description:
        'Открыть приложение на компьютере пользователя. Подходит для: калькулятор, блокнот, браузер, проводник, настройки, steam, telegram, discord, spotify, vscode.',
      parameters: {
        type: 'object',
        properties: {
          name: { type: 'string', description: 'Название приложения (по-русски или по-английски)' }
        },
        required: ['name']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'open_url',
      description: 'Открыть сайт в браузере пользователя по ссылке.',
      parameters: {
        type: 'object',
        properties: {
          url: { type: 'string', description: 'Полная ссылка, начиная с https://' }
        },
        required: ['url']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'system_info',
      description: 'Узнать состояние компьютера: процессор, объём памяти, сколько свободно, время работы.',
      parameters: { type: 'object', properties: {} }
    }
  },
  {
    type: 'function',
    function: {
      name: 'get_weather',
      description:
        'Узнать текущую погоду в городе. Вызывай для любых вопросов про погоду, температуру, дождь, снег, ветер.',
      parameters: {
        type: 'object',
        properties: {
          city: {
            type: 'string',
            description: 'Город, например Москва. Если не указан — берётся город из настроек.'
          }
        }
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'get_time',
      description: 'Узнать текущие дату и время на компьютере пользователя.',
      parameters: { type: 'object', properties: {} }
    }
  },
  {
    type: 'function',
    function: {
      name: 'screenshot',
      description: 'Сделать скриншот экрана и сохранить его в папку «Изображения/Jarvis».',
      parameters: { type: 'object', properties: {} }
    }
  },
  {
    type: 'function',
    function: {
      name: 'media_control',
      description:
        'Управление текущим музыкальным плеером (Spotify и любой другой): пауза, воспроизведение, следующий или предыдущий трек. Используй именно этот инструмент для команд «включи музыку», «поставь на паузу», «следующий трек» — никогда не ищи mp3-файлы и не сканируй диск.',
      parameters: {
        type: 'object',
        properties: {
          action: {
            type: 'string',
            enum: ['playpause', 'play', 'pause', 'next', 'prev'],
            description: 'playpause — включить/пауза, next — следующий, prev — предыдущий'
          }
        },
        required: ['action']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'remember',
      description:
        'Запомнить важный факт о пользователе надолго. Пиши факт в третьем лице, коротко и по делу, например: «Пользователя зовут Алексей», «Любит тёмный шоколад», «Живёт в Москве». Не начинай со слов «я запомнила» или «запомни».',
      parameters: {
        type: 'object',
        properties: {
          fact: { type: 'string', description: 'Короткий факт на русском, до 140 символов' }
        },
        required: ['fact']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'run_powershell',
      description:
        'Выполнить команду PowerShell на компьютере пользователя. Использовать только если без этого не обойтись: сложные действия, которых нет среди других инструментов.',
      parameters: {
        type: 'object',
        properties: {
          command: { type: 'string', description: 'Команда PowerShell' },
          reason: { type: 'string', description: 'Зачем это нужно, коротко' }
        },
        required: ['command']
      }
    }
  }
]

const APP_ALIASES: Record<string, string> = {
  калькулятор: 'calc',
  calc: 'calc',
  calculator: 'calc',
  блокнот: 'notepad',
  notepad: 'notepad',
  браузер: 'chrome',
  хром: 'chrome',
  chrome: 'chrome',
  'google chrome': 'chrome',
  firefox: 'firefox',
  'фейр фокс': 'firefox',
  фаерфокс: 'firefox',
  мозила: 'firefox',
  опера: 'opera',
  opera: 'opera',
  проводник: 'explorer',
  explorer: 'explorer',
  steam: 'steam',
  стим: 'steam',
  telegram: 'telegram',
  телеграм: 'telegram',
  телега: 'telegram',
  discord: 'discord',
  дискорд: 'discord',
  дс: 'discord',
  spotify: 'spotify',
  спотифай: 'spotify',
  спотик: 'spotify',
  vscode: 'code',
  code: 'code',
  код: 'code',
  paint: 'mspaint',
  пейнт: 'mspaint',
  терминал: 'wt',
  terminal: 'wt',
  cmd: 'cmd',
  настройки: 'ms-settings:',
  settings: 'ms-settings:',
  диспетчер: 'taskmgr',
  taskmgr: 'taskmgr'
}

const LOCALAPPDATA = process.env.LOCALAPPDATA ?? ''
const ROAMINGAPPDATA = process.env.APPDATA ?? ''

const KNOWN_APPS: Record<string, { paths: string[]; args?: string[]; protocol?: string }> = {
  steam: {
    paths: ['C:\\Program Files (x86)\\Steam\\steam.exe', 'C:\\Program Files\\Steam\\steam.exe'],
    protocol: 'steam://open/main'
  },
  discord: {
    paths: [`${LOCALAPPDATA}\\Discord\\Update.exe`, `${LOCALAPPDATA}\\Discord\\Discord.exe`],
    args: ['--processStart', 'Discord.exe'],
    protocol: 'discord://'
  },
  spotify: {
    paths: [`${ROAMINGAPPDATA}\\Spotify\\Spotify.exe`, `${LOCALAPPDATA}\\Microsoft\\WindowsApps\\Spotify.exe`],
    protocol: 'spotify://'
  },
  code: {
    paths: [`${LOCALAPPDATA}\\Programs\\Microsoft VS Code\\Code.exe`]
  },
  telegram: {
    paths: [`${ROAMINGAPPDATA}\\Telegram Desktop\\Telegram.exe`, `${LOCALAPPDATA}\\Telegram Desktop\\Telegram.exe`]
  }
}

function launch(target: string): void {
  const child = spawn('cmd.exe', ['/c', 'start', '', target], {
    windowsHide: true,
    detached: true,
    stdio: 'ignore'
  })
  child.unref()
}

function runPowerShell(command: string, timeoutMs = 25000): Promise<{ code: number; out: string; err: string }> {
  return new Promise((resolve) => {
    const child = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command], {
      windowsHide: true
    })
    let out = ''
    let err = ''
    const timer = setTimeout(() => child.kill(), timeoutMs)
    child.stdout.on('data', (data) => {
      out += String(data)
    })
    child.stderr.on('data', (data) => {
      err += String(data)
    })
    child.on('close', (code) => {
      clearTimeout(timer)
      resolve({ code: code ?? -1, out: out.trim(), err: err.trim() })
    })
    child.on('error', (error) => {
      clearTimeout(timer)
      resolve({ code: -1, out: '', err: String(error) })
    })
  })
}

export async function executeTool(call: ToolCallRequest, ctx: ToolContext): Promise<ToolOutcome> {
  const name = call.name
  const args = call.arguments ?? {}

  if (name === 'get_time') {
    const now = new Date()
    return {
      ok: true,
      text: now.toLocaleString('ru-RU', { dateStyle: 'full', timeStyle: 'short' })
    }
  }

  if (name === 'get_weather') {
    const city = String(args.city ?? '').trim() || ctx.defaultCity || 'Москва'
    return getWeatherForCity(city)
  }

  if (name === 'media_control') {
    const action = String(args.action ?? 'playpause').toLowerCase()
    const keys: Record<string, number> = { playpause: 0xb3, play: 0xb3, pause: 0xb3, next: 0xb0, prev: 0xb1, stop: 0xb2 }
    const key = keys[action] ?? 0xb3
    const source =
      'using System; using System.Runtime.InteropServices; public class Kbd { [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo); }'
    const result = await runPowerShell(
      `Add-Type -TypeDefinition '${source}'; [Kbd]::keybd_event(${key},0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [Kbd]::keybd_event(${key},0,2,[UIntPtr]::Zero)`
    )
    if (result.code !== 0) return { ok: false, text: result.err || 'Не получилось управлять плеером' }
    if (action === 'next') return { ok: true, text: 'Включаю следующий трек.' }
    if (action === 'prev') return { ok: true, text: 'Возвращаю предыдущий трек.' }
    return { ok: true, text: 'Готово.' }
  }

  if (name === 'open_app') {
    const raw = String(args.name ?? '').trim().toLowerCase()
    if (!raw) return { ok: false, text: 'Не указано приложение' }

    if (/visual studio$|визуал студио|вижн студио|вижуал студио/.test(raw)) {
      const vswhere = 'C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer\\vswhere.exe'
      if (!existsSync(vswhere)) return { ok: false, text: 'Visual Studio не найдена на этом компьютере' }
      const found = await runPowerShell(`& "${vswhere}" -latest -property productPath`)
      const path = found.out.split(/\r?\n/)[0]?.trim()
      if (!path || !existsSync(path)) return { ok: false, text: 'Visual Studio не найдена на этом компьютере' }
      spawn(path, [], { detached: true, stdio: 'ignore', windowsHide: true }).unref()
      return { ok: true, text: 'Открываю Visual Studio' }
    }

    const target = APP_ALIASES[raw] ?? raw

    const known = KNOWN_APPS[target] ?? KNOWN_APPS[raw]
    if (known) {
      const executable = known.paths.find((candidate) => candidate && existsSync(candidate))
      if (executable) {
        spawn(executable, known.args ?? [], { detached: true, stdio: 'ignore', windowsHide: true }).unref()
        return { ok: true, text: `Открываю «${raw}»` }
      }
      if (known.protocol) {
        try {
          await shell.openExternal(known.protocol)
          return { ok: true, text: `Открываю «${raw}»` }
        } catch {
          // ниже попробуем ярлык из меню «Пуск»
        }
      }
    }

    if (!(raw in APP_ALIASES) || known) {
      // если это не известный алиас — поищем ярлык в меню «Пуск»
      const safeName = raw.replace(/['"`]/g, '')
      const found = await runPowerShell(
        `$n='${safeName}'; Get-ChildItem "$env:ProgramData\\Microsoft\\Windows\\Start Menu\\Programs","$env:APPDATA\\Microsoft\\Windows\\Start Menu\\Programs" -Recurse -Filter *.lnk -ErrorAction SilentlyContinue | Where-Object { $_.BaseName -match [regex]::Escape($n) } | Select-Object -First 1 -ExpandProperty FullName`
      )
      const shortc = found.out.split(/\r?\n/)[0]?.trim()
      if (shortc && existsSync(shortc)) {
        spawn('cmd.exe', ['/c', 'start', '', shortc], { windowsHide: true, detached: true, stdio: 'ignore' }).unref()
        return { ok: true, text: `Открываю «${raw}»` }
      }
    }
    const safe = /^[a-zа-яё0-9 ._-]{1,64}$/i.test(target) || target.startsWith('ms-settings:')
    if (!safe) return { ok: false, text: `Не рискну запускать «${raw}»` }
    launch(target)
    return { ok: true, text: `Открываю «${raw}»` }
  }

  if (name === 'open_url') {
    const url = String(args.url ?? '').trim()
    if (!/^https?:\/\//i.test(url)) return { ok: false, text: 'Ссылка должна начинаться с http:// или https://' }
    await shell.openExternal(url)
    return { ok: true, text: `Открываю ${url}` }
  }

  if (name === 'system_info') {
    const cpus = os.cpus()
    const total = Math.round((os.totalmem() / 1024 ** 3) * 10) / 10
    const free = Math.round((os.freemem() / 1024 ** 3) * 10) / 10
    const hours = Math.round(os.uptime() / 3600)
    return {
      ok: true,
      text: `Процессор: ${cpus[0]?.model ?? 'неизвестен'} (${cpus.length} потоков). ОЗУ: ${total} ГБ, свободно ${free} ГБ. Аптайм: ${hours} ч.`
    }
  }

  if (name === 'screenshot') {
    try {
      const dir = join(app.getPath('pictures'), 'Jarvis')
      await mkdir(dir, { recursive: true })
      const file = join(dir, `screenshot-${new Date().toISOString().replace(/[:.]/g, '-')}.png`)
      const script = [
        'Add-Type -AssemblyName System.Windows.Forms,System.Drawing;',
        '$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds;',
        '$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height;',
        '$g=[System.Drawing.Graphics]::FromImage($bmp);',
        '$g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size);',
        `$bmp.Save('${file.replace(/'/g, "''")}',[System.Drawing.Imaging.ImageFormat]::Png);`,
        '$g.Dispose(); $bmp.Dispose();'
      ].join(' ')
      const result = await runPowerShell(script, 20000)
      if (result.code !== 0) return { ok: false, text: `Скриншот не удался: ${result.err.slice(0, 200)}` }
      return { ok: true, text: `Скриншот сохранён: ${file}` }
    } catch (error) {
      return { ok: false, text: `Скриншот не удался: ${String(error).slice(0, 200)}` }
    }
  }

  if (name === 'remember') {
    const fact = String(args.fact ?? '').trim()
    if (!fact) return { ok: false, text: 'Пустой факт' }
    ctx.remember(fact)
    return { ok: true, text: `Запомнила: ${fact}` }
  }

  if (name === 'run_powershell') {
    const command = String(args.command ?? '').trim()
    const reason = String(args.reason ?? '').trim()
    if (!command) return { ok: false, text: 'Пустая команда' }
    const allowed = await ctx.confirm({
      description: reason || 'Выполнить команду PowerShell',
      detail: command
    })
    if (!allowed) return { ok: false, text: 'Пользователь отклонил выполнение' }
    const result = await runPowerShell(command)
    const output = (result.out || result.err || 'без вывода').slice(0, 1500)
    return { ok: result.code === 0, text: `Код ${result.code}. ${output}` }
  }

  return { ok: false, text: `Неизвестный инструмент: ${name}` }
}

const WEATHER_CODES: Record<number, string> = {
  0: 'ясно',
  1: 'преимущественно ясно',
  2: 'переменная облачность',
  3: 'пасмурно',
  45: 'туман',
  48: 'изморозь',
  51: 'лёгкая морось',
  53: 'морось',
  55: 'сильная морось',
  56: 'ледяная морось',
  57: 'сильная ледяная морось',
  61: 'небольшой дождь',
  63: 'дождь',
  65: 'сильный дождь',
  66: 'ледяной дождь',
  67: 'сильный ледяной дождь',
  71: 'небольшой снег',
  73: 'снег',
  75: 'сильный снег',
  77: 'снежные зёрна',
  80: 'небольшие ливни',
  81: 'ливни',
  82: 'сильные ливни',
  85: 'снегопады',
  86: 'сильные снегопады',
  95: 'гроза',
  96: 'гроза с градом',
  99: 'сильная гроза с градом'
}

export async function getWeatherForCity(city: string): Promise<ToolOutcome> {
  try {
    const geoResponse = await fetch(
      `https://geocoding-api.open-meteo.com/v1/search?name=${encodeURIComponent(city)}&count=1&language=ru&format=json`,
      { signal: AbortSignal.timeout(8000) }
    )
    if (!geoResponse.ok) return { ok: false, text: 'Сервис погоды недоступен' }
    const geo = (await geoResponse.json()) as {
      results?: Array<{ latitude: number; longitude: number; name: string }>
    }
    const place = geo.results?.[0]
    if (!place) return { ok: false, text: `Не нашла город «${city}»` }

    const forecastResponse = await fetch(
      `https://api.open-meteo.com/v1/forecast?latitude=${place.latitude}&longitude=${place.longitude}` +
        '&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m' +
        '&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1',
      { signal: AbortSignal.timeout(8000) }
    )
    if (!forecastResponse.ok) return { ok: false, text: 'Сервис погоды недоступен' }

    const data = (await forecastResponse.json()) as {
      current?: Record<string, number>
      daily?: { temperature_2m_max?: number[]; temperature_2m_min?: number[] }
    }
    const current = data.current
    if (!current) return { ok: false, text: 'Нет данных о погоде' }

    const temp = Math.round(Number(current.temperature_2m))
    const feels = Math.round(Number(current.apparent_temperature))
    const wind = Math.round(Number(current.wind_speed_10m))
    const humidity = Math.round(Number(current.relative_humidity_2m))
    const description = WEATHER_CODES[Number(current.weather_code)] ?? 'без описания'
    const max = data.daily?.temperature_2m_max?.[0]
    const min = data.daily?.temperature_2m_min?.[0]
    const range = max !== undefined && min !== undefined ? ` Сегодня ${Math.round(min)}…${Math.round(max)}°.` : ''

    return {
      ok: true,
      text: `${place.name}: сейчас ${temp}°, ощущается как ${feels}°, ${description}, ветер ${wind} км/ч, влажность ${humidity}%.${range}`
    }
  } catch {
    return { ok: false, text: 'Не удалось получить погоду — сервис недоступен' }
  }
}

export function describeTool(call: ToolCallRequest): string {
  if (call.name === 'open_app') return `Открываю «${String(call.arguments.name ?? '')}»`
  if (call.name === 'open_url') return `Открываю ссылку`
  if (call.name === 'system_info') return 'Смотрю состояние компьютера'
  if (call.name === 'screenshot') return 'Делаю скриншот'
  if (call.name === 'remember') return 'Запоминаю'
  if (call.name === 'get_weather') return 'Узнаю погоду'
  if (call.name === 'get_time') return 'Смотрю время'
  if (call.name === 'run_powershell') return 'Готовлю команду PowerShell'
  return 'Выполняю действие'
}
