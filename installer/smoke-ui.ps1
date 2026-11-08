param([int]$ProcessId, [string]$DataPath, [string]$Screenshots)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AirDeskWindow {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y, int width, int height, uint flags);
}
'@
[void](New-Item -ItemType Directory -Path $Screenshots -Force)
$process = Get-Process -Id $ProcessId
$handle = $process.MainWindowHandle
$window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$scope = [System.Windows.Automation.TreeScope]::Descendants
$journal = Join-Path $DataPath 'journal.json'

function Find-ByName([string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $window.FindFirst($scope, $condition)
}

function Select-Tab([string]$Name) {
    $tab = Find-ByName $Name
    if (-not $tab) { throw "Onglet absent : $Name" }
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
}

function Snapshot([string]$Name) {
    $bounds = $window.Current.BoundingRectangle
    $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc = $graphics.GetHdc()
            try {
                if (-not [AirDeskWindow]::PrintWindow($handle, $dc, 2)) { throw 'PrintWindow a échoué.' }
            } finally { $graphics.ReleaseHdc($dc) }
        } finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $Screenshots "$Name.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

Select-Tab 'État et comptes'
Snapshot '01-etat-et-comptes'
$editType = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
$code = $window.FindFirst($scope, $editType)
$associate = Find-ByName 'Associer'
$code.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('invalide')
$associate.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 350
if (-not (Find-ByName "Saisissez le code d’association à 32 caractères généré dans AirDesk.")) {
    throw 'Le message de validation du code est absent.'
}
Snapshot '02-code-invalide'

$pause = Find-ByName 'Mettre la collecte en pause'
$toggle = $pause.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$toggle.Toggle()
Start-Sleep -Milliseconds 1300
$state = Get-Content $journal -Raw | ConvertFrom-Json
if (-not $state.settings.paused -or $state.latest.state -ne 'paused') { throw 'Pause locale non appliquée.' }
$toggle.Toggle()

Select-Tab 'Activité et sites'
if (-not (Find-ByName "Configurer l’extension Edge")) { throw 'Accès à la configuration Edge absent.' }
Snapshot '03-activite-et-sites'
Select-Tab 'Captures'
if (-not (Find-ByName 'Capturer maintenant')) { throw 'Commande de capture absente.' }
Snapshot '04-captures'
Select-Tab 'Système'
if (-not (Find-ByName 'Exporter le bilan technique')) { throw 'Export du diagnostic absent.' }
Snapshot '05-systeme'
if (-not [AirDeskWindow]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, 530, 440, 6)) {
    throw 'Redimensionnement impossible.'
}
Select-Tab 'État et comptes'
Snapshot '06-etat-fenetre-minimale'
Select-Tab 'Captures'
Snapshot '07-captures-fenetre-minimale'
Write-Host 'Parcours UI et captures de contrôle enregistrés.'
