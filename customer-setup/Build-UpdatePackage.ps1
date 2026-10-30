param(
    [Parameter(Mandatory=$true)][string]$ApplicationPath,
    [Parameter(Mandatory=$true)][string]$VerifierPath,
    [Parameter(Mandatory=$true)][string]$PackageUrl,
    [Parameter(Mandatory=$true)][string]$DestinationPath
)
$ErrorActionPreference = 'Stop'
# Source preparation only: no signing, publication, registration or execution of
# RustDesk, helpers or driver installers. Approval of the source/provenance and
# signing the final metadata remain separate owner actions.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$application = [IO.Path]::GetFullPath($ApplicationPath)
$destination = [IO.Path]::GetFullPath($DestinationPath)
$verifier = [IO.Path]::GetFullPath($VerifierPath)
foreach ($path in @($application,$destination,$verifier)) {
    if ($path.StartsWith('\\') -or $path -notmatch '^[A-Za-z]:\\' -or $path.Substring(2).Contains(':') -or
        [IO.DriveInfo]::new([IO.Path]::GetPathRoot($path)).DriveType -ne [IO.DriveType]::Fixed) { throw 'Use canonical local fixed-drive Windows paths for package preparation.' }
    $ancestor=$path
    while ($ancestor) {
        if (([IO.File]::Exists($ancestor) -or [IO.Directory]::Exists($ancestor)) -and
            (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Package preparation cannot use redirected paths.' }
        $ancestor=[IO.Path]::GetDirectoryName($ancestor)
    }
}
if (!(Test-Path -LiteralPath $application -PathType Container) -or !(Test-Path -LiteralPath $verifier -PathType Leaf)) { throw 'The approved application directory or trusted source-built verifier is missing.' }
if (Test-Path -LiteralPath $destination) { throw 'The package destination must be new; existing data is never overwritten.' }
if ($destination.StartsWith($application.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output cannot be nested inside the source application.' }
$uri = $null
if (![Uri]::TryCreate($PackageUrl,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -cne 'https' -or $uri.Port -ne 443 -or $uri.IsLoopback -or
    $uri.UserInfo -or $uri.Query -or $uri.Fragment -or [Uri]::CheckHostName($uri.DnsSafeHost) -ne [UriHostNameType]::Dns -or
    $uri.DnsSafeHost.EndsWith('.') -or $uri.DnsSafeHost.IndexOf('.') -lt 0 -or !$uri.AbsolutePath.EndsWith('.zip',[StringComparison]::OrdinalIgnoreCase)) { throw 'Declare an approved public HTTPS ZIP URL without credentials, query or fragment. This tool does not publish or contact it.' }
[Reflection.Assembly]::LoadFile($verifier) | Out-Null
[Touch365Support.SafeFiles]::NoReparse($application)
[Touch365Support.SafeFiles]::NoReparse($destination)
[Touch365Support.SafeFiles]::NoReparse($verifier)
$manifest = [Touch365Support.UpdateManifest]::new()
$manifest.PackageLayout = [Touch365Support.UpdatePackage]::CompleteLayout
$manifest.PackageUrl = $uri.AbsoluteUri
$rawVersion = (Get-Item -LiteralPath (Join-Path $application 'RustDesk.exe')).VersionInfo.ProductVersion
if ($rawVersion -notmatch '^([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:\+.*)?$') { throw 'The actual RustDesk product version could not be read.' }
$manifest.BinaryVersion = [Version]::Parse($Matches[1])
$sourceFingerprint = [Touch365Support.UpdatePackage]::Fingerprint($application)
foreach ($entry in Get-ChildItem -LiteralPath $application -Recurse -File -Force) {
    $relative = $entry.FullName.Substring($application.TrimEnd('\').Length+1).Replace('\','/')
    if ([Touch365Support.UpdatePackage]::IsPreparationOnlyExtra($relative)) { continue }
    $file = [Touch365Support.UpdateFile]::new()
    $file.Path = $relative; $file.Bytes = $entry.Length
    $file.Sha256 = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest.Files.Add($file)
}
[Touch365Support.UpdatePackage]::RequireCompleteApplication($manifest)
New-Item -ItemType Directory -Path $destination | Out-Null
[Touch365Support.SafeFiles]::NoReparse($destination)
$prepared = Join-Path $destination 'prepared-application'
New-Item -ItemType Directory -Path $prepared | Out-Null
foreach ($file in $manifest.Files) {
    $source = Join-Path $application $file.Path.Replace('/',[IO.Path]::DirectorySeparatorChar)
    $target = Join-Path $prepared $file.Path.Replace('/',[IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    [Touch365Support.SafeFiles]::NoReparse($source); [Touch365Support.SafeFiles]::NoReparse($target)
    [IO.File]::Copy($source,$target,$false)
}
[Touch365Support.UpdatePackage]::VerifyDirectory($prepared,$manifest)
$zipPath = Join-Path $destination 'application.zip'
$zip = [IO.File]::Open($zipPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new($zip,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($file in $manifest.Files) {
            $sourcePath = Join-Path $prepared $file.Path.Replace('/',[IO.Path]::DirectorySeparatorChar)
            [Touch365Support.SafeFiles]::NoReparse($sourcePath)
            $entry = $archive.CreateEntry($file.Path,[IO.Compression.CompressionLevel]::Optimal)
            $input = [IO.File]::Open($sourcePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try { $output=$entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
        }
    } finally { $archive.Dispose() }
    $zip.Flush($true)
} finally { $zip.Dispose() }
$manifest.PackageBytes = (Get-Item -LiteralPath $zipPath).Length
if ($manifest.PackageBytes -gt [Touch365Support.UpdateProtocol]::MaximumPackageBytes) { throw 'The prepared ZIP exceeds updater transport limits.' }
$manifest.PackageSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
# The same production ZIP extractor rechecks all signed-inventory-shaped bytes,
# native PE/catalog publishers and exact local Windows broker. This unsigned
# preparation inventory cannot authorize a live update; the worker requires the
# separately authenticated signed envelope before accepting it.
$roundTrip = Join-Path $destination 'verified-application'
[Touch365Support.UpdatePackage]::ExtractVerified($zipPath,$roundTrip,$manifest)
$preparedFingerprint = [Touch365Support.UpdatePackage]::Fingerprint($prepared)
if ([Touch365Support.UpdatePackage]::Fingerprint($roundTrip) -cne $preparedFingerprint) { throw 'The verified package round-trip changed the prepared application identity.' }
if ([Touch365Support.UpdatePackage]::Fingerprint($application) -cne $sourceFingerprint) { throw 'Application files changed during package preparation. This package must not be signed.' }
$builder = [Text.StringBuilder]::new()
$settings = [Xml.XmlWriterSettings]::new(); $settings.OmitXmlDeclaration=$true; $settings.Indent=$true
$writer = [Xml.XmlWriter]::Create($builder,$settings)
try {
    $writer.WriteStartElement('binary')
    foreach ($pair in @(@('version',$manifest.BinaryVersion.ToString()),@('layout',$manifest.PackageLayout),@('url',$manifest.PackageUrl),@('bytes',$manifest.PackageBytes.ToString([Globalization.CultureInfo]::InvariantCulture)),@('sha256',$manifest.PackageSha256))) { $writer.WriteAttributeString($pair[0],$pair[1]) }
    foreach ($file in $manifest.Files) { $writer.WriteStartElement('file'); $writer.WriteAttributeString('path',$file.Path); $writer.WriteAttributeString('bytes',$file.Bytes.ToString([Globalization.CultureInfo]::InvariantCulture)); $writer.WriteAttributeString('sha256',$file.Sha256); $writer.WriteEndElement() }
    $writer.WriteEndElement()
} finally { $writer.Dispose() }
$fragment = Join-Path $destination 'unsigned-binary-fragment.xml'
[IO.File]::WriteAllText($fragment,$builder.ToString(),[Text.UTF8Encoding]::new($false))
$receipt = [ordered]@{
    Layout=$manifest.PackageLayout;ActualProductVersion=$rawVersion;ManifestVersion=$manifest.BinaryVersion.ToString()
    SourceDirectory=$application;SourceFingerprint=$sourceFingerprint;VerifierSHA256=(Get-FileHash -LiteralPath $verifier -Algorithm SHA256).Hash
    Files=$manifest.Files.Count;Package=$zipPath;Bytes=$manifest.PackageBytes;SHA256=$manifest.PackageSha256
    DeclaredPackageUrl=$manifest.PackageUrl;RetainedShortcutExcluded=$true;ReviewedObsoleteOverlayAssetsExcluded=$true;NativePeCatalogAndLocalBrokerChecks='passed'
    UnsignedFragment=$fragment;RoundTripDirectory=$roundTrip;RoundTripVerification='passed';PreparedFingerprint=$preparedFingerprint
    Provenance='Operator-approved directory snapshot; non-PE resources require approved upstream provenance before signing.'
    Signed=$false;Published=$false;Installed=$false;CompletedUtc=[DateTime]::UtcNow.ToString('o')
}
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'package-receipt.json') -Encoding UTF8
$receipt | ConvertTo-Json -Depth 4
