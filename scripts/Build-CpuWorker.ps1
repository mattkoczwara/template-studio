param([string]$NcnnRoot = "$PSScriptRoot/../.tools/ai/ncnn/ncnn-20261020-windows-vs2022/x64")
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
$ncnn = (Resolve-Path $NcnnRoot).Path
$vs = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$buildFolder = Join-Path $repo '.tools\ai\cpu-build'
New-Item -ItemType Directory -Force $buildFolder | Out-Null
$cpp = Join-Path $repo 'vendor\ai\cpu-src\mosaic-cunet-cpu.cpp'
$exe = Join-Path $repo 'vendor\ai\mosaic-cunet-cpu.exe'
# cmd is used only to load the compiler environment and invoke MSVC, never for file operations.
$sdk = Join-Path $repo '.tools\ai\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0'
$sdkLib = Join-Path $repo '.tools\ai\microsoft.windows.sdk.cpp.x64\c'
$includes = @('ucrt','shared','um','winrt') | ForEach-Object { '/I"' + (Join-Path $sdk $_) + '"' }
$compiler = 'cl /nologo /std:c++17 /O2 /EHsc /MD /openmp ' + ($includes -join ' ') + ' /I"' + $ncnn + '\include\ncnn" "' + $cpp + '" /Fe:"' + $exe + '" /link /LIBPATH:"' + $ncnn + '\lib" /LIBPATH:"' + $sdkLib + '\um\x64" /LIBPATH:"' + $sdkLib + '\ucrt\x64" ncnn.lib glslang.lib SPIRV.lib user32.lib advapi32.lib'
$batch = Join-Path $buildFolder 'build.cmd'
@('@echo off', ('call "' + $vcvars + '" >nul'), $compiler, 'exit /b %errorlevel%') | Set-Content -LiteralPath $batch -Encoding Ascii
Push-Location $buildFolder
try { & cmd.exe /d /c $batch; if ($LASTEXITCODE -ne 0) { throw 'CPU worker build failed' } }
finally { Pop-Location }
Get-FileHash -Algorithm SHA256 $exe
