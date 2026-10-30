param()
$ErrorActionPreference = 'Stop'
$setupRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is unavailable.' }
$testOutput = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$testExe = Join-Path $testOutput 'UpdateTests.exe'
$sources = @('SetupEngine.cs', 'LocalSetting.cs', 'UpdateProtocol.cs', 'UpdatePackage.cs', 'UpdateCoordinator.cs', 'UpdateEnvironment.cs', 'UpdateProfile.cs', 'UpdateHandoff.cs', 'UpdateController.cs') | ForEach-Object { Join-Path (Join-Path $setupRoot 'src') $_ }
$sources += @('UpdateTests.cs', 'UpdateProtocolTests.cs', 'UpdatePackageTests.cs', 'UpdateCoordinatorTests.cs', 'UpdateEnvironmentTests.cs', 'UpdateProfileTests.cs', 'UpdateHandoffTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/optimize+', '/warnaserror+', ('/out:' + $testExe), '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:System.ServiceProcess.dll', '/r:System.Xml.dll', '/r:System.Xml.Linq.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll') + $sources
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Updater test compilation failed.' }
$applicationSources = Get-ChildItem -LiteralPath (Join-Path $setupRoot 'src') -File -Filter '*.cs' | Select-Object -ExpandProperty FullName
$compileOnlyExe = Join-Path $testOutput 'SetupCompilation.exe'
$applicationArguments = @('/nologo', '/target:winexe', '/platform:x64', '/langversion:5', '/optimize+', '/warnaserror+', ('/out:' + $compileOnlyExe), ('/win32manifest:' + (Join-Path $setupRoot 'app.manifest')), '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:System.ServiceProcess.dll', '/r:System.Xml.dll', '/r:System.Xml.Linq.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.CSharp.dll') + $applicationSources
& $compiler @applicationArguments
if ($LASTEXITCODE -ne 0) { throw 'Full controller/application source compilation failed.' }
# This compile-only file deliberately has no payload or production trust resource.
# Execute only the read-only disabled-channel validation mode, never setup/enrollment.
$disabledProbe = Start-Process -FilePath $compileOnlyExe -ArgumentList '--validate-update-trust' -WindowStyle Hidden -Wait -PassThru
if ($disabledProbe.ExitCode -ne 2) { throw 'An unprovisioned build did not fail closed as disabled.' }
Write-Output 'PASS full application compiles and unprovisioned update channel is disabled.'
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Updater tests failed.' }
