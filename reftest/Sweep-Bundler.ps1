# Rebuild every .rdf that ships alongside a Microsoft-built .xpr and compare the
# two byte for byte.  The samples carry hundreds of such pairs, which makes them
# the broadest ground truth available for the bundler.
param(
    [string]$Root = 'D:\Git\RXDK-VS20XX\XDKSamples',
    [string]$Bundler = 'D:\Git\RXDK-Tools\src\Rxdk.Bundler\bin\Debug\net8.0\bundler.exe',
    [switch]$ShowAll
)

$work = Join-Path $env:TEMP 'bundlersweep'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work > $null

$exact = 0
$differ = [System.Collections.Generic.List[object]]::new()
$failed = [System.Collections.Generic.List[object]]::new()
$total = 0

foreach ($rdf in Get-ChildItem -Path $Root -Recurse -Filter *.rdf -File |
                 Where-Object { $_.FullName -notmatch '\\out\\' }) {
    $ref = Join-Path $rdf.Directory "Media\$($rdf.BaseName).xpr"
    if (-not (Test-Path $ref)) { $ref = Join-Path $rdf.Directory "$($rdf.BaseName).xpr" }
    if (-not (Test-Path $ref)) { continue }

    $total++
    # Every output goes to the scratch directory: the shipped .xpr sits next to
    # the .rdf, so letting the tool use its default paths would overwrite it.
    $out = Join-Path $work "$($rdf.BaseName).xpr"
    $hdr = Join-Path $work "$($rdf.BaseName).h"
    $err = Join-Path $work "$($rdf.BaseName).err"
    Remove-Item $out -Force -ErrorAction SilentlyContinue
    Push-Location $rdf.Directory
    $log = & $Bundler -q -o $out -h $hdr -e $err $rdf.Name 2>&1
    $code = $LASTEXITCODE
    Pop-Location

    $name = $rdf.FullName.Replace("$Root\", '')
    if ($code -ne 0 -or -not (Test-Path $out)) {
        $failed.Add([pscustomobject]@{ Name = $name; Reason = ($log | Select-Object -Last 1) })
        continue
    }

    $a = [IO.File]::ReadAllBytes($ref)
    $b = [IO.File]::ReadAllBytes($out)
    if ($a.Length -ne $b.Length) {
        $differ.Add([pscustomobject]@{ Name = $name; Bytes = "size $($a.Length) vs $($b.Length)" })
        continue
    }
    $bad = 0
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { $bad++ } }
    if ($bad -eq 0) { $exact++ }
    else { $differ.Add([pscustomobject]@{ Name = $name; Bytes = "$bad of $($a.Length)" }) }
}

$report = Join-Path $PSScriptRoot 'bundler-sweep.txt'
$lines = @("$total pairs: $exact byte-identical, $($differ.Count) differing, $($failed.Count) failed to build", '')
$lines += $differ | ForEach-Object { "DIFF  {0,-62} {1}" -f $_.Name, $_.Bytes }
$lines += $failed | ForEach-Object { "FAIL  {0,-62} {1}" -f $_.Name, $_.Reason }
Set-Content -Path $report -Value $lines
"$total pairs: $exact byte-identical, $($differ.Count) differing, $($failed.Count) failed to build"
"full report: $report"
if ($differ.Count) {
    "`ndiffering:"
    $(if ($ShowAll) { $differ } else { $differ | Select-Object -First 25 }) |
        ForEach-Object { "  {0,-60} {1}" -f $_.Name, $_.Bytes }
}
if ($failed.Count) {
    "`nfailed:"
    $(if ($ShowAll) { $failed } else { $failed | Select-Object -First 25 }) |
        ForEach-Object { "  {0,-60} {1}" -f $_.Name, $_.Reason }
}
