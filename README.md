# ThinkPad Modes

Contrôle manuel de la ventilation et de la puissance CPU sur le **ThinkPad P14s Gen 6 Intel** sous Windows. L’application fonctionne à la demande. Quand tu la quittes, elle restitue la ventilation au firmware Lenovo ainsi que les réglages CPU précédents, puis ses processus se terminent.

## Commandes

| Commande | Effet |
| --- | --- |
| **Auto Lenovo** | Laisse le firmware piloter les deux ventilateurs. |
| **0 dB** | Demande l’arrêt des deux ventilateurs après 5 secondes de veille RTX observée en Auto, fenêtre au premier plan. |
| **Bas / Renforcé** | Applique une consigne manuelle avec surveillance des températures CPU et GPU. |
| **Éco / Normal / Turbo** | Applique le profil de performances sélectionné. Le plafond CPU manuel est réglable à 10, 15 ou 20 W. |
| **Test** | Échantillonne toutes les 500 ms et interroge la RTX. Cette lecture peut la réveiller. |

Le tableau de bord affiche la température et la puissance du CPU, la consigne de ventilation et le régime mesuré de chaque ventilateur.

## Activer le 0 dB

1. Sélectionne **Auto Lenovo**, désactive **Test** et garde la fenêtre au premier plan.
2. Après cinq secondes de veille RTX observée par l’application, clique sur **0 dB**. La température CPU doit être disponible et inférieure à 65 °C.
3. L’application applique le plafond CPU choisi si nécessaire et vérifie sa relecture. Les deux compteurs RPM permettent de confirmer l’arrêt physique des ventilateurs.

En Auto, avec Test désactivé, et en 0 dB, l’état de veille RTX est lu via Windows sans interroger NVML. Si la RTX devient active ou que son état est inconnu, si le CPU atteint 70 °C ou si le capteur ou le plafond CPU cesse d’être vérifiable, l’application revient à **Auto Lenovo**.

Pour quitter, ferme la fenêtre ou utilise **Quitter et restaurer Auto** dans la zone de notification. Une fin forcée de tous les processus peut interrompre la restauration. Dans ce cas, relance l’application, vérifie **Auto Lenovo** et ferme-la normalement.

## Compatibilité et compilation

Le contrôle des ventilateurs est limité à la configuration qualifiée **21QTS2AH00**, BIOS **R2WET45W (1.27)** et EC **1.17**. Le code vérifie aussi les empreintes du pilote et du module de ventilation Lenovo. Une autre configuration est refusée.

Depuis PowerShell sous Windows x64, avec le compilateur .NET Framework installé, lance la compilation depuis la racine du dépôt.

```powershell
.\Build-Manual.ps1
```

Le script produit `ThinkPadModesManual.exe` et lance `--verify`. Ce test vérifie notamment la limite de cinq secondes, la perte de veille RTX et les seuils de protection 0 dB. Il n’envoie pas de consigne aux ventilateurs.

Pour les commandes matérielles, l’application dépend d’une installation ThinkPad Modes configurée dans Program Files et des composants **Lenovo Diagnostics Evolution** et **PawnIO** avec le module **IntelMSR**. Ce dépôt fournit les sources et le script de compilation. L’installation des pilotes et le provisionnement de la configuration matérielle restent à faire séparément.

Projet indépendant, sans affiliation avec Lenovo ou NVIDIA.

## Mesurer la consommation du PC

Le dossier [`tools/power-monitor`](tools/power-monitor) contient un outil autonome pour Windows et PowerShell 7. Lance **Mesurer consommation PC.bat** pour relever cinq minutes de données après 30 secondes de stabilisation. Le rapport HTML, les échantillons CSV et le résumé JSON sont enregistrés dans le dossier `results`, à côté du script.

Le rapport distingue la décharge de la batterie, la puissance du package CPU Intel (si Windows expose le capteur EMI) et les watts de la carte NVIDIA. **OFF (veille)** indique que Windows a signalé l’état D3. La colonne GPU reste vide pendant la veille, et le résumé calcule la moyenne et le maximum uniquement à partir des watts relevés quand la carte était active.

Le script vérifie l’état NVIDIA via Windows avant de lancer une lecture ponctuelle avec `nvidia-smi`. Les lectures sont espacées d’au moins 15 secondes. Si la carte change d’état entre la vérification et la lecture, celle-ci peut prolonger son activité. Pour une mesure de batterie sans aucune interrogation du pilote NVIDIA, utilise le mode passif. Il affiche les états de veille et d’activité.

```powershell
.\tools\power-monitor\Measure-PCPower.ps1 -GpuPassive
```

Les références techniques sont la [propriété d’alimentation Windows](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-powerdata) et la [documentation NVIDIA sur les relevés de puissance](https://docs.nvidia.com/deploy/nvidia-smi/index.html).
