# Ligne de commande et PowerShell

Avec l'installation MSI, `Corral.exe` pilote le service depuis un terminal ou un script. Les commandes respectent les [stratégies](strategies.md)
et la [configuration de parc](parc.md) ; l'interface ouverte affiche aussitôt les règles modifiées.

## Commandes

| Commande | Effet |
|---|---|
| `Corral status [--json]` | État : pause, Mode Jeu, CPU, mémoire, profil et règles |
| `Corral processes [--json]` | Processus suivis |
| `Corral rules [--json]` | Règles du profil actif |
| `Corral rules add <nom> [options]` | Ajoute ou remplace une règle (`--priority`, `--affinity 0xFF`, `--cpu-limit 50`, `--efficiency on/off`, `--game`) |
| `Corral rules remove <nom>` (ou `enable`, `disable`) | Supprime, active ou désactive une règle |
| `Corral rules export <fichier>` | Exporte les règles |
| `Corral rules import <fichier> [--replace]` | Importe des règles |
| `Corral pause`, `Corral resume` | Suspend ou reprend Corral |
| `Corral game on` ou `off` | Mode Jeu manuel |
| `Corral clean` | Nettoie la mémoire |
| `Corral priority <pid> <priorité>` | Change la priorité d'un processus une fois |
| `Corral fleet keygen`, `sign`, `check` | Outils de la [configuration de parc](parc.md) |

Codes de sortie : **0** succès, **1** erreur, **2** commande invalide, **3** service injoignable.

!!! note "Invite de commandes"
    `Corral.exe` est une application fenêtrée : dans `cmd`, sa réponse s'affiche après le retour de l'invite. PowerShell et le module ci-dessous n'ont pas ce défaut.

## Module PowerShell

Installé avec le MSI, disponible dans Windows PowerShell 5.1 et PowerShell 7 :

```powershell
Get-CorralStatus
Get-CorralProcess chrome* | Set-CorralPriority -Priority BelowNormal
Add-CorralRule obs64.exe -Priority AboveNormal
Get-CorralRule | Export-Csv regles.csv -NoTypeInformation
Set-CorralGameMode $true
```

| Commande | Équivalent |
|---|---|
| `Get-CorralStatus`, `Get-CorralProcess`, `Get-CorralRule` | `status`, `processes`, `rules` (objets PowerShell) |
| `Add-CorralRule`, `Remove-CorralRule`, `Enable-CorralRule`, `Disable-CorralRule` | `rules add/remove/enable/disable` |
| `Export-CorralRule`, `Import-CorralRule` | `rules export/import` |
| `Suspend-Corral`, `Resume-Corral`, `Set-CorralGameMode`, `Set-CorralPriority`, `Clear-CorralMemory` | `pause`, `resume`, `game`, `priority`, `clean` |
| `Invoke-Corral` | N'importe quelle commande, sortie texte |
