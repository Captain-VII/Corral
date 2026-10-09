# Journal des modifications

## 2.1.0

- **Service Windows** (installation MSI) : le moteur tourne dans le service « Corral » ; l'interface s'y connecte par un pipe nommé protégé, sans droits administrateur ni invite UAC. Elle se relance seule après une mise à jour.
- **Stratégies de groupe** : modèles ADMX/ADML (français, anglais) pour verrouiller les réglages, imposer des règles depuis un fichier, forcer ProBalance, désactiver les mises à jour, la fin de processus ou la gestion du démarrage.
- **Observateur d'événements** : avertissements, erreurs et événements importants dans le journal Application (source « Corral »).
- Mises à jour par l'installeur MSI signé pour les installations MSI.
- L'exe portable demande lui-même les droits administrateur (invite UAC) au lieu de les exiger dans son manifeste.

## 2.0.0

- **Open source** sous licence GPL-3.0, avec guide de contribution, politique de sécurité et modèles d'issues.
- **Installeur MSI** (Program Files, raccourci du menu Démarrer pour tous les utilisateurs, installation silencieuse `msiexec /i Corral.msi /qn`). La désinstallation retire aussi le démarrage automatique.
- **Mises à jour signées** : en plus de la somme SHA-256, chaque mise à jour est signée (ECDSA P-256) par le workflow de release et vérifiée avant installation.
- **Attestations de provenance** GitHub pour l'exe et le MSI.
- Tests automatiques à chaque push et pull request.

## 1.8.0 — 2026-10-06

Profils de règles, mini-fenêtre toujours visible, charge du processeur dans l'icône de notification, interface en anglais.

## 1.7.0 — 2026-10-06

Disque et GPU par processus, historique de 5 minutes dans la fiche d'un processus, gestion des programmes au démarrage.

## 1.6.0 — 2026-10-06

Optimisations : boost de la fenêtre au premier plan, plan économique au repos, nettoyage de la mémoire, carte graphique par programme.

## 1.5.0 — 2026-10-06

Suivi de la mémoire, programmes les plus gourmands, fiche détaillée d'un processus, statistiques ProBalance.

## 1.3.0 — 2026-10-05

Mode efficacité, choix de cœurs adaptés au processeur, priorités disque et mémoire, Mode Jeu.

## 1.2.0 — 2026-10-05

Infobulles et explications, modèles de règles, import/export, journal filtrable, raccourcis clavier, préréglages ProBalance.

## 1.1.0 — 2026-10-05

Nouveau design et nouveau logo.

## 1.0.0 — 2026-10-05

Première version : règles par application, ProBalance, restauration à l'arrêt, mises à jour automatiques.
