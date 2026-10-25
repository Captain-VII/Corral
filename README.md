# Corral

<img src="tools/icon-preview.png" width="96" align="right" alt="Icône de Corral">

Gestionnaire de processus libre et gratuit pour Windows 10/11, alternative open source à Process Lasso.

📖 **Documentation complète : [captain-vii.github.io/Corral](https://captain-vii.github.io/Corral/)** (guide d'utilisation, déploiement en entreprise, stratégies, configuration de parc, ligne de commande).

[![CI](../../actions/workflows/ci.yml/badge.svg)](../../actions/workflows/ci.yml)
[![Licence GPL-3.0](https://img.shields.io/badge/licence-GPL--3.0-blue)](LICENSE)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/Captain-VII/Corral/badge)](https://scorecard.dev/viewer/?uri=github.com/Captain-VII/Corral)

- **Règles par application** : priorité, affinité CPU (avec choix adaptés au processeur : cœurs P, CCD avec V-Cache, sans SMT), plan d'alimentation, plafonds CPU et RAM (Job Objects), mode efficacité, priorités disque et mémoire, carte graphique préférée. Jokers acceptés (`chrome*`), modèles prêts à l'emploi (Jeu, Streaming, Tâche de fond, Brider), import et export.
- **Automatisations** : bloquer un programme ou n'en autoriser qu'une instance, empêcher la mise en veille tant qu'il tourne, alerte (ou baisse de priorité, ou fermeture) quand il dépasse un seuil de CPU ou de mémoire.
- **ProBalance** : abaisse temporairement la priorité des processus qui saturent le CPU, avec préréglages et statistiques. La fenêtre au premier plan et les processus système ne sont jamais touchés.
- **Mode Jeu** : plan Performances, ProBalance réactif et programmes de fond calmés, automatiquement quand un jeu tourne ou à la demande.
- **Optimisations** (désactivées par défaut) : boost de la fenêtre au premier plan, plan économique après une période d'inactivité, nettoyage de la mémoire.
- **Profils** : plusieurs jeux de règles (Travail, Jeu, Silencieux…) à activer en un clic, depuis la page Règles ou l'icône de notification.
- **Surveillance** : CPU, mémoire, disque (E/S) et GPU par processus, fiche détaillée avec l'historique des 5 dernières minutes, graphiques du CPU et de la mémoire du PC, programmes les plus gourmands, journal filtrable.
- **Démarrage** : liste des programmes lancés avec Windows, à activer ou désactiver comme dans le Gestionnaire des tâches.
- **Restauration** : priorités, affinités et plan d'alimentation d'origine sont rétablis à la fermeture ou en pause, et aussi après un plantage (pour le plan d'alimentation).
- **Interface** : en français ou en anglais (selon Windows), d'autres langues grâce aux traductions de la communauté, accessible (lecteurs d'écran, clavier, contraste élevé), thème clair ou sombre, icône de notification avec la charge du processeur, mini-fenêtre toujours visible, raccourcis clavier globaux (Mode Jeu, pause), démarrage à l'ouverture de session, raccourci dans le menu Démarrer.
- **Mises à jour automatiques** via les releases GitHub, vérifiées par somme SHA-256 et signature numérique.
- **Entreprise** : service Windows (aucune invite UAC ; les comptes standard consultent, seuls les administrateurs modifient les réglages), modèles de stratégie de groupe (ADMX) pour verrouiller les réglages ou imposer des règles, événements dans l'Observateur d'événements.

## Installation

Depuis la page [Releases](../../releases/latest), au choix :

- **`Corral.msi`** (recommandé) : installe Corral dans Program Files avec un **service Windows** qui applique les règles. L'interface se lance à l'ouverture de session, sans invite UAC, y compris pour un utilisateur standard (en lecture seule : seuls les administrateurs du PC modifient les réglages). Désinstallation depuis les Paramètres de Windows.
- Sur un PC ARM64 (Snapdragon…), prenez **`Corral-arm64.msi`** ou **`Corral-arm64.exe`**.
- **`Corral.exe`** : version portable, à lancer depuis n'importe quel dossier. Elle demande les droits administrateur à chaque lancement (invite UAC), sauf si elle est lancée par sa tâche de démarrage.

Aucune installation de .NET n'est nécessaire.

Avec winget (une fois le paquet accepté dans le dépôt winget) : `winget install CaptainVII.Corral`.

Pour vérifier qu'un fichier vient bien du workflow de ce dépôt :

```bash
gh attestation verify Corral.exe --repo Captain-VII/Corral
```

La configuration et le journal sont dans `%ProgramData%\Corral\` avec le MSI, dans `%AppData%\Corral\` pour la version portable. En passant de la version portable au MSI, la configuration est reprise automatiquement.

## Déploiement en entreprise

- **Installation silencieuse** : `msiexec /i Corral.msi /qn` (Intune, SCCM, GPO…).
- **Stratégies de groupe** : `Corral-ADMX.zip` (page Releases) contient les modèles ADMX/ADML en français et en anglais. Copiez `Corral.admx` et les dossiers `fr-FR` / `en-US` dans `C:\Windows\PolicyDefinitions` ou le magasin central. Les réglages sont écrits dans `HKLM\SOFTWARE\Policies\Corral` et peuvent aussi être poussés par Intune (OMA-URI) :

  | Stratégie | Valeur | Effet |
  |---|---|---|
  | Verrouiller les réglages | `LockSettings` = 1 | Règles, ProBalance, Mode Jeu et optimisations en lecture seule |
  | Autoriser les comptes standard à modifier les réglages | `AllowStandardUsers` = 1 | Rend aux comptes standard le droit de modifier les réglages (désactivé par défaut) |
  | Règles imposées | `RulesFile` = chemin | Les règles d'un export Corral (partage réseau possible) remplacent les règles locales |
  | ProBalance | `ProBalance` = 0 ou 1 | Forcé désactivé ou activé |
  | Désactiver les mises à jour | `DisableUpdates` = 1 | Les versions sont déployées par l'équipe informatique |
  | Interdire de terminer des processus | `DisableProcessTermination` = 1 | |
  | Interdire la gestion du démarrage | `DisableStartupManager` = 1 | Page Démarrage en lecture seule |
  | Configuration de parc signée | `ConfigFile` = chemin, partage ou https ; `ConfigPublicKey` = clé publique | Voir ci-dessous |

  Les stratégies sont lues au démarrage du service.
- **Configuration de parc signée** : un seul fichier JSON pour tous les postes, signé avec la clé de votre organisation. Chaque section présente (`Rules`, `ProBalance`, `GameMode`) remplace la configuration locale et passe en lecture seule.

  ```json
  { "App": "Corral", "Serial": 1,
    "Rules": [ { "Pattern": "Teams.exe", "Priority": "AboveNormal" },
               { "Pattern": "backup*.exe", "Priority": "BelowNormal", "When": { "From": "09:00:00", "To": "18:00:00" } } ],
    "ProBalance": { "Enabled": true } }
  ```

  ```
  Corral fleet keygen cle.pem                 (une fois ; gardez cle.pem en lieu sûr)
  Corral fleet sign config.json cle.pem       (à chaque modification, en augmentant Serial)
  Corral fleet check config.json <clé publique>
  ```

  Publiez `config.json` et `config.json.sig` côte à côte (partage réseau ou https), puis renseignez la stratégie *Configuration de parc signée*. Le service relit le fichier toutes les 15 minutes ; un fichier mal signé ou plus ancien que le dernier reçu est refusé, et hors réseau la dernière version valide reste appliquée.
- **Ligne de commande et PowerShell** (installation MSI) : `Corral status`, `Corral rules add jeu.exe --priority High`, `Corral help` pour la liste. Le module PowerShell est installé avec Corral :

  ```powershell
  Get-CorralStatus
  Get-CorralProcess chrome* | Set-CorralPriority -Priority BelowNormal
  Add-CorralRule obs64.exe -Priority AboveNormal
  Get-CorralRule | Export-Csv regles.csv
  ```

  Les commandes passent par le service et respectent *Verrouiller les réglages* ; l'interface ouverte affiche aussitôt les règles modifiées.
- **Serveurs RDS / VDI** : activez *Équité entre les sessions* (page ProBalance) pour qu'aucun utilisateur ne monopolise le processeur ; chaque session ouverte lance sa propre interface, toutes reliées au même service.
- **Supervision** : avertissements, erreurs et événements importants sont inscrits dans l'Observateur d'événements (journal Application, source `Corral`).
- **Sécurité** : le service agit avec les droits du système sur les processus de toutes les sessions. Par défaut, seuls les administrateurs du PC (y compris sans élévation UAC) peuvent modifier les règles, changer une priorité, mettre en pause ou activer le Mode Jeu ; les comptes standard consultent en lecture seule. *Autoriser les comptes standard* lève cette restriction, *Verrouiller les réglages* l'étend aux administrateurs.

## Développement

Prérequis : SDK .NET 8.

```bash
dotnet test
dotnet run --project src/Corral
```

Pour publier une version, poussez un tag : le workflow [`release.yml`](.github/workflows/release.yml) teste, compile l'exe et le MSI, signe la mise à jour et crée la release. Il a besoin du secret `CORRAL_SIGNING_KEY` (clé privée créée par `tools/UpdateSigner keygen`, dont la clé publique est dans `Updater.PublicKey`).

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

Seuls les exe produits par ce workflow se mettent à jour automatiquement : une version compilée localement a les mises à jour désactivées.

## Traduire Corral

Les textes de l'interface sont dans [`translations/`](translations) : `fr.json` (texte d'origine) et `en.json` sont générés depuis le code. Pour une nouvelle langue, créez `translations/xx.json` (`xx` = code de la langue, par exemple `de`) avec les mêmes clés et vos traductions en valeur, puis ouvrez une pull request. Les textes non traduits s'affichent en anglais. Le fichier peut aussi être déposé dans un dossier `lang` à côté de l'exe pour l'essayer sans recompiler.

## Contribuer

Les contributions sont les bienvenues : voir [CONTRIBUTING.md](CONTRIBUTING.md). Les failles de sécurité se signalent en privé (voir [SECURITY.md](SECURITY.md)).

## Licence

Corral est un logiciel libre publié sous licence [GPL-3.0](LICENSE) : vous pouvez l'utiliser, l'étudier, le modifier et le redistribuer, à condition que les versions redistribuées restent sous la même licence.

Corral n'est pas affilié à Bitsum ni à Process Lasso.
