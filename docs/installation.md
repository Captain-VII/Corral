# Installation

Tous les fichiers sont sur la page [Releases](https://github.com/Captain-VII/Corral/releases/latest). Aucune installation de .NET n'est nécessaire.

| Fichier | Pour qui |
|---|---|
| `Corral.msi` | **Recommandé.** Installe Corral dans Program Files avec un service Windows. Aucune invite UAC. Un compte standard peut consulter ; seuls les administrateurs du PC modifient les réglages. |
| `Corral.exe` | Version portable, à lancer depuis n'importe quel dossier. Demande les droits administrateur à chaque lancement. |
| `Corral-arm64.msi`, `Corral-arm64.exe` | Les mêmes, pour les PC ARM (Snapdragon…). |
| `Corral-ADMX.zip` | Modèles de stratégie de groupe, pour les administrateurs. |

## Avec le MSI

Double-cliquez sur `Corral.msi`. Corral s'installe, démarre son service et s'ouvre ; il se relancera ensuite à chaque ouverture de session.
Il se désinstalle depuis **Paramètres › Applications**.

Si vous utilisiez la version portable, sa configuration est reprise automatiquement.

## Alerte SmartScreen

Corral n'est pas encore signé par un certificat de signature de code : Windows peut afficher « Windows a protégé votre ordinateur ».
Cliquez sur **Informations complémentaires** puis **Exécuter quand même**.

Pour vérifier qu'un fichier vient bien du dépôt officiel, avec [GitHub CLI](https://cli.github.com/) :

```bash
gh attestation verify Corral.msi --repo Captain-VII/Corral
```

## Mises à jour

Corral vérifie les nouvelles versions au démarrage et les propose. Chaque fichier téléchargé est contrôlé (somme SHA-256 et signature numérique) avant d'être installé.
Avec le MSI, c'est le service qui installe la mise à jour, puis relance l'interface.

## Où sont les fichiers ?

| | MSI | Portable |
|---|---|---|
| Configuration et journal | `%ProgramData%\Corral\` | `%AppData%\Corral\` |
| Programme | `C:\Program Files\Corral\` | là où vous l'avez mis |
