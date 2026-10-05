# Corral

<img src="tools/icon-preview.png" width="96" align="right" alt="Icône de Corral">

Gestionnaire de processus pour Windows 10/11, dans l'esprit de Process Lasso.

- **Règles par application** : priorité, affinité CPU, plan d'alimentation, plafonds CPU et RAM (Job Objects). Jokers acceptés (`chrome*`).
- **ProBalance** : abaisse temporairement la priorité des processus qui saturent le CPU. La fenêtre au premier plan et les processus système ne sont jamais touchés.
- **Restauration** : priorités, affinités et plan d'alimentation d'origine sont rétablis à la fermeture ou en pause, et aussi après un plantage (pour le plan d'alimentation).
- **Interface** : icône dans la zone de notification, thème clair ou sombre, démarrage à l'ouverture de session.
- **Mises à jour automatiques** via les releases GitHub, avec vérification SHA-256.

## Installation

Téléchargez `Corral.exe` depuis la page [Releases](../../releases/latest) et lancez-le. Aucune installation de .NET n'est nécessaire. Corral demande les droits administrateur pour agir sur tous les processus.

La configuration et le journal sont dans `%AppData%\Corral\`.

## Développement

Prérequis : SDK .NET 8.

```bash
dotnet test
dotnet run --project src/Corral
```

Pour publier une version, poussez un tag : le workflow [`release.yml`](.github/workflows/release.yml) teste, compile et crée la release.

```bash
git tag v1.0.1
git push origin v1.0.1
```

Seuls les exe produits par ce workflow se mettent à jour automatiquement : une version compilée localement a les mises à jour désactivées.
