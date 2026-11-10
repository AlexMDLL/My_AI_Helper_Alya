Add-Type -AssemblyName System.Speech

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$ru = $synth.GetInstalledVoices() |
  Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq 'ru-RU' } |
  Select-Object -First 1
if ($ru) { $synth.SelectVoice($ru.VoiceInfo.Name) }
$synth.Rate = 0
$synth.Volume = if ($env:JARVIS_TTS_VOLUME) { [int]$env:JARVIS_TTS_VOLUME } else { 75 }

$utf8 = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = $utf8

while ($true) {
  $line = [Console]::In.ReadLine()
  if ($null -eq $line) { break }
  $line = $line.Trim()
  if ($line -eq '') { continue }
  if ($line -eq 'QUIT') { break }

  [Console]::Out.WriteLine('START')
  [Console]::Out.Flush()
  try {
    $text = [System.IO.File]::ReadAllText($line, $utf8)
    if ($text.Trim().Length -gt 0) { $synth.Speak($text) }
  } catch {
  }
  [Console]::Out.WriteLine('END')
  [Console]::Out.Flush()
}

$synth.Dispose()
