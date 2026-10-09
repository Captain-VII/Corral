# Sécurité

## Versions maintenues

Seule la dernière version publiée reçoit des correctifs de sécurité.

## Signaler une faille

Ne publiez pas de faille dans une issue. Utilisez le signalement privé de GitHub : onglet **Security** → **Report a vulnerability** ([lien direct](../../security/advisories/new)).

Indiquez la version concernée, les étapes pour reproduire et l'impact. Vous recevrez une réponse sous 7 jours.

## Ce qui protège les utilisateurs

- Les mises à jour automatiques vérifient la somme SHA-256 **et** une signature ECDSA P-256 faite par le workflow de release avec une clé privée secrète : un exe déposé sur la page des releases sans cette clé est refusé.
- Chaque exe et MSI publié a une [attestation de provenance](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations) GitHub, vérifiable avec :

  ```bash
  gh attestation verify Corral.exe --repo Captain-VII/Corral
  ```
