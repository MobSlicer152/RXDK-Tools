# Probe candidate ALL_SAME_THRESHOLD values and report the resulting skin diff.
$sb  = "D:\Git\RXDK-Tools\src\Rxdk.SkinBld\bin\Debug\net8.0\skinbld.exe"
$ref = "D:\Git\RXDK-VS20XX\XDKSamples\Networking\UIXAuth\Media\UIXAuth.uix"
$out = "D:\Git\RXDK-Tools\reftest\skins\probe.uix"

$candidates = @(
    @{ n = '1/2048 (leaked)'; v = '' }
    @{ n = '1/1024';          v = '0.0009765625' }
    @{ n = '1/512';           v = '0.001953125' }
    @{ n = '1/256';           v = '0.00390625' }
    @{ n = '1/255';           v = '0.0039215688' }
    @{ n = '1.5/255';         v = '0.005882353' }
    @{ n = '2/255';           v = '0.007843138' }
    @{ n = '1/128';           v = '0.0078125' }
    @{ n = '3/255';           v = '0.011764706' }
)

Push-Location "D:\Git\RXDK-VS20XX\XDKSamples\Common\uix"
foreach ($c in $candidates) {
    if ($c.v -eq '') { Remove-Item Env:RXDK_ALLSAME -ErrorAction SilentlyContinue }
    else { $env:RXDK_ALLSAME = $c.v }

    & $sb default.inx $out 2>&1 | Out-Null

    $a = [IO.File]::ReadAllBytes($ref)
    $b = [IO.File]::ReadAllBytes($out)
    $d = 0
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { $d++ } }
    "{0,-16} {1,8} differing bytes" -f $c.n, $d
}
Remove-Item Env:RXDK_ALLSAME -ErrorAction SilentlyContinue
Pop-Location
