# Stratégies de groupe

`Corral-ADMX.zip` (page [Releases](https://github.com/Captain-VII/Corral/releases/latest)) contient les modèles ADMX/ADML en français et en anglais.
Copiez `Corral.admx` et les dossiers `fr-FR` et `en-US` dans `C:\Windows\PolicyDefinitions`, ou dans le magasin central de votre domaine.
Les stratégies apparaissent sous **Configuration ordinateur › Modèles d'administration › Corral**.

Elles sont écrites dans `HKLM\SOFTWARE\Policies\Corral` : vous pouvez aussi les pousser par Intune (OMA-URI, ou ingestion ADMX) ou par un script.

| Stratégie | Valeur de registre | Effet |
|---|---|---|
| Verrouiller les réglages | `LockSettings` = 1 | Règles, ProBalance, Mode Jeu et optimisations en lecture seule, y compris en ligne de commande |
| Règles imposées | `RulesFile` = chemin | Les règles d'un export Corral (partage réseau possible) remplacent les règles locales |
| Configuration de parc signée | `ConfigFile` = chemin, partage ou https ; `ConfigPublicKey` = clé | Voir [Configuration de parc](parc.md) |
| ProBalance | `ProBalance` = 0 ou 1 | Forcé désactivé ou activé |
| Désactiver les mises à jour | `DisableUpdates` = 1 | Les versions sont déployées par l'équipe informatique |
| Interdire de terminer des processus | `DisableProcessTermination` = 1 | |
| Interdire la gestion du démarrage | `DisableStartupManager` = 1 | Page Démarrage en lecture seule |

Les stratégies sont lues au démarrage du service : redémarrez-le (`Restart-Service Corral`) ou le PC pour qu'un changement prenne effet.
Les pages concernées affichent un bandeau 🔒 dans l'interface.
