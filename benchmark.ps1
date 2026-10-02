param(
    [Parameter(Mandatory)][string]$Game,
    [Parameter(Mandatory)][string]$Maps,
    [string]$Output = (Join-Path $PSScriptRoot 'out/benchmarks'),
    [int]$Runs = 3
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'publish/LightningMap.Cli.exe'
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$samples = @('2_arabian_oasis.map', '2_across_the_frost.map', '2_blockade.map', '2_bajor_le.map', '2_paris_revisited.map', '4_space_race.map', '2_tropic_thunder_le.map')
$records = @()
foreach ($name in $samples) {
    $mapPath = Join-Path $Maps $name
    if (!(Test-Path -LiteralPath $mapPath)) { continue }
    foreach ($mode in @('preview-png', 'native-png', 'preview-jpg')) {
        $width = if ($mode -eq 'native-png') { 0 } else { 1024 }
        $ext = if ($mode -eq 'preview-jpg') { 'jpg' } else { 'png' }
        $stem = [IO.Path]::GetFileNameWithoutExtension($name)
        for ($i = 0; $i -lt $Runs; $i++) {
            $report = Join-Path $Output "$stem-$mode-$i.json"
            $image = Join-Path $Output "$stem-$mode.$ext"
            $watch = [Diagnostics.Stopwatch]::StartNew()
            & $exe $mapPath --game $Game --output $image --width $width --quality 90 --report $report
            $exitCode = $LASTEXITCODE
            $watch.Stop()
            if ($exitCode -ne 0) { throw "Renderer failed for $name ($mode)." }
            $data = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            $r = $data.records[0].result
            $records += [pscustomobject]@{
                Map=$name; Mode=$mode; Run=$i; Width=$r.Width; Height=$r.Height
                ProcessMs=$watch.Elapsed.TotalMilliseconds; InitializationMs=$data.initializationMs
                RenderMs=$r.TotalMs; PrepareMs=$r.PrepareMs; DrawMs=$r.DrawMs; EncodeMs=$r.EncodeMs
                PeakMiB=$r.PeakWorkingSetMiB; Bytes=(Get-Item -LiteralPath $image).Length
                Warnings=($r.Warnings -join '; ')
            }
        }
    }
}
$records | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $Output 'measurements.csv')
Write-Output "Saved $(Join-Path $Output 'measurements.csv'). Each run uses a new process; the OS file cache is not flushed."
