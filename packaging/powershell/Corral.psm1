# Module PowerShell de Corral : enveloppe la ligne de commande de Corral.exe (installation MSI).
# Installé par le MSI dans Program Files\WindowsPowerShell\Modules\Corral (PowerShell 5.1 et 7).

function Get-CorralExe {
    $dir = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Corral' -Name InstallDir -ErrorAction SilentlyContinue).InstallDir
    if ($dir -and (Test-Path (Join-Path $dir 'Corral.exe'))) { return Join-Path $dir 'Corral.exe' }
    $cmd = Get-Command Corral.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "Corral n'est pas installé (Corral.exe introuvable)."
}

# Corral.exe est une application fenêtrée : sa sortie n'est lisible que redirigée.
function Invoke-Corral {
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo (Get-CorralExe)
    $psi.Arguments = ($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw ($err.Result.Trim()) }
    $out
}

function Invoke-CorralJson { (Invoke-Corral @args --json) -join "`n" | ConvertFrom-Json }

function Get-CorralStatus { Invoke-CorralJson status }

function Get-CorralProcess {
    param([string] $Name = '*')
    Invoke-CorralJson processes | ForEach-Object { $_ } | Where-Object Name -like $Name
}

function Get-CorralRule {
    param([string] $Pattern = '*')
    Invoke-CorralJson rules | ForEach-Object { $_ } | Where-Object Pattern -like $Pattern
}

function Add-CorralRule {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Pattern,
        [ValidateSet('Idle', 'BelowNormal', 'Normal', 'AboveNormal', 'High')] [string] $Priority,
        [long] $AffinityMask,
        [ValidateRange(1, 99)] [int] $CpuLimitPercent,
        [Nullable[bool]] $EfficiencyMode,
        [switch] $Game
    )
    $a = @('rules', 'add', $Pattern)
    if ($Priority) { $a += '--priority', $Priority }
    if ($PSBoundParameters.ContainsKey('AffinityMask')) { $a += '--affinity', ('0x{0:X}' -f $AffinityMask) }
    if ($CpuLimitPercent) { $a += '--cpu-limit', $CpuLimitPercent }
    if ($null -ne $EfficiencyMode) { $a += '--efficiency', $(if ($EfficiencyMode) { 'on' } else { 'off' }) }
    if ($Game) { $a += '--game' }
    if ($PSCmdlet.ShouldProcess($Pattern, 'Ajouter la règle')) { Invoke-Corral @a | Write-Verbose }
}

function Remove-CorralRule {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)] [string] $Pattern)
    process { if ($PSCmdlet.ShouldProcess($Pattern, 'Supprimer la règle')) { Invoke-Corral rules remove $Pattern | Write-Verbose } }
}

function Enable-CorralRule {
    param([Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)] [string] $Pattern)
    process { Invoke-Corral rules enable $Pattern | Write-Verbose }
}

function Disable-CorralRule {
    param([Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)] [string] $Pattern)
    process { Invoke-Corral rules disable $Pattern | Write-Verbose }
}

function Export-CorralRule {
    param([Parameter(Mandatory, Position = 0)] [string] $Path)
    Invoke-Corral rules export ([System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))) | Write-Verbose
}

function Import-CorralRule {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory, Position = 0)] [string] $Path, [switch] $Replace)
    $a = @('rules', 'import', (Resolve-Path $Path).ProviderPath)
    if ($Replace) { $a += '--replace' }
    if ($PSCmdlet.ShouldProcess($Path, 'Importer les règles')) { Invoke-Corral @a | Write-Verbose }
}

function Suspend-Corral { Invoke-Corral pause | Write-Verbose }
function Resume-Corral { Invoke-Corral resume | Write-Verbose }

function Set-CorralGameMode {
    param([Parameter(Mandatory, Position = 0)] [bool] $Enabled)
    Invoke-Corral game $(if ($Enabled) { 'on' } else { 'off' }) | Write-Verbose
}

function Set-CorralPriority {
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)] [Alias('Pid')] [int] $Id,
        [Parameter(Mandatory, Position = 1)] [ValidateSet('Idle', 'BelowNormal', 'Normal', 'AboveNormal', 'High')] [string] $Priority
    )
    process { Invoke-Corral priority $Id $Priority | Write-Verbose }
}

function Clear-CorralMemory { Invoke-Corral clean | Write-Verbose }

Export-ModuleMember -Function Invoke-Corral, Get-CorralStatus, Get-CorralProcess, Get-CorralRule, Add-CorralRule, Remove-CorralRule,
    Enable-CorralRule, Disable-CorralRule, Export-CorralRule, Import-CorralRule, Suspend-Corral, Resume-Corral,
    Set-CorralGameMode, Set-CorralPriority, Clear-CorralMemory
