param(
    [Parameter(Mandatory = $true)]
    [string]$GamePath,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'bin\COM3D2.DoubleNipple.dll'),
    [string]$CscPath = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
)

$ErrorActionPreference = 'Stop'
$managed = Join-Path $GamePath 'COM3D2x64_Data\Managed'
$bepInEx = Join-Path $GamePath 'BepInEx\core'
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

& $CscPath /nologo /noconfig /target:library /langversion:7.3 /out:$OutputPath `
  /nostdlib+ `
  /reference:"$managed\mscorlib.dll" `
  /reference:"$managed\System.dll" `
  /reference:"$managed\System.Core.dll" `
  /reference:"$bepInEx\BepInEx.dll" `
  /reference:"$bepInEx\0Harmony.dll" `
  /reference:"$managed\Assembly-CSharp.dll" `
  /reference:"$managed\UnityEngine.dll" `
  (Join-Path $PSScriptRoot 'src\DoubleNipple.cs')

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host $OutputPath
