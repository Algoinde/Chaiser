<#
.SYNOPSIS
    Renders every currently-present .glb model in the Chaiser output folder to a
    top-down PNG sprite via Blender headless. Always renders the full current model set (not
    just newly-extracted ones) so all sprites stay visually consistent with each other.

.DESCRIPTION
    Extraction and rendering are intentionally decoupled: Chaiser's `extract-all` skips
    models that already exist on disk (expensive, diffed against intermediate .glb output), but
    this script re-renders every .glb every time it runs (cheap, and framing/lighting tweaks
    should apply uniformly across the whole set rather than leaving old sprites stale).

    Looks up each building's `range.width`/`range.depth` in FactoryBuildingTable.json (when the
    building id is present there) to pass as an explicit tile_size to render_sprite.py - this
    frames tight to the known gameplay footprint instead of trusting the mesh's own bounding box
    (which can extend past the visual tile edge, e.g. from collision/connector geometry).
    Buildings absent from the table (decorations/props without ports - still real factory
    buildings, just without registered footprint data) fall back to bounding-box framing.

.EXAMPLE
    ./render_all_sprites.ps1 -BlenderExe "C:\Programs\blender-4.5.2-windows-x64\blender.exe"
#>
param(
    [string] $ModelsDir = "C:\Projects\15_EnkaNetwork\Chaiser\output\models",
    [string] $SpritesOut = "C:\Projects\15_EnkaNetwork\Chaiser\output\sprites",
    [string] $BlenderExe = "C:\Programs\blender-4.5.2-windows-x64\blender.exe",
    [string] $FactoryBuildingTablePath = "C:\Projects\34_AIC\src\lib\data\FactoryBuildingTable.json",
    [int] $Resolution = 1024,
    [string[]] $Names = @()
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $SpritesOut | Out-Null

$table = Get-Content $FactoryBuildingTablePath -Raw | ConvertFrom-Json

$glbFiles = Get-ChildItem -Path $ModelsDir -Filter "*.glb"
if ($Names.Count -gt 0) {
    $glbFiles = $glbFiles | Where-Object { $n = $_.BaseName; ($Names | Where-Object { $n -match $_ }).Count -gt 0 }
}
if ($glbFiles.Count -eq 0) {
    throw "No .glb files found in $ModelsDir"
}
Write-Host "Rendering $($glbFiles.Count) model(s) -> $SpritesOut" -ForegroundColor Cyan

$renderScript = Join-Path $PSScriptRoot "render_sprite.py"
$succeeded = 0
$failed = @()
$noTableEntry = @()

for ($i = 0; $i -lt $glbFiles.Count; $i++) {
    $glb = $glbFiles[$i]
    $buildingId = $glb.BaseName
    $outPng = Join-Path $SpritesOut ("item_port_{0}.png" -f $buildingId)

    $tileSize = $null
    $def = $table.$buildingId
    if ($def -and $def.range -and $def.range.width) {
        # Footprints aren't always square (e.g. udpipe_unloader_2 is 3 wide x 5 deep) - using
        # width alone under-framed the depth axis and cropped the base pedestal out of shot.
        # max(width, depth) keeps both axes fully in frame at the cost of some empty margin on
        # the shorter axis, which matches how the hand-captured reference sprites are framed.
        $depth = if ($def.range.depth) { [double]$def.range.depth } else { [double]$def.range.width }
        $tileSize = [Math]::Max([double]$def.range.width, $depth)
    } else {
        $noTableEntry += $buildingId
    }

    Write-Host "  [$($i+1)/$($glbFiles.Count)] $buildingId$(if ($tileSize) { " (tile_size=$tileSize)" })"

    $renderArgs = @("--background", "--python", $renderScript, "--", $glb.FullName, $outPng, $Resolution)
    if ($tileSize) { $renderArgs += $tileSize }

    & $BlenderExe @renderArgs *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Render failed for $buildingId"
        $failed += $buildingId
        continue
    }

    $succeeded++
}

Write-Host "Done. $succeeded succeeded, $($failed.Count) failed." -ForegroundColor Green
if ($failed.Count -gt 0) {
    Write-Warning ("Failed: " + ($failed -join ", "))
}
if ($noTableEntry.Count -gt 0) {
    Write-Host "No FactoryBuildingTable entry (used bounding-box framing instead of tile_size): $($noTableEntry.Count) model(s)" -ForegroundColor DarkYellow
}
