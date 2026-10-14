param([string]$InputPath = "")

$logFile = "$env:USERPROFILE\claude-launch-debug.log"
$distro  = "Ubuntu"
$wslHome = "/home/agent-workbench-user"

"$(Get-Date) - START InputPath='$InputPath'" | Out-File -Append $logFile

if ($InputPath -ne "") {
    wsl.exe -d $distro -e bash -c "printf '%s' '$InputPath' > ~/.claude-launch-path"
} else {
    wsl.exe -d $distro -e bash -c "rm -f ~/.claude-launch-path"
}

"$(Get-Date) - Before Start-Process wt.exe" | Out-File -Append $logFile

try {
    Start-Process "wt.exe" "-p `"Claude Code`""
    "$(Get-Date) - Start-Process succeeded" | Out-File -Append $logFile
} catch {
    "$(Get-Date) - Start-Process FAILED: $_" | Out-File -Append $logFile
}
