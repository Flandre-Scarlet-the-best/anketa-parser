param(
    [Parameter(Mandatory = $true)][int] $CommandProcessId,
    [Parameter(Mandatory = $true)][int] $WatcherProcessId,
    [Parameter(Mandatory = $true)][string] $JobId
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

try {
    $commandProcess = [System.Diagnostics.Process]::GetProcessById($CommandProcessId)
    $watcherProcess = [System.Diagnostics.Process]::GetProcessById($WatcherProcessId)
    $null = $commandProcess.Handle
    $null = $watcherProcess.Handle
    if ($commandProcess.HasExited -or $watcherProcess.HasExited) { return }

    $icon = [System.Windows.Forms.NotifyIcon]::new()
    $icon.Icon = [System.Drawing.SystemIcons]::Information
    $icon.Text = "Codex: фоновая задача $($JobId.Substring(0, 8))"
    $icon.Visible = $true
    $timer = [System.Windows.Forms.Timer]::new()
    $timer.Interval = 500
    $timer.Add_Tick({
        try {
            if ($commandProcess.HasExited -or $watcherProcess.HasExited) {
                [System.Windows.Forms.Application]::ExitThread()
            }
        }
        catch {
            [System.Windows.Forms.Application]::ExitThread()
        }
    })
    $timer.Start()
    [System.Windows.Forms.Application]::Run()
}
finally {
    if ($timer) { $timer.Dispose() }
    if ($icon) {
        $icon.Visible = $false
        $icon.Dispose()
    }
    if ($commandProcess) { $commandProcess.Dispose() }
    if ($watcherProcess) { $watcherProcess.Dispose() }
}
