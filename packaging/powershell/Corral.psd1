@{
    RootModule        = 'Corral.psm1'
    ModuleVersion     = '2.3.0'
    GUID              = '6b0f1c52-8a4e-4d0b-9a57-3f1c2e8b7d41'
    Author            = 'Corral'
    Description       = 'Pilote Corral (gestion des processus) depuis PowerShell : état, processus, règles, pause, Mode Jeu.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Invoke-Corral', 'Get-CorralStatus', 'Get-CorralProcess', 'Get-CorralRule', 'Add-CorralRule',
        'Remove-CorralRule', 'Enable-CorralRule', 'Disable-CorralRule', 'Export-CorralRule', 'Import-CorralRule',
        'Suspend-Corral', 'Resume-Corral', 'Set-CorralGameMode', 'Set-CorralPriority', 'Clear-CorralMemory')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData       = @{ PSData = @{ LicenseUri = 'https://github.com/Captain-VII/Corral/blob/main/LICENSE'; ProjectUri = 'https://github.com/Captain-VII/Corral' } }
}
