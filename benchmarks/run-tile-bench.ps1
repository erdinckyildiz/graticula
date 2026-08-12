# Tile path benchmark runner.
#
# Answers A-019: does in-process MVT encoding meet latency targets, given that
# ST_AsMVT does not exist on SQL Server or Oracle?
#
# Compares three paths over the same tiles:
#   B  ST_AsMVT            PostGIS fast path, unavailable on two of three providers
#   C  NTS Intersection    naive in-process clip
#   C' our RectClip        bbox trivial-accept plus Sutherland-Hodgman
#
# Usage:  pwsh benchmarks/run-tile-bench.ps1 [-Runs 7]

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

function Invoke-Bench {
    param([string]$Url, [string]$Label, [int]$Runs)

    # Warm: JIT, plan cache, buffer cache. Measuring cold start is a different
    # experiment and mixing them would confound both.
    1..3 | ForEach-Object { Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec 300 | Out-Null }

    $rows = 1..$Runs | ForEach-Object {
        $r = Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec 300
        $H = $r.Headers
        [pscustomobject]@{
            Total     = Get-Num $H 'X-Total-Ms'
            Clip      = [math]::Round((Get-Num $H 'X-Us-Clip') / 1000, 1)
            Simplify  = [math]::Round((Get-Num $H 'X-Us-Simplify') / 1000, 1)
            Transform = [math]::Round((Get-Num $H 'X-Us-Transform') / 1000, 1)
            Encode    = [math]::Round((Get-Num $H 'X-Us-Encode') / 1000, 1)
            Query     = [math]::Round((Get-Num $H 'X-Us-Query') / 1000, 1)
            Fetch     = [math]::Round((Get-Num $H 'X-Us-Fetch') / 1000, 1)
            Parse     = [math]::Round((Get-Num $H 'X-Us-Parse') / 1000, 1)
            Bytes     = Get-Num $H 'X-Bytes'
            Emitted   = Get-Num $H 'X-Emitted'
            Fetched   = Get-Num $H 'X-Fetched'
            Inside    = Get-Num $H 'X-Trivial-Inside'
        }
    }

    $sorted = $rows.Total | Sort-Object
    [pscustomobject]@{
        Path      = $Label
        MedianMs  = $sorted[[int]($sorted.Count / 2)]
        MinMs     = $sorted[0]
        MaxMs     = $sorted[-1]
        ClipMs    = [math]::Round((($rows | Measure-Object -Property Clip -Average).Average), 1)
        SimpMs    = [math]::Round((($rows | Measure-Object -Property Simplify -Average).Average), 1)
        XformMs   = [math]::Round((($rows | Measure-Object -Property Transform -Average).Average), 2)
        EncodeMs  = [math]::Round((($rows | Measure-Object -Property Encode -Average).Average), 1)
        DbMs      = [math]::Round((($rows | Measure-Object -Property Query -Average).Average +
                                   ($rows | Measure-Object -Property Fetch -Average).Average), 1)
        Bytes     = $rows[0].Bytes
        Emitted   = $rows[0].Emitted
        Inside    = $rows[0].Inside
    }
}

# Tiles chosen to span density, because a single tile measures one point on a
# curve. z14 central Istanbul is the dense case; z12 is a wider view with more
# but coarser features; z16 is a sparse close-up.
$tiles = @(
    @{ Label = "z14 Istanbul (dense)"; Z = 14; X = 9510; Y = 6142 },
    @{ Label = "z12 Istanbul (wide)";  Z = 12; X = 2377; Y = 1535 },
    @{ Label = "z16 Istanbul (close)"; Z = 16; X = 38041; Y = 24570 }
)

foreach ($t in $tiles) {
    Write-Host ""
    Write-Host "=== $($t.Label)  z$($t.Z)/$($t.X)/$($t.Y) ===" -ForegroundColor Cyan
    $results = @(
        Invoke-Bench "$BaseUrl/tiles/$($t.Z)/$($t.X)/$($t.Y).mvt" "B  ST_AsMVT (PostGIS)" $Runs
        Invoke-Bench "$BaseUrl/tiles-local/$($t.Z)/$($t.X)/$($t.Y).mvt" "C  NTS Intersection" $Runs
        Invoke-Bench "$BaseUrl/tiles-local/$($t.Z)/$($t.X)/$($t.Y).mvt?clip=fast" "C' our RectClip" $Runs
    )
    $results | Format-Table -AutoSize
}
