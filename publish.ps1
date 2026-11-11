param(
    [Parameter(Mandatory = $true)]
    [string]$CsprojPath
)

$ErrorActionPreference = "Stop"

function Write-Step   { param([string]$Message) Write-Host $Message -ForegroundColor Yellow }
function Write-Action { param([string]$Message) Write-Host $Message -ForegroundColor Blue }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI ('gh') not found. Install it and run 'gh auth login' first."
}

if (-not (Test-Path $CsprojPath)) {
    Write-Error "Csproj not found: $CsprojPath"
}
$CsprojPath = (Resolve-Path $CsprojPath).Path
$csprojDir  = Split-Path $CsprojPath -Parent

# ---------------------------------------------------------------------
# Repo root & current branch - read-only, nothing is committed or
# pushed locally in this version.
# ---------------------------------------------------------------------
$repoRoot = (git -C $csprojDir rev-parse --show-toplevel).Trim()
$branch   = (git -C $csprojDir rev-parse --abbrev-ref HEAD).Trim()

function ToRepoRelative {
    param([string]$FullPath)
    $rel = [System.IO.Path]::GetRelativePath($repoRoot, $FullPath)
    return ($rel -replace '\\', '/')
}
$csprojRel = ToRepoRelative $CsprojPath

# ---------------------------------------------------------------------
# Figure out owner/repo from the git remote, so this script works
# unmodified in any repo.
# ---------------------------------------------------------------------
$remoteUrl = git -C $repoRoot remote get-url origin
if ($remoteUrl -notmatch 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)(\.git)?$') {
    throw "Could not parse a GitHub owner/repo out of remote URL: $remoteUrl"
}
$repoFull = "$($Matches['owner'])/$($Matches['repo'])"
$workflowFile = "publish-nuget.yml"

# ---------------------------------------------------------------------
# Trigger the workflow and wait for the result
# ---------------------------------------------------------------------
Write-Action "triggering '$workflowFile' on $repoFull (branch $branch) for $csprojRel ..."
$dispatchTime = (Get-Date).ToUniversalTime()

gh workflow run $workflowFile --repo $repoFull --ref $branch -f csproj_path="$csprojRel"
if ($LASTEXITCODE -ne 0) { throw "Failed to dispatch the workflow via 'gh workflow run'." }

Write-Step "waiting for the run to show up..."
$run = $null
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    $runsJson = gh run list --repo $repoFull --workflow $workflowFile --branch $branch --limit 5 --json databaseId,createdAt
    $runs = $runsJson | ConvertFrom-Json
    $candidate = $runs |
        Where-Object { [DateTime]::Parse($_.createdAt).ToUniversalTime() -ge $dispatchTime.AddSeconds(-10) } |
        Sort-Object createdAt -Descending | Select-Object -First 1
    if ($candidate) { $run = $candidate; break }
}

if (-not $run) {
    throw "Could not find the dispatched run after 60s. Check the Actions tab on GitHub manually."
}

Write-Step "found run $($run.databaseId); watching until it finishes ..."
gh run watch $run.databaseId --repo $repoFull --exit-status

if ($LASTEXITCODE -eq 0) {
    Write-Step "publish workflow finished successfully."
} else {
    Write-Step "publish workflow failed or was cancelled - see the run on GitHub for details."
    exit 1
}
