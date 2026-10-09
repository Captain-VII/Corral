# Corral

<img src="tools/icon-preview.png" width="96" align="right" alt="Icône de Corral">

Gestionnaire de processus libre et gratuit pour Windows 10/11, alternative open source à Process Lasso.

[![CI](../../actions/workflows/ci.yml/badge.svg)](../../actions/workflows/ci.yml)
[![Licence GPL-3.0](https://img.shields.io/badge/licence-GPL--3.0-blue)](LICENSE)

- **Règles par application** : priorité, affinité CPU (avec choix adaptés au processeur : cœurs P, CCD avec V-Cache, sans SMT), plan d'alimentation, plafonds CPU et RAM (Job Objects), mode efficacité, priorités disque et mémoire, carte graphique préférée. Jokers acceptés (`chrome*`), modèles prêts à l'emploi (Jeu, Streaming, Tâche de fond, Brider), import et export.
- **Automatisations** : bloquer un programme ou n'en autoriser qu'une instance, empêcher la mise en veille tant qu'il tourne, alerte (ou baisse de priorité, ou fermeture) quand il dépasse un seuil de CPU ou de mémoire.
- **ProBalance** : abaisse temporairement la priorité des processus qui saturent le CPU, avec préréglages et statistiques. La fenêtre au premier plan et les processus système ne sont jamais touchés.
- **Mode Jeu** : plan Performances, ProBalance réactif et programmes de fond calmés, automatiquement quand un jeu tourne ou à la demande.
- **Optimisations** (désactivées par défaut) : boost de la fenêtre au premier plan, plan économique après une période d'inactivité, nettoyage de la mémoire.
- **Profils** : plusieurs jeux de règles (Travail, Jeu, Silencieux…) à activer en un clic, depuis la page Règles ou l'icône de notification.
- **Surveillance** : CPU, mémoire, disque (E/S) et GPU par processus, fiche détaillée avec l'historique des 5 dernières minutes, graphiques du CPU et de la mémoire du PC, programmes les plus gourmands, journal filtrable.
- **Démarrage** : liste des programmes lancés avec Windows, à activer ou désactiver comme dans le Gestionnaire des tâches.
- **Restauration** : priorités, affinités et plan d'alimentation d'origine sont rétablis à la fermeture ou en pause, et aussi après un plantage (pour le plan d'alimentation).
- **Interface** : en français ou en anglais (selon Windows), thème clair ou sombre, icône de notification avec la charge du processeur, mini-fenêtre toujours visible, raccourcis clavier globaux (Mode Jeu, pause), démarrage à l'ouverture de session, raccourci dans le menu Démarrer.
- **Mises à jour automatiques** via les releases GitHub, vérifiées par somme SHA-256 et signature numérique.

## Installation

Depuis la page [Releases](../../releases/latest), au choix :

- **`Corral.msi`** : installeur classique (Program Files, menu Démarrer, désinstallation depuis les Paramètres de Windows). Installation silencieuse pour un déploiement : `msiexec /i Corral.msi /qn`.
- **`Corral.exe`** : version portable, à lancer depuis n'importe quel dossier.

Aucune installation de .NET n'est nécessaire. Corral demande les droits administrateur pour agir sur tous les processus.

Pour vérifier qu'un fichier vient bien du workflow de ce dépôt :

```bash
gh attestation verify Corral.exe --repo Captain-VII/Corral
```

La configuration et le journal sont dans `%AppData%\Corral\`.

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

## Contribuer

Les contributions sont les bienvenues : voir [CONTRIBUTING.md](CONTRIBUTING.md). Les failles de sécurité se signalent en privé (voir [SECURITY.md](SECURITY.md)).

## Licence

Corral est un logiciel libre publié sous licence [GPL-3.0](LICENSE) : vous pouvez l'utiliser, l'étudier, le modifier et le redistribuer, à condition que les versions redistribuées restent sous la même licence.

Corral n'est pas affilié à Bitsum ni à Process Lasso.
