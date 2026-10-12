# Sécurité

## Mises à jour et fichiers publiés

- Chaque fichier publié est accompagné de sa somme SHA-256 et d'une **signature ECDSA P-256** faite par le workflow de release avec une clé secrète. Les mises à jour automatiques refusent tout fichier mal signé.
- Chaque exe et MSI a une **attestation de provenance** GitHub, qui prouve qu'il a été compilé par le workflow du dépôt :

  ```bash
  gh attestation verify Corral.msi --repo Captain-VII/Corral
  ```

## Service et interface

Avec le MSI, le moteur tourne dans un service (compte SYSTEM). L'interface et la ligne de commande lui parlent par un **pipe nommé** :

- accessible uniquement aux utilisateurs connectés sur le PC (refusé depuis le réseau) ;
- seuls SYSTEM et les administrateurs peuvent en créer une instance : un programme ordinaire ne peut pas se faire passer pour le service ;
- l'interface vérifie que le pipe est servi depuis la session des services avant de s'y connecter ;
- le service peut lire l'identité de l'interface (pour les préférences de carte graphique de l'utilisateur), pas agir en son nom.

Le dossier `%ProgramData%\Corral\` n'est modifiable que par SYSTEM et les administrateurs.

## Ce que peut faire un utilisateur

Sans stratégie, tout utilisateur connecté peut modifier les règles, comme avec la version portable sur un PC personnel.
Sur un poste partagé ou un serveur, activez **Verrouiller les réglages** ou une [configuration de parc](parc.md).

## Signaler une faille

Voir [SECURITY.md](https://github.com/Captain-VII/Corral/blob/main/SECURITY.md) : signalement privé par GitHub, sans ouvrir d'issue publique.
