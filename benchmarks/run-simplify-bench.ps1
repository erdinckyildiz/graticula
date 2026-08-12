# Simplify benchmark runner.
#
# The first tile run (benchmarks/mvt-generation/RESULTS.md) fixed clipping and
# the bottleneck moved: at z12, DouglasPeuckerSimplifier was 807.6 ms of a
# 1,471 ms request. This measures where that time actually goes.
#
# All three variants use our RectClip, so clipping is held constant and the
# only difference is simplification:
#
#   nts     DouglasPeuckerSimplifier, default — repairs topology, which means
#           IsValid on every simplified polygon and Buffer(0) on any that fails
#   ntsraw  the same simplifier with EnsureValidTopology off. The difference
#           between this and nts IS the cost of the repair
#   ours    TileSimplify — runs after the tile-space transform, on the integer
#           grid, on flat arrays, with no topology repair
#
# Vertices in/out are reported because a simplifier that is fast by throwing
# away more geometry has not been shown to be better, only different.
#
# Usage:  pwsh benchmarks/run-simplify-bench.ps1 [-Runs 7]

param(
    [int]$Runs = 7,
    [string]$BaseUrl = "http://localhost:5080"
)

function Get-Num($headers, $name) {
    $v = $headers[$name]
    if ($v -is [array]) { $v = $v[0] }
    if ($null -eq $v) { return 0 }
    return [double]$v
}

# Variants are measured INTERLEAVED, not in blocks: run 1 of every variant,
# then run 2 of every variant, and so on. This machine carries 25-30% background
# load from unrelated containers, and two block-sequential runs of the *same*
# configuration differed by 1.7x. Interleaving does not remove the noise, it
# spreads it evenly across the variants so the comparison between them survives
# it. Min is reported alongside median for the same reason: for CPU-bound work
# the fastest observed run is the one least polluted by another process.
function Measure-Once {
    param([string]$Url)
    $r = Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec 300
    $H = $r.Headers
    [pscustomobject]@{
        Total     = Get-Num $H 'X-Total-Ms'
        Clip      = (Get-Num $H 'X-Us-Clip') / 1000
        Simplify  = (Get-Num $H 'X-Us-Simplify') / 1000
        Transform = (Get-Num $H 'X-Us-Transform') / 1000
        Encode    = (Get-Num $H 'X-Us-Encode') / 1000
        Db        = ((Get-Num $H 'X-Us-Query') + (Get-Num $H 'X-Us-Fetch')) / 1000
        Bytes     = Get-Num $H 'X-Bytes'
        Emitted   = Get-Num $H 'X-Emitted'
        VIn       = Get-Num $H 'X-Vertices-In'
        VOut      = Get-Num $H 'X-Vertices-Out'
        Dropped   = Get-Num $H 'X-Rings-Dropped'
    }
}

function Summarise {
    param([string]$Label, $Rows)
    $sorted = $Rows.Total | Sort-Object
    [pscustomobject]@{
        Simplify = $Label
        MedianMs = $sorted[[int]($sorted.Count / 2)]
        MinMs    = $sorted[0]
        SimpMs   = [math]::Round((($Rows | Measure-Object -Property Simplify -Minimum).Minimum), 1)
        XformMs  = [math]::Round((($Rows | Measure-Object -Property Transform -Minimum).Minimum), 2)
        ClipMs   = [math]::Round((($Rows | Measure-Object -Property Clip -Minimum).Minimum), 1)
        EncodeMs = [math]::Round((($Rows | Measure-Object -Property Encode -Minimum).Minimum), 1)
        DbMs     = [math]::Round((($Rows | Measure-Object -Property Db -Minimum).Minimum), 1)
        Bytes    = $Rows[0].Bytes
        Emitted  = $Rows[0].Emitted
        VertOut  = $Rows[0].VOut
        Dropped  = $Rows[0].Dropped
    }
}

$tiles = @(
    @{ Label = "z12 Istanbul (wide)";  Z = 12; X = 2377;  Y = 1535 },
    @{ Label = "z14 Istanbul (dense)"; Z = 14; X = 9510;  Y = 6142 },
    @{ Label = "z16 Istanbul (close)"; Z = 16; X = 38041; Y = 24570 }
)

foreach ($t in $tiles) {
    Write-Host ""
    Write-Host "=== $($t.Label)  z$($t.Z)/$($t.X)/$($t.Y)  (RectClip throughout) ===" -ForegroundColor Cyan
    $u = "$BaseUrl/tiles-local/$($t.Z)/$($t.X)/$($t.Y).mvt?clip=fast"
    $variants = [ordered]@{
        "nts     DP + topology repair" = "$u&simplify=nts"
        "ntsraw  DP, no repair"        = "$u&simplify=ntsraw"
        "ours    TileSimplify"         = "$u&simplify=ours"
    }

    $acc = @{}
    foreach ($k in $variants.Keys) {
        $acc[$k] = New-Object System.Collections.ArrayList
        1..3 | ForEach-Object { Invoke-WebRequest $variants[$k] -UseBasicParsing -TimeoutSec 300 | Out-Null }
    }

    1..$Runs | ForEach-Object {
        foreach ($k in $variants.Keys) { [void]$acc[$k].Add((Measure-Once $variants[$k])) }
    }

    $variants.Keys | ForEach-Object { Summarise $_ $acc[$_] } | Format-Table -AutoSize
}
