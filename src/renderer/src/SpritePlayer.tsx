import { useEffect, useRef, useState, type ReactElement } from 'react'

interface Props {
  scene: string
  fps?: number
  loop?: boolean
  sceneKey?: number
}

const FPS: Record<string, number> = {
  VideoStand: 60,
  VideoWink: 60,
  VideoDeny: 60,
  VideoSurprise: 60,
  VideoTalk: 60,
  VideoShyJoy: 60
}

const IDLE_SCENE = 'VideoStand'
const TARGET_FPS = 120

export default function SpritePlayer({ scene, fps, loop = false, sceneKey = 0 }: Props): ReactElement {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const framesRef = useRef<string[]>([])
  const images = useRef<Map<number, HTMLImageElement>>(new Map())
  const [current, setCurrent] = useState(scene)
  const [ready, setReady] = useState(0)
  const meta = useRef<Record<string, number>>({})
  const startRef = useRef(performance.now())
  const lastRef = useRef<HTMLImageElement | null>(null)

  useEffect(() => {
    void window.pet.animationMeta().then((fpsMap) => {
      meta.current = fpsMap
    })
  }, [])

  useEffect(() => {
    setCurrent(scene)
  }, [scene, sceneKey])

  const preloadRange = (list: string[], from: number, count: number): void => {
    const end = Math.min(list.length, from + count)
    for (let i = Math.max(0, from); i < end; i += 1) {
      if (images.current.has(i)) continue
      const image = new Image()
      image.decoding = 'async'
      image.src = list[i]
      void image.decode?.().catch(() => undefined)
      images.current.set(i, image)
    }
  }

  useEffect(() => {
    let cancelled = false
    void window.pet.animationFrames(current).then((list) => {
      if (cancelled) return
      if (list.length === 0) {
        if (current !== IDLE_SCENE) setCurrent(IDLE_SCENE)
        return
      }
      images.current.clear()
      framesRef.current = list
      startRef.current = performance.now()
      preloadRange(list, 0, 24)
      setReady((value) => value + 1)
    })
    return () => {
      cancelled = true
    }
  }, [current])

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const context = canvas.getContext('2d')
    if (!context) return

    const sourceFps = fps ?? meta.current[current] ?? FPS[current] ?? 60
    let raf = 0
    let lastDraw = 0

    const ensure = (index: number): HTMLImageElement | null => {
      const frames = framesRef.current
      if (index < 0 || index >= frames.length) return null
      let image = images.current.get(index)
      if (!image) {
        image = new Image()
        image.decoding = 'async'
        image.src = frames[index]
        images.current.set(index, image)
      }
      return image
    }

    const draw = (): void => {
      const now = performance.now()
      if (now - lastDraw < 15) {
        raf = requestAnimationFrame(draw)
        return
      }
      lastDraw = now
      const frames = framesRef.current
      const total = frames.length
      if (total === 0) {
        raf = requestAnimationFrame(draw)
        return
      }
      const elapsed = (performance.now() - startRef.current) / 1000
      let position = elapsed * sourceFps
      if (position >= total) {
        if (loop || current === IDLE_SCENE) {
          startRef.current = performance.now()
          position = 0
        } else {
          setCurrent(IDLE_SCENE)
          return
        }
      }
      const index = Math.floor(position)
      const fraction = position - index
      const a = ensure(index)
      const b = ensure(Math.min(index + 1, total - 1))
      for (let look = 2; look <= 15; look += 1) ensure(index + look)
      for (const key of images.current.keys()) {
        if (key < index - 15 || key > index + 90) images.current.delete(key)
      }
      const base = a && a.complete && a.naturalWidth > 0 ? a : lastRef.current
      if (base) {
        context.clearRect(0, 0, canvas.width, canvas.height)
        context.drawImage(base, 0, 0, canvas.width, canvas.height)
        lastRef.current = base
      }
      if (b && b.complete && b.naturalWidth > 0 && fraction > 0.02 && base) {
        context.globalAlpha = fraction
        context.drawImage(b, 0, 0, canvas.width, canvas.height)
        context.globalAlpha = 1
      }
      raf = requestAnimationFrame(draw)
    }

    raf = requestAnimationFrame(draw)
    return () => cancelAnimationFrame(raf)
  }, [current, fps, loop, ready])

  return (
    <div className="sprite-anim">
      <canvas ref={canvasRef} width={540} height={840} className="sprite-canvas" />
    </div>
  )
}
