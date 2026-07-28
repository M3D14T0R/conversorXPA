[CmdletBinding()]
param(
    [string]$Target = "CGGeral",
    [string[]]$XmlRoots = @("E:\exp", "E:\exp2"),
    [string]$ProbeRoot = "E:\probes",
    [string]$RunDirectory = "",
    [string]$PublishedDllDirectory = "E:\Dlls\Dlls",
    [string]$RuntimeCoreDirectory = "E:\Dlls\RuntimeCore",
    [string]$ConverterExe = "",
    [string]$StartAfter = "",
    [string]$StopAfter = "",
    [string[]]$Task = @(),
    [string[]]$TaskRange = @(),
    [switch]$WithDependencies,
    [switch]$IncrementalOutput,
    [switch]$IsolatedTaskProject,
    [switch]$SkipProjectBuild,
    [switch]$PlanOnly,
    [switch]$SkipConverterBuild,
    [switch]$SkipRegressionTests,
    [string]$MagicIni = "E:\temp\Geral\magic.ini"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ConverterExe)) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $ConverterExe = Join-Path $repositoryRoot "XpaConverterMvp\bin\Release\net10.0\XpaConverterMvp.exe"
}
else {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
}

$runtimeCoreDll = Join-Path $RuntimeCoreDirectory "XPARuntimeCore.Box.dll"
$frameworkReferenceDll = "D:\Projetos_CSharp\Conversoes\OnlineSample\ExternalRefs\System\System.dll"

foreach ($requiredDirectory in @($ProbeRoot, $PublishedDllDirectory, $RuntimeCoreDirectory)) {
    if (-not (Test-Path -LiteralPath $requiredDirectory -PathType Container)) {
        throw "Diretorio obrigatorio nao encontrado: $requiredDirectory"
    }
}
if (-not (Test-Path -LiteralPath $runtimeCoreDll -PathType Leaf)) {
    throw "Runtime principal nao encontrado: $runtimeCoreDll"
}

if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $RunDirectory = Join-Path $ProbeRoot "$timestamp-CGGeralPipeline"
}
$RunDirectory = [System.IO.Path]::GetFullPath($RunDirectory)

$projectsDirectory = Join-Path $RunDirectory "projects"
$logsDirectory = Join-Path $RunDirectory "logs"
$reportsDirectory = Join-Path $RunDirectory "reports"
$backupsDirectory = Join-Path $RunDirectory "backups"
foreach ($directory in @($RunDirectory, $projectsDirectory, $logsDirectory, $reportsDirectory, $backupsDirectory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

function Get-ComponentReferences {
    param([Parameter(Mandatory)][string]$XmlPath)

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.IgnoreWhitespace = $true
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $reader = [System.Xml.XmlReader]::Create($XmlPath, $settings)
    $references = [System.Collections.Generic.List[object]]::new()
    $insideRepository = $false
    try {
        while ($reader.Read()) {
            if ($reader.NodeType -eq [System.Xml.XmlNodeType]::Element -and
                $reader.Name -eq "ComponentsRepository") {
                $insideRepository = $true
                continue
            }
            if ($insideRepository -and
                $reader.NodeType -eq [System.Xml.XmlNodeType]::EndElement -and
                $reader.Name -eq "ComponentsRepository") {
                break
            }
            if (-not $insideRepository -or
                $reader.NodeType -ne [System.Xml.XmlNodeType]::Element -or
                $reader.Name -ne "Component") {
                continue
            }

            $name = $reader.GetAttribute("name")
            $kind = ""
            $subtree = $reader.ReadSubtree()
            try {
                while ($subtree.Read()) {
                    if ($subtree.NodeType -eq [System.Xml.XmlNodeType]::Element -and
                        $subtree.Name -eq "ComponentType") {
                        $kind = $subtree.GetAttribute("val")
                        break
                    }
                }
            }
            finally {
                $subtree.Dispose()
            }

            if (-not [string]::IsNullOrWhiteSpace($name) -and
                -not [string]::IsNullOrWhiteSpace($kind)) {
                $references.Add([pscustomobject]@{
                    Name = $name.Trim()
                    Kind = $kind.Trim()
                })
            }
        }
    }
    finally {
        $reader.Dispose()
    }

    return @($references | Sort-Object Kind, Name -Unique)
}

function Find-FileCaseInsensitive {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$FileName
    )

    $direct = Join-Path $Directory $FileName
    if (Test-Path -LiteralPath $direct -PathType Leaf) {
        return [System.IO.Path]::GetFullPath($direct)
    }

    $match = Get-ChildItem -LiteralPath $Directory -File |
        Where-Object { [string]::Equals($_.Name, $FileName, [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $match) {
        return $null
    }
    return $match.FullName
}

function Resolve-DotNetReference {
    param([Parameter(Mandatory)][string]$Name)

    $fileName = if ($Name.EndsWith(".dll", [System.StringComparison]::OrdinalIgnoreCase)) {
        $Name
    }
    else {
        "$Name.dll"
    }

    # RuntimeCore is the authoritative source for ENV/XPARuntimeCore binaries.
    # Published project DLLs may contain older copies of those same assemblies.
    foreach ($root in @($RuntimeCoreDirectory, $PublishedDllDirectory)) {
        $candidate = Find-FileCaseInsensitive -Directory $root -FileName $fileName
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            return $candidate
        }
    }

    if ($Name -in @("System", "System.Drawing", "System.Windows.Forms") -and
        (Test-Path -LiteralPath $frameworkReferenceDll -PathType Leaf)) {
        return $frameworkReferenceDll
    }

    return $null
}

$catalog = [System.Collections.Generic.Dictionary[string, string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($xmlRoot in $XmlRoots) {
    if (-not (Test-Path -LiteralPath $xmlRoot -PathType Container)) {
        continue
    }
    foreach ($xml in Get-ChildItem -LiteralPath $xmlRoot -Filter "*.xml" -File | Sort-Object Name) {
        if (-not $catalog.ContainsKey($xml.BaseName)) {
            $catalog.Add($xml.BaseName, $xml.FullName)
        }
    }
}
if (-not $catalog.ContainsKey($Target)) {
    throw "XML do projeto alvo '$Target' nao foi encontrado em: $($XmlRoots -join ', ')"
}

$referenceCache = [System.Collections.Generic.Dictionary[string, object[]]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
function Get-ProjectReferences {
    param([Parameter(Mandatory)][string]$Project)

    if ($referenceCache.ContainsKey($Project)) {
        return $referenceCache[$Project]
    }
    $result = @(Get-ComponentReferences -XmlPath $catalog[$Project])
    $referenceCache[$Project] = $result
    return $result
}

$visitState = [System.Collections.Generic.Dictionary[string, int]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$orderedProjects = [System.Collections.Generic.List[string]]::new()
$externalXpaReferences = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$cycleBootstrapProjects = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

function Visit-Project {
    param([Parameter(Mandatory)][string]$Project)

    if ($visitState.ContainsKey($Project)) {
        if ($visitState[$Project] -eq 1) {
            # XPA components can reference one another cyclically. The first
            # project in that strongly connected group is compiled against the
            # already-published binary; subsequent projects consume the newly
            # published binaries from this run.
            [void]$cycleBootstrapProjects.Add($Project)
        }
        return
    }

    $visitState[$Project] = 1
    foreach ($reference in Get-ProjectReferences -Project $Project) {
        if ($reference.Kind -ne "Magic xpa") {
            continue
        }
        if ($catalog.ContainsKey($reference.Name)) {
            Visit-Project -Project $reference.Name
        }
        else {
            [void]$externalXpaReferences.Add($reference.Name)
        }
    }
    $visitState[$Project] = 2
    $orderedProjects.Add($Project)
}

Visit-Project -Project $Target

$unresolvedExternalXpaReferences = [System.Collections.Generic.List[string]]::new()
foreach ($externalReference in $externalXpaReferences) {
    $externalDll = Find-FileCaseInsensitive -Directory $PublishedDllDirectory -FileName "$externalReference.dll"
    $externalManifest = Find-FileCaseInsensitive -Directory $PublishedDllDirectory -FileName "$externalReference.xpa-manifest.json"
    if ([string]::IsNullOrWhiteSpace($externalDll) -or [string]::IsNullOrWhiteSpace($externalManifest)) {
        $unresolvedExternalXpaReferences.Add($externalReference)
    }
}
$unresolvedExternalXpaReferences |
    Set-Content -LiteralPath (Join-Path $reportsDirectory "unresolved-external-xpa-references.txt") -Encoding UTF8

$plan = for ($index = 0; $index -lt $orderedProjects.Count; $index++) {
    $project = $orderedProjects[$index]
    $xpaDependencies = @(Get-ProjectReferences -Project $project |
        Where-Object { $_.Kind -eq "Magic xpa" } |
        ForEach-Object { $_.Name })
    [pscustomobject]@{
        Order = $index + 1
        Project = $project
        Xml = $catalog[$project]
        XpaDependencies = $xpaDependencies
        OutputType = if (
            [string]::Equals($project, $Target, [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($Target, "CGGeral", [System.StringComparison]::OrdinalIgnoreCase)) {
            "WinExe"
        }
        else {
            "ClassLibrary"
        }
    }
}

$planPath = Join-Path $reportsDirectory "conversion-plan.json"
$plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $planPath -Encoding UTF8
$plan | Format-Table Order, Project, OutputType, @{ Label = "Dependencies"; Expression = { $_.XpaDependencies -join ", " } } |
    Out-String | Set-Content -LiteralPath (Join-Path $reportsDirectory "conversion-plan.txt") -Encoding UTF8

Write-Host "Run: $RunDirectory"
Write-Host "Ordem: $($orderedProjects -join ' -> ')"
Write-Host "Conversao paralela de tasks: habilitada (--parallel-tasks)"
if ($cycleBootstrapProjects.Count -gt 0) {
    Write-Host "Ciclos com bootstrap pelas DLLs publicadas: $($cycleBootstrapProjects -join ', ')"
}
if ($unresolvedExternalXpaReferences.Count -gt 0) {
    Write-Warning "Referencias XPA externas sem DLL/manifesto (serao tratadas pela compatibilidade do conversor): $($unresolvedExternalXpaReferences -join ', ')"
}
if ($PlanOnly) {
    Write-Host "Plano salvo em: $planPath"
    return
}

if (-not $SkipConverterBuild) {
    $converterProject = Join-Path $repositoryRoot "XpaConverterMvp\XpaConverterMvp.csproj"
    & dotnet build $converterProject -c Release --nologo --disable-build-servers
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao compilar o conversor em Release."
    }
}
if (-not (Test-Path -LiteralPath $ConverterExe -PathType Leaf)) {
    throw "Executavel do conversor nao encontrado: $ConverterExe"
}

$startIndex = 0
if (-not [string]::IsNullOrWhiteSpace($StartAfter)) {
    $found = -1
    for ($index = 0; $index -lt $orderedProjects.Count; $index++) {
        if ([string]::Equals(
                $orderedProjects[$index],
                $StartAfter,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            $found = $index
            break
        }
    }
    if ($found -lt 0) {
        throw "StartAfter '$StartAfter' nao pertence ao plano."
    }
    $startIndex = $found + 1
}

$state = [System.Collections.Generic.List[object]]::new()
function Save-State {
    $state | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $reportsDirectory "pipeline-state.json") -Encoding UTF8
}

function Publish-ValidatedFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$BackupFolder
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Artefato validado nao encontrado: $Source"
    }

    New-Item -ItemType Directory -Force -Path $BackupFolder | Out-Null
    $destinationName = Split-Path -Leaf $Destination
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        Copy-Item -LiteralPath $Destination -Destination (Join-Path $BackupFolder $destinationName) -Force
    }

    $temporary = "$Destination.pipeline-new-$([Guid]::NewGuid().ToString('N'))"
    Copy-Item -LiteralPath $Source -Destination $temporary -Force
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $replaceBackup = Join-Path $BackupFolder "$destinationName.replace-backup"
        [System.IO.File]::Replace($temporary, $Destination, $replaceBackup)
    }
    else {
        Move-Item -LiteralPath $temporary -Destination $Destination
    }
}

for ($planIndex = $startIndex; $planIndex -lt $plan.Count; $planIndex++) {
    $entry = $plan[$planIndex]
    $project = $entry.Project
    $sequence = "{0:D2}" -f $entry.Order
    $projectOutput = Join-Path $projectsDirectory "$sequence-$project"
    $conversionLog = Join-Path $logsDirectory "$sequence-$project-convert.log"
    $buildLog = Join-Path $logsDirectory "$sequence-$project-build.log"
    $startedAt = Get-Date

    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @(
        $entry.Xml,
        $projectOutput,
        $project,
        "--output-type", $entry.OutputType,
        "--runtime-core-ref", "Dll",
        "--runtime-core-dll", $runtimeCoreDll,
        "--runtime-root", $RuntimeCoreDirectory,
        "--no-implicit-component-xml",
        "--full-solution",
        "--parallel-tasks"
    )) {
        $arguments.Add([string]$argument)
    }
    foreach ($taskFilter in $Task) {
        if (-not [string]::IsNullOrWhiteSpace($taskFilter)) {
            $arguments.Add("--task")
            $arguments.Add($taskFilter.Trim())
        }
    }
    foreach ($taskRangeFilter in $TaskRange) {
        if (-not [string]::IsNullOrWhiteSpace($taskRangeFilter)) {
            $arguments.Add("--task-range")
            $arguments.Add($taskRangeFilter.Trim())
        }
    }
    if ($WithDependencies) {
        $arguments.Add("--with-dependencies")
    }
    if ($IncrementalOutput) {
        $arguments.Add("--incremental-output")
    }
    if ($IsolatedTaskProject) {
        $arguments.Add("--isolated-task-project")
    }

    foreach ($reference in Get-ProjectReferences -Project $project) {
        $mappedPath = $null
        if ($reference.Kind -eq "Magic xpa") {
            $mappedPath = Find-FileCaseInsensitive -Directory $PublishedDllDirectory -FileName "$($reference.Name).dll"
            $manifestPath = Find-FileCaseInsensitive -Directory $PublishedDllDirectory -FileName "$($reference.Name).xpa-manifest.json"
            if ([string]::IsNullOrWhiteSpace($mappedPath) -or [string]::IsNullOrWhiteSpace($manifestPath)) {
                if ($catalog.ContainsKey($reference.Name)) {
                    throw "[$project] Dependencia planejada ainda nao publicada: $($reference.Name)"
                }
                Write-Warning "[$project] Referencia XPA externa sem artefatos: $($reference.Name)"
                continue
            }
        }
        elseif ($reference.Kind -eq ".NET") {
            $mappedPath = Resolve-DotNetReference -Name $reference.Name
        }

        if (-not [string]::IsNullOrWhiteSpace($mappedPath)) {
            $arguments.Add("--dll-ref-map")
            $arguments.Add("$($reference.Name)=$mappedPath")
        }
    }

    Write-Host "[$sequence/$($plan.Count)] Convertendo $project com tasks paralelas..."
    if ([string]::IsNullOrWhiteSpace($env:XPA_CONVERTER_TELEMETRY_LEVEL)) {
        $env:XPA_CONVERTER_TELEMETRY_LEVEL = "minimal"
    }
    & $ConverterExe @arguments 2>&1 | Tee-Object -LiteralPath $conversionLog
    $conversionExitCode = $LASTEXITCODE
    if ($conversionExitCode -ne 0) {
        $state.Add([pscustomobject]@{
            Project = $project
            Status = "conversion-failed"
            ExitCode = $conversionExitCode
            Log = $conversionLog
        })
        Save-State
        throw "Conversao de '$project' falhou. Log: $conversionLog"
    }

    $projectFile = Join-Path (Join-Path $projectOutput $project) "$project.csproj"
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "Projeto gerado nao encontrado: $projectFile"
    }

    if (-not $SkipRegressionTests -and
        $Task.Count -eq 0 -and
        [string]::Equals($project, $Target, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($Target, "CGGeral", [System.StringComparison]::OrdinalIgnoreCase)) {
        $regressionScript = Join-Path $PSScriptRoot "Test-CGGeralGeneratedRegressions.ps1"
        $regressionReport = Join-Path $reportsDirectory "CGGeral-generated-regressions.json"
        Write-Host "[$sequence/$($plan.Count)] Validando regressoes conhecidas do CGGeral..."
        & $regressionScript `
            -GeneratedProjectDirectory $projectOutput `
            -PipelineProjectsDirectory $projectsDirectory `
            -MagicIni $MagicIni `
            -ReportPath $regressionReport
        if ($LASTEXITCODE -ne 0) {
            throw "Testes de regressao do CGGeral falharam. Relatorio: $regressionReport"
        }
    }

    if (-not $SkipRegressionTests -and
        $Task.Count -eq 0 -and
        [string]::Equals($project, $Target, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($Target, "CGLFCore", [System.StringComparison]::OrdinalIgnoreCase)) {
        $regressionScript = Join-Path $PSScriptRoot "Test-CGLFCoreGeneratedRegressions.ps1"
        $regressionReport = Join-Path $reportsDirectory "CGLFCore-generated-regressions.json"
        Write-Host "[$sequence/$($plan.Count)] Validando regressoes conhecidas do CGLFCore..."
        & $regressionScript `
            -GeneratedProjectDirectory $projectOutput `
            -ReportPath $regressionReport
        if ($LASTEXITCODE -ne 0) {
            throw "Testes de regressao do CGLFCore falharam. Relatorio: $regressionReport"
        }
    }

    if (-not $SkipRegressionTests -and
        $Task.Count -eq 0 -and
        [string]::Equals($project, $Target, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($Target, "CGFunctions", [System.StringComparison]::OrdinalIgnoreCase)) {
        $regressionScript = Join-Path $PSScriptRoot "Test-CGFunctionsGeneratedRegressions.ps1"
        $regressionReport = Join-Path $reportsDirectory "CGFunctions-generated-regressions.json"
        Write-Host "[$sequence/$($plan.Count)] Validando regressoes conhecidas do CGFunctions..."
        & $regressionScript `
            -GeneratedProjectDirectory $projectOutput `
            -ReportPath $regressionReport
        if ($LASTEXITCODE -ne 0) {
            throw "Testes de regressao do CGFunctions falharam. Relatorio: $regressionReport"
        }
    }

    if ($SkipProjectBuild) {
        $state.Add([pscustomobject]@{
            Project = $project
            Status = "converted"
            StartedAt = $startedAt.ToString("o")
            FinishedAt = (Get-Date).ToString("o")
            Xml = $entry.Xml
            ProjectFile = $projectFile
            ConversionLog = $conversionLog
            ParallelTasks = $true
            Published = $false
        })
        Save-State
        Write-Host "[$sequence/$($plan.Count)] Build de $project omitido por solicitacao."
        if (-not [string]::IsNullOrWhiteSpace($StopAfter) -and
            [string]::Equals($project, $StopAfter, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Intervalo solicitado concluido em $project."
            break
        }
        continue
    }

    Write-Host "[$sequence/$($plan.Count)] Compilando $project..."
    & dotnet build $projectFile -c Release --nologo --disable-build-servers `
        -p:MSBuildWarningsAsMessages=MSB3277 2>&1 |
        Tee-Object -LiteralPath $buildLog
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -ne 0) {
        $state.Add([pscustomobject]@{
            Project = $project
            Status = "build-failed"
            ExitCode = $buildExitCode
            ProjectFile = $projectFile
            Log = $buildLog
        })
        Save-State
        throw "Build de '$project' falhou. Log: $buildLog"
    }

    $projectDirectory = Split-Path -Parent $projectFile
    if ($entry.OutputType -eq "ClassLibrary") {
        $manifest = Join-Path $projectDirectory "$project.xpa-manifest.json"
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
            throw "Manifesto de '$project' nao foi gerado: $manifest"
        }
        $manifestData = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if (-not [string]::Equals(
                [string]$manifestData.ComponentName,
                $project,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Manifesto de '$project' possui ComponentName invalido: $($manifestData.ComponentName)"
        }

        $builtDll = Join-Path $projectDirectory "bin\Release\net472\$project.dll"
        [void][System.Reflection.AssemblyName]::GetAssemblyName($builtDll)
        $backupFolder = Join-Path $backupsDirectory $project
        Publish-ValidatedFile `
            -Source $builtDll `
            -Destination (Join-Path $PublishedDllDirectory "$project.dll") `
            -BackupFolder $backupFolder
        Publish-ValidatedFile `
            -Source $manifest `
            -Destination (Join-Path $PublishedDllDirectory "$project.xpa-manifest.json") `
            -BackupFolder $backupFolder
    }

    $state.Add([pscustomobject]@{
        Project = $project
        Status = "success"
        StartedAt = $startedAt.ToString("o")
        FinishedAt = (Get-Date).ToString("o")
        Xml = $entry.Xml
        ProjectFile = $projectFile
        ConversionLog = $conversionLog
        BuildLog = $buildLog
        ParallelTasks = $true
        Published = ($entry.OutputType -eq "ClassLibrary")
    })
    Save-State

    if (-not [string]::IsNullOrWhiteSpace($StopAfter) -and
        [string]::Equals($project, $StopAfter, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Intervalo solicitado concluido em $project."
        break
    }
}

Write-Host "Pipeline concluido com sucesso."
Write-Host "Resultados: $RunDirectory"
