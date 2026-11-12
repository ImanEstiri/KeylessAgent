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
    $full = (Resolve-Path $FullPath).Path
    $root = (Resolve-Path $repoRoot).Path
    if (-not ($root.EndsWith('\') -or $root.EndsWith('/'))) {
        $root += [System.IO.Path]::DirectorySeparatorChar
    }
    if ($full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $full.Substring($root.Length)
    } else {
        $rel = $full
    }
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
gh run watch $run.databaseId --repo $repoFull --exit-status | Out-Null

# ---------------------------------------------------------------------
# Three-state result: FAILED / PASSED WITH WARNINGS / PASSED.
# The two critical steps (push to nuget.org, push to GitHub Packages)
# fail the whole run if they fail. The cleanup steps (unlisting old
# versions) are best-effort and only emit a "::warning::" line when
# they hit a problem, which still shows up in the raw log text.
# ---------------------------------------------------------------------
$runInfo   = gh run view $run.databaseId --repo $repoFull --json conclusion | ConvertFrom-Json
$conclusion = $runInfo.conclusion

if ($conclusion -ne "success") {
    Write-Host ""
    Write-Host "RESULT: FAILED (conclusion: $conclusion)" -ForegroundColor Red
    Write-Host "see https://github.com/$repoFull/actions/runs/$($run.databaseId) for details."
    exit 1
}

$log = gh run view $run.databaseId --repo $repoFull --log
$warnings = $log | Select-String -Pattern '::warning::' -SimpleMatch |
    ForEach-Object { ($_.Line -replace '^.*?::warning::', '').Trim() } |
    Select-Object -Unique

if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "RESULT: PASSED WITH WARNINGS (push to nuget.org and GitHub Packages succeeded; cleanup had issues):" -ForegroundColor DarkYellow
    foreach ($w in $warnings) { Write-Host "  - $w" -ForegroundColor DarkYellow }
} else {
    Write-Host ""
    Write-Host "RESULT: PASSED" -ForegroundColor Green
}