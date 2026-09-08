#!/usr/bin/env pwsh
# Cross-platform benchmark comparison script
# Compares benchmark results between current branch and a base branch
#
# Usage:
#   pwsh scripts/benchmark.ps1                       # Run all benchmarks
#   pwsh scripts/benchmark.ps1 -Filter "*Source*"   # Filter by pattern
#   pwsh scripts/benchmark.ps1 -Filter "*Registry*","*Source*"  # Several patterns, matched as OR
#   pwsh scripts/benchmark.ps1 -Stash               # Auto-stash uncommitted changes
#   pwsh scripts/benchmark.ps1 -Short               # Quick benchmark (fewer iterations)
#   pwsh scripts/benchmark.ps1 -LocalOnly           # Run on current branch only (no comparison)
#   pwsh scripts/benchmark.ps1 -LaunchCount 3       # Run 3 process launches per benchmark (more stable)
#   pwsh scripts/benchmark.ps1 -MemoryRandomization:$false  # Disable heap-layout randomization (on by default)
#   pwsh scripts/benchmark.ps1 -BaseBranch performance/foo  # Compare against a non-master base
#   pwsh scripts/benchmark.ps1 -Filter "*Write*" -Stash -Short
#
# Output: benchmark_YYYY-MM-DD_HHmmss.md in current directory

param(
    [string[]]$Filter = @("*"),
    [switch]$Stash,
    [switch]$Short,
    [switch]$LocalOnly,
    [int]$LaunchCount = 1,
    [bool]$MemoryRandomization = $true,
    [string]$BaseBranch = "master"
)

# ============ CONFIGURATION ============
$BenchmarkProject = "src/Namotion.Interceptor.Benchmark/Namotion.Interceptor.Benchmark.csproj"
# =======================================

$ErrorActionPreference = "Stop"
$ExtraArgs = @()
if ($Short) { $ExtraArgs += "--job"; $ExtraArgs += "short" }
if ($LaunchCount -gt 1) { $ExtraArgs += "--launchCount"; $ExtraArgs += "$LaunchCount" }
# On by default: randomizes the managed heap layout between iterations so a single lucky/unlucky
# layout cannot bias the comparison (this is a decision tool). Disable with -MemoryRandomization:$false
# for a faster, layout-fixed run. Note: it does not randomize JIT code placement.
if ($MemoryRandomization) { $ExtraArgs += "--memoryRandomization" }

# BenchmarkDotNet rejects a repeated --filter flag, so several patterns have to travel as positional
# values behind a single one. PowerShell on Linux glob-expands a splatted pattern against the working
# directory before the process sees it, and only the combined --filter=<pattern> form escapes that, so
# exactly one pattern can be protected. Put a pattern that would otherwise expand into that slot.
$Ordered = @($Filter)
$Expanding = @($Ordered | Where-Object { @(Get-ChildItem -Path $_ -Force -ErrorAction SilentlyContinue).Count -gt 0 })
if ($Expanding.Count -gt 1) {
    Write-Error "More than one -Filter pattern matches a file or directory in $(Get-Location), and only one can be protected from shell expansion: $($Expanding -join ', '). Make them more specific."
    exit 1
}
if ($Expanding.Count -eq 1) { $Ordered = @($Expanding[0]) + @($Ordered | Where-Object { $_ -ne $Expanding[0] }) }
$FilterArgs = @("--filter=$($Ordered[0])")
if ($Ordered.Count -gt 1) { $FilterArgs += $Ordered[1..($Ordered.Count - 1)] }
$FilterDisplay = $Ordered -join " "

# ============ HELPER FUNCTIONS ============

function Restore-OriginalBranch {
    Write-Host "Restoring original branch ($OriginalBranch)..."
    git checkout $OriginalBranch --quiet
}

function Restore-Stash {
    if ($script:DidStash) {
        $stashList = git stash list
        $stashIndex = $null
        $index = 0
        foreach ($line in $stashList -split "`n") {
            if ($line -match "benchmark-script-auto-stash") {
                $stashIndex = $index
                break
            }
            $index++
        }

        if ($null -ne $stashIndex) {
            Write-Host "Restoring $($script:StashedFileCount) stashed file(s) from stash@{$stashIndex}..."
            git stash pop "stash@{$stashIndex}" --quiet
        } else {
            Write-Warning "Could not find benchmark stash to restore. Manual recovery may be needed."
        }
    }
}

function Clear-BenchmarkArtifacts {
    if (Test-Path "BenchmarkDotNet.Artifacts") {
        Remove-Item -Recurse -Force "BenchmarkDotNet.Artifacts" -ErrorAction SilentlyContinue
    }
}

function Run-Benchmark {
    param([string]$Label, [string]$OutputPath)

    Write-Host "Running benchmark on $Label (filter: $script:FilterDisplay)..."
    dotnet run --project $BenchmarkProject -c Release -- @script:FilterArgs --exporters markdown --join @script:ExtraArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Benchmark failed on $Label"
        return $false
    }

    $artifactsPath = "BenchmarkDotNet.Artifacts/results"
    if (-not (Test-Path $artifactsPath)) {
        Write-Error "No benchmark results found for $Label"
        return $false
    }
    $file = Get-ChildItem -Path $artifactsPath -Filter "*-github.md" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $file) {
        Write-Error "No benchmark results found for $Label"
        return $false
    }

    Get-Content $file.FullName -Raw | Out-File -FilePath $OutputPath -Encoding utf8
    Clear-BenchmarkArtifacts
    return $true
}

# ==========================================

# Save original branch
$OriginalBranch = git rev-parse --abbrev-ref HEAD
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to get current branch. Are you in a git repository?"
    exit 1
}

Write-Host "Current branch: $OriginalBranch"
if (-not $LocalOnly) { Write-Host "Base branch: $BaseBranch" }
Write-Host "Filter: $FilterDisplay"
Write-Host ""

# Check for uncommitted changes
$GitStatus = git status --porcelain
$script:DidStash = $false
$script:StashedFileCount = 0
if ($GitStatus -and -not $LocalOnly) {
    if ($Stash) {
        $script:StashedFileCount = ($GitStatus -split "`n").Count
        Write-Host "Stashing $($script:StashedFileCount) uncommitted file(s)..."
        git stash push -u -m "benchmark-script-auto-stash" --quiet
        $stashList = git stash list
        if ($stashList -match "benchmark-script-auto-stash") {
            $script:DidStash = $true
        } else {
            Write-Host "No changes were stashed (files may already be committed or ignored)."
            $script:DidStash = $false
        }
    } else {
        Write-Error "Uncommitted changes detected. Please commit or stash before running benchmarks, or use -Stash option."
        exit 1
    }
}

# Temp files
$Timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$TempBase = [System.IO.Path]::GetTempPath()
$BaseBranchResult = Join-Path $TempBase "benchmark_base_$Timestamp.md"
$CurrentBranchResult = Join-Path $TempBase "benchmark_current_$Timestamp.md"

Clear-BenchmarkArtifacts

# Run benchmark on base branch
if (-not $LocalOnly) {
    Write-Host "Checking out $BaseBranch..."
    git checkout $BaseBranch --quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to checkout $BaseBranch"
        Restore-OriginalBranch
        Restore-Stash
        exit 1
    }

    if (-not (Run-Benchmark -Label $BaseBranch -OutputPath $BaseBranchResult)) {
        Restore-OriginalBranch
        Restore-Stash
        exit 1
    }

    Write-Host ""
    Write-Host "Checking out $OriginalBranch..."
    git checkout $OriginalBranch --quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to checkout $OriginalBranch"
        Restore-Stash
        exit 1
    }

    Restore-Stash

    Write-Host "Waiting 5 seconds before running benchmark..."
    Start-Sleep -Seconds 5
}

if (-not (Run-Benchmark -Label $OriginalBranch -OutputPath $CurrentBranchResult)) {
    Remove-Item $BaseBranchResult -ErrorAction SilentlyContinue
    exit 1
}

# Build report
$OutputFile = "benchmark_$Timestamp.md"
$CurrentBranchContent = Get-Content $CurrentBranchResult -Raw

if ($LocalOnly) {
    $Report = @"
# Benchmark Results

**Date:** $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
**Filter:** $FilterDisplay
**Branch:** $OriginalBranch

---

$CurrentBranchContent
"@
} else {
    $BaseBranchContent = Get-Content $BaseBranchResult -Raw

    $Report = @"
# Benchmark Comparison

**Date:** $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
**Filter:** $FilterDisplay
**Base branch:** $BaseBranch
**Compared branch:** $OriginalBranch

---

## $OriginalBranch (current):

$CurrentBranchContent

---

## $BaseBranch branch:

$BaseBranchContent
"@
}

$Report | Out-File -FilePath $OutputFile -Encoding utf8

# Cleanup temp files
Remove-Item $BaseBranchResult -ErrorAction SilentlyContinue
Remove-Item $CurrentBranchResult -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Benchmark results saved to: $OutputFile" -ForegroundColor Green
