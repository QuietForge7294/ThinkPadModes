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

## Compatibilité et compilation

Le contrôle des ventilateurs est limité à la configuration qualifiée **21QTS2AH00**, BIOS **R2WET45W (1.27)** et EC **1.17**. Le code vérifie aussi les empreintes du pilote et du module de ventilation Lenovo. Une autre configuration est refusée.

Depuis PowerShell sous Windows x64, avec le compilateur .NET Framework installé, lance la compilation depuis la racine du dépôt.

```powershell
.\Build-Manual.ps1
```

Le script produit `ThinkPadModesManual.exe` et lance `--verify`. Ce test vérifie notamment la limite de cinq secondes, la perte de veille RTX et les seuils de protection 0 dB. Il n’envoie pas de consigne aux ventilateurs.

Pour les commandes matérielles, l’application dépend d’une installation ThinkPad Modes configurée dans Program Files et des composants **Lenovo Diagnostics Evolution** et **PawnIO** avec le module **IntelMSR**. Ce dépôt fournit les sources et le script de compilation. L’installation des pilotes et le provisionnement de la configuration matérielle restent à faire séparément.

Projet indépendant, sans affiliation avec Lenovo ou NVIDIA.
