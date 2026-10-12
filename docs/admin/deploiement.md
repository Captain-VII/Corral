# Déploiement

Pour un parc de PC, utilisez **`Corral.msi`** (ou `Corral-arm64.msi`). Il installe :

- `Corral.exe` dans `C:\Program Files\Corral\` (ajouté au `PATH`) ;
- le **service Windows « Corral »** (compte SYSTEM, démarrage automatique, redémarré en cas d'échec) qui applique les règles ;
- le lancement de l'interface à l'ouverture de session de chaque utilisateur, sans invite UAC ;
- le module PowerShell `Corral` (voir [Ligne de commande](ligne-de-commande.md)).

## Installation silencieuse

```bat
msiexec /i Corral.msi /qn
```

Compatible Intune (application Windows MSI), Configuration Manager, GPO de déploiement logiciel ou tout outil qui sait lancer `msiexec`.
Après une installation silencieuse, l'interface s'ouvre à la prochaine ouverture de session.

Désinstallation : `msiexec /x Corral.msi /qn`. Tout ce que Corral avait modifié est restauré à l'arrêt du service.

## Mises à jour

Par défaut, le service installe lui-même les nouvelles versions signées publiées sur GitHub.
Pour contrôler les versions déployées, activez la stratégie **Désactiver les mises à jour** et redéployez le nouveau MSI vous-même : il remplace l'ancien.

## Réglages imposés

- [Stratégies de groupe](strategies.md) : verrouiller l'interface, forcer ProBalance, interdire certaines actions.
- [Configuration de parc](parc.md) : les mêmes règles sur tous les postes, depuis un fichier signé.

## Supervision

Avertissements, erreurs et événements importants sont inscrits dans l'**Observateur d'événements** (journal *Application*, source `Corral`),
exploitable par votre outil de supervision. Le journal détaillé est dans `%ProgramData%\Corral\`.
