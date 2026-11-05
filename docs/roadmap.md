# Roadmap · server (API .NET + worker Rust)

Dépôt `launchkit-server` (GitHub ounokoya) : l'API ASP.NET Core, la base ArangoDB et le worker Rust
qui exécute les tâches de fond.

Légende : ✅ validé · 💡 proposé, à valider · 🚧 en cours

| Version | Thème | Statut |
|---|---|---|
| v0.1.0 | Démo portfolio : tout le parcours de A et B | 🚧 (code et tests faits, publication en attente) |
| v0.2.0 | Paiement réel en mode test, comptes complets | 💡 |
| v0.3.0 | Intégrations et sécurité avancée | 💡 |
| v1.0.0 | Kit stable, prêt pour les projets clients | 💡 |

## v0.1.0 · Démo portfolio 🚧

**API ASP.NET Core (.NET 10) + ArangoDB** ✅
- Comptes : inscription, connexion, déconnexion, vérification de l'e-mail.
- Organisations multi-entreprise ; membres et rôles propriétaire / admin / membre ; invitations par e-mail.
- Formules Free (3 projets, 3 membres) et Pro (illimité) ; paiement simulé.
- Module d'exemple « Projets » ; activité récente ; tableau de bord.
- Back-office super-admin : statistiques, organisations, utilisateurs.
- Messages en français et en anglais.

**Worker Rust** ✅
- Envoi des e-mails vers Mailpit (boîte de démo, adresses inventées).
- Traitement du webhook de paiement simulé : l'organisation passe en Pro.
- Tâches planifiées (expiration des invitations).

**Choix techniques** 💡 (schéma « back-end », points B2 à B7)
- 10 collections ArangoDB ; l'appartenance à une organisation est une arête du graphe (`memberships`).
- 4e projet en Free refusé avec 409 « limite atteinte » ; limites lues dans la configuration.
- Jeton de session stocké dans ArangoDB (révocable) ; mots de passe hachés.

**Qualité** ✅ (outils et seuils 💡, point B10)
- Tests unitaires : xUnit (API), `cargo test` (worker).
- Tests e2e : scénario A / B sur une vraie ArangoDB et Mailpit en conteneurs.
- Couverture : coverlet et cargo-llvm-cov, seuil 80 % en CI.
- Sécurité : CodeQL, gitleaks, dépendances vulnérables, cargo audit / deny, tests d'accès (rôles, isolation entre organisations).

**Livraison** : images Docker de l'API et du worker.

## v0.2.0 · Paiement réel et comptes complets 💡

- Stripe en mode test derrière l'interface de paiement (CinetPay ou Wave possibles ensuite).
- Mot de passe oublié, changement d'e-mail.
- Limitation du nombre d'essais de connexion.
- Journal d'audit des actions sensibles.

## v0.3.0 · Intégrations et sécurité avancée 💡

- Connexion avec Google et GitHub.
- Webhooks sortants pour les intégrations des clients.
- Export des données d'une organisation.
- Double authentification.

## v1.0.0 · Kit stable 💡

- API documentée (OpenAPI) et versionnée.
- Guide « démarrer le MVP d'un client à partir du kit ».
- Modèle de module métier prêt à copier.
