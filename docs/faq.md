# Questions fréquentes

??? question "Corral ralentit-il mon PC ?"
    Non : il examine les processus une fois par seconde (réglable), ce qui prend une fraction de pour cent du processeur.

??? question "Que se passe-t-il si je ferme ou désinstalle Corral ?"
    Les priorités, cœurs, plans d'alimentation et autres réglages modifiés sont restaurés. Si Corral s'arrête brutalement, le plan d'alimentation d'origine est rétabli au lancement suivant ; les priorités reviennent d'elles-mêmes au redémarrage du programme concerné.

??? question "Pourquoi Windows affiche-t-il une alerte SmartScreen ?"
    Corral n'est pas encore signé par un certificat de signature de code (payant). Voir [Installation](installation.md#alerte-smartscreen) pour vérifier l'origine du fichier.

??? question "Mon antivirus ou mon jeu proteste-t-il ?"
    Corral ne s'injecte dans aucun programme : il utilise les réglages standard de Windows (priorité, affinité, Job Objects), comme le Gestionnaire des tâches. Certains anti-triche refusent toutefois qu'on change la priorité de leur jeu ; Corral l'indique alors dans le journal.

??? question "« Le service Corral ne répond pas »"
    Redémarrez le service (`Restart-Service Corral` en administrateur) ou le PC. Si le message persiste, réinstallez le MSI et consultez l'Observateur d'événements (journal *Application*, source `Corral`).

??? question "Version portable ou MSI ?"
    Le MSI est recommandé : pas d'invite UAC, fonctionne pour un utilisateur standard, mises à jour par le service. La version portable convient pour essayer Corral ou l'emporter sur une clé USB.

??? question "Comment signaler un problème ?"
    **Options › Aide › Signaler un problème** ouvre un formulaire GitHub avec les versions de Corral et de Windows déjà remplies.
