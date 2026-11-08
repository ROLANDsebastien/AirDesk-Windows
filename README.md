# AirDesk Companion pour Windows

Compagnon natif qui envoie ses mesures au même superviseur AirDesk que le compagnon Mac. L’icône de la zone de notification donne accès à la configuration, à la pause et à la capture manuelle.

## Installer et associer un PC

1. Sur la page [Releases](https://github.com/ROLANDsebastien/AirDesk-Windows/releases), ouvrez la préversion souhaitée et téléchargez **AirDesk-Companion-Windows-x64-Setup.exe**.
2. Exécutez l’installateur sur Windows 11 x64 ou Windows 10 x64 à partir de la version 2004. Le paquet autonome s’installe pour l’utilisateur courant et inclut .NET. Cette version pilote est non signée et Windows SmartScreen peut afficher un avertissement.
3. Dans le superviseur AirDesk, générez un code d’association. Ouvrez **AirDesk Companion** dans le menu Démarrer, collez le code et attendez le premier envoi confirmé. Le PC apparaît dans la liste des appareils distants avec le suffixe **Windows**.
4. Activez le démarrage à l’ouverture de session et les options de collecte souhaitées dans le compagnon. Pour rouvrir la configuration, double-cliquez sur l’icône AirDesk dans la zone de notification. Fermer la fenêtre laisse le compagnon actif. Le menu **Quitter** arrête la collecte.

## Données collectées

- Toutes les 15 secondes, le compagnon relève l’activité ou l’inactivité, la disponibilité de la session, l’application au premier plan si activée et l’état de la batterie si disponible. La file hors ligne conserve jusqu’à 5 760 mesures, avec une limite supplémentaire pour les URL. Les mesures confirmées quittent la file.
- Le suivi de sites est désactivé au départ. Edge et Chrome sont interrogés par les interfaces d’accessibilité de Windows quand ils sont au premier plan et exposent leur barre d’adresse. Le domaine suffit par défaut. Les liens complets sont facultatifs, et les exclusions couvrent aussi les sous-domaines.
- Les captures automatiques et le partage distant des images sont désactivés au départ. Les captures concernent l’écran principal. Les copies locales expirent après sept jours, avec un maximum de 200 images. Une suppression locale laisse les copies distantes à gérer depuis le superviseur.
- Les plages de confidentialité AirDesk suspendent les captures et le suivi des sites. La pause locale persiste au redémarrage. La collecte tourne dans la session Windows ouverte. Les fonctions de mise à jour logicielle du compagnon Mac sont hors périmètre de ce pilote. Le superviseur existant peut afficher la version de Windows sous son libellé historique « macOS ».
- Les réglages, le journal et les captures sont sous `%LOCALAPPDATA%\AirDesk Companion`. Le jeton d’association est protégé par Windows DPAPI pour l’utilisateur courant. Désinstaller le compagnon conserve les données locales. Quittez le compagnon depuis la zone de notification avant une mise à jour ou une désinstallation.

## Compiler et publier

Le workflow **Installateur Windows** fonctionne sur le runner standard Windows de GitHub. Un lancement manuel fournit un artefact de test. Un tag `vX.Y.Z` correspondant à la propriété `Version` du projet compile, vérifie le cycle d’installation et publie l’EXE en préversion. Les releases constituent le lien de téléchargement durable.

Pour compiler localement sur Windows avec le SDK .NET 10 et Inno Setup 6 :

```powershell
dotnet publish src/AirDesk.Companion.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer/AirDesk.iss
```
