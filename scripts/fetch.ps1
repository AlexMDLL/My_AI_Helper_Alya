param(
  [Parameter(Mandatory = $true)][string]$Url,
  [Parameter(Mandatory = $true)][string]$Out,
  [int]$Connections = 6
)

$ErrorActionPreference = 'Stop'

$head = (& curl.exe -sIL -m 30 $Url | Out-String)
$matches = [regex]::Matches($head, 'content-length:\s*(\d+)', 'IgnoreCase')
if ($matches.Count -eq 0) { throw "Не удалось узнать размер файла: $Url" }
$length = [int64]$matches[$matches.Count - 1].Groups[1].Value

$chunk = [int64][math]::Ceiling($length / $Connections)
$tmp = "$Out.parts"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force $tmp | Out-Null

$jobs = @()
for ($i = 0; $i -lt $Connections; $i++) {
  $start = [int64]$i * $chunk
  if ($start -ge $length) { break }
  $end = [math]::Min($length - 1, $start + $chunk - 1)
  $part = Join-Path $tmp ("{0:d3}.part" -f $i)
  $jobs += Start-Job -ScriptBlock {
    param($u, $a, $b, $o)
    & curl.exe -sL --retry 5 --retry-delay 2 -r "$a-$b" -o $o $u
  } -ArgumentList $Url, $start, $end, $part
}

$jobs | Wait-Job | Out-Null
foreach ($job in $jobs) { Receive-Job $job | Out-Null; Remove-Job $job -Force }

$stream = [System.IO.File]::Create($Out)
foreach ($part in Get-ChildItem $tmp -Filter *.part | Sort-Object Name) {
  $bytes = [System.IO.File]::ReadAllBytes($part.FullName)
  $stream.Write($bytes, 0, $bytes.Length)
}
$stream.Close()
Remove-Item $tmp -Recurse -Force

$final = (Get-Item $Out).Length
$mb = [math]::Round($final / 1MB, 1)
"готово: $mb МБ из $([math]::Round($length / 1MB, 1)) МБ"
if ($final -ne $length) { exit 1 }
