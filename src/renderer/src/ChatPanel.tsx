import { useEffect, useRef, useState } from 'react'
import type { BackendStatus, ChatMessage, ToolConfirmRequest } from '../../shared/types'

export interface UiMessage extends ChatMessage {
  id: string
  streaming?: boolean
  error?: boolean
}

interface Props {
  name: string
  messages: UiMessage[]
  busy: boolean
  status: BackendStatus | null
  interim?: string | null
  listening?: boolean
  toolNote?: string | null
  pendingTool?: ToolConfirmRequest | null
  onAnswerTool?: (allow: boolean) => void
  onSend: (text: string) => void
  onStop: () => void
  onOpenSettings: () => void
  onClose: () => void
}

function formatTime(ts?: number): string {
  if (!ts) return ''
  return new Date(ts).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })
}

export default function ChatPanel({
  name,
  messages,
  busy,
  status,
  interim,
  listening,
  toolNote,
  pendingTool,
  onAnswerTool,
  onSend,
  onStop,
  onOpenSettings,
  onClose
}: Props) {
  const [draft, setDraft] = useState('')
  const listRef = useRef<HTMLDivElement | null>(null)
  const inputRef = useRef<HTMLInputElement | null>(null)

  useEffect(() => {
    const list = listRef.current
    if (list) list.scrollTop = list.scrollHeight
  }, [messages, busy])

  useEffect(() => {
    inputRef.current?.focus()
  }, [])

  const submit = (): void => {
    const value = draft.trim()
    if (!value || busy) return
    onSend(value)
    setDraft('')
  }

  const online = status?.ok ?? false
  const statusText = status ? status.detail : 'проверяю связь…'

  return (
    <section className="panel">
      <header className="panel-head">
        <span className={`dot ${online ? 'online' : 'offline'}`} />
        <div className="brand">
          <b>{name}</b>
          <span title={statusText}>{statusText}</span>
        </div>
        <div className="head-actions">
          <button type="button" className="icon-btn" title="Настройки" onClick={onOpenSettings}>
            ⚙
          </button>
          <button type="button" className="icon-btn" title="Свернуть" onClick={onClose}>
            ⌄
          </button>
        </div>
      </header>

      <div className="messages" ref={listRef}>
        {messages.map((message) => (
          <div
            key={message.id}
            className={`row ${message.role === 'user' ? 'row-user' : 'row-assistant'}`}
          >
            <div
              className={`msg ${message.role === 'user' ? 'user' : 'assistant'}${message.error ? ' error' : ''}`}
            >
              {message.content}
              {message.streaming && !message.content && (
                <span className="thinking-dots">
                  <i />
                  <i />
                  <i />
                </span>
              )}
              {message.streaming && message.content && <span className="caret" />}
              {!message.streaming && message.ts ? (
                <span className="msg-time">
                  {formatTime(message.ts)}
                  {message.role === 'user' && <span className="msg-checks">✓✓</span>}
                </span>
              ) : null}
            </div>
          </div>
        ))}
      </div>

      {(listening || interim) && (
        <div className="interim">
          <span className="mic-dot" />
          <span>{interim || 'Слушаю…'}</span>
        </div>
      )}

      {toolNote && (
        <div className="tool-note">
          <span className="tool-dot" />
          <span>{toolNote}</span>
        </div>
      )}

      {pendingTool && (
        <div className="confirm-card">
          <div className="confirm-title">Нужно разрешение</div>
          <div className="confirm-desc">{pendingTool.description}</div>
          {pendingTool.detail && <pre className="confirm-detail">{pendingTool.detail}</pre>}
          <div className="confirm-actions">
            <button type="button" className="ghost" onClick={() => onAnswerTool?.(false)}>
              Отклонить
            </button>
            <button type="button" className="primary" onClick={() => onAnswerTool?.(true)}>
              Разрешить
            </button>
          </div>
        </div>
      )}

      <form
        className="composer"
        onSubmit={(event) => {
          event.preventDefault()
          submit()
        }}
      >
        <input
          ref={inputRef}
          value={draft}
          placeholder={busy ? `${name} печатает…` : listening ? 'Скажи команду…' : 'Сообщение…'}
          onChange={(event) => setDraft(event.target.value)}
          disabled={busy}
        />
        {busy ? (
          <button type="button" className="send stop" title="Остановить" onClick={onStop}>
            ■
          </button>
        ) : (
          <button type="submit" className="send" title="Отправить" disabled={!draft.trim()}>
            ↑
          </button>
        )}
      </form>
    </section>
  )
}
