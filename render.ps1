param(
    [Parameter(Mandatory, Position=0)][string]$Map,
    [string]$Output = '',
    [string]$Game = '',
    [ValidateRange(0, 100000)][int]$Width = 0,
    [ValidateRange(1, 100)][int]$Quality = 90,
    [ValidateRange(0.1, 4)][double]$Brightness = 1,
    [switch]$NoLighting,
    [switch]$FullMap
)
$ErrorActionPreference = 'Stop'
if (!$Game) {
    $Game = Join-Path $PSScriptRoot '../xna-cncnet-client/DXMainClient/bin/Debug/WindowsDX/net48'
    if (!(Test-Path -LiteralPath $Game)) { throw 'Specify -Game with your Yuri''s Revenge directory.' }
}
$exe = Join-Path $PSScriptRoot 'publish/LightningMap.Cli.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Build first: dotnet publish LightningMap.Cli/LightningMap.Cli.csproj -c Release -r win-x64 -o publish' }
$renderArgs = @($Map, '--game', $Game, '--width', "$Width", '--quality', "$Quality", '--brightness', $Brightness.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($Output) { $renderArgs += @('--output', $Output) }
if ($NoLighting) { $renderArgs += '--no-lighting' }
if ($FullMap) { $renderArgs += '--full-map' }
& $exe @renderArgs
exit $LASTEXITCODE
