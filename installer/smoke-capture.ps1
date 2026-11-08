param([int]$ProcessId, [string]$DataPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$process = Get-Process -Id $ProcessId
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
function Find-Control([string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
$tab = Find-Control 'Captures'
if (-not $tab) { throw 'Onglet Captures absent.' }
$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$button = Find-Control 'Capturer maintenant'
if (-not $button -or -not $button.Current.IsEnabled) { throw 'Capture manuelle indisponible dans cette session.' }
$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 1
    $image = Get-ChildItem (Join-Path $DataPath 'Captures') -Filter '*.jpg' | Select-Object -First 1
    if ($image) {
        $bytes = [IO.File]::ReadAllBytes($image.FullName)
        if ($bytes.Length -gt 4 -and $bytes[0] -eq 255 -and $bytes[1] -eq 216 -and
            $bytes[-2] -eq 255 -and $bytes[-1] -eq 217) {
            Write-Host 'Capture JPEG réelle confirmée.'
            exit 0
        }
        throw 'La capture ne contient pas un JPEG valide.'
    }
}
throw 'Aucune image locale créée.'
