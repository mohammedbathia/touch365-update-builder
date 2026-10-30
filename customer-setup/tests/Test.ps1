param(
    [string]$PayloadPath,
    [string]$InstalledPath = 'C:\Program Files\RustDesk\RustDesk.exe'
)
$ErrorActionPreference = 'Stop'
$setupRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $setupRoot
if ([string]::IsNullOrWhiteSpace($PayloadPath)) {
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $workspaceRoot 'clients') -File -Filter '*.exe')
    if ($candidates.Count -ne 1) { throw 'Specify the exact bundled RustDesk payload with -PayloadPath.' }
    $PayloadPath = $candidates[0].FullName
}
$PayloadPath = (Resolve-Path -LiteralPath $PayloadPath).Path
$InstalledPath = (Resolve-Path -LiteralPath $InstalledPath).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is unavailable.' }
$testOutput = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$testExe = Join-Path $testOutput 'EngineTests.exe'
$arguments = @(
    '/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/optimize+',
    ('/out:' + $testExe),
    '/r:System.Core.dll', '/r:System.Management.dll', '/r:System.ServiceProcess.dll', '/r:System.Xml.Linq.dll',
    (Join-Path $setupRoot 'src\SetupEngine.cs'),
    (Join-Path $setupRoot 'src\LocalSetting.cs'),
    (Join-Path $PSScriptRoot 'EngineTests.cs')
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Engine test compilation failed.' }
& $testExe $PayloadPath $InstalledPath
if ($LASTEXITCODE -ne 0) { throw 'Engine tests failed.' }
