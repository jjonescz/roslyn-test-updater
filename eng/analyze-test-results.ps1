Set-StrictMode -version 3.0
$ErrorActionPreference = "Stop"

if (git status --porcelain) {
    Write-Output "Snapshots changed:"
    git diff
    exit 1
} else {
    Write-Output "No snapshot changes detected."
}
