param([Parameter(Mandatory)][string]$Setup, [switch]$TestEdge)
$ErrorActionPreference = 'Stop'
$install = Join-Path $env:LOCALAPPDATA 'Programs\AirDesk Companion'
$data = Join-Path $env:LOCALAPPDATA 'AirDesk Companion'
$journal = Join-Path $data 'journal.json'
$binary = Join-Path $install 'AirDeskCompanion.exe'
$arguments = '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART'
$result = Start-Process (Resolve-Path $Setup) -ArgumentList $arguments -Wait -PassThru
if ($result.ExitCode -ne 0) { throw "Installation échouée ($($result.ExitCode))." }
$app = Start-Process $binary -PassThru
try {
    $state = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $app.Refresh()
        if ($app.HasExited) { throw "Le compagnon s’est arrêté ($($app.ExitCode))." }
        if ((Test-Path $journal) -and $app.MainWindowHandle -ne [IntPtr]::Zero) {
            $state = Get-Content $journal -Raw | ConvertFrom-Json
            if ($state.pending.Count -gt 0) { break }
        }
    }
    if (-not $state.installationId -or -not $state.latest.id -or $state.pending.Count -lt 1) {
        throw 'Aucune première observation locale détectée.'
    }

    $registration = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.airdesk.companion'
    $manifestPath = (Get-Item $registration).GetValue('')
    $hostManifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $extensionPath = Join-Path $install 'Extension Edge\manifest.json'
    $extension = Get-Content $extensionPath -Raw | ConvertFrom-Json
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Convert]::FromBase64String($extension.key))
    } finally { $sha.Dispose() }
    $id = -join ($hash[0..15] | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
    $origin = "chrome-extension://$id/"
    if ($hostManifest.allowed_origins -notcontains $origin) { throw "Identifiant d’extension non autorisé." }

    $info = [Diagnostics.ProcessStartInfo]::new($binary)
    $info.Arguments = "$origin --parent-window=0"
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $bridge = [Diagnostics.Process]::Start($info)
    try {
        $payload = [Text.Encoding]::UTF8.GetBytes('{"type":"check","focused":true}')
        $inputStream = $bridge.StandardInput.BaseStream
        $length = [BitConverter]::GetBytes([int]$payload.Length)
        $inputStream.Write($length, 0, $length.Length)
        $inputStream.Write($payload, 0, $payload.Length)
        $inputStream.Flush()
        $inputStream.Close()
        $outputStream = $bridge.StandardOutput.BaseStream
        $response = [IO.MemoryStream]::new()
        $copy = $outputStream.CopyToAsync($response)
        if (-not $copy.Wait(5000)) { throw 'Hôte Edge sans réponse.' }
        $bytes = $response.ToArray()
        if ($bytes.Length -lt 5 -or [BitConverter]::ToInt32($bytes, 0) -ne $bytes.Length - 4) {
            throw 'Réponse Native Messaging mal formée.'
        }
        $reply = [Text.Encoding]::UTF8.GetString($bytes, 4, $bytes.Length - 4) | ConvertFrom-Json
        if (-not $reply.connected -or $reply.type -ne 'status' -or $reply.grant) {
            throw 'Le pont doit être connecté et refuser la lecture tant que le suivi des sites est désactivé.'
        }
    } finally {
        if (-not $bridge.HasExited) { $bridge.Kill() }
        $bridge.Dispose()
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'smoke-capture.ps1') -ProcessId $app.Id -DataPath $data
    if ($LASTEXITCODE -ne 0) { throw 'Test de capture échoué.' }

    if ($TestEdge) {
        Stop-Process -Id $app.Id
        $app.WaitForExit()
        node (Join-Path $PSScriptRoot '../tests/edge-live.mjs') $binary $data
        if ($LASTEXITCODE -ne 0) { throw "Test Edge sur navigateur réel échoué." }
    }
} finally {
    if (-not $app.HasExited) { Stop-Process -Id $app.Id; $app.WaitForExit() }
}

$result = Start-Process (Join-Path $install 'unins000.exe') -ArgumentList $arguments -Wait -PassThru
if ($result.ExitCode -ne 0 -or (Test-Path $binary) -or (Test-Path $registration)) { throw 'Désinstallation échouée.' }
if (-not (Test-Path $journal)) { throw 'Le journal local doit rester accessible après la désinstallation.' }
