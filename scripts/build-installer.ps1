param([Parameter(Mandatory=$true)][string]$AppSource,[Parameter(Mandatory=$true)][string]$SetupOutput,[string]$Compiler='ISCC.exe')
$ErrorActionPreference='Stop'
$sourcePath=(Resolve-Path -LiteralPath $AppSource).Path
if(-not(Test-Path -LiteralPath (Join-Path $sourcePath 'AutoScope.exe'))){throw 'Publish the self-contained Windows app first.'}
New-Item -ItemType Directory -Force -Path $SetupOutput | Out-Null
$outputPath=(Resolve-Path -LiteralPath $SetupOutput).Path
& $Compiler "/DAppSource=$sourcePath" "/DSetupOutput=$outputPath" (Join-Path $PSScriptRoot 'ArmadaAnalyzer.iss')
if($LASTEXITCODE -ne 0){throw "Installer compilation failed: $LASTEXITCODE"}
