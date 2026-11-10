import { useEffect, useRef, useState, type PointerEvent } from 'react'
import Live2DView from './Live2D'
import SpritePlayer from './SpritePlayer'

export type CharacterState = 'idle' | 'thinking' | 'speaking' | 'listening' | 'shy' | 'happy' | 'angry'

interface Props {
  state: CharacterState
  backdrop?: { r: number; g: number; b: number } | null
  sprite?: string | null
  emotion?: string | null
  scale?: number
  mode?: 'sprite' | 'orb' | 'live2d' | 'none' | 'sprites'
  scene?: string
  sceneKey?: number
  loop?: boolean
  live2dModel?: 'hiyori' | 'alexia' | 'jane' | 'workshop'
  jacket?: boolean
  gesture?: { name: string; id: number } | null
  onTap: () => void
}

const SIZE = 300

const VERTEX = `
attribute vec2 aPos;
void main() {
  gl_Position = vec4(aPos, 0.0, 1.0);
}
`

const FRAGMENT = `
precision highp float;

uniform vec2 uRes;
uniform float uTime;
uniform float uSpeak;
uniform vec3 uBase;
uniform vec3 uAccent;
uniform vec2 uLook;

float hash3(vec3 p) {
  p = fract(p * 0.3183099 + vec3(0.11, 0.17, 0.23));
  p *= 17.0;
  return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float noise3(vec3 x) {
  vec3 i = floor(x);
  vec3 f = fract(x);
  f = f * f * (3.0 - 2.0 * f);
  float n000 = hash3(i);
  float n100 = hash3(i + vec3(1.0, 0.0, 0.0));
  float n010 = hash3(i + vec3(0.0, 1.0, 0.0));
  float n110 = hash3(i + vec3(1.0, 1.0, 0.0));
  float n001 = hash3(i + vec3(0.0, 0.0, 1.0));
  float n101 = hash3(i + vec3(1.0, 0.0, 1.0));
  float n011 = hash3(i + vec3(0.0, 1.0, 1.0));
  float n111 = hash3(i + vec3(1.0, 1.0, 1.0));
  return mix(
    mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
    mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y),
    f.z
  );
}

float fbm(vec3 p) {
  float s = 0.0;
  float a = 0.55;
  for (int i = 0; i < 3; i++) {
    s += a * noise3(p);
    p = p * 2.03 + 17.0;
    a *= 0.5;
  }
  return s;
}

vec3 hsv2rgb(vec3 c) {
  vec3 rgb = clamp(abs(mod(c.x * 6.0 + vec3(0.0, 4.0, 2.0), 6.0) - 3.0) - 1.0, 0.0, 1.0);
  return c.z * mix(vec3(1.0), rgb, c.y);
}

float wobble(vec3 p) {
  float t = uTime;
  return 0.05 * (
    sin(p.x * 1.1 + t * 0.6) * 0.5 +
    sin(p.y * 1.3 - t * 0.5) * 0.3 +
    sin(p.z * 1.2 + t * 0.4) * 0.2
  );
}

float map(vec3 p) {
  float t = uTime;
  float breathe = 0.018 * sin(t * 1.7);
  float r = 1.05 + breathe + wobble(p) * mix(1.0, 0.06, uSpeak);
  float band = sin(dot(p, normalize(vec3(0.4, 0.75, 0.5))) * 9.0 - t * 6.0);
  r += smoothstep(0.88, 1.0, band) * 0.035 * uSpeak;
  return length(p) - r;
}

vec3 normalAt(vec3 p) {
  vec2 e = vec2(0.004, 0.0);
  return normalize(vec3(
    map(p + e.xyy) - map(p - e.xyy),
    map(p + e.yxy) - map(p - e.yxy),
    map(p + e.yyx) - map(p - e.yyx)
  ));
}

void main() {
  vec2 uv = (gl_FragCoord.xy - 0.5 * uRes) / (0.5 * uRes.y);
  vec3 ro = vec3(0.0, 0.0, 2.85);
  vec3 rd = normalize(vec3(uv, -1.9));

  float ax = uLook.x * 0.0;
  float ay = -uLook.y * 0.0;
  mat3 rotY = mat3(cos(ax), 0.0, -sin(ax), 0.0, 1.0, 0.0, sin(ax), 0.0, cos(ax));
  mat3 rotX = mat3(1.0, 0.0, 0.0, 0.0, cos(ay), -sin(ay), 0.0, sin(ay), cos(ay));
  rd = rotX * rotY * rd;

  float t = 0.0;
  float hit = 0.0;
  for (int i = 0; i < 96; i++) {
    vec3 p = ro + rd * t;
    float d = map(p);
    if (d < 0.001) {
      hit = 1.0;
      break;
    }
    t += d * 0.5;
    if (t > 5.5) break;
  }

  if (hit < 0.5) {
    gl_FragColor = vec4(0.0);
    return;
  }

  vec3 p = ro + rd * t;
  vec3 n = normalAt(p);
  vec3 v = -rd;
  float ndv = clamp(dot(n, v), 0.0, 1.0);
  float fres = pow(1.0 - ndv, 2.2);

  vec3 r = reflect(rd, n);

  vec3 env = vec3(0.015, 0.01, 0.04);
  env += vec3(0.5, 0.55, 0.85) * smoothstep(0.35, 0.95, r.y);
  env += vec3(1.0, 0.99, 1.0) * pow(max(dot(r, normalize(vec3(-0.5, 0.82, 0.55))), 0.0), 26.0) * 2.6;
  env += vec3(1.0, 0.55, 0.95) * pow(max(dot(r, normalize(vec3(0.75, -0.2, 0.6))), 0.0), 18.0) * 0.8;
  env += vec3(0.6, 0.7, 1.0) * pow(max(dot(r, normalize(vec3(0.1, 0.95, 0.3))), 0.0), 8.0) * 0.35;

  float hue = 0.74 + 0.1 * sin(dot(n, vec3(1.7, 2.3, 0.9)) + uTime * 0.25) + fres * 0.3;
  vec3 irid = hsv2rgb(vec3(fract(hue), 0.75, 1.0));

  vec3 body = mix(uBase * 0.5, irid, 0.38);
  vec3 color = body * (0.3 + 0.3 * (1.0 - fres)) + env * (0.55 + 0.7 * fres);
  color += irid * pow(fres, 1.4) * 1.2;
  color *= 0.75 + 0.35 * smoothstep(-0.9, 0.9, p.y);

  float slick = pow(0.5 + 0.5 * sin(dot(n, vec3(2.4, 1.6, 1.2)) * 2.2 + fres * 5.0 + uTime * 0.2), 4.0);
  color += hsv2rgb(vec3(fract(0.55 + slick * 0.3 + fres * 0.2), 0.9, 1.0)) * slick * 0.45;

  float wavePhase = dot(n, normalize(vec3(0.5, 0.25, 0.83))) * 7.0 - uTime * 5.5;
  float wave = smoothstep(0.5, 1.0, sin(wavePhase)) * uSpeak;
  vec3 waveCol = mix(hsv2rgb(vec3(fract(0.55 + wavePhase * 0.12), 0.75, 1.0)), uAccent, 0.45);
  color += waveCol * wave * 1.2 + vec3(0.6) * wave * 0.15;

  float alpha = mix(0.58, 0.94, fres);

  color = color / (color + vec3(1.0));
  color = pow(color, vec3(0.82));

  gl_FragColor = vec4(color, alpha);
}
`

const ACCENTS: Record<CharacterState, [number, number, number]> = {
  idle: [0.95, 0.45, 0.85],
  thinking: [1.0, 0.7, 0.25],
  speaking: [0.25, 0.9, 1.0],
  listening: [0.4, 1.0, 0.6],
  shy: [0.98, 0.45, 0.55],
  happy: [1.0, 0.85, 0.3],
  angry: [1.0, 0.35, 0.3]
}

const BASE_COLOR: [number, number, number] = [0.55, 0.34, 0.97]
const SHIFTED_COLOR: [number, number, number] = [0.16, 0.78, 0.95]

function rgbToHsl(r: number, g: number, b: number): [number, number, number] {
  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2
  if (max === min) return [0, 0, l]
  const d = max - min
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
  let h: number
  if (max === r) h = ((g - b) / d + (g < b ? 6 : 0)) * 60
  else if (max === g) h = ((b - r) / d + 2) * 60
  else h = ((r - g) / d + 4) * 60
  return [h, s, l]
}

function hueDistance(a: number, b: number): number {
  const diff = Math.abs(a - b) % 360
  return diff > 180 ? 360 - diff : diff
}

/** Если фон уже фиолетовый — уходим в циан, чтобы не сливаться. */
function adaptBase(color: { r: number; g: number; b: number } | null): [number, number, number] {
  if (!color) return BASE_COLOR
  const [h, s, l] = rgbToHsl(color.r / 255, color.g / 255, color.b / 255)
  if (s > 0.2 && l > 0.05 && l < 0.92 && hueDistance(h, 272) < 26) return SHIFTED_COLOR
  return BASE_COLOR
}

function compile(gl: WebGLRenderingContext, type: number, source: string): WebGLShader {
  const shader = gl.createShader(type)
  if (!shader) throw new Error('createShader failed')
  gl.shaderSource(shader, source)
  gl.compileShader(shader)
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const log = gl.getShaderInfoLog(shader)
    gl.deleteShader(shader)
    throw new Error(log || 'shader compile error')
  }
  return shader
}

function createProgram(gl: WebGLRenderingContext, vertex: string, fragment: string): WebGLProgram {
  const program = gl.createProgram()
  if (!program) throw new Error('createProgram failed')
  const vs = compile(gl, gl.VERTEX_SHADER, vertex)
  const fs = compile(gl, gl.FRAGMENT_SHADER, fragment)
  gl.attachShader(program, vs)
  gl.attachShader(program, fs)
  gl.linkProgram(program)
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    throw new Error(gl.getProgramInfoLog(program) || 'link error')
  }
  return program
}

export default function Character({ state, backdrop, sprite, emotion, scale = 1, mode = 'orb', live2dModel = 'workshop', jacket = true, gesture = null, scene = 'Idle', sceneKey = 0, loop = false, onTap }: Props) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const stateRef = useRef<CharacterState>(state)
  stateRef.current = state
  const lookRef = useRef({ x: 0, y: 0 })
  const dragRef = useRef<{ sx: number; sy: number; moved: boolean } | null>(null)
  const targetBaseRef = useRef<[number, number, number]>([...BASE_COLOR])
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    targetBaseRef.current = adaptBase(backdrop ?? null)
  }, [backdrop])

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const gl = canvas.getContext('webgl', { alpha: true, antialias: true, premultipliedAlpha: false })
    if (!gl) {
      setFailed(true)
      return
    }

    let program: WebGLProgram
    try {
      program = createProgram(gl, VERTEX, FRAGMENT)
    } catch (error) {
      console.error('[character] WebGL:', error)
      setFailed(true)
      return
    }

    const buffer = gl.createBuffer()
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer)
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW)

    gl.useProgram(program)
    const aPos = gl.getAttribLocation(program, 'aPos')
    gl.enableVertexAttribArray(aPos)
    gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0)

    const uRes = gl.getUniformLocation(program, 'uRes')
    const uTime = gl.getUniformLocation(program, 'uTime')
    const uSpeak = gl.getUniformLocation(program, 'uSpeak')
    const uBase = gl.getUniformLocation(program, 'uBase')
    const uAccent = gl.getUniformLocation(program, 'uAccent')
    const uLook = gl.getUniformLocation(program, 'uLook')

    const dpr = Math.min(window.devicePixelRatio || 1, 2)
    const px = Math.round(SIZE * dpr)
    canvas.width = px
    canvas.height = px
    gl.viewport(0, 0, px, px)

    const accent = [...ACCENTS[stateRef.current]] as [number, number, number]
    const base = [...BASE_COLOR] as [number, number, number]
    let speak = 0
    const started = performance.now()

    const onMove = (event: MouseEvent) => {
      const rect = canvas.getBoundingClientRect()
      lookRef.current = {
        x: Math.max(-1, Math.min(1, ((event.clientX - rect.left) / rect.width) * 2 - 1)),
        y: Math.max(-1, Math.min(1, ((event.clientY - rect.top) / rect.height) * 2 - 1))
      }
    }
    window.addEventListener('mousemove', onMove)

    let raf = 0
    const frame = (): void => {
      const time = (performance.now() - started) / 1000
      const target = ACCENTS[stateRef.current]
      for (let i = 0; i < 3; i += 1) accent[i] += (target[i] - accent[i]) * 0.06
      for (let i = 0; i < 3; i += 1) base[i] += (targetBaseRef.current[i] - base[i]) * 0.03
      speak += ((stateRef.current === 'speaking' ? 1 : 0) - speak) * 0.08

      gl.clearColor(0, 0, 0, 0)
      gl.clear(gl.COLOR_BUFFER_BIT)
      gl.uniform2f(uRes, px, px)
      gl.uniform1f(uTime, time)
      gl.uniform1f(uSpeak, speak)
      gl.uniform3f(uBase, base[0], base[1], base[2])
      gl.uniform3f(uAccent, accent[0], accent[1], accent[2])
      gl.uniform2f(uLook, lookRef.current.x, lookRef.current.y)
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4)

      raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)

    return () => {
      cancelAnimationFrame(raf)
      window.removeEventListener('mousemove', onMove)
      gl.deleteProgram(program)
      gl.deleteBuffer(buffer)
    }
  }, [])

  const onPointerDown = (event: PointerEvent<HTMLDivElement>): void => {
    if (event.button !== 0) return
    dragRef.current = { sx: event.screenX, sy: event.screenY, moved: false }
    document.body.dataset.dragging = '1'
    event.currentTarget.setPointerCapture(event.pointerId)
  }

  const onPointerMove = (event: PointerEvent<HTMLDivElement>): void => {
    const drag = dragRef.current
    if (!drag) return
    const dx = event.screenX - drag.sx
    const dy = event.screenY - drag.sy
    if (!drag.moved && Math.hypot(dx, dy) > 5) {
      drag.moved = true
      window.pet.dragStart(drag.sx, drag.sy)
    }
    if (drag.moved) window.pet.dragMove(event.screenX, event.screenY)
  }

  const onPointerUp = (): void => {
    const drag = dragRef.current
    dragRef.current = null
    delete document.body.dataset.dragging
    if (!drag) return
    if (drag.moved) window.pet.dragEnd()
    else onTap()
  }

  if (mode === 'none') return null

  return (
    <div
      className={`character${sprite ? ' has-sprite' : ''}${mode === 'live2d' || mode === 'sprites' ? ' live2d' : ''}${emotion ? ` emotion-${emotion}` : ''}`}
      data-interactive="true"
      style={{ transform: `scale(${scale})`, transformOrigin: 'bottom center' }}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
    >
      <div className="aura" />
      {mode === 'sprites' ? (
        <SpritePlayer scene={scene} sceneKey={sceneKey} loop={loop} />
      ) : mode === 'live2d' ? (
        <Live2DView emotion={emotion ?? null} speaking={state === 'speaking'} model={live2dModel} jacket={jacket} gesture={gesture} />
      ) : failed ? (
        <div className="orb-fallback" />
      ) : (
        <canvas ref={canvasRef} />
      )}
      {sprite && mode === 'sprite' ? <img className="character-sprite" src={sprite} alt="" draggable={false} /> : null}
    </div>
  )
}
