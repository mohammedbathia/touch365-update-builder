param([string]$PayloadPath, [string]$UpdateTrustPath)
$ErrorActionPreference = 'Stop'
$setupRoot = $PSScriptRoot
$projectRoot = Split-Path $setupRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if ($PayloadPath) {
    $payload = Get-Item -LiteralPath $PayloadPath
    if ((Get-FileHash -LiteralPath $payload.FullName).Hash -ne '8555777215510D83D2D61C9DC984E4FCC838BD7E79F9D18A42585431F5E8BB47') { throw 'Wrong RustDesk payload hash.' }
} else {
    $payload = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'clients') -Filter '*.exe' | Where-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq '8555777215510D83D2D61C9DC984E4FCC838BD7E79F9D18A42585431F5E8BB47' } | Select-Object -First 1
}
if (!$payload) { throw 'The verified official RustDesk 1.5.0 payload was not found.' }
$signature = Get-AuthenticodeSignature -LiteralPath $payload.FullName
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|, )CN=PURSLANE(?:,|$)') { throw 'The official RustDesk publisher signature is invalid.' }
$dist = Join-Path $setupRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$build = Join-Path $setupRoot 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$embeddedPayload = Join-Path $build 'rustdesk-1.5.0-x86_64.exe'
Copy-Item -LiteralPath $payload.FullName -Destination $embeddedPayload -Force
if ((Get-FileHash -LiteralPath $embeddedPayload).Hash -ne (Get-FileHash -LiteralPath $payload.FullName).Hash) { throw 'Payload staging verification failed.' }
$output = Join-Path $dist 'Touch365-Remote-Support-Setup-1.1.0.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $setupRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
$compileArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warnaserror+', '/langversion:5', "/win32manifest:$(Join-Path $setupRoot 'app.manifest')", "/out:$output", "/resource:$embeddedPayload,RustDeskPayload", "/resource:$(Join-Path $setupRoot 'RustDesk-LICENSE.txt'),RustDeskLicense", "/resource:$(Join-Path $setupRoot 'THIRD-PARTY-NOTICES.txt'),ThirdPartyNotices", '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:System.ServiceProcess.dll', '/r:System.Xml.dll', '/r:System.Xml.Linq.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:Microsoft.CSharp.dll') + $sources
if ($UpdateTrustPath) {
    $trustProfile = Get-Item -LiteralPath $UpdateTrustPath
    if ($trustProfile.Length -lt 1 -or $trustProfile.Length -gt 16384) { throw 'Invalid public update trust profile size.' }
    $compileArgs += ('/resource:' + $trustProfile.FullName + ',UpdaterTrust')
}
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'The setup build failed.' }
if ($UpdateTrustPath) {
    $trustValidation = Start-Process -FilePath $output -ArgumentList '--validate-update-trust' -WindowStyle Hidden -Wait -PassThru
    if ($trustValidation.ExitCode -ne 0) { throw 'The embedded public update trust profile was rejected.' }
}
$receipt = [ordered]@{
    Product = 'Touch365 Remote Support Setup'; Version = '1.1.0'; BuiltUtc = [DateTime]::UtcNow.ToString('o')
    File = (Split-Path $output -Leaf); SHA256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    RustDeskVersion = '1.5.0'; RustDeskSHA256 = (Get-FileHash -LiteralPath $payload.FullName -Algorithm SHA256).Hash
    RustDeskSignature = $signature.Status.ToString(); SetupSigned = $false; UpdateTrustConfigured = [bool]$UpdateTrustPath
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'build-receipt.json') -Encoding utf8
Copy-Item -LiteralPath $output -Destination (Join-Path $dist 'Touch365-Remote-Support-Setup.exe') -Force
Copy-Item -LiteralPath (Join-Path $setupRoot 'CUSTOMER-START-HERE.txt'),(Join-Path $setupRoot 'THIRD-PARTY-NOTICES.txt'),(Join-Path $setupRoot 'RustDesk-LICENSE.txt') -Destination $dist -Force
$receipt | ConvertTo-Json
