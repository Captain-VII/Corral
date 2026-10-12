# Configuration de parc

Un seul fichier JSON, publié sur un partage réseau ou un serveur https, impose la même configuration à tous les postes.
Il est **signé avec la clé de votre organisation** : un fichier modifié, ou signé par quelqu'un d'autre, est refusé.

## Contenu

```json
{
  "App": "Corral",
  "Serial": 1,
  "Rules": [
    { "Pattern": "Teams.exe", "Priority": "AboveNormal" },
    { "Pattern": "backup*.exe", "Priority": "BelowNormal", "CpuLimitPercent": 25,
      "When": { "From": "08:00:00", "To": "19:00:00", "Days": [ "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" ] } }
  ],
  "ProBalance": { "Enabled": true, "SessionFairness": true },
  "GameMode": { "Automatic": false }
}
```

- **`Serial`** : à augmenter à chaque publication. Un fichier dont le numéro est inférieur au dernier reçu est refusé (protection contre le rejeu).
- **`Rules`**, **`ProBalance`**, **`GameMode`** : chaque section présente remplace la configuration du poste et devient non modifiable (page verrouillée, ligne de commande refusée). Une section absente laisse la configuration locale.
- Les règles ont les mêmes champs qu'un export de la page Règles : le plus simple est de régler un poste de référence, d'exporter ses règles et de les copier ici.

## Signature

Avec `Corral.exe`, sur le poste de l'administrateur :

```bat
Corral fleet keygen cle.pem
```

Crée la clé privée `cle.pem` et affiche la **clé publique**. Gardez `cle.pem` en lieu sûr et sauvegardé : elle seule permet de publier une configuration.

```bat
Corral fleet sign config.json cle.pem
Corral fleet check config.json <clé publique>
```

`sign` écrit `config.json.sig` à côté du fichier ; `check` vérifie la signature et résume le contenu.

## Publication

1. Déposez `config.json` et `config.json.sig` **côte à côte**, sur un partage lisible par les comptes ordinateur (le service tourne sous SYSTEM) ou sur une adresse **https**.
2. Activez la stratégie **Configuration de parc signée** avec l'emplacement (`\\serveur\corral\config.json` ou `https://…/config.json`) et la clé publique.

Le service relit le fichier toutes les **15 minutes** et applique aussitôt une nouvelle version, sans redémarrage.
Hors réseau, la dernière version valide reçue reste appliquée. Les refus (signature, version plus ancienne, fichier injoignable) sont inscrits dans l'Observateur d'événements.
