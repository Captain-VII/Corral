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

## Traduire

Voir la section « Traduire Corral » du [README](README.md#traduire-corral). Les clés sont les textes français du code ; les éléments entre accolades (`{name}`) sont remplacés par Corral et doivent rester tels quels dans la traduction.

Après avoir ajouté ou modifié un texte `Tr("…", "…")` dans le code, régénérez les catalogues :

```bash
CORRAL_UPDATE_TRANSLATIONS=1 dotnet test --filter TranslationTests
```

## Règles du code

- Corral agit sur tous les processus du PC : toute modification doit être **restaurée** à l'arrêt, en pause ou au changement de réglages, et ne jamais toucher aux processus protégés (`Exclusions.IsProtected`).
- Tout texte visible passe par `Tr("français", "English")`, avec des littéraux (pas de concaténation) pour qu'il soit traduisible.
- Tout contrôle sans texte visible a un nom pour les lecteurs d'écran (`AccessibleName`, ou `SettingRow` qui le donne).
- Suivez le style du code existant (commentaires en français, noms en anglais).
- Ajoutez un test pour chaque correction ou nouvelle fonction quand c'est possible.

## Licence

En contribuant, vous acceptez que votre contribution soit publiée sous la licence [GPL-3.0](LICENSE) du projet.
