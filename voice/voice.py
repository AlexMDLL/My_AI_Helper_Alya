#!/usr/bin/env python3
"""Голосовой сервис Джарвиса: потоковое распознавание речи + wake-word.

Читает микрофон, шлёт в stdout JSON-события, принимает команды из stdin.
Запускается Electron'ом как дочерний процесс.
"""

from __future__ import annotations

import json
import os
import queue
import re
import sys
import tempfile
import threading
import time
import wave
from pathlib import Path

import numpy as np
import sounddevice as sd
import sherpa_onnx
import win32com.client

SAMPLE_RATE = 16000
BLOCK = 1600  # 0.1 c
DEFAULT_WAKE_WORDS = ("джарвис", "джарвиз", "чарвис", "жарвис", "джарви", "jarvis")
MODEL_DIR = (
    Path(__file__).resolve().parent
    / "models"
    / "sherpa-onnx-streaming-zipformer-small-ru-vosk-int8-2025-08-16"
)
COMMAND_SILENCE = 1.2  # сколько тишины ждём после последней фразы
WAKE_TIMEOUT = 6.0  # сколько ждём команду после пробуждения
MAX_COMMAND = 30.0


def emit(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def normalize(text: str) -> str:
    cleaned = []
    for ch in text.lower():
        cleaned.append(ch if ch.isalnum() or ch.isspace() else " ")
    return " ".join("".join(cleaned).split())


def levenshtein(a: str, b: str) -> int:
    if a == b:
        return 0
    if not a:
        return len(b)
    if not b:
        return len(a)
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def is_wake_word(word: str, wake_words: tuple[str, ...] | list[str]) -> bool:
    if word in wake_words:
        return True
    if len(word) >= 6:
        for candidate in wake_words:
            if len(candidate) >= 6 and levenshtein(word, candidate) <= 2:
                return True
    return False


def split_wake(text: str, wake_words: tuple[str, ...] | list[str]) -> tuple[bool, str]:
    """Отделяет wake-word от команды. Возвращает (нашли, текст команды)."""
    words = normalize(text).split()
    if not words:
        return False, ""
    for index, word in enumerate(words):
        if is_wake_word(word, wake_words):
            return True, " ".join(words[index + 1 :]).strip()
    return False, text.strip()


def tts_log(message: str) -> None:
    try:
        path = os.path.join(tempfile.gettempdir(), "jarvis-tts.log")
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%H:%M:%S')} {message}\n")
    except Exception:
        pass


def pick_output_device() -> int | None:
    """Возвращает индекс устройства вывода, если дефолтное — виртуальное."""
    try:
        default = sd.query_devices(kind="output")
        name = str(default.get("name", "")).lower()
        virtual = ("voicemeeter", "steam streaming", "nvidia", "virtual")
        if not any(marker in name for marker in virtual):
            return None
        for index, device in enumerate(sd.query_devices()):
            if device["max_output_channels"] <= 0:
                continue
            candidate = str(device["name"]).lower()
            if "graphite" in candidate or "headset" in candidate or "динамики" in candidate:
                return index
    except Exception:
        return None
    return None


STRESS_WORDS = {
    "Аля": "А+ля",
    "Алю": "А+лю",
    "Але": "А+ле",
    "Алей": "А+лей",
    "Алё": "А+лё"
}


EMOTION_FACTOR = {
    "happy": 1.045,
    "shy": 1.015,
    "angry": 1.005,
    "sad": 0.935,
    "neutral": 1.0,
}


RU_UNITS = [
    "ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
    "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать",
    "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать",
]
RU_TENS = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"]
RU_HUNDREDS = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"]


def _num_to_words(number: int) -> str:
    if number < 0:
        return "минус " + _num_to_words(-number)
    if number == 0:
        return "ноль"
    parts = []
    millions = number // 1_000_000
    thousands = (number // 1000) % 1000
    rest = number % 1000
    if millions:
        if millions % 10 == 1 and millions % 100 != 11:
            word = "миллион"
        elif 2 <= millions % 10 <= 4 and not 12 <= millions % 100 <= 14:
            word = "миллиона"
        else:
            word = "миллионов"
        parts.append(f"{_num_to_words(millions)} {word}")
    if thousands:
        if thousands % 10 == 1 and thousands % 100 != 11:
            word = "тысяча"
        elif 2 <= thousands % 10 <= 4 and not 12 <= thousands % 100 <= 14:
            word = "тысячи"
        else:
            word = "тысяч"
        prefix = "одна" if thousands == 1 else _num_to_words(thousands).replace("один", "одна", 1).replace("два", "две", 1)
        parts.append(f"{prefix} {word}")
    if rest:
        hundreds = rest // 100
        tens = (rest % 100) // 10
        units = rest % 10
        if hundreds:
            parts.append(RU_HUNDREDS[hundreds])
        if tens == 1:
            parts.append(RU_UNITS[10 + units])
        else:
            if tens:
                parts.append(RU_TENS[tens])
            if units:
                parts.append(RU_UNITS[units])
    return " ".join(parts)


ABBREV_UNITS = {
    "км": "километр",
    "м": "метр",
    "см": "сантиметр",
    "мм": "миллиметр",
    "кг": "килограмм",
    "г": "грамм",
    "л": "литр",
    "мл": "миллилитр",
    "мб": "мегабайт",
    "гб": "гигабайт",
    "кб": "килобайт",
    "тб": "терабайт",
    "мбит": "мегабит",
    "гбит": "гигабит",
    "кбит": "килобит",
    "мгц": "мегагерц",
    "ггц": "гигагерц",
    "кгц": "килогерц",
    "гц": "герц",
    "мин": "минута",
    "ч": "час",
    "шт": "штука",
}

COMPOUND_UNITS: list[tuple[str, tuple[str, str, str], str, str]] = [
    ("мбит/с", ("мегабит", "мегабита", "мегабит"), "m", "в секунду"),
    ("кбит/с", ("килобит", "килобита", "килобит"), "m", "в секунду"),
    ("гбит/с", ("гигабит", "гигабита", "гигабит"), "m", "в секунду"),
    ("мб/с", ("мегабайт", "мегабайта", "мегабайт"), "m", "в секунду"),
    ("кб/с", ("килобайт", "килобайта", "килобайт"), "m", "в секунду"),
    ("гб/с", ("гигабайт", "гигабайта", "гигабайт"), "m", "в секунду"),
    ("тб/с", ("терабайт", "терабайта", "терабайт"), "m", "в секунду"),
    ("км/ч", ("километр", "километра", "километров"), "m", "в час"),
    ("м/с", ("метр", "метра", "метров"), "m", "в секунду"),
]


def _number_words(number_text: str, gender: str) -> str:
    if "." in number_text:
        integer, fraction = number_text.split(".", 1)
        integer_words = _num_words_gendered(int(integer), gender) if integer else "ноль"
        fraction_words = _num_to_words(int(fraction)) if fraction else ""
        if len(fraction) == 1:
            suffix = "десятых"
        elif len(fraction) == 2:
            suffix = "сотых"
        else:
            suffix = "тысячных"
        return f"{integer_words} целых {fraction_words} {suffix}"
    return _num_words_gendered(int(number_text), gender)


def expand_compound_units(text: str) -> str:
    for pattern, forms, gender, suffix in COMPOUND_UNITS:
        escaped = pattern.replace("/", r"[/\\]")

        def repl(match: "re.Match[str]", forms=forms, gender=gender, suffix=suffix) -> str:
            raw = match.group(1).replace(",", ".")
            negative = raw.startswith("-")
            plain = raw.lstrip("-")
            words = _number_words(plain, gender)
            noun = _noun_form(plain, forms)
            return f"{('минус ' if negative else '')}{words} {noun} {suffix}"

        text = re.sub(rf"(-?\d+(?:[.,]\d+)?)\s*{escaped}\b", repl, text, flags=re.IGNORECASE)
    return text


UNIT_FORMS = {
    "градус": ("градус", "градуса", "градусов", "m"),
    "процент": ("процент", "процента", "процентов", "m"),
    "метр": ("метр", "метра", "метров", "m"),
    "километр": ("километр", "километра", "километров", "m"),
    "час": ("час", "часа", "часов", "m"),
    "минута": ("минута", "минуты", "минут", "f"),
    "секунда": ("секунда", "секунды", "секунд", "f"),
    "день": ("день", "дня", "дней", "m"),
    "год": ("год", "года", "лет", "m"),
    "рубль": ("рубль", "рубля", "рублей", "m"),
    "доллар": ("доллар", "доллара", "долларов", "m"),
    "килограмм": ("килограмм", "килограмма", "килограммов", "m"),
    "грамм": ("грамм", "грамма", "граммов", "m"),
    "гигабайт": ("гигабайт", "гигабайта", "гигабайт", "m"),
    "мегабайт": ("мегабайт", "мегабайта", "мегабайт", "m"),
    "кадр": ("кадр", "кадра", "кадров", "m"),
    "раз": ("раз", "раза", "раз", "m"),
    "балл": ("балл", "балла", "баллов", "m"),
    "человек": ("человек", "человека", "человек", "m"),
    "игра": ("игра", "игры", "игр", "f"),
    "файл": ("файл", "файла", "файлов", "m"),
    "сообщение": ("сообщение", "сообщения", "сообщений", "n"),
    "окно": ("окно", "окна", "окон", "n"),
    "трек": ("трек", "трека", "треков", "m"),
    "песня": ("песня", "песни", "песен", "f"),
    "сантиметр": ("сантиметр", "сантиметра", "сантиметров", "m"),
    "миллиметр": ("миллиметр", "миллиметра", "миллиметров", "m"),
    "литр": ("литр", "литра", "литров", "m"),
    "миллилитр": ("миллилитр", "миллилитра", "миллилитров", "m"),
    "терабайт": ("терабайт", "терабайта", "терабайт", "m"),
    "килобайт": ("килобайт", "килобайта", "килобайт", "m"),
    "мегабит": ("мегабит", "мегабита", "мегабит", "m"),
    "гигабит": ("гигабит", "гигабита", "гигабит", "m"),
    "килобит": ("килобит", "килобита", "килобит", "m"),
    "герц": ("герц", "герца", "герц", "m"),
    "килогерц": ("килогерц", "килогерца", "килогерц", "m"),
    "мегагерц": ("мегагерц", "мегагерца", "мегагерц", "m"),
    "гигагерц": ("гигагерц", "гигагерца", "гигагерц", "m"),
    "штука": ("штука", "штуки", "штук", "f"),
}


def _num_words_gendered(number: int, gender: str) -> str:
    words = _num_to_words(number)
    if gender == "f":
        words = re.sub(r"\bодин$", "одна", words)
        words = re.sub(r"\bдва$", "две", words)
    elif gender == "n":
        words = re.sub(r"\bодин$", "одно", words)
    return words


def _noun_form(number_text: str, forms: tuple[str, str, str]) -> str:
    if "." in number_text or "," in number_text:
        return forms[1]
    number = int(number_text.lstrip("-"))
    if 11 <= number % 100 <= 14:
        return forms[2]
    digit = number % 10
    if digit == 1:
        return forms[0]
    if digit in (2, 3, 4):
        return forms[1]
    return forms[2]


def agree_numbers(text: str) -> str:
    units_by_form: dict[str, tuple[tuple[str, str, str], str]] = {}
    for lemma, forms in UNIT_FORMS.items():
        for form in forms[:3]:
            units_by_form[form] = (forms[:3], forms[3])

    def repl(match: "re.Match[str]") -> str:
        raw = match.group(1)
        word = match.group(2)
        key = word.lower()
        unit = units_by_form.get(key)
        if not unit and key in ABBREV_UNITS:
            lemma = ABBREV_UNITS[key]
            forms = UNIT_FORMS.get(lemma)
            if forms:
                unit = (forms[:3], forms[3])
        if not unit:
            if key in ("°c", "°"):
                unit = (("градус", "градуса", "градусов"), "m")
            elif key == "%":
                unit = (("процент", "процента", "процентов"), "m")
        if not unit:
            return match.group(0)
        forms, gender = unit
        number_text = raw.replace(",", ".")
        negative = number_text.startswith("-")
        plain = number_text.lstrip("-")
        if "." in plain:
            integer, fraction = plain.split(".", 1)
            integer_words = _num_words_gendered(int(integer), gender) if integer else "ноль"
            fraction_words = _num_to_words(int(fraction)) if fraction else ""
            if len(fraction) == 1:
                suffix = "десятых"
            elif len(fraction) == 2:
                suffix = "сотых"
            else:
                suffix = "тысячных"
            words = f"{integer_words} целых {fraction_words} {suffix}"
        else:
            words = _num_words_gendered(int(plain), gender)
        noun = _noun_form(plain, forms)
        result = f"{('минус ' if negative else '')}{words} {noun}"
        return result

    return re.sub(r"(-?\d+(?:[.,]\d+)?)\s*(°C|°|%|[А-Яа-яЁё]+)", repl, text)


def numbers_to_words(text: str) -> str:
    text = expand_compound_units(text)
    text = agree_numbers(text)

    def repl(match: "re.Match[str]") -> str:
        raw = match.group(0).replace(",", ".")
        negative = raw.startswith("-")
        raw = raw.lstrip("-")
        if "." in raw:
            integer, fraction = raw.split(".", 1)
            integer_words = _num_to_words(int(integer)) if integer else "ноль"
            fraction_words = _num_to_words(int(fraction)) if fraction else ""
            if len(fraction) == 1:
                suffix = "десятых"
            elif len(fraction) == 2:
                suffix = "сотых"
            else:
                suffix = "тысячных"
            result = f"{integer_words} целых {fraction_words} {suffix}".strip()
        else:
            result = _num_to_words(int(raw))
        return ("минус " + result) if negative else result

    result = re.sub(r"(?<![\w.])-?\d+(?:[.,]\d+)?", repl, text)
    result = result.replace("°C", " градусов").replace("°", " градусов")
    result = result.replace("%", " процентов")
    return result


LATIN_MAP = {
    "a": "а", "b": "б", "c": "к", "d": "д", "e": "е", "f": "ф", "g": "г", "h": "х",
    "i": "и", "j": "дж", "k": "к", "l": "л", "m": "м", "n": "н", "o": "о", "p": "п",
    "q": "к", "r": "р", "s": "с", "t": "т", "u": "у", "v": "в", "w": "в", "x": "кс",
    "y": "и", "z": "з",
}
LATIN_DIGRAPHS = [
    ("sh", "ш"), ("ch", "ч"), ("ph", "ф"), ("th", "т"), ("ck", "к"), ("oo", "у"),
    ("qu", "кв"), ("ee", "и"), ("ea", "и"), ("ai", "ай"), ("ou", "оу"),
]


def latin_to_cyrillic(text: str) -> str:
    def repl(match: "re.Match[str]") -> str:
        word = match.group(0).lower()
        for source, target in LATIN_DIGRAPHS:
            word = word.replace(source, target)
        return "".join(LATIN_MAP.get(ch, ch) for ch in word)

    return re.sub(r"\b[A-Za-z]{2,}\b", repl, text)


def apply_stress(text: str, engine: str) -> str:
    """Ставит ударение в имени: А́ля, а не Аля́."""
    for word, marked in STRESS_WORDS.items():
        pattern = rf"(?<![А-Яа-яЁё]){word}(?![А-Яа-яЁё])"
        if engine == "silero":
            text = re.sub(pattern, marked, text)
        else:
            accented = word[0] + "\u0301" + word[1:]
            text = re.sub(pattern, accented, text)
    return text


class SileroVoice:
    """Нейросетевой русский голос Silero — живой, без робота."""

    def __init__(self, model_id: str = "v5") -> None:
        self.model_id = model_id
        self.model = None
        self.ready = False
        self.failed = False
        self.started = False
        self.lock = threading.Lock()

    def ensure_started(self) -> None:
        if self.started or self.failed:
            return
        self.started = True
        threading.Thread(target=self._load, daemon=True).start()

    def _load(self) -> None:
        try:
            import torch
            from torch import package

            base = os.path.dirname(os.path.abspath(__file__))
            local_model = os.path.join(base, "models", f"{self.model_id}_ru.pt")
            if os.path.exists(local_model):
                importer = package.PackageImporter(local_model)
                model = importer.load_pickle("tts_models", "model")
            else:
                model, _ = torch.hub.load(
                    "snakers4/silero-models",
                    "silero_tts",
                    language="ru",
                    speaker=f"{self.model_id}_ru",
                    trust_repo=True,
                    verbose=False,
                )
            model.to("cpu")
            with self.lock:
                self.model = model
                self.ready = True
            tts_log(f"silero {self.model_id} ready")
        except Exception as error:  # noqa: BLE001
            self.failed = True
            tts_log(f"silero {self.model_id} load failed: {error}")

    def wait_ready(self, seconds: float) -> bool:
        self.ensure_started()
        deadline = time.time() + seconds
        while time.time() < deadline:
            if self.ready:
                return True
            if self.failed:
                return False
            time.sleep(0.2)
        return self.ready

    def synth(self, text: str, speaker: str, pitch: float, emotion: str = "neutral") -> tuple[np.ndarray, int] | None:
        self.ensure_started()
        with self.lock:
            if not self.ready or self.model is None:
                return None
            try:
                audio = self.model.apply_tts(
                    text=text,
                    speaker=speaker,
                    sample_rate=48000,
                    put_accent=True,
                    put_yo=True,
                )
                data = audio.detach().cpu().numpy().astype(np.float32).squeeze()
                if len(data) > 1:
                    factor = max(0.85, min(1.3, float(pitch) * EMOTION_FACTOR.get(emotion, 1.0)))
                    count = max(1, int(len(data) / factor))
                    data = np.interp(
                        np.linspace(0, len(data), count, endpoint=False),
                        np.arange(len(data)),
                        data,
                    ).astype(np.float32)
                    kernel = np.array([0.12, 0.76, 0.12], dtype=np.float32)
                    data = np.convolve(data, kernel, mode="same").astype(np.float32)
                    fade = min(int(0.012 * 48000), len(data) // 2)
                    if fade > 0:
                        data[:fade] *= np.linspace(0.0, 1.0, fade, dtype=np.float32)
                        data[-fade:] *= np.linspace(1.0, 0.0, fade, dtype=np.float32)
                return data, 48000
            except Exception as error:  # noqa: BLE001
                tts_log(f"silero synth failed: {error}")
                return None


class Speaker:
    """Озвучка Silero, при недоступности — SAPI."""

    def __init__(self) -> None:
        self.queue: queue.Queue[tuple[str, float, str, str, str] | None] = queue.Queue()
        self.active = False
        self.models: dict[str, SileroVoice] = {}
        self.thread = threading.Thread(target=self._loop, daemon=True)
        self.thread.start()

    def say(self, text: str, pitch: float, speaker: str, model: str, emotion: str = "neutral") -> None:
        self.queue.put((text, pitch, speaker, model, emotion))

    def preload(self, model: str) -> None:
        engine = self._model(model)
        engine.ensure_started()
        tts_log(f"preload {model}")

    def stop(self) -> None:
        try:
            sd.stop()
        except Exception:
            pass

    def shutdown(self) -> None:
        self.queue.put(None)

    def _model(self, model_id: str) -> SileroVoice:
        key = model_id if model_id in ("v4", "v5") else "v5"
        if key not in self.models:
            self.models[key] = SileroVoice(key)
        return self.models[key]

    def _loop(self) -> None:
        while True:
            item = self.queue.get()
            if item is None:
                break
            text, pitch, speaker, model, emotion = item
            try:
                self._speak(text, pitch, speaker, model, emotion)
            except Exception as error:  # noqa: BLE001
                emit({"type": "tts_error", "message": str(error)[:300]})

    def _sapi(self, text: str, pitch: float) -> tuple[np.ndarray, int]:
        try:
            import pythoncom

            pythoncom.CoInitialize()
        except Exception:
            pass

        path = os.path.join(tempfile.gettempdir(), f"jarvis-tts-{int(time.time() * 1000)}.wav")
        stream = win32com.client.Dispatch("SAPI.SpFileStream")
        stream.Open(path, 3, False)
        voice = win32com.client.Dispatch("SAPI.SpVoice")
        voices = voice.GetVoices()
        for index in range(voices.Count):
            candidate = voices.Item(index)
            if "Irina" in candidate.GetDescription():
                voice.Voice = candidate
                break
        voice.Rate = -1
        voice.AudioOutputStream = stream
        voice.Speak(text)
        stream.Close()

        with wave.open(path, "rb") as source:
            rate = source.getframerate()
            frames = source.getnframes()
            pcm = np.frombuffer(source.readframes(frames), dtype=np.int16).astype(np.float32) / 32768.0
        try:
            os.remove(path)
        except OSError:
            pass

        if pitch and abs(pitch - 1.0) > 0.01 and len(pcm) > 1:
            count = max(1, int(len(pcm) / pitch))
            pcm = np.interp(
                np.linspace(0, len(pcm), count, endpoint=False),
                np.arange(len(pcm)),
                pcm,
            ).astype(np.float32)
        return pcm, rate

    def _speak(self, text: str, pitch: float, speaker: str, model: str, emotion: str = "neutral") -> None:
        text = text.replace("!", ".").replace("…", "...").strip()
        text = latin_to_cyrillic(numbers_to_words(text))
        text = apply_stress(text, "silero")
        pcm = None
        rate = 48000

        engine = self._model(model)
        if not engine.ready and not engine.wait_ready(30.0):
            tts_log(f"silero {model} не успел загрузиться")

        if engine.ready:
            result = engine.synth(text, speaker, pitch, emotion)
            if result:
                pcm, rate = result
                tts_log(f"silero {model} {speaker} {emotion} {len(pcm) / rate:.1f}s")

        if pcm is None:
            pcm, rate = self._sapi(text, pitch)
            tts_log(f"sapi {len(pcm) / rate:.1f}s pitch={pitch}")

        peak = float(np.max(np.abs(pcm))) if len(pcm) else 0.0
        if peak > 0.001:
            pcm = (pcm / peak) * 0.82

        device = pick_output_device()
        tts_log(f"play {len(pcm) / rate:.1f}s rate={rate} device={device} peak={peak:.3f}")

        emit({"type": "tts_start"})
        self.active = True
        if os.environ.get("JARVIS_TTS_MUTE"):
            time.sleep(min(len(pcm) / rate, 8.0))
        else:
            sd.play(pcm, rate, device=device)
            sd.wait()
        self.active = False
        emit({"type": "tts_end"})


class VoiceService:
    def __init__(self) -> None:
        self.commands: queue.Queue[str] = queue.Queue()
        self.audio: queue.Queue[np.ndarray] = queue.Queue(maxsize=200)
        self.wake_enabled = True
        self.always_listen = False
        self.wake_words = list(DEFAULT_WAKE_WORDS)
        self.mode = "idle"
        self.text = ""
        self.last_change = 0.0
        self.command_started = 0.0
        self.running = True
        self.blocks = 0
        self.input_rate = SAMPLE_RATE
        self.idle_text = ""
        self.idle_changed = 0.0
        self.speaker = Speaker()
        self.was_speaking = False

        self.recognizer = sherpa_onnx.OnlineRecognizer.from_transducer(
            tokens=str(MODEL_DIR / "tokens.txt"),
            encoder=str(MODEL_DIR / "encoder.int8.onnx"),
            decoder=str(MODEL_DIR / "decoder.onnx"),
            joiner=str(MODEL_DIR / "joiner.int8.onnx"),
            num_threads=2,
            sample_rate=SAMPLE_RATE,
            feature_dim=80,
            decoding_method="greedy_search",
            enable_endpoint_detection=True,
            rule1_min_trailing_silence=2.4,
            rule2_min_trailing_silence=1.0,
            rule3_min_utterance_length=20.0,
        )
        self.stream = self.recognizer.create_stream()

    # ---------- микрофон ----------

    def open_microphone(self) -> sd.InputStream:
        try:
            stream = sd.InputStream(
                samplerate=SAMPLE_RATE,
                channels=1,
                dtype="float32",
                blocksize=BLOCK,
                callback=self._callback,
            )
            self.input_rate = SAMPLE_RATE
            return stream
        except Exception:
            rate = int(sd.query_devices(kind="input")["default_samplerate"])
            self.input_rate = rate
            stream = sd.InputStream(
                samplerate=rate,
                channels=1,
                dtype="float32",
                blocksize=max(BLOCK, int(rate * 0.1)),
                callback=self._callback,
            )
            return stream

    def _callback(self, indata, frames, time_info, status) -> None:  # noqa: ANN001
        if status:
            pass
        samples = indata[:, 0].copy() if indata.ndim > 1 else indata.copy()
        if self.input_rate != SAMPLE_RATE and len(samples) > 1:
            count = max(1, int(len(samples) * SAMPLE_RATE / self.input_rate))
            samples = np.interp(
                np.linspace(0, len(samples), count, endpoint=False),
                np.arange(len(samples)),
                samples,
            ).astype(np.float32)
        try:
            self.audio.put_nowait(samples)
        except queue.Full:
            pass

    # ---------- логика ----------

    def reset_stream(self) -> None:
        self.recognizer.reset(self.stream)
        self.text = ""

    def begin_command(self) -> None:
        self.mode = "command"
        self.command_started = time.time()
        self.last_change = self.command_started
        self.text = ""
        emit({"type": "listening"})

    def finish_command(self) -> None:
        text = self.text.strip()
        if text and re.match(r"^(стоп|хватит|выключи|перестань)\s+(слушать|слушай|подслушивать)", text.lower()):
            self.always_listen = False
            emit({"type": "always", "enabled": False})
            self.reset_stream()
            self.mode = "idle"
            self.idle_text = ""
            emit({"type": "idle"})
            return
        if text:
            emit({"type": "final", "text": text})
        self.reset_stream()
        self.mode = "idle"
        self.idle_text = ""
        emit({"type": "idle"})

    def handle_audio(self, samples: np.ndarray) -> None:
        # пока Аля говорит, не слушаем микрофон — иначе она услышит саму себя
        if self.speaker.active:
            if not self.was_speaking:
                self.was_speaking = True
                if self.mode == "idle":
                    self.reset_stream()
                    self.idle_text = ""
            return
        self.was_speaking = False

        self.blocks += 1
        self.stream.accept_waveform(SAMPLE_RATE, samples)
        while self.recognizer.is_ready(self.stream):
            self.recognizer.decode_stream(self.stream)

        if self.blocks % 5 == 0:
            level = float(np.sqrt(np.mean(samples**2)))
            emit({"type": "level", "value": round(min(1.0, level * 6.0), 3)})

        raw = self.recognizer.get_result(self.stream).strip()
        now = time.time()

        if self.mode == "idle":
            if self.always_listen:
                # режим «всегда слушает»: любая фраза — команда, без wake-слова
                if raw and len(raw) >= 2:
                    self.idle_text = ""
                    self.begin_command()
                    self.text = raw
                    emit({"type": "partial", "text": raw})
                return

            # реагируем на wake-word только когда фраза устоялась,
            # иначе случайные промежуточные распознавания будят зря
            if raw != self.idle_text:
                self.idle_text = raw
                self.idle_changed = now
            stable = bool(raw) and (now - self.idle_changed) > 0.7
            endpoint = self.recognizer.is_endpoint(self.stream)
            if self.wake_enabled and (endpoint or stable):
                woke, command = split_wake(raw, self.wake_words)
                if woke:
                    self.idle_text = ""
                    self.begin_command()
                    emit({"type": "wake"})
                    self.text = command
                    if command:
                        emit({"type": "partial", "text": command})
            return

        woke, command = split_wake(raw, self.wake_words)
        text = command if woke else raw
        if text != self.text:
            self.text = text
            self.last_change = now
            if text:
                emit({"type": "partial", "text": text})

        silent_for = now - self.last_change
        if self.text and silent_for > COMMAND_SILENCE:
            self.finish_command()
            return

        if not self.text and now - self.command_started > WAKE_TIMEOUT:
            self.mode = "idle"
            self.reset_stream()
            emit({"type": "idle"})
            return

        if self.text and now - self.command_started > MAX_COMMAND:
            self.finish_command()

    def read_commands(self) -> None:
        for line in sys.stdin:
            line = line.lstrip("\ufeff").strip()
            if not line:
                continue
            try:
                payload = json.loads(line)
            except json.JSONDecodeError:
                continue
            command = payload.get("cmd")
            if command == "listen":
                self.reset_stream()
                self.begin_command()
            elif command == "stop":
                if self.mode == "command":
                    self.finish_command()
            elif command == "wake":
                self.wake_enabled = bool(payload.get("enabled", True))
            elif command == "alwaysListen":
                self.always_listen = bool(payload.get("enabled", False))
                if self.always_listen:
                    self.mode = "idle"
                    self.text = ""
                    self.idle_text = ""
                    tts_log("always-listen: включён")
                else:
                    tts_log("always-listen: выключен")
            elif command == "preload":
                self.speaker.preload(str(payload.get("model", "v5")))
            elif command == "wakeWords":
                words = [str(word).lower().strip() for word in (payload.get("words") or []) if str(word).strip()]
                if words:
                    self.wake_words = words
            elif command == "speak":
                text = str(payload.get("text", "")).strip()
                if text:
                    speaker = str(payload.get("speaker", "baya"))
                    model = str(payload.get("model", "v5"))
                    emotion = str(payload.get("emotion", "neutral"))
                    self.speaker.say(text, float(payload.get("pitch", 0.97)), speaker, model, emotion)
            elif command == "stopSpeech":
                self.speaker.stop()
            elif command == "quit":
                self.running = False
                break

    def run(self, max_seconds: float | None = None) -> None:
        threading.Thread(target=self.read_commands, daemon=True).start()

        try:
            mic = self.open_microphone()
        except Exception as error:  # noqa: BLE001
            emit({"type": "error", "message": f"Микрофон недоступен: {error}"})
            return

        device = sd.query_devices(kind="input")
        deadline = time.time() + max_seconds if max_seconds else None
        with mic:
            emit({"type": "ready", "device": device["name"], "rate": self.input_rate})
            while self.running:
                if deadline and time.time() > deadline:
                    break
                try:
                    samples = self.audio.get(timeout=0.25)
                except queue.Empty:
                    continue
                self.handle_audio(samples)


def main() -> None:
    try:
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    if "--list-devices" in sys.argv:
        for index, device in enumerate(sd.query_devices()):
            if device["max_input_channels"] > 0:
                print(index, device["name"])
        return

    max_seconds = None
    if "--seconds" in sys.argv:
        max_seconds = float(sys.argv[sys.argv.index("--seconds") + 1])

    service = VoiceService()
    service.run(max_seconds)
    emit({"type": "bye"})


if __name__ == "__main__":
    main()
