# Contribuer à Corral

Merci de votre intérêt ! Les contributions sont les bienvenues, en français ou en anglais.

## Signaler un bug ou proposer une idée

Ouvrez une [issue](../../issues/new/choose) avec le modèle adapté. Pour un bug, joignez la version de Corral, la version de Windows et, si possible, l'extrait du journal (`%AppData%\Corral\corral.log`).

Une faille de sécurité ne se signale **pas** dans une issue publique : voir [SECURITY.md](SECURITY.md).

## Proposer une modification

1. Forkez le dépôt et créez une branche par sujet (`fix/...`, `feat/...`).
2. Prérequis : Windows 10/11 et le SDK .NET 8.
3. Lancez les tests avant d'ouvrir la pull request :

   ```bash
   dotnet test
   ```

4. Ouvrez une pull request qui explique le **pourquoi** de la modification.

## Règles du code

- Corral agit sur tous les processus du PC : toute modification doit être **restaurée** à l'arrêt, en pause ou au changement de réglages, et ne jamais toucher aux processus protégés (`Exclusions.IsProtected`).
- Tout texte visible passe par `Tr("français", "English")`.
- Suivez le style du code existant (commentaires en français, noms en anglais).
- Ajoutez un test pour chaque correction ou nouvelle fonction quand c'est possible.

## Licence

En contribuant, vous acceptez que votre contribution soit publiée sous la licence [GPL-3.0](LICENSE) du projet.
