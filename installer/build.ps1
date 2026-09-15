param(
    [Parameter(Mandatory)]
    [string]$PayloadDirectory,
    [string]$IsccPath = 'ISCC.exe'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$buildDirectory = Join-Path $PSScriptRoot 'obj'
$outputDirectory = Join-Path $repository 'dist'
$prerequisite = Get-Content (Join-Path $PSScriptRoot 'prerequisites.json') -Raw | ConvertFrom-Json
$package = Get-Content (Join-Path $repository 'pyproject.toml') -Raw
if ($package -notmatch '(?m)^version = "(\d+\.\d+\.\d+)"') { throw 'Cannot read the Dahlia package version.' }
$version = $Matches[1]
$compiler = (Get-Command $IsccPath -ErrorAction Stop).Source

foreach ($name in @('SldworksPlugin.dll', 'SldworksCore.dll', 'SldworksPlugin.comhost.dll',
    'SldworksPlugin.tlb', 'SldworksPlugin.deps.json', 'SldworksPlugin.runtimeconfig.json',
    'THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) {
        throw "Missing payload file: $name"
    }
}
if (Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object Extension -in '.cs', '.csproj', '.sln') {
    throw 'The installer payload must contain compiled binaries, not C# source or projects.'
}
foreach ($name in @('SldworksPlugin.dll', 'SldworksCore.dll')) {
    $binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload $name)).ProductVersion
    if ($binaryVersion.Split('+')[0] -ne $version) {
        throw "$name version $binaryVersion does not match the client version $version."
    }
}

New-Item -ItemType Directory -Path $buildDirectory, $outputDirectory -Force | Out-Null
$runtimePath = Join-Path $buildDirectory "windowsdesktop-runtime-$($prerequisite.version)-win-x64.exe"
if (-not (Test-Path -LiteralPath $runtimePath)) {
    Invoke-WebRequest -Uri $prerequisite.url -OutFile $runtimePath
}
if ((Get-FileHash -LiteralPath $runtimePath -Algorithm SHA512).Hash -ne $prerequisite.sha512) {
    throw "Runtime checksum mismatch: $runtimePath"
}
$signature = Get-AuthenticodeSignature -LiteralPath $runtimePath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation,') {
    throw 'The runtime installer must have a valid Microsoft signature.'
}

& $compiler "/DAppVersion=$version" "/DRuntimeVersion=$($prerequisite.version)" "/DRuntimeInstaller=$runtimePath" `
    "/DPayloadDirectory=$payload" "/DOutputDirectory=$outputDirectory" (Join-Path $PSScriptRoot 'dahlia.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
$installer = Join-Path $outputDirectory "Dahlia-SOLIDWORKS-$version-x64-Setup.exe"
$checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $(Split-Path $installer -Leaf)" | Set-Content -LiteralPath "$installer.sha256" -Encoding ASCII
Write-Host "Installer: $installer"
