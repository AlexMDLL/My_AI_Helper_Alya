using Alya.Core;
using Alya.System;
using Alya.Voice;

var system = new WindowsSystemController();
ITtsService tts = new SileroTtsService();
IAsrService asr = new SherpaAsrService();

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"=== {AlyaInfo.Name} {AlyaInfo.Version} (C# host) ===");
Console.WriteLine("Ядро: C# · Голос: заглушка Silero · Система: Windows");
Console.WriteLine($"Фокус сейчас: {system.ForegroundProcess() ?? "неизвестно"}");
Console.WriteLine("Команды: say <текст> | open <программа> | close <программа> | volume <app> <0-100> | listen | quit");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null) break;
    var text = line.Trim();
    if (text.Length == 0) continue;

    if (text is "quit" or "exit" or "выход") break;

    if (text == "listen")
    {
        var heard = await asr.ListenAsync();
        Console.WriteLine(heard is null ? "Не расслышала." : $"Услышала: {heard}");
        continue;
    }

    if (text.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
    {
        await tts.SpeakAsync(text[4..]);
        continue;
    }

    if (text.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
    {
        var target = text[5..].Trim();
        var ok = system.Launch(target);
        await tts.SpeakAsync(ok ? $"Открываю {target}." : $"Не нашла {target}.");
        continue;
    }

    if (text.StartsWith("close ", StringComparison.OrdinalIgnoreCase))
    {
        var target = text[6..].Trim();
        var ok = system.Close(target);
        await tts.SpeakAsync(ok ? $"Закрыла {target}." : $"{target} не запущено.");
        continue;
    }

    if (text.StartsWith("volume ", StringComparison.OrdinalIgnoreCase))
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && int.TryParse(parts[2], out var percent))
        {
            var value = system.SetAppVolume(parts[1], percent);
            Console.WriteLine(value < 0 ? $"[volume] {parts[1]}: активной сессии нет" : $"[volume] {parts[1]} = {value}%");
        }
        else if (parts.Length == 2)
        {
            var value = system.GetAppVolume(parts[1]);
            Console.WriteLine(value < 0 ? $"[volume] {parts[1]}: активной сессии нет" : $"[volume] {parts[1]} = {value}%");
        }
        continue;
    }

    await tts.SpeakAsync($"Пока я умею немного. Ты сказал: {text}", "shy");
}
