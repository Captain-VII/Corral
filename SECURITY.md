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
- Avec l'installation MSI, le moteur tourne dans un service (compte SYSTEM). L'interface lui parle par un pipe nommé :
  - accessible uniquement aux utilisateurs connectés sur le PC (refusé depuis le réseau) ;
  - seuls SYSTEM et les administrateurs peuvent en créer une instance ;
  - l'interface vérifie que le pipe est bien servi depuis la session des services avant de s'y connecter.
- Le service n'installe que des MSI signés par la clé du projet.
- Sans stratégie, tout utilisateur connecté peut modifier les règles (comme avec l'exe portable sur un PC personnel). Sur un poste partagé, activez la stratégie « Verrouiller les réglages ».
