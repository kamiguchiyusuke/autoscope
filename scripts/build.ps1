param([string]$DotNet='dotnet',[ValidateSet('MSVC','CMake')][string]$NativeToolchain='MSVC',[switch]$SkipTests,[switch]$FrameworkDependent)
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Force artifacts | Out-Null
    $env:DOTNET_CLI_HOME=Join-Path $repositoryRoot '.local/dotnet-home'
    $env:APPDATA=Join-Path $repositoryRoot '.local/appdata'
    $env:NUGET_PACKAGES=Join-Path $repositoryRoot '.local/packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    New-Item -ItemType Directory -Force $env:APPDATA | Out-Null
    if($NativeToolchain -eq 'MSVC') {
        $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if(!(Test-Path -LiteralPath $vswhere)){throw 'Install the free Visual Studio C++ Build Tools or select -NativeToolchain CMake.'}
        $installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if(!$installation){throw 'The C++ x64 build tools were not found.'}
        $vcvars=Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
        $commands=@('@echo off',('call "'+$vcvars+'" >nul'),'if errorlevel 1 exit /b 1',
          'cl /nologo /std:c++20 /O2 /EHsc /MT /LD Native\montecarlo.cpp /Feartifacts\ArmadaCompute.dll /Foartifacts\compute.obj /link /IMPLIB:artifacts\compute.lib',
          'if errorlevel 1 exit /b 1',
          'cl /nologo /std:c++20 /O2 /EHsc /MT /LD Native\tds_metadata.cpp /Feartifacts\AutoScopeMetadata.dll /Foartifacts\metadata.obj /link /IMPLIB:artifacts\metadata.lib',
          'if errorlevel 1 exit /b 1',
          'cl /nologo /O2 /MT /LD /DSQLITE_API=__declspec(dllexport) /DSQLITE_THREADSAFE=1 /DSQLITE_OMIT_LOAD_EXTENSION Vendor\sqlite\sqlite3.c /Feartifacts\ArmadaSqlite.dll /Foartifacts\sqlite.obj /link /IMPLIB:artifacts\sqlite.lib')
        Set-Content -LiteralPath artifacts/build-native.cmd -Value $commands -Encoding utf8
        & $env:ComSpec /d /c 'artifacts\build-native.cmd'
        if($LASTEXITCODE -ne 0){throw 'Native build failed'}
    } else {
        & cmake -S . -B artifacts/native -DCMAKE_BUILD_TYPE=Release
        if($LASTEXITCODE -ne 0){throw 'CMake configure failed'}
        & cmake --build artifacts/native --config Release
        if($LASTEXITCODE -ne 0){throw 'CMake build failed'}
        & cmake --install artifacts/native --config Release --prefix artifacts
        if($LASTEXITCODE -ne 0){throw 'CMake install failed'}
    }
    Copy-Item -LiteralPath artifacts/AutoScopeMetadata.dll -Destination App/TdsBridge/AutoScopeMetadata.dll -Force
    & $DotNet build SamplePlugins -c Release -o artifacts/plugins --nologo
    if($LASTEXITCODE -ne 0){throw 'Plugin build failed'}
    if(!$SkipTests){& $DotNet run --project Tests -c Release -- all; if($LASTEXITCODE -ne 0){throw 'Tests failed'}}
    $selfContained=if($FrameworkDependent){'false'}else{'true'}
    & $DotNet publish App -c Release -r win-x64 --self-contained $selfContained -o artifacts/package --nologo
    if($LASTEXITCODE -ne 0){throw 'Publish failed'}
    # Keep the package limited to the current executable.
    $packageRoot=[IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/package'))
    foreach($obsolete in @('ArmadaAnalyzer.exe','ArmadaAnalyzer.dll','ArmadaAnalyzer.deps.json','ArmadaAnalyzer.runtimeconfig.json','ArmadaAnalyzer.pdb')) {
        $obsoletePath=Join-Path $packageRoot $obsolete
        if(Test-Path -LiteralPath $obsoletePath){Remove-Item -LiteralPath $obsoletePath -Force}
    }
    New-Item -ItemType Directory -Force artifacts/package/Plugins,artifacts/package/Samples | Out-Null
    Copy-Item -LiteralPath artifacts/plugins/ArmadaAnalyzer.SamplePlugins.dll -Destination artifacts/package/Plugins
    Get-ChildItem -LiteralPath samples | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination artifacts/package/Samples -Recurse -Force}
    foreach($name in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md')){Copy-Item -LiteralPath $name -Destination artifacts/package}
    Copy-Item -LiteralPath Legal -Destination artifacts/package -Recurse -Force
    Write-Output 'Build complete: artifacts/package/AutoScope.exe'
} finally {Pop-Location}
