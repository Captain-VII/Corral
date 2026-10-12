# Manifestes winget (https://github.com/microsoft/winget-pkgs) pour une version publiée.
# Appelé par .github/workflows/release.yml ; le résultat (Corral-winget.zip) se soumet par une PR sur microsoft/winget-pkgs :
#   wingetcreate submit <dossier de la version>     ou copie manuelle dans manifests/c/CaptainVII/Corral/<version>/
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $MsiDir = 'out',
    [string] $OutDir = 'out/winget',
    [string] $Repository = 'Captain-VII/Corral'
)
$ErrorActionPreference = 'Stop'
$id = 'CaptainVII.Corral'
$manifest = '1.12.0'
$upgradeCode = '{31D4CBD5-E6DE-4F73-A8C0-4205DD50BBB3}' # packaging/Corral.wxs

function Get-MsiProperty([string] $Path, [string] $Name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @((Resolve-Path $Path).Path, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property = '$Name'"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    $value = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
    $value
}

$installers = foreach ($arch in 'x64', 'arm64') {
    $name = if ($arch -eq 'arm64') { 'Corral-arm64.msi' } else { 'Corral.msi' }
    $msi = Join-Path $MsiDir $name
    @"
- Architecture: $arch
  InstallerUrl: https://github.com/$Repository/releases/download/v$Version/$name
  InstallerSha256: $((Get-FileHash $msi -Algorithm SHA256).Hash)
  ProductCode: '$(Get-MsiProperty $msi 'ProductCode')'
  AppsAndFeaturesEntries:
  - UpgradeCode: '$upgradeCode'
"@
}

$folder = Join-Path $OutDir "manifests/c/CaptainVII/Corral/$Version"
New-Item -ItemType Directory -Force $folder | Out-Null
$header = "# Généré par packaging/winget/make-manifests.ps1"

Set-Content -Encoding utf8 (Join-Path $folder "$id.yaml") @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$manifest.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: fr-FR
ManifestType: version
ManifestVersion: $manifest
"@

Set-Content -Encoding utf8 (Join-Path $folder "$id.installer.yaml") @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$manifest.schema.json
PackageIdentifier: $id
PackageVersion: $Version
Platform:
- Windows.Desktop
MinimumOSVersion: 10.0.17763.0
InstallerType: wix
Scope: machine
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
ElevationRequirement: elevatesSelf
Commands:
- corral
ReleaseDate: $(Get-Date -Format 'yyyy-MM-dd')
Installers:
$($installers -join "`n")
ManifestType: installer
ManifestVersion: $manifest
"@

$common = @"
PackageIdentifier: $id
PackageVersion: $Version
Publisher: Captain-VII
PublisherUrl: https://github.com/Captain-VII
PublisherSupportUrl: https://github.com/$Repository/issues
PackageName: Corral
PackageUrl: https://captain-vii.github.io/Corral/
License: GPL-3.0
LicenseUrl: https://github.com/$Repository/blob/main/LICENSE
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/v$Version
Tags:
- cpu
- performance
- priority
- probalance
- process-lasso
- process-manager
"@

Set-Content -Encoding utf8 (Join-Path $folder "$id.locale.fr-FR.yaml") @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$manifest.schema.json
$common
PackageLocale: fr-FR
ShortDescription: Gestionnaire de processus libre et gratuit, alternative open source à Process Lasso.
Description: Règles par application (priorité, cœurs, plan d'alimentation, plafonds), ProBalance, Mode Jeu, surveillance, service Windows, stratégies de groupe et ligne de commande.
ManifestType: defaultLocale
ManifestVersion: $manifest
"@

Set-Content -Encoding utf8 (Join-Path $folder "$id.locale.en-US.yaml") @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.$manifest.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Captain-VII
PackageName: Corral
License: GPL-3.0
ShortDescription: Free and open source process manager, an alternative to Process Lasso.
Description: Per-application rules (priority, cores, power plan, limits), ProBalance, Game Mode, monitoring, Windows service, group policies and command line.
ManifestType: locale
ManifestVersion: $manifest
"@

Write-Host "Manifestes winget : $folder"
