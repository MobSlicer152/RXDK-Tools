param(
    [string]$DllPath = (Join-Path $PSScriptRoot "bin\Debug\Rxdk.MsBuild.dll"),
    [string]$Task,
    [string]$Parent
)

$assembly = Add-Type -AssemblyName $DllPath -PassThru

$taskObj = New-Object -TypeName Rxdk.MsBuild.Tasks.$Task
$taskObj.DumpTargetsFragment($Parent)
