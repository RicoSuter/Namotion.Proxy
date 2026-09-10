<#
.SYNOPSIS
    Prints a markdown table of lines added, removed, and net per area for a diff range.

.DESCRIPTION
    Buckets every changed file into production code, tests and benchmarks, documentation,
    or project and CI files, and totals the line counts of each. The output is the diff
    composition table used in pull request descriptions.

    Binary files report no line counts and are skipped.

.PARAMETER Range
    Any range git diff --numstat accepts, for example "origin/master...HEAD" or "v0.9.0 v0.9.1".
    Defaults to origin/master...HEAD, the changes on the current branch.

.PARAMETER PerProject
    Break production code down per project instead of reporting it as one row.

.EXAMPLE
    pwsh scripts/diff-composition.ps1

.EXAMPLE
    pwsh scripts/diff-composition.ps1 -Range "v0.9.0 v0.9.1" -PerProject
#>
param(
    [string] $Range = "origin/master...HEAD",
    [switch] $PerProject
)

$ErrorActionPreference = "Stop"

$numstat = @(git diff --numstat @($Range -split '\s+'))
if ($LASTEXITCODE -ne 0) { throw "git diff failed for range '$Range'." }

# A single-revision range (for example "HEAD") diffs the working tree against that revision,
# and git diff never lists untracked files, so a pre-commit measurement would silently omit
# every newly added file. Count them as pure additions; binary files stay skipped because the
# probe reports "-" for them like any other numstat line.
$rangeTokens = @($Range -split '\s+' | Where-Object { $_ })
if ($rangeTokens.Length -eq 1 -and $rangeTokens[0] -notmatch '\.\.') {
    foreach ($untracked in @(git ls-files --others --exclude-standard)) {
        $probe = @(git diff --numstat --no-index -- /dev/null $untracked)
        if (-not $probe) { continue }
        $added = (@($probe)[0] -split "`t")[0]
        $numstat += "$added`t0`t$untracked"
    }
}

function Get-Area {
    param([string] $Path)

    if ($Path -like 'scripts/*' -or $Path -like '.github/*') { return "Scripts and CI" }
    if ($Path -match '\.md$' -or $Path -like 'docs/*') { return "Documentation" }
    if ($Path -match 'Tests|Testing|Benchmark') { return "Tests and benchmarks" }
    if ($Path -match '\.(csproj|slnx|props|targets|yml|yaml)$') { return "Project and CI files" }

    if ($PerProject -and $Path -like 'src/*') {
        # Projects sit directly under src/, except beneath src/HomeBlaze/, where
        # src/HomeBlaze/HomeBlaze.OpcUa/... is one level further down.
        $segments = $Path -split '/'
        $project = if ($segments.Length -gt 3 -and $segments[1] -eq "HomeBlaze") { $segments[2] } else { $segments[1] }
        if ($project) { return $project }
    }

    return "Production code"
}

$areas = [ordered] @{}
foreach ($line in $numstat) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    $added, $removed, $path = $line -split "`t", 3
    if ($added -eq '-' -or $removed -eq '-') { continue }

    $area = Get-Area -Path $path
    if (-not $areas.Contains($area)) {
        $areas[$area] = [pscustomobject] @{ Files = 0; Added = 0; Removed = 0 }
    }

    $areas[$area].Files++
    $areas[$area].Added += [int] $added
    $areas[$area].Removed += [int] $removed
}

if ($areas.Count -eq 0) {
    Write-Output "No changes in range '$Range'."
    return
}

$order = @("Production code", "Tests and benchmarks", "Documentation", "Scripts and CI", "Project and CI files")
$sorted = @($areas.Keys | Sort-Object {
    $index = $order.IndexOf($_)
    if ($index -ge 0) { "1-{0:d3}" -f $index } else { "0-$_" }
})

"| Area | Files | Added | Removed | Net |"
"|---|---:|---:|---:|---:|"
foreach ($area in $sorted) {
    $row = $areas[$area]
    "| {0} | {1} | {2:n0} | {3:n0} | {4:+#,##0;-#,##0;0} |" -f $area, $row.Files, $row.Added, $row.Removed, ($row.Added - $row.Removed)
}

$files = ($areas.Values | Measure-Object -Property Files -Sum).Sum
$added = ($areas.Values | Measure-Object -Property Added -Sum).Sum
$removed = ($areas.Values | Measure-Object -Property Removed -Sum).Sum
"| **Total** | **{0}** | **{1:n0}** | **{2:n0}** | **{3:+#,##0;-#,##0;0}** |" -f $files, $added, $removed, ($added - $removed)
