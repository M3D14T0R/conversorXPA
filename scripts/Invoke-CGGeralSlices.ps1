[CmdletBinding()]
param(
    [string]$PipelineRunDirectory = "E:\probes\20260725-112205-CGGeralPipeline",
    [string[]]$Slice = @(),
    [switch]$Fresh,
    [switch]$SkipTests,
    [string]$MagicIni = "E:\temp\Geral\magic-cg06353.ini"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pipelineScript = Join-Path $PSScriptRoot "Invoke-CGGeralPipeline.ps1"
$testScript = Join-Path $PSScriptRoot "Test-CGGeralSlices.ps1"
$slicesRoot = Join-Path $PipelineRunDirectory "slices"

$definitions = @(
    [pscustomobject]@{ Name = "CG00347"; Ranges = @("347"); StartProgram = "CG00347"; TraceTargets = "CG00347" },
    [pscustomobject]@{ Name = "CG00341"; Ranges = @("341", "374"); StartProgram = "CG00341"; TraceTargets = "CG00341;CG00374" },
    [pscustomobject]@{ Name = "CG00343"; Ranges = @("343"); StartProgram = "CG00343"; TraceTargets = "CG00343;CGLFCore.KF00013" },
    [pscustomobject]@{ Name = "CG00344"; Ranges = @("344"); StartProgram = "CG00344"; TraceTargets = "CG00344" },
    [pscustomobject]@{ Name = "CG00374"; Ranges = @("374"); StartProgram = "CG00374"; TraceTargets = "CG00374" },
    [pscustomobject]@{ Name = "CG02103"; Ranges = @("2103", "2114", "3377"); StartProgram = "CG02103"; TraceTargets = "CG02103;CG02114;CG03377" },
    [pscustomobject]@{ Name = "CG02109"; Ranges = @("2109"); StartProgram = "CG02109"; TraceTargets = "CG02109" },
    [pscustomobject]@{ Name = "CG06301"; Ranges = @("374", "6301"); StartProgram = "CG06301|ZPROD001"; TraceTargets = "CG06301;CG00374" },
    [pscustomobject]@{ Name = "CG06388"; Ranges = @("6388"); StartProgram = "CG06388"; TraceTargets = "CG06388" },
    [pscustomobject]@{
        Name = "CG06353Flow"
        Ranges = @("374", "2103", "2109", "2114", "3377", "6301", "6353", "6365", "6385", "6388", "6391", "6393", "6395", "6401", "6404", "6417", "6435", "6446", "6464")
        StartProgram = "CG06353|990001"
        TraceTargets = "CG06353;CG06365;CG06385;CG06388;CG06391;CG06393;CG06401;CG06404;CG06301;CG06395;CG06417;CG06435;CG06446;CG06464;CG02109;CG02103;CG00374"
    }
)

$selected = @(
    if ($Slice.Count -eq 0) {
        $definitions
    }
    else {
        $definitions | Where-Object { $Slice -contains $_.Name }
    }
)

if ($selected.Count -eq 0) {
    throw "Nenhum recorte reconhecido. Valores validos: $($definitions.Name -join ', ')"
}

dotnet build (Join-Path $repositoryRoot "XpaConverterMvp\XpaConverterMvp.csproj") `
    -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Build Release do conversor falhou."
}

foreach ($definition in $selected) {
    $sliceDirectory = Join-Path $slicesRoot $definition.Name
    $projectFile = Join-Path $sliceDirectory "projects\28-CGGeral\CGGeral\CGGeral.csproj"
    $useIncremental = -not $Fresh -and (Test-Path -LiteralPath $projectFile -PathType Leaf)

    Write-Host ""
    Write-Host "=== Recorte $($definition.Name) ==="
    $pipelineParameters = @{
        RunDirectory = $sliceDirectory
        StartAfter = "CGOutlook"
        StopAfter = "CGGeral"
        TaskRange = [string[]]$definition.Ranges
        IsolatedTaskProject = $true
        SkipConverterBuild = $true
        SkipRegressionTests = $true
        MagicIni = $MagicIni
    }
    if ($useIncremental) {
        $pipelineParameters.IncrementalOutput = $true
    }

    & $pipelineScript @pipelineParameters

    $applicationDirectory = Join-Path $sliceDirectory "projects\28-CGGeral\CGGeral\bin\Release\net472"
    $applicationAssembly = Join-Path $applicationDirectory "CGGeral.exe"
    $profilePath = Join-Path $sliceDirectory "XpaRuntime.GenericLauncher.$($definition.Name).ini"
    $profile = @"
[XPA.Runtime]
ApplicationAssembly=$applicationAssembly
Mode=InProcess
WorkingDirectory=$applicationDirectory
StartProgram=$($definition.StartProgram)
Arguments=/GENERALERRORLOG=diagnostics\$($definition.Name.ToLowerInvariant())-general-error.log /DBLOGFILE=diagnostics\$($definition.Name.ToLowerInvariant())-database.log /ALLWAYSSHOWDBERRORS=Y
ProbePaths=E:\Dlls\RuntimeCore;E:\Dlls\Dlls
RefreshProbeFiles=Y
WaitForExit=Y

[Data]
MagicIni=$MagicIni

[Security]
CurrentUser=A00
Administrator=Y

[Parameters]
VERIFICOU_LICENCA=SIM
VER_COMP_ESET=69
CFG_549=02
BANCO_DADOS=02

[Diagnostics]
ControllerTrace=$($definition.TraceTargets)
ControllerTraceFile=diagnostics\$($definition.Name.ToLowerInvariant())-controller.log
RuntimeProfilerFile=diagnostics\$($definition.Name.ToLowerInvariant())-runtime.prof
RuntimeProfilerTrace=Y
"@
    [System.IO.File]::WriteAllText(
        $profilePath,
        $profile,
        [System.Text.UTF8Encoding]::new($false))
}

if (-not $SkipTests) {
    & $testScript -SlicesRoot $slicesRoot -Slice @($selected.Name)
}
