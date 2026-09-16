param(
    [Parameter(Mandatory)]
    [string]$SolidWorksInteropPath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $PSScriptRoot 'plugin\SldworksPlugin.csproj'
$payload = Join-Path $PSScriptRoot 'obj\payload'
$interop = (Resolve-Path -LiteralPath $SolidWorksInteropPath).Path
$package = Get-Content -LiteralPath (Join-Path $repository 'pyproject.toml') -Raw
if ($package -notmatch '(?m)^version = "(\d+\.\d+\.\d+)"') { throw 'Cannot read the Dahlia package version.' }
$version = $Matches[1]
$null = Get-Command dotnet, dscom -ErrorAction Stop
foreach ($name in @('sldworks', 'swcommands', 'swconst', 'swpublished')) {
    if (-not (Test-Path -LiteralPath (Join-Path $interop "SolidWorks.Interop.$name.dll") -PathType Leaf)) {
        throw "Missing SOLIDWORKS interop assembly: SolidWorks.Interop.$name.dll"
    }
}

if (Test-Path -LiteralPath $payload) {
    $resolvedPayload = (Resolve-Path -LiteralPath $payload).Path
    if ($resolvedPayload -ne [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'obj\payload'))) {
        throw "Unexpected staging directory: $resolvedPayload"
    }
    Remove-Item -LiteralPath $resolvedPayload -Recurse -Force
}

& dotnet publish $project -c $Configuration --self-contained false -o $payload "-p:Version=$version" "-p:SolidWorksInteropPath=$interop" --nologo
if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed ($LASTEXITCODE)." }
& dscom tlbexport (Join-Path $payload 'SldworksPlugin.dll') --out (Join-Path $payload 'SldworksPlugin.tlb')
if ($LASTEXITCODE -ne 0) { throw "Type library export failed ($LASTEXITCODE)." }

$notices = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $notices | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin\obj\project.assets.json') -Raw | ConvertFrom-Json
$summary = @('Third-party components', '', 'SOLIDWORKS interop assemblies: Dassault Systemes SolidWorks Corporation.',
    'SOLIDWORKS is installed and licensed separately.', '')
foreach ($library in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
    $packageDirectory = $assets.packageFolders.PSObject.Properties.Name |
        ForEach-Object { Join-Path $_ $library.Value.path } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $packageDirectory) { throw "Missing restored package: $($library.Name)" }
    $manifest = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' | Select-Object -First 1
    [xml]$metadata = Get-Content -LiteralPath $manifest.FullName -Raw
    $summary += "$($library.Name) - $($metadata.package.metadata.license.InnerText)"
    $summary += [string]$metadata.package.metadata.licenseUrl
    $summary += [string]$metadata.package.metadata.copyright
    $summary += [string]$metadata.package.metadata.projectUrl
    $summary += ''
    $destination = Join-Path $notices ($library.Name.Replace('/', '-'))
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem -LiteralPath $packageDirectory -File |
        Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)' } |
        Copy-Item -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $repository 'installer\Apache-2.0.txt') -Destination $notices
$summary | Set-Content -LiteralPath (Join-Path $payload 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8
Write-Host "Built native payload: $payload"
