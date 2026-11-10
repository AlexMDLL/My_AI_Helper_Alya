import { useEffect, useState } from 'react'
import {
  PERSONA_PRESETS,
  PROVIDERS,
  VOICE_SPEAKERS,
  WAKE_LABELS,
  type BackendStatus,
  type MemoryFact,
  type ProviderId,
  type Settings,
  type VoiceModel,
  type VoiceSpeaker,
  type WakeWordId
} from '../../shared/types'

interface Props {
  settings: Settings
  status: BackendStatus | null
  onSave: (patch: Partial<Settings>) => void
  onClose: () => void
}

export default function SettingsPanel({ settings, status, onSave, onClose }: Props) {
  const [provider, setProvider] = useState<ProviderId>(settings.provider)
  const [model, setModel] = useState(settings.model)
  const [apiKey, setApiKey] = useState(settings.apiKey)
  const [ollamaUrl, setOllamaUrl] = useState(settings.ollamaUrl)
  const [persona, setPersona] = useState(settings.persona)
  const [custom, setCustom] = useState(settings.customInstructions ?? '')
  const [name, setName] = useState(settings.assistantName)
  const [wakeWord, setWakeWord] = useState<WakeWordId>(settings.wakeWord)
  const [wake, setWake] = useState(settings.wake)
  const [alwaysListen, setAlwaysListen] = useState(settings.alwaysListen ?? false)
  const [ttsEnabled, setTtsEnabled] = useState(settings.tts)
  const [scale, setScale] = useState(settings.scale ?? 0.85)
  const [character, setCharacter] = useState<'sprite' | 'orb' | 'live2d'>(settings.character ?? 'sprite')
  const [live2dModel, setLive2dModel] = useState<'hiyori' | 'alexia' | 'jane' | 'workshop'>(settings.live2dModel ?? 'workshop')
  const [jacket, setJacket] = useState(settings.workshopJacket !== false)
  const [vtsMode, setVtsMode] = useState(settings.vtsMode ?? false)
  const [animationMode, setAnimationMode] = useState<'live2d' | 'sprites'>(settings.animationMode ?? 'live2d')
  const [bakeStatus, setBakeStatus] = useState('')
  const [vtsStatus, setVtsStatus] = useState<string>('')
  const [city, setCity] = useState(settings.city ?? 'Москва')
  const [geoStatus, setGeoStatus] = useState<string | null>(null)
  const [bubbles, setBubbles] = useState(settings.bubbles ?? true)
  const [care, setCare] = useState(settings.care ?? true)
  const [hideFullscreen, setHideFullscreen] = useState(settings.hideFullscreen ?? true)
  const [bedtime, setBedtime] = useState(settings.careBedtime ?? 23)
  const [session, setSession] = useState(settings.careSession ?? 120)
  const [speaker, setSpeaker] = useState<VoiceSpeaker>(settings.voiceSpeaker ?? 'kseniya')
  const [pitch, setPitch] = useState(settings.voicePitch ?? 0.97)
  const [voiceModel, setVoiceModel] = useState<VoiceModel>(settings.voiceModel ?? 'v5')
  const [check, setCheck] = useState<BackendStatus | null>(null)
  const [checking, setChecking] = useState(false)
  const [facts, setFacts] = useState<MemoryFact[]>([])
  const [rebuildNote, setRebuildNote] = useState<string | null>(null)

  useEffect(() => {
    const off = window.pet.onBakeProgress((payload) => {
      setBakeStatus(`${payload.scene} ${payload.index + 1}/${payload.total}`)
    })
    return off
  }, [])
  const [newFact, setNewFact] = useState('')

  useEffect(() => {
    void window.pet.memoryList().then(setFacts)
  }, [])

  const meta = PROVIDERS[provider]

  const addFact = async (): Promise<void> => {
    const text = newFact.trim()
    if (!text) return
    await window.pet.memoryAdd(text)
    setFacts(await window.pet.memoryList())
    setNewFact('')
  }

  const removeFact = async (id: string): Promise<void> => {
    setFacts(await window.pet.memoryDelete(id))
  }

  const clearFacts = async (): Promise<void> => {
    await window.pet.memoryClear()
    setFacts([])
  }

  const rebuildFacts = async (): Promise<void> => {
    setRebuildNote('думаю…')
    const result = await window.pet.memoryRebuild()
    setFacts(result.facts)
    setRebuildNote(result.added > 0 ? `добавлено фактов: ${result.added}` : 'нового не нашла')
  }


  const pickProvider = (id: ProviderId): void => {
    setProvider(id)
    setModel(PROVIDERS[id].defaultModel)
    setCheck(null)
  }

  const pickPreset = (presetId: string): void => {
    const preset = PERSONA_PRESETS.find((item) => item.id === presetId)
    if (!preset) return
    setName(preset.name)
    setPersona(preset.persona)
  }

  const runCheck = async (): Promise<void> => {
    setChecking(true)
    setCheck(null)
    const result = await window.pet.checkBackend({ ...settings, provider, model, apiKey, ollamaUrl })
    setCheck(result)
    setChecking(false)
  }

  const save = (): void => {
    onSave({
      provider,
      model,
      apiKey,
      ollamaUrl,
      persona,
      customInstructions: custom,
      assistantName: name.trim() || 'Аля',
      wakeWord,
      wake,
      alwaysListen,
      tts: ttsEnabled,
      scale,
      character,
      live2dModel,
      workshopJacket: jacket,
      vtsMode,
      animationMode,
      city,
      bubbles,
      care,
      hideFullscreen,
      careBedtime: bedtime,
      careSession: session,
      voiceSpeaker: speaker,
      voicePitch: pitch,
      voiceModel
    })
  }

  const shown = check ?? status

  return (
    <section className="panel settings">
      <header className="panel-head">
        <div className="brand">
          <b>НАСТРОЙКИ</b>
          <span>характер и мозг</span>
        </div>
        <div className="head-actions">
          <button type="button" className="icon-btn" title="Закрыть" onClick={onClose}>
            ✕
          </button>
        </div>
      </header>

      <div className="settings-body">
        <div className="field">
          <label>Персонаж</label>
          <div className="presets">
            {PERSONA_PRESETS.map((preset) => (
              <button
                key={preset.id}
                type="button"
                className={`preset${persona === preset.persona ? ' active' : ''}`}
                onClick={() => pickPreset(preset.id)}
              >
                <b>{preset.name}</b>
                <span>{preset.tagline}</span>
              </button>
            ))}
          </div>
          <input value={name} onChange={(event) => setName(event.target.value)} placeholder="Имя" maxLength={24} />
        </div>

        <div className="field">
          <label>На что откликаться</label>
          <div className="segmented">
            {(Object.keys(WAKE_LABELS) as WakeWordId[]).map((id) => (
              <button
                key={id}
                type="button"
                className={`segment${wakeWord === id ? ' active' : ''}`}
                onClick={() => setWakeWord(id)}
              >
                {WAKE_LABELS[id]}
              </button>
            ))}
          </div>
        </div>

        <div className="field">
          <label>Мозг</label>
          <div className="providers">
            {(Object.keys(PROVIDERS) as ProviderId[]).map((id) => (
              <button
                key={id}
                type="button"
                className={`provider${provider === id ? ' active' : ''}`}
                onClick={() => pickProvider(id)}
              >
                <span className="provider-name">{PROVIDERS[id].label}</span>
                <span className="provider-hint">{PROVIDERS[id].hint}</span>
              </button>
            ))}
          </div>
        </div>

        {provider === 'ollama' && (
          <div className="field">
            <label>Адрес Ollama</label>
            <input value={ollamaUrl} onChange={(event) => setOllamaUrl(event.target.value)} spellCheck={false} />
          </div>
        )}

        <div className="field">
          <label>Модель</label>
          <input list="model-options" value={model} onChange={(event) => setModel(event.target.value)} spellCheck={false} />
          <datalist id="model-options">
            {meta.models.map((item) => (
              <option key={item} value={item} />
            ))}
          </datalist>
        </div>

        {meta.needsKey && (
          <div className="field">
            <label>
              API-ключ
              {meta.keyUrl && (
                <button type="button" className="link" onClick={() => void window.pet.openExternal(meta.keyUrl as string)}>
                  получить бесплатно
                </button>
              )}
            </label>
            <input
              type="password"
              value={apiKey}
              onChange={(event) => setApiKey(event.target.value)}
              placeholder="sk-…"
              spellCheck={false}
            />
          </div>
        )}

        <div className="field">
          <label>
            Память · {facts.length}
            <button type="button" className="link" onClick={() => void rebuildFacts()}>
              обновить
            </button>
            {facts.length > 0 && (
              <button type="button" className="link" onClick={() => void clearFacts()}>
                очистить
              </button>
            )}
          </label>
          {rebuildNote && <span className="hint">{rebuildNote}</span>}
          <div className="facts">
            {facts.length === 0 && <span className="hint">Пока пусто. Скажи «запомни, что…» — и я запишу.</span>}
            {facts.map((fact) => (
              <div key={fact.id} className="fact">
                <span>{fact.text}</span>
                <button type="button" className="fact-del" title="Удалить" onClick={() => void removeFact(fact.id)}>
                  ✕
                </button>
              </div>
            ))}
          </div>
          <div className="fact-add">
            <input
              value={newFact}
              placeholder="Добавить факт вручную…"
              onChange={(event) => setNewFact(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault()
                  void addFact()
                }
              }}
            />
            <button type="button" className="ghost small" onClick={() => void addFact()}>
              +
            </button>
          </div>
        </div>

        <div className="field">
          <label>
            Город для погоды
            <button
              type="button"
              className="link"
              onClick={() => {
                setGeoStatus('определяю…')
                void window.pet.detectCity().then((detected) => {
                  if (detected) {
                    setCity(detected)
                    setGeoStatus(`определён: ${detected}`)
                  } else {
                    setGeoStatus('не удалось, впиши вручную')
                  }
                })
              }}
            >
              определить
            </button>
          </label>
          <input value={city} onChange={(event) => setCity(event.target.value)} placeholder="Москва" />
          {geoStatus && <span className="hint">{geoStatus}</span>}
        </div>

        <div className="field">
          <label>Уведомления</label>
          <label className="toggle">
            <input type="checkbox" checked={bubbles} onChange={(event) => setBubbles(event.target.checked)} />
            Показывать ответы пузырями над персонажем
          </label>
          <label className="toggle">
            <input type="checkbox" checked={care} onChange={(event) => setCare(event.target.checked)} />
            Заботиться: напоминать про сон и перерывы
          </label>
          <label className="toggle">
            <input
              type="checkbox"
              checked={hideFullscreen}
              onChange={(event) => setHideFullscreen(event.target.checked)}
            />
            Сворачиваться в полноэкранных приложениях
          </label>
          {care && (
            <>
              <label>
                Пора спать в {bedtime}:00
              </label>
              <input
                type="range"
                min="20"
                max="23"
                step="1"
                value={bedtime}
                onChange={(event) => setBedtime(Number(event.target.value))}
              />
              <label>Перерыв после {session} минут за компьютером</label>
              <input
                type="range"
                min="60"
                max="240"
                step="10"
                value={session}
                onChange={(event) => setSession(Number(event.target.value))}
              />
            </>
          )}
        </div>

        <div className="field">
          <label>Модель</label>
          <div className="segmented">
            <button
              type="button"
              className={`segment${character === 'sprite' ? ' active' : ''}`}
              onClick={() => setCharacter('sprite')}
            >
              Спрайт Али
            </button>
            <button
              type="button"
              className={`segment${character === 'orb' ? ' active' : ''}`}
              onClick={() => setCharacter('orb')}
            >
              Пузырь
            </button>
            <button
              type="button"
              className={`segment${character === 'live2d' ? ' active' : ''}`}
              onClick={() => setCharacter('live2d')}
            >
              Живая 2D
            </button>
          </div>
          {character === 'live2d' && (
            <div className="segmented">
              <button
                type="button"
                className={`segment${live2dModel === 'alexia' ? ' active' : ''}`}
                onClick={() => setLive2dModel('alexia')}
              >
                Alexia
              </button>
              <button
                type="button"
                className={`segment${live2dModel === 'workshop' ? ' active' : ''}`}
                onClick={() => setLive2dModel('workshop')}
              >
                VTS
              </button>
              <button
                type="button"
                className={`segment${live2dModel === 'jane' ? ' active' : ''}`}
                onClick={() => setLive2dModel('jane')}
              >
                Jane
              </button>
              <button
                type="button"
                className={`segment${live2dModel === 'hiyori' ? ' active' : ''}`}
                onClick={() => setLive2dModel('hiyori')}
              >
                Hiyori
              </button>
            </div>
          )}
          {character === 'live2d' && live2dModel === 'workshop' && (
            <div className="segmented">
              <button
                type="button"
                className={`segment${jacket ? ' active' : ''}`}
                onClick={() => setJacket(true)}
              >
                В куртке
              </button>
              <button
                type="button"
                className={`segment${!jacket ? ' active' : ''}`}
                onClick={() => setJacket(false)}
              >
                Без куртки
              </button>
            </div>
          )}
          <label className="toggle">
            <input
              type="checkbox"
              checked={vtsMode}
              onChange={(event) => setVtsMode(event.target.checked)}
            />
            Показывать модель в VTubeStudio (свою скрыть)
          </label>
          {vtsMode && (
            <>
              <button
                type="button"
                className="ghost"
                onClick={() => {
                  setVtsStatus('подключаюсь…')
                  void window.pet
                    .vtsConnect()
                    .then((status) => setVtsStatus(status.message))
                    .catch((error: unknown) => setVtsStatus(String(error)))
                }}
              >
                Подключить VTubeStudio
              </button>
              {vtsStatus && <span className="hint">{vtsStatus}</span>}
          <button
            type="button"
            className="ghost"
            onClick={() => {
              setBakeStatus('записываю из VTS 6 секунд…')
              void window.pet
                .recordFromVts(6, 30)
                .then((result) => setBakeStatus(`записано: ${result.scene}, кадров ${result.frames}`))
                .catch((error: unknown) => setBakeStatus(String(error)))
            }}
          >
            Записать из VTS (6 с)
          </button>
              <span className="hint">
                В VTS: Settings → API → «Allow API access». Модель должна быть загружена в VTS.
              </span>
            </>
          )}
        </div>

        <div className="field">
          <label>Размер персонажа · {Math.round(scale * 100)}%</label>
          <input
            type="range"
            min="0.5"
            max="1.3"
            step="0.05"
            value={scale}
            onChange={(event) => setScale(Number(event.target.value))}
          />
        </div>

        <div className="field">
          <label>Голос</label>
          <label className="toggle">
            <input type="checkbox" checked={wake} onChange={(event) => setWake(event.target.checked)} />
            Слушать «{name.trim() || 'Аля'}» (микрофон всегда включён)
          </label>
          <label className="toggle">
            <input
              type="checkbox"
              checked={alwaysListen}
              onChange={(event) => setAlwaysListen(event.target.checked)}
            />
            Слушать без слова-активатора (говори сразу)
          </label>
          {alwaysListen && (
            <span className="hint">
              Может ловить фразы из видео и музыки. Выключается фразой «стоп слушать».
            </span>
          )}
          <label className="toggle">
            <input type="checkbox" checked={ttsEnabled} onChange={(event) => setTtsEnabled(event.target.checked)} />
            Озвучивать ответы (Silero)
          </label>

          <div className="segmented">
            <button
              type="button"
              className={`segment${voiceModel === 'v5' ? ' active' : ''}`}
              onClick={() => setVoiceModel('v5')}
            >
              Silero v5
            </button>
            <button
              type="button"
              className={`segment${voiceModel === 'v4' ? ' active' : ''}`}
              onClick={() => setVoiceModel('v4')}
            >
              Silero v4
            </button>
          </div>

          <div className="segmented">
            {(Object.keys(VOICE_SPEAKERS) as VoiceSpeaker[]).map((id) => (
              <button
                key={id}
                type="button"
                className={`segment${speaker === id ? ' active' : ''}`}
                onClick={() => setSpeaker(id)}
              >
                {VOICE_SPEAKERS[id]}
              </button>
            ))}
          </div>

          <label>
            Тембр · {pitch.toFixed(2)} (0.85 — ниже и мягче, 1.30 — выше)
          </label>
          <input
            type="range"
            min="0.85"
            max="1.3"
            step="0.01"
            value={pitch}
            onChange={(event) => setPitch(Number(event.target.value))}
          />

          <button
            type="button"
            className="ghost"
            onClick={() =>
              window.pet.previewVoice({
                text: 'Привет! Это Аля. Вот как звучит мой голос.',
                speaker,
                model: voiceModel,
                pitch
              })
            }
          >
            Проверить голос
          </button>
          <div className="segmented">
            <button
              type="button"
              className={`segment${animationMode === 'live2d' ? ' active' : ''}`}
              onClick={() => setAnimationMode('live2d')}
            >
              Live2D
            </button>
            <button
              type="button"
              className={`segment${animationMode === 'sprites' ? ' active' : ''}`}
              onClick={() => setAnimationMode('sprites')}
            >
              Спрайты
            </button>
          </div>
          <button
            type="button"
            className="ghost"
            onClick={() => window.pet.testAnimation()}
          >
            Тест анимации
          </button>
          <button
            type="button"
            className="ghost"
            onClick={() => {
              setBakeStatus('запекаю…')
              void window.pet
                .bakeAnimations()
                .then(() => setBakeStatus('готово'))
                .catch((error: unknown) => setBakeStatus(String(error)))
            }}
          >
            Запечь анимации в PNG
          </button>
          {bakeStatus && <span className="hint">{bakeStatus}</span>}
          <div className="field">
            <label>Версия</label>
            <span className="hint">сборка 10.11 · motion-анимации</span>
          </div>
        </div>

        <div className="field">
          <label>Характер</label>
          <textarea rows={7} value={persona} onChange={(event) => setPersona(event.target.value)} spellCheck={false} />
        </div>

        <div className="field">
          <label>Чему научить (правила)</label>
          <textarea
            rows={4}
            value={custom}
            onChange={(event) => setCustom(event.target.value)}
            placeholder="Например: музыку включай в Spotify, не ищи файлы. Называй меня по имени. Отвечай короче."
            spellCheck={false}
          />
        </div>

        {shown && <p className={`check ${shown.ok ? 'ok' : 'bad'}`}>{shown.detail}</p>}
      </div>

      <footer className="settings-foot">
        <button type="button" className="ghost" onClick={() => void runCheck()} disabled={checking}>
          {checking ? 'Проверяю…' : 'Проверить связь'}
        </button>
        <button type="button" className="primary" onClick={save}>
          Сохранить
        </button>
      </footer>
    </section>
  )
}
