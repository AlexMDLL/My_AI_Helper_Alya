$ff = "C:\Users\foxif\AppData\Local\CapCut\Apps\9.7.1.4203\ffmpeg.exe"
$video = "C:\Users\foxif\Videos\NVIDIA\Desktop\Desktop 2026.11.10 - 17.22.37.05.mp4"
$segments = @(
  @(97.4, 102.0),
  @(151.0, 155.5),
  @(155.5, 160.2),
  @(161.6, 162.8),
  @(164.2, 166.2),
  @(170.8, 171.6),
  @(175.2, 176.0),
  @(180.0, 180.6),
  @(183.4, 184.2),
  @(188.8, 189.8)
)
$i = 0
foreach ($s in $segments) {
  $i++
  $a = $s[0]
  $b = $s[1]
  $dur = [math]::Round($b - $a, 2)
  $dir = "C:\Users\foxif\Projects\jarvis\docs\vidwork\raw_$i"
  Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force $dir | Out-Null
  & $ff -hide_banner -loglevel error -ss $a -t $dur -i $video -vf "crop=347:771:760:45,scale=252:560" -r 30 -start_number 0 "$dir\%04d.png" -y
  $count = (Get-ChildItem $dir -Filter *.png | Measure-Object).Count
  Write-Output "raw_$i frames=$count dur=$dur"
}
