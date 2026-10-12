# Serveurs RDS / VDI

Sur un serveur Bureau à distance (RDS) ou un VDI multi-session (Windows 10/11 Enterprise multi-session, Citrix…), installez **le MSI** :
le service applique les règles pour toutes les sessions, et chaque utilisateur qui ouvre une session obtient sa propre interface, reliée au même service.

## Équité entre les sessions

Sans arbitrage, un seul utilisateur qui lance un traitement lourd ralentit tous les autres.
Avec l'option **Équité entre les sessions** (page ProBalance), quand le processeur est saturé :

1. Corral calcule la part de chaque session qui travaille : la charge totale divisée par le nombre de sessions actives.
2. Une session qui prend **plus d'une fois et demie sa part** pendant la durée de déclenchement de ProBalance voit ses programmes actifs passer en priorité *inférieure à la normale*.
3. Leur priorité d'origine leur est rendue quand la session revient dans sa part ou que le processeur se calme.

Les seuils et durées sont ceux de [ProBalance](../utilisation/probalance.md). Les processus système, les programmes avec une règle de priorité et les exclusions ne sont jamais touchés.
La page ProBalance affiche la charge de chaque session ouverte et celles qui sont abaissées.

L'option est désactivée par défaut. Pour l'imposer à tous les serveurs, utilisez la [configuration de parc](parc.md) :

```json
{ "App": "Corral", "Serial": 2, "ProBalance": { "Enabled": true, "SessionFairness": true } }
```

!!! tip "Réglages conseillés"
    Verrouillez les réglages (stratégie **Verrouiller les réglages**) : sur un serveur partagé, un utilisateur ne doit pas pouvoir changer les règles des autres.
