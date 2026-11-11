# Alya Next — C#-ядро (миграция с Electron + Python)

Новая архитектура: **C#-хост** + нативные модули. Старое приложение (`../jarvis`) продолжает работать, пока новая версия не догонит по функциям.

## Структура

| Проект | Роль |
|---|---|
| `src/Alya.Core` | Контракты: `IAsrService`, `ITtsService`, `ILlmClient`, `ISystemController`, модели |
| `src/Alya.Voice` | Голос: Silero TTS (ONNX) и sherpa-onnx ASR — пока заглушки |
| `src/Alya.System` | Windows: процессы, фокус окна, запуск/закрытие, громкость приложений |
| `src/Alya.App` | Хост (сейчас консоль, далее Avalonia) |
| `native/Cubism` | C++ DLL с Cubism Native SDK (рендер Live2D) — позже |

## Дорожная карта

1. **Каркас** (готово): solution, контракты, консольный хост, self-test.
2. **Система**: громкость приложений через Core Audio COM, закрытие/запуск, Steam/Epic URI.
3. **Голос**: Silero через ONNX Runtime, sherpa-onnx ASR, WASAPI-плеер.
4. **LLM**: Ollama HTTP-клиент, память, история.
5. **UI**: Avalonia-окно (трей, прозрачность, drag, пузыри).
6. **Live2D**: C++ DLL (Cubism SDK) + P/Invoke, рендер в C#-окно.
7. **Порт функций**: игры, Telegram, игры/работа-реакции, жесты.

## Сборка

```bash
dotnet build
dotnet run --project src/Alya.App
```

Требуется .NET 10 SDK.
