import { useEffect, useRef, type ReactElement } from 'react'

type ModelId = 'hiyori' | 'alexia' | 'jane' | 'workshop'

interface Props {
  emotion?: string | null
  speaking?: boolean
  model?: ModelId
  jacket?: boolean
  gesture?: { name: string; id: number } | null
}

interface ModelConfig {
  url: string
  expressions: Record<string, string>
  hideSecondArms: boolean
  idleMotion?: string
}

const MODELS: Record<ModelId, ModelConfig> = {
  hiyori: {
    url: 'pet://live2d/Hiyori/Hiyori.model3.json',
    expressions: {},
    hideSecondArms: true
  },
  alexia: {
    url: 'pet://live2d/Alexia/Alexia/Alexia.model3.json',
    expressions: { happy: 'y', shy: 'lh', angry: 'wh', sad: 'k', thinking: 'mj' },
    hideSecondArms: false,
    idleMotion: 'Idle'
  },
  jane: {
    url: `pet://live2d/Jane/${encodeURIComponent('简')}.model3.json`,
    expressions: { happy: '星星眼', shy: '脸红', angry: '生气', sad: '泪' },
    hideSecondArms: false,
    idleMotion: 'Idle'
  },
  workshop: {
    url: 'pet://live2d/Workshop/aaa.model3.json',
    expressions: { happy: '星星眼', shy: '脸红', angry: '白眼', sad: '哭泣', thinking: '眯眼' },
    hideSecondArms: false,
    idleMotion: 'Idle'
  }
}

const MOTION_NAMES: Record<string, string> = {
  wink: 'Wink',
  bow: 'Bow',
  nod: 'Nod',
  dance: 'Dance',
  laugh: 'Laugh',
  smile: 'Smile',
  shy: 'Shy',
  stretch: 'Stretch',
  wave: 'Wave',
  angry: 'Angry',
  sad: 'Sad',
  lookAround: 'LookAround',
  hum: 'Hum',
  yawn: 'Yawn',
  giggle: 'Giggle',
  curtsy: 'Curtsy',
  shrug: 'Shrug'
}

const GESTURE_DURATION: Record<string, number> = {
  bow: 1500,
  nod: 1100,
  wink: 1200,
  smile: 1600,
  laugh: 1700,
  dance: 3200,
  stretch: 1700,
  shy: 1900,
  tilt: 900,
  wave: 1400,
  lookAround: 2400,
  hum: 2000,
  yawn: 2200,
  giggle: 1800,
  curtsy: 1800,
  shrug: 1600,
  surprise: 1200,
  deny: 1200
}

export default function Live2DView({ emotion, speaking, model: modelId = 'workshop', jacket = true, gesture }: Props): ReactElement {
  const hostRef = useRef<HTMLDivElement>(null)
  const modelRef = useRef<any>(null)
  const appRef = useRef<any>(null)
  const speakingRef = useRef(Boolean(speaking))
  const emotionRef = useRef(emotion ?? 'neutral')
  const jacketRef = useRef(jacket)
  const pointerRef = useRef({ x: 0, y: 0 })
  const bounceUntilRef = useRef(0)
  const emotionAtRef = useRef(0)
  const gestureRef = useRef({ kind: '', start: 0, dur: 0, next: 0 })
  const frameRef = useRef(0)

  speakingRef.current = Boolean(speaking)
  emotionRef.current = emotion ?? 'neutral'
  jacketRef.current = jacket

  useEffect(() => {
    const host = hostRef.current
    if (!host) return
    const config = MODELS[modelId] ?? MODELS.alexia
    let disposed = false
    let observer: ResizeObserver | null = null
    let internal: any = null
    let rafId = 0

    const onPointerMove = (event: PointerEvent): void => {
      pointerRef.current = {
        x: (event.clientX / Math.max(1, window.innerWidth)) * 2 - 1,
        y: (event.clientY / Math.max(1, window.innerHeight)) * 2 - 1
      }
    }
    const onPointerDown = (): void => {
      bounceUntilRef.current = performance.now() + 700
      const now = performance.now()
      gestureRef.current = { kind: 'tilt', start: now, dur: 900, next: now + 5000 }
      try {
        modelRef.current?.motion('TapBody')
      } catch {
        // группы TapBody может не быть
      }
    }
    window.addEventListener('pointermove', onPointerMove)
    host.addEventListener('pointerdown', onPointerDown)

    const layout = (): void => {
      const app = appRef.current
      const model = modelRef.current
      if (!app || !model) return
      const width = host.clientWidth
      const height = host.clientHeight
      if (!width || !height) return
      app.renderer.resize(width, height)
      const baseWidth = model.__baseWidth ?? model.width
      const baseHeight = model.__baseHeight ?? model.height
      model.x = width / 2
      model.y = height + 2
      model.__baseY = model.y
      model.scale.set(Math.min(width / baseWidth, height / baseHeight))
    }

    const applyExpression = (name?: string): void => {
      const model = modelRef.current
      if (!model) return
      const expression = config.expressions[name ?? 'neutral'] ?? config.expressions.neutral
      if (!expression) return
      try {
        model.expression(expression)
      } catch {
        // выражение может отсутствовать
      }
    }

    const setup = async (): Promise<void> => {      try {
        const PIXI = await import('pixi.js')
        const { install } = await import('@pixi/unsafe-eval')
        install(PIXI)
        const { Live2DModel } = await import('pixi-live2d-display/cubism4')
        if (disposed) return

        host.replaceChildren()
        const app = new PIXI.Application({
          transparent: true,
          width: host.clientWidth,
          height: host.clientHeight,
          antialias: true,
          autoDensity: true,
          preserveDrawingBuffer: true,
          resolution: Math.min(window.devicePixelRatio || 1, 2)
        })
        appRef.current = app
        app.ticker.maxFPS = 60
        app.ticker.minFPS = 60
        app.ticker.start()
        const view = app.view as HTMLCanvasElement
        view.style.position = 'absolute'
        view.style.inset = '0'
        view.style.width = '100%'
        view.style.height = '100%'
        host.appendChild(view)

        const model = await Live2DModel.from(config.url, { autoInteract: false })
        if (disposed) {
          model.destroy()
          return
        }
        modelRef.current = model
        ;(window as unknown as { __alyaBake?: unknown }).__alyaBake = async (
          scene: string,
          fps: number,
          duration: number
        ): Promise<string[]> => {
          const appCurrent = appRef.current
          const modelCurrent = modelRef.current
          if (!appCurrent || !modelCurrent) return []
          const frames: string[] = []
          try {
            modelCurrent.motion(MOTION_NAMES[scene] ?? scene)
          } catch {
            // клипа может не быть
          }
          const start = performance.now()
          const interval = 1000 / Math.max(1, fps)
          let next = start
          return await new Promise<string[]>((resolve) => {
            const tick = (): void => {
              const now = performance.now()
              if (now >= next) {
                try {
                  modelCurrent.update(1 / 60)
                  appCurrent.renderer.render(appCurrent.stage)
                  const view = appCurrent.view as HTMLCanvasElement
                  frames.push(view.toDataURL('image/png'))
                } catch {
                  // кадр пропускаем
                }
                next += interval
              }
              if (now - start >= duration * 1000) resolve(frames)
              else requestAnimationFrame(tick)
            }
            requestAnimationFrame(tick)
          })
        }
        try {
          const manager = (model.internalModel as unknown as { motionManager?: { definitions?: Record<string, unknown> } }).motionManager
          console.log('[live2d] motions', Object.keys(manager?.definitions ?? {}).join(','))
        } catch {
          // ignore
        }
        ;(model as unknown as { __baseWidth: number; __baseHeight: number }).__baseWidth = model.width
        ;(model as unknown as { __baseWidth: number; __baseHeight: number }).__baseHeight = model.height
        model.anchor.set(0.5, 1)
        app.stage.addChild(model)

        // принудительный ре-рендер каждый кадр: не даём композитору «залипнуть»
        // на статичной картинке, если окно перекрыто или не в фокусе
        const forceRender = (): void => {
          if (disposed) return
          try {
            app.renderer.render(app.stage)
          } catch {
            // контекст мог быть потерян
          }
          rafId = requestAnimationFrame(forceRender)
        }
        rafId = requestAnimationFrame(forceRender)

        internal = model.internalModel as unknown as {
          update: (dt: number, now: number) => void
          coreModel?: {
            setPartOpacityById?: (id: string, value: number) => void
            setParameterValueById?: (id: string, value: number) => void
          }
        }

        if (config.hideSecondArms) {
          const originalUpdate = internal.update.bind(internal)
          internal.update = (dt: number, now: number) => {
            originalUpdate(dt, now)
            try {
              internal.coreModel?.setPartOpacityById?.('PartArmB', 0)
              internal.coreModel?.setPartOpacityById?.('PartArmA', 1)
            } catch {
              // нет таких частей
            }
          }
        }

        layout()
        observer = new ResizeObserver(() => layout())
        observer.observe(host)
        applyExpression(emotionRef.current)
        emotionAtRef.current = performance.now()
        gestureRef.current = { kind: '', start: 0, dur: 0, next: performance.now() + 4000 }
        setTimeout(() => {
          gestureRef.current = { kind: 'wave', start: performance.now(), dur: 1600, next: performance.now() + 8000 }
        }, 1200)

        if (config.idleMotion) {
          try {
            model.motion(config.idleMotion)
          } catch {
            // нет такой группы
          }
        }

        app.ticker.add(() => {
          frameRef.current += 1
          if (frameRef.current % 180 === 0) {
            console.log('[live2d] frames', frameRef.current, 'gesture=', gestureRef.current.kind)
          }
          const time = performance.now()
          const t = time / 1000
          const current = modelRef.current
          if (current && typeof current.__baseY === 'number') {
            const bounceLeft = bounceUntilRef.current - time
            const bounce = bounceLeft > 0 ? Math.sin((bounceLeft / 700) * Math.PI * 3) * 5 : 0
            current.y = current.__baseY + Math.sin(t * 1.15) * 2 + bounce
          }

          const core = internal?.coreModel
          if (!core?.setParameterValueById) return

          const lookX = Math.max(-1, Math.min(1, pointerRef.current.x))
          const lookY = Math.max(-1, Math.min(1, pointerRef.current.y))
          const speakingNow = speakingRef.current
          const sinceEmotion = time - emotionAtRef.current
          const impulse = sinceEmotion < 1600 ? Math.sin((sinceEmotion / 1600) * Math.PI) * 6 : 0

          // активные жесты: периодически смотрим по сторонам, киваем, наклоняемся
          const gesture = gestureRef.current
          if (!gesture.kind && time > gesture.next) {
            const kinds = [
              'lookLeft',
              'lookRight',
              'tilt',
              'lean',
              'nod',
              'lookAround',
              'hum',
              'giggle',
              'shrug',
              'smile',
              'wink',
              'shy',
              'stretch',
              'wave',
              'bow',
              'curtsy',
              'yawn',
              'laugh'
            ]
            gesture.kind = kinds[Math.floor(Math.random() * kinds.length)]
            gesture.start = time
            gesture.dur = 1400 + Math.random() * 800
          }
          let gestureX = 0
          let gestureY = 0
          let gestureZ = 0
          let gestureBody = 0
          if (gesture.kind) {
            const progress = (time - gesture.start) / gesture.dur
            if (progress >= 1) {
              gesture.kind = ''
              gesture.next = time + 2500 + Math.random() * 4000
            } else {
              const envelope = Math.sin(Math.PI * Math.min(1, Math.max(0, progress)))
              if (gesture.kind === 'lookLeft') gestureX = -25 * envelope
              if (gesture.kind === 'lookRight') gestureX = 25 * envelope
              if (gesture.kind === 'nod') gestureY = Math.sin(progress * Math.PI * 2) * 16
              if (gesture.kind === 'tilt') gestureZ = 16 * envelope
              if (gesture.kind === 'lean') gestureBody = 10 * envelope
              if (gesture.kind === 'shake') gestureX = Math.sin(progress * Math.PI * 8) * 14
              if (gesture.kind === 'sad') gestureY = -10 * envelope
              if (gesture.kind === 'bow') {
                gestureY = -20 * envelope
                gestureBody = 10 * envelope
              }
              if (gesture.kind === 'laugh') gestureY = 12 * envelope
              if (gesture.kind === 'stretch') gestureY = 16 * envelope
              if (gesture.kind === 'shy') {
                gestureZ = 10 * envelope
                gestureY = -7 * envelope
              }
              if (gesture.kind === 'dance') {
                gestureBody = Math.sin(progress * Math.PI * 8) * 10
                gestureZ = Math.sin(progress * Math.PI * 6) * 11
              }
              if (gesture.kind === 'wave') gestureZ = Math.sin(progress * Math.PI * 6) * 15
              if (gesture.kind === 'wink') {
                gestureZ = 7 * envelope
                gestureY = 3 * envelope
              }
            }
          }

          // куртка управляется всегда, даже во время motion-клипа
          if (modelId === 'workshop') {
            try {
              core.setParameterValueById('jiake', jacketRef.current ? 0 : 2)
            } catch {
              // параметра может не быть
            }
          }

          // если для жеста есть motion-файл, не перекрываем его своими параметрами
          if (gesture.kind && MOTION_NAMES[gesture.kind]) {
            return
          }

          try {
            core.setParameterValueById('ParamBreath', 0.5 + 0.5 * Math.sin(t * 0.9))
            core.setParameterValueById('ParamBodyAngleX', Math.sin(t * 0.42) * 3 + gestureBody)
            core.setParameterValueById('ParamBodyAngleZ', Math.sin(t * 0.5) * 2)
            core.setParameterValueById('ParamAngleX', Math.sin(t * 0.55) * 9 + lookX * 9 + gestureX)
            core.setParameterValueById(
              'ParamAngleY',
              (speakingNow ? Math.sin(time / 150) * 4 : Math.sin(t * 0.7) * 4) - lookY * 5 + gestureY
            )
            core.setParameterValueById('ParamAngleZ', Math.sin(t * 0.37) * 3 + impulse + gestureZ)
            core.setParameterValueById('ParamEyeBallX', lookX * 1.2)
            core.setParameterValueById('ParamEyeBallY', -lookY)
          } catch {
            // у модели может не быть этих параметров
          }

          const mouth = speakingNow ? 0.18 + 0.5 * Math.abs(Math.sin(time / 90)) : 0
          try {
            core.setParameterValueById('ParamMouthOpenY', mouth)
          } catch {
            // параметра может не быть
          }

          // мимические жесты: подмигивание, улыбка, смех, смущение
          let eyeLOpen = 1
          let eyeROpen = 1
          let mouthForm = 0
          let cheek = 0
          let eyeSmile = 0
          if (gesture.kind) {
            const progress = Math.min(1, Math.max(0, (time - gesture.start) / gesture.dur))
            const envelope = Math.sin(Math.PI * progress)
            if (gesture.kind === 'wink') {
              // закрываем видимый (левый) глаз — правый скрыт под чёлкой
              eyeLOpen = 1 - envelope
              cheek = 0.6 * envelope
              mouthForm = 0.5 * envelope
            }
            if (gesture.kind === 'smile') {
              mouthForm = envelope
              cheek = 0.8 * envelope
            }
            if (gesture.kind === 'laugh') {
              mouthForm = 0.8 * envelope
              eyeSmile = envelope
              cheek = 0.6 * envelope
            }
            if (gesture.kind === 'shy') cheek = 0.9 * envelope
          }
          try {
            core.setParameterValueById('ParamEyeLOpen', eyeLOpen)
            core.setParameterValueById('ParamEyeROpen', eyeROpen)
            core.setParameterValueById('ParamMouthForm', mouthForm)
            core.setParameterValueById('ParamCheek', cheek)
            core.setParameterValueById('ParamEyeLSmile', eyeSmile)
            core.setParameterValueById('ParamEyeRSmile', eyeSmile)
          } catch {
            // у модели может не быть этих параметров
          }
        })
      } catch (error) {
        console.warn('[live2d] failed', String(error))
      }
    }

    void setup()

    return () => {
      disposed = true
      if (rafId) cancelAnimationFrame(rafId)
      observer?.disconnect()
      observer = null
      window.removeEventListener('pointermove', onPointerMove)
      host.removeEventListener('pointerdown', onPointerDown)
      modelRef.current?.destroy()
      modelRef.current = null
      appRef.current?.destroy(true)
      appRef.current = null
      host.replaceChildren()
    }
  }, [modelId])

  useEffect(() => {
    console.log('[live2d] jacket prop =', jacket)
  }, [jacket])

  useEffect(() => {
    if (!gesture) return
    const now = performance.now()
    console.log('[live2d] gesture', gesture.name)
    const motion = MOTION_NAMES[gesture.name]
    if (motion) {
      try {
        modelRef.current?.motion(motion)
      } catch {
        // у другой модели может не быть этой группы
      }
    }
    gestureRef.current = {
      kind: gesture.name,
      start: now,
      dur: GESTURE_DURATION[gesture.name] ?? 1200,
      next: now + 7000
    }
    if (gesture.name === 'nod') bounceUntilRef.current = now + 500
  }, [gesture?.id])

  useEffect(() => {
    emotionAtRef.current = performance.now()
    const now = performance.now()
    const kind =
      emotion === 'happy' ? 'nod' : emotion === 'shy' ? 'tilt' : emotion === 'angry' ? 'shake' : emotion === 'sad' ? 'sad' : 'tilt'
    const emotionMotion = emotion === 'happy' ? 'Smile' : emotion === 'shy' ? 'Shy' : emotion === 'angry' ? 'Angry' : emotion === 'sad' ? 'Sad' : ''
    if (emotionMotion) {
      try {
        modelRef.current?.motion(emotionMotion)
      } catch {
        // ignore
      }
    }
    gestureRef.current = { kind, start: now, dur: emotion === 'angry' ? 1200 : 1400, next: now + 7000 }
    const config = MODELS[modelId] ?? MODELS.alexia
    const expression = config.expressions[emotion ?? 'neutral']
    if (!expression) return
    try {
      modelRef.current?.expression(expression)
    } catch {
      // ignore
    }
  }, [emotion, modelId])

  useEffect(() => {
    if (!speaking) return
    try {
      modelRef.current?.motion(MODELS[modelId]?.idleMotion || 'TapBody')
    } catch {
      // ignore
    }
  }, [speaking, modelId])

  return <div className="live2d-stage" ref={hostRef} />
}
