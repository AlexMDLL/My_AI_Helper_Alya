import { useCallback, useEffect, useRef, useState } from 'react'
import Character, { type CharacterState } from './Character'
import ChatPanel, { type UiMessage } from './ChatPanel'
import SettingsPanel from './SettingsPanel'
import type { BackendStatus, Settings, ToolConfirmRequest } from '../../shared/types'

const greetingFor = (name: string): UiMessage => ({
  id: 'greeting',
  role: 'assistant',
  content: `Привет. Я ${name}. Ну что, соскучился? Спрашивай, только не зависай.`
})

export default function App() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [messages, setMessages] = useState<UiMessage[]>([])
  const [busy, setBusy] = useState(false)
  const [panelOpen, setPanelOpen] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [status, setStatus] = useState<BackendStatus | null>(null)
  const [bubble, setBubble] = useState<{ role: 'user' | 'assistant'; text: string; ts: number } | null>(null)
  const [bubbleLeaving, setBubbleLeaving] = useState(false)
  const [backdrop, setBackdrop] = useState<{ r: number; g: number; b: number } | null>(null)
  const [listening, setListening] = useState(false)
  const [interim, setInterim] = useState<string | null>(null)
  const [ttsSpeaking, setTtsSpeaking] = useState(false)
  const [pendingTool, setPendingTool] = useState<ToolConfirmRequest | null>(null)
  const [toolNote, setToolNote] = useState<string | null>(null)
  const [sprite, setSprite] = useState<string | null>(null)
  const [gestureCmd, setGestureCmd] = useState<{ name: string; id: number } | null>(null)
  const [baseSprite, setBaseSprite] = useState<string | null>(null)
  const [emotion, setEmotion] = useState<string | null>(null)
  const spriteTimer = useRef<number | null>(null)
  const activeId = useRef<string | null>(null)
  const panelOpenRef = useRef(false)
  const messagesRef = useRef<UiMessage[]>([])

  const sendRef = useRef<(text: string, opts?: { voice?: boolean }) => void>(() => undefined)
  const settingsRef = useRef<Settings | null>(null)

  useEffect(() => {
    messagesRef.current = messages
  }, [messages])

  const prevPanelOpen = useRef(false)

  useEffect(() => {
    panelOpenRef.current = panelOpen
    if (panelOpen) {
      setBubble(null)
      setBubbleLeaving(false)
    } else if (prevPanelOpen.current && settingsRef.current?.bubbles !== false) {
      const lastAssistant = [...messagesRef.current]
        .reverse()
        .find((message) => message.role === 'assistant' && !message.streaming && message.content)
      if (lastAssistant) setBubble({ role: 'assistant', text: lastAssistant.content, ts: Date.now() })
    }
    prevPanelOpen.current = panelOpen
  }, [panelOpen])

  useEffect(() => {
    settingsRef.current = settings
  }, [settings])

  useEffect(() => {
    void (async () => {
      const loaded = await window.pet.getSettings()
      setSettings(loaded)

      const stored = await window.pet.getHistory()
      if (stored.length > 0) {
        setMessages(stored.map((message, index) => ({ ...message, id: `h${index}` })))
      } else {
        setMessages([greetingFor(loaded.assistantName)])
        setPanelOpen(true)
      }

      setStatus(await window.pet.checkBackend(loaded))
    })()
  }, [])

  useEffect(() => {
    const off = window.pet.onChatEvent((event) => {
      if (event.type === 'status') {
        setToolNote(event.text)
        return
      }

      if (event.type === 'delta') {
        setMessages((prev) =>
          prev.map((message) =>
            message.id === event.id
              ? { ...message, content: message.content + event.text, streaming: true }
              : message
          )
        )
        return
      }

      if (event.type === 'done') {
        setMessages((prev) =>
          prev.map((message) =>
            message.id === event.id
              ? { ...message, content: event.text || message.content, streaming: false }
              : message
          )
        )
        setBusy(false)
        activeId.current = null
        if (!panelOpenRef.current && settingsRef.current?.bubbles !== false && event.text.trim()) {
          setBubble({ role: 'assistant', text: event.text.trim(), ts: Date.now() })
        }
        setToolNote(null)
        const lastUser = [...messagesRef.current].reverse().find((message) => message.role === 'user')
        const flirty = lastUser
          ? /(люблю|скучал|неравнодушн|красив|милая|обним|поцелу|признайся|соскучил|нравишься)/i.test(
              lastUser.content
            )
          : false
        const emotion = flirty ? 'shy' : detectEmotion(event.text)
        if (settingsRef.current?.tts && event.text.trim()) {
          window.pet.speak(event.text, emotion ?? 'neutral')
        }
        if (emotion) void showSprite(emotion)
        return
      }

      setMessages((prev) =>
        prev.map((message) =>
          message.id === event.id
            ? { ...message, content: event.text, streaming: false, error: true }
            : message
        )
      )
      setBusy(false)
      activeId.current = null
      setToolNote(null)
    })
    return off
  }, [])

  useEffect(() => {
    const offToggle = window.pet.onToggleChat(() => setPanelOpen((open) => !open))
    const offSettings = window.pet.onOpenSettings(() => {
      setPanelOpen(true)
      setSettingsOpen(true)
    })
    return () => {
      offToggle()
      offSettings()
    }
  }, [])

  useEffect(() => {
    const off = window.pet.onVoiceEvent((event) => {
      if (event.type === 'wake' || event.type === 'listening') {
        setListening(true)
        setInterim(null)
        return
      }
      if (event.type === 'partial') {
        setInterim(event.text ?? '')
        if (!panelOpenRef.current && settingsRef.current?.bubbles !== false && event.text?.trim()) {
          setBubble({ role: 'user', text: event.text.trim(), ts: Date.now() })
        }
        return
      }
      if (event.type === 'final') {
        setListening(false)
        setInterim(null)
        if (event.text?.trim()) {
          if (!panelOpenRef.current && settingsRef.current?.bubbles !== false) {
            setBubble({ role: 'user', text: event.text.trim(), ts: Date.now() })
          }
          sendRef.current(event.text, { voice: true })
        }
        return
      }
      if (event.type === 'idle' || event.type === 'bye') {
        setListening(false)
        setInterim(null)
        return
      }
      if (event.type === 'tts_start') {
        setTtsSpeaking(true)
        return
      }
      if (event.type === 'tts_end' || event.type === 'tts_error') {
        setTtsSpeaking(false)
        return
      }
      if (event.type === 'error') {
        console.warn('[voice]', event.message)
      }
    })
    return off
  }, [])

  useEffect(() => {
    const off = window.pet.onTtsState((speaking) => setTtsSpeaking(speaking))
    return off
  }, [])

  useEffect(() => {
    const off = window.pet.onToolConfirm((request) => {
      setPanelOpen(true)
      setPendingTool(request)
      setToolNote(null)
    })
    return off
  }, [])

  const answerTool = useCallback(
    (allow: boolean) => {
      if (!pendingTool) return
      window.pet.answerTool(pendingTool.id, allow)
      setPendingTool(null)
      setToolNote(allow ? 'Выполняю…' : 'Отменено')
    },
    [pendingTool]
  )

  // прозрачное окно: клики проходят сквозь всё, кроме персонажа и панели
  useEffect(() => {
    let interactive = false
    const onMove = (event: MouseEvent) => {
      if (document.body.dataset.dragging === '1') return
      const element = document.elementFromPoint(event.clientX, event.clientY)
      const next = !!element?.closest('[data-interactive="true"]')
      if (next !== interactive) {
        interactive = next
        window.pet.setInteractive(next)
      }
    }
    window.addEventListener('mousemove', onMove)
    window.pet.setInteractive(false)
    return () => window.removeEventListener('mousemove', onMove)
  }, [])

  useEffect(() => {
    if (!bubble) return
    setBubbleLeaving(false)
    const startLeaving = setTimeout(() => setBubbleLeaving(true), 9000)
    const clear = setTimeout(() => {
      setBubble(null)
      setBubbleLeaving(false)
    }, 9400)
    return () => {
      clearTimeout(startLeaving)
      clearTimeout(clear)
    }
  }, [bubble])

  useEffect(() => {
    const off = window.pet.onBackdrop((color) => setBackdrop(color))
    return off
  }, [])

  const send = useCallback(
    (raw: string, opts?: { voice?: boolean }) => {
      const text = raw.trim()
      if (!text || busy) return
      const stamp = Date.now()
      const assistantId = `a${stamp}`
      activeId.current = assistantId
      if (opts?.voice) {
        if (!panelOpenRef.current && settingsRef.current?.bubbles !== false) {
          setBubble({ role: 'user', text, ts: stamp })
        }
      } else {
        setBubble(null)
      }
      setMessages((prev) => [
        ...prev,
        { id: `u${stamp}`, role: 'user', content: text, ts: stamp },
        { id: assistantId, role: 'assistant', content: '', streaming: true, ts: stamp }
      ])
      setBusy(true)
      window.pet.sendMessage(assistantId, text)
    },
    [busy]
  )

  useEffect(() => {
    sendRef.current = send
  }, [send])

  const stop = useCallback(() => {
    if (activeId.current) window.pet.abort(activeId.current)
    window.pet.stopSpeech()
  }, [])

  useEffect(() => {
    window.pet.setChatSize(panelOpen)
  }, [panelOpen])

  useEffect(() => {
    const off = window.pet.onAsk((text) => sendRef.current(text))
    return off
  }, [])

  useEffect(() => {
    if (!settings) return
    if (!settings.vtsMode) return
    void window.pet.vtsConnect().catch(() => undefined)
    void window.pet
      .vtsExpression('夹克.exp3.json', settings.workshopJacket === false)
      .catch(() => undefined)
  }, [settings?.vtsMode, settings?.workshopJacket])

  useEffect(() => {
    const off = window.pet.onGesture((name) => setGestureCmd({ name, id: Date.now() }))
    return off
  }, [])

  useEffect(() => {
    if (!gestureCmd) return
    const timer = setTimeout(() => setGestureCmd(null), 6500)
    return () => clearTimeout(timer)
  }, [gestureCmd])

  useEffect(() => {
    const off = window.pet.onSettingsSync((synced) => setSettings(synced))
    return off
  }, [])

  useEffect(() => {
    const off = window.pet.onCare((text) => {
      setMessages((prev) => [
        ...prev,
        { id: `c${Date.now()}`, role: 'assistant', content: text, ts: Date.now() }
      ])
      if (!panelOpenRef.current) {
        setBubble({ role: 'assistant', text, ts: Date.now() })
      }
      if (settingsRef.current?.tts) window.pet.speak(text)
    })
    return off
  }, [])

  useEffect(() => {
    void (async () => {
      const idle = await window.pet.getSprite('idle')
      const fallback = idle ?? (await window.pet.getSprite('shy'))
      setBaseSprite(fallback)
    })()
  }, [])

  const showSprite = useCallback(async (name: string) => {
    if (settingsRef.current?.character !== 'sprite') {
      setEmotion(name)
      if (spriteTimer.current) window.clearTimeout(spriteTimer.current)
      spriteTimer.current = window.setTimeout(() => setEmotion(null), 11000)
      return
    }
    const url = await window.pet.getSprite(name)
    if (!url) return
    setSprite(url)
    setEmotion(name)
    if (spriteTimer.current) window.clearTimeout(spriteTimer.current)
    spriteTimer.current = window.setTimeout(() => {
      setSprite(null)
      setEmotion(null)
    }, 11000)
  }, [])

  const detectEmotion = useCallback(
    (text: string) => {
      const lower = text.toLowerCase()
      if (/(смущ|покрасн|дурак|неловк|засмущ|ну тебя)/.test(lower)) return 'shy'
      if (/(рада|ура|отлично|здорово|люблю|хихи)/.test(lower)) return 'happy'
      if (/(бесит|злюсь|надоел|хватит)/.test(lower)) return 'angry'
      return null
    },
    []
  )

  const saveSettings = useCallback(async (patch: Partial<Settings>) => {
    const next = await window.pet.saveSettings(patch)
    setSettings(next)
    setSettingsOpen(false)
    setStatus(await window.pet.checkBackend(next))
  }, [])

  useEffect(() => {
    if (settings) document.title = settings.assistantName
  }, [settings])

  if (!settings) return null

  const last = messages[messages.length - 1]
  const displaySprite = sprite ?? (settings.character === 'sprite' ? baseSprite : null)
  const characterState: CharacterState = listening
    ? 'listening'
    : busy
      ? last && last.content
        ? 'speaking'
        : 'thinking'
      : ttsSpeaking
        ? 'speaking'
        : 'idle'

  const GESTURE_SCENE: Record<string, string> = {
    wink: 'VideoWink',
    surprise: 'VideoSurprise',
    deny: 'VideoDeny'
  }

  const spriteScene = (() => {
    if (gestureCmd?.name && GESTURE_SCENE[gestureCmd.name]) return GESTURE_SCENE[gestureCmd.name]
    if (ttsSpeaking) return 'VideoTalk'
    if (emotion === 'happy' || emotion === 'shy') return 'VideoShyJoy'
    if (emotion === 'angry') return 'VideoDeny'
    if (emotion === 'sad') return 'Sad'
    return 'VideoStand'
  })()

  return (
    <div className={`app${panelOpen ? ' chat-open' : ''}`}>
      {panelOpen && (
        <div className="panel-wrap" data-interactive="true">
          {settingsOpen ? (
            <SettingsPanel
              settings={settings}
              status={status}
              onSave={saveSettings}
              onClose={() => setSettingsOpen(false)}
            />
          ) : (
            <ChatPanel
              name={settings.assistantName}
              messages={messages}
              busy={busy}
              status={status}
              interim={interim}
              listening={listening}
              toolNote={toolNote}
              pendingTool={pendingTool}
              onAnswerTool={answerTool}
              onSend={send}
              onStop={stop}
              onOpenSettings={() => setSettingsOpen(true)}
              onClose={() => setPanelOpen(false)}
            />
          )}
        </div>
      )}

      {!panelOpen && bubble && (
        <div
          className={`bubble-msg ${bubble.role}${bubbleLeaving ? ' leaving' : ''}`}
          data-interactive="true"
          onClick={() => setPanelOpen(true)}
          role="button"
        >
          <div className="bubble-text">{bubble.text.length > 200 ? `${bubble.text.slice(0, 200)}…` : bubble.text}</div>
          <div className="bubble-meta">
            <span>{new Date(bubble.ts).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}</span>
            <span className="bubble-checks">✓✓</span>
          </div>
        </div>
      )}

      <Character
        state={characterState}
        backdrop={backdrop}
        sprite={displaySprite}
        emotion={emotion}
        scale={settings.scale ?? 0.85}
        mode={settings.vtsMode ? 'none' : settings.animationMode === 'sprites' ? 'sprites' : settings.character}
        scene={spriteScene}
        sceneKey={gestureCmd?.id ?? 0}
        loop={spriteScene === 'VideoTalk'}
        live2dModel={settings.live2dModel}
        jacket={settings.workshopJacket !== false}
        gesture={gestureCmd}
        onTap={() => setPanelOpen((open) => !open)}
      />
    </div>
  )
}
