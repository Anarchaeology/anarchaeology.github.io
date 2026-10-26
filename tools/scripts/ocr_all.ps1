# Reads the text in every downloaded picture with Windows' built-in OCR (pictures enlarged 2x, which helps with the
# game's small fonts) and keeps it, so names can be searched for later without reading the pictures again.
# Resumable: pictures already in ocr_text.tsv are skipped. Writes OUTDIR\ocr_text.tsv: file path, OCR text (lines
# joined with " | "), and OUTDIR\progress.log.
param([string[]]$Roots, [string]$OutDir)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -AssemblyName System.Drawing
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Storage.StorageFile, Windows.Foundation, ContentType = WindowsRuntime]
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(-1) | Out-Null; $t.Result }
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
New-Item -ItemType Directory -Force $OutDir | Out-Null
$outFile = Join-Path $OutDir 'ocr_text.tsv'; $log = Join-Path $OutDir 'progress.log'
$done = @{}; if (Test-Path $outFile) { foreach ($l in [IO.File]::ReadLines($outFile)) { $done[$l.Split("`t")[0]] = 1 } }
$files = foreach ($r in $Roots) { Get-ChildItem -LiteralPath $r -Recurse -File | Where-Object { $_.Extension -match '^\.(jpe?g|gif|png|bmp)$' } }
"$(Get-Date -Format s) start: $($files.Count) pictures, $($done.Count) already read" | Add-Content $log
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'ocr_all_tmp.png'
$writer = New-Object IO.StreamWriter($outFile, $true, (New-Object Text.UTF8Encoding($false)))
$n = 0
foreach ($f in $files) {
  if ($done.ContainsKey($f.FullName)) { continue }
  $text = ''
  try {
    $src = [System.Drawing.Image]::FromFile($f.FullName)
    $scale = [Math]::Min(2.0, 4000.0 / [Math]::Max($src.Width, $src.Height))
    $bmp = New-Object System.Drawing.Bitmap ([int][Math]::Max(1, $src.Width * $scale)), ([int][Math]::Max(1, $src.Height * $scale))
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($src, 0, 0, $bmp.Width, $bmp.Height); $g.Dispose(); $src.Dispose()
    $bmp.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($tmp)) ([Windows.Storage.StorageFile])
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $sbmp = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $result = Await ($engine.RecognizeAsync($sbmp)) ([Windows.Media.Ocr.OcrResult])
    $stream.Dispose(); $sbmp.Dispose()
    $text = (($result.Lines | ForEach-Object { $_.Text }) -join ' | ') -replace "[`t`r`n]", ' '
  } catch { $text = "ERROR: $($_.Exception.Message)" -replace "[`t`r`n]", ' ' }
  $writer.WriteLine("$($f.FullName)`t$text"); $writer.Flush()
  $n++; if ($n % 250 -eq 0) { "$(Get-Date -Format s) $n read" | Add-Content $log }
}
$writer.Close()
"$(Get-Date -Format s) finished: $n read this run" | Add-Content $log
