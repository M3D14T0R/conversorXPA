[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GeneratedProjectDirectory,
    [string]$PipelineProjectsDirectory = "",
    [string]$MagicIni = "",
    [string]$ReportPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-CGGeralProjectDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath (Join-Path $resolved "CGGeral.csproj") -PathType Leaf) {
        return $resolved
    }

    $nested = Join-Path $resolved "CGGeral"
    if (Test-Path -LiteralPath (Join-Path $nested "CGGeral.csproj") -PathType Leaf) {
        return $nested
    }

    throw "Projeto CGGeral gerado nao encontrado em: $resolved"
}

$cgGeralDirectory = Resolve-CGGeralProjectDirectory -Path $GeneratedProjectDirectory
$failures = [System.Collections.Generic.List[object]]::new()
$successes = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][string]$Evidence
    )

    $result = [pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Evidence = $Evidence
    }
    if ($Passed) {
        $successes.Add($result)
        Write-Host "[OK] $Name"
    }
    else {
        $failures.Add($result)
        Write-Host "[FALHOU] $Name - $Evidence" -ForegroundColor Red
    }
}

function Read-GeneratedFile {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [string]$BaseDirectory = $cgGeralDirectory
    )

    $path = Join-Path $BaseDirectory $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }
    return [pscustomobject]@{
        Path = $path
        Text = [System.IO.File]::ReadAllText($path)
    }
}

function Test-Contains {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Literal,
        [string]$BaseDirectory = $cgGeralDirectory
    )

    $file = Read-GeneratedFile -RelativePath $RelativePath -BaseDirectory $BaseDirectory
    if ($null -eq $file) {
        Add-Result -Name $Name -Passed $false -Evidence "Arquivo ausente: $RelativePath"
        return
    }

    $found = $file.Text.IndexOf($Literal, [System.StringComparison]::Ordinal) -ge 0
    Add-Result -Name $Name -Passed $found -Evidence "$($file.Path): esperado '$Literal'"
}

function Test-NotContains {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Literal
    )

    $file = Read-GeneratedFile -RelativePath $RelativePath
    if ($null -eq $file) {
        Add-Result -Name $Name -Passed $false -Evidence "Arquivo ausente: $RelativePath"
        return
    }

    $found = $file.Text.IndexOf($Literal, [System.StringComparison]::Ordinal) -ge 0
    Add-Result -Name $Name -Passed (-not $found) -Evidence "$($file.Path): nao deve conter '$Literal'"
}

function Test-InOrder {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$First,
        [Parameter(Mandatory)][string]$Second
    )

    $file = Read-GeneratedFile -RelativePath $RelativePath
    if ($null -eq $file) {
        Add-Result -Name $Name -Passed $false -Evidence "Arquivo ausente: $RelativePath"
        return
    }

    $firstIndex = $file.Text.IndexOf($First, [System.StringComparison]::Ordinal)
    $secondIndex = $file.Text.IndexOf($Second, [System.StringComparison]::Ordinal)
    $passed = $firstIndex -ge 0 -and $secondIndex -gt $firstIndex
    Add-Result -Name $Name -Passed $passed -Evidence "$($file.Path): '$First' deve preceder '$Second'"
}

Test-NotContains `
    -Name "Update sem WithValue nao gera atribuicao C# vazia no programa 514" `
    -RelativePath "CG00514_AjustaNotasSaida514.cs" `
    -Literal ".Value = ;"

Test-NotContains `
    -Name "Update sem WithValue nao gera atribuicao C# vazia no programa 3781" `
    -RelativePath "CG03781_ExportaNotasParaLiscal.cs" `
    -Literal ".Value = ;"

Test-Contains `
    -Name "RaiseEvent do 5628 respeita Parent=2 e chama o evento da task raiz" `
    -RelativePath "CG05628_Efetiva_ExcluiOD.cs" `
    -Literal 'Invoke(_parent._parent.CriaMovimentoCommandWithArgs('

foreach ($structuralTaskOrdinal in @(
    10254, 10781, 11510, 11511, 11522, 11523, 11524,
    11528, 11532, 11547, 11548, 11551, 11552
)) {
    Test-Contains `
        -Name "SelectProgram gera a task estrutural referenciada $structuralTaskOrdinal" `
        -RelativePath "Task_$structuralTaskOrdinal.cs" `
        -Literal "internal class Task_$structuralTaskOrdinal"
}

Test-InOrder `
    -Name "Botao Sair do Centro de Armazenagem preserva a descricao" `
    -RelativePath "Views\CentroDeArmazenagemCentroDeArmazenagem.cs" `
    -First 'btnBSair.BindText' `
    -Second 'btnBSair.Data ='

Test-InOrder `
    -Name "Botao Confirmar da pesquisa de UN preserva a descricao" `
    -RelativePath "Views\PesquisaDeUnDeNegCioPesquisaDeUnidadeDeNegCio.cs" `
    -First 'btnBConfirmarSelect.BindText' `
    -Second 'btnBConfirmarSelect.Data ='

Test-InOrder `
    -Name "Botao Cancelar da pesquisa de UN preserva a descricao" `
    -RelativePath "Views\PesquisaDeUnDeNegCioPesquisaDeUnidadeDeNegCio.cs" `
    -First 'btnBCancelarCancel.BindText' `
    -Second 'btnBCancelarCancel.Data ='

Test-InOrder `
    -Name "Botao Confirmar do Auditor preserva a descricao" `
    -RelativePath "Views\_LogAlteracaoCAAuditor.cs" `
    -First 'btnBConfirmar.BindText' `
    -Second 'btnBConfirmar.Data ='

Test-NotContains `
    -Name "Forma Materiais NOVO nao esconde rotulos atras das abas" `
    -RelativePath "Views\_CadastroDeMateriaisMateriaisNOVO.Designer.cs" `
    -Literal ".SendToBack();"

Test-NotContains `
    -Name "Celula Descricao da pesquisa de materiais nao recebe Anchor do formulario" `
    -RelativePath "Views\ConsultaMateriaisConsultaMateriaisPreOMaior.Designer.cs" `
    -Literal "txtDescricao.Anchor ="

Test-Contains `
    -Name "Cadastro de Materiais filtra o codigo pelo parametro recebido" `
    -RelativePath "CG00341_CadastroDeMateriais.cs" `
    -Literal 'MATERIAL.CodigoMaterial.IsEqualTo(p_rangePesquisaPaginada)'

Test-NotContains `
    -Name "Cadastro de Materiais nao usa o valor aleatorio de inicializacao como filtro" `
    -RelativePath "CG00341_CadastroDeMateriais.cs" `
    -Literal 'MATERIAL.CodigoMaterial.IsEqualTo(() => u.Str(u.Abs(u.Rand'

Test-Contains `
    -Name "Forma alternativa da pesquisa de UN conecta o evento Confirmar" `
    -RelativePath "Views\PesquisaDeUnDeNegCioPesquisaDeUnidadeDeNegCio.Designer.cs" `
    -Literal "btnBConfirmarSelect.Click += new XPARuntimeCore.Box.UI.Advanced.ButtonClickEventHandler(OnbtnBConfirmarSelectClick);"

Test-Contains `
    -Name "Forma alternativa da pesquisa de UN conecta o evento Cancelar" `
    -RelativePath "Views\PesquisaDeUnDeNegCioPesquisaDeUnidadeDeNegCio.Designer.cs" `
    -Literal "btnBCancelarCancel.Click += new XPARuntimeCore.Box.UI.Advanced.ButtonClickEventHandler(OnbtnBCancelarCancelClick);"

foreach ($binding in @(
        'txtUnidadeDeNegocio.Data = _controller.UNIDADE_NEGOCIO.UnidadeNegocio;',
        'txtEmpresa.Data = _controller.UNIDADE_NEGOCIO.Empresa;',
        'txtNome.Data = _controller.UNIDADE_NEGOCIO.NomeCompleto;'
    )) {
    Test-Contains `
        -Name "Pesquisa de UN possui binding: $binding" `
        -RelativePath "Views\PesquisaDeUnDeNegCioPesquisaDeUnidadeDeNegCio.cs" `
        -Literal $binding
}

Test-Contains `
    -Name "Pesquisa de Empresa usa EMPRESA como entidade principal" `
    -RelativePath "CG02103_EmpresasPesquisa2103.cs" `
    -Literal "From = EMPRESA;"

Test-Contains `
    -Name "Pesquisa de OP inclui a entidade ORDEM_PRODUCAO" `
    -RelativePath "CG06445_PesquisaOrdemProducao_6445.cs" `
    -Literal "From = ORDEM_PRODUCAO;"

Test-Contains `
    -Name "Pesquisa de OP seleciona Liberacao da entidade principal" `
    -RelativePath "CG06445_PesquisaOrdemProducao_6445.cs" `
    -Literal "Columns.Add(ORDEM_PRODUCAO.Liberacao);"

Test-Contains `
    -Name "Zoom de Empresa da OP e gerado a partir do SelectProgram do controle" `
    -RelativePath "CG06353_OrdemDeProduO.cs" `
    -Literal 'Handlers.Add(Command.Expand, "Empresa", HandlerScope.CurrentTaskOnly)'

Test-Contains `
    -Name "Zoom de Empresa da OP chama o programa de pesquisa com o campo correto" `
    -RelativePath "CG06353_OrdemDeProduO.cs" `
    -Literal "CG02103_EmpresasPesquisa2103().Run(ORDEM_PRODUCAO.Empresa);"

Test-Contains `
    -Name "Zoom de UN da OP continua registrado" `
    -RelativePath "CG06353_OrdemDeProduO.cs" `
    -Literal 'Handlers.Add(Command.Expand, "Unidade de Negocio", HandlerScope.CurrentTaskOnly)'

Test-Contains `
    -Name "Subform da engenharia filtra obrigatoriamente pelo codigo pai recebido" `
    -RelativePath "CG06301_CadastroDeEngenharia.cs" `
    -Literal "Where.Add(ENGENHARIA.CodigoPai.IsEqualTo(p_codigoPai));"

Test-Contains `
    -Name "Subform da engenharia aplica o tipo PCP somente quando a aba o informa" `
    -RelativePath "CG06301_CadastroDeEngenharia.cs" `
    -Literal 'Where.Add(CndRange(() => p_form != "", ENGENHARIA.TipoPCP.IsEqualTo(p_form)));'

Test-Contains `
    -Name "Subform da engenharia condiciona o inicio da validade ao filtro solicitado" `
    -RelativePath "CG06301_CadastroDeEngenharia.cs" `
    -Literal 'NonDbWhere.Add(CndRange(() => (_parent._parent.p_FiltrouValidade) && (_parent._parent.p_Validade_Filtro != Date.Empty), V_ValidadeInicial.IsLessOrEqualTo(_parent._parent.p_Validade_Filtro)));'

Test-Contains `
    -Name "Subform da engenharia condiciona o fim da validade ao filtro solicitado" `
    -RelativePath "CG06301_CadastroDeEngenharia.cs" `
    -Literal 'NonDbWhere.Add(CndRange(() => (_parent._parent.p_FiltrouValidade) && (_parent._parent.p_Validade_Filtro != Date.Empty), V_ValidadeFinal.BindEqualTo(_parent._parent.p_Validade_Filtro)));'

Test-NotContains `
    -Name "Subform da engenharia nao forca processo enquanto a relation ainda nao foi encontrada" `
    -RelativePath "CG06301_CadastroDeEngenharia.cs" `
    -Literal 'ENGENHARIA.TipoPCP.BindEqualTo(u.If((!((v_existeEngenharia)))'

Test-NotContains `
    -Name "SelectProgram nao confunde indice publico com ordinal de subtask" `
    -RelativePath "CG02103_EmpresasPesquisa2103.cs" `
    -Literal "new global::CGGeral.CG06353_OrdemDeProduO.OrdemDeProduO.ProcessosDaOP"

foreach ($invalidNestedTask in @(
        "ExcluiImpostosPedido",
        "MudaSitItensInternos",
        "ExcluiComplementoPedido",
        "ExcluiDetalhe"
    )) {
    Test-NotContains `
        -Name "Subform de CG02075 nao vincula task interna alheia: $invalidNestedTask" `
        -RelativePath "CG02075_Empresas_2075.cs" `
        -Literal $invalidNestedTask
}

Test-NotContains `
    -Name "Subform de CG00717 nao vincula task interna ChamaEngenharia de outro programa" `
    -RelativePath "CG00717_GerenciarMatrizComissoes_717.cs" `
    -Literal "ChamaEngenharia"

if (-not [string]::IsNullOrWhiteSpace($PipelineProjectsDirectory)) {
    $kernelDirectory = Join-Path ([System.IO.Path]::GetFullPath($PipelineProjectsDirectory)) "04-CGKernel\CGKernel"
    Test-Contains `
        -Name "CK02149 nao executa a mensagem nativa duplicada" `
        -RelativePath "CK02149_2149MensagemOk.cs" `
        -BaseDirectory $kernelDirectory `
        -Literal "if ((false))"
}

if (-not [string]::IsNullOrWhiteSpace($MagicIni)) {
    if (-not (Test-Path -LiteralPath $MagicIni -PathType Leaf)) {
        Add-Result -Name "magic.ini existe" -Passed $false -Evidence "Arquivo ausente: $MagicIni"
    }
    else {
        $magicText = [System.IO.File]::ReadAllText([System.IO.Path]::GetFullPath($MagicIni))
        $hasVersionSetting = [System.Text.RegularExpressions.Regex]::IsMatch(
            $magicText,
            '(?im)^\s*VER_COMP_ESET\s*=\s*69\s*$')
        Add-Result `
            -Name "magic.ini configura VER_COMP_ESET" `
            -Passed $hasVersionSetting `
            -Evidence "${MagicIni}: esperado VER_COMP_ESET=69"
    }
}

$report = [pscustomobject]@{
    GeneratedProjectDirectory = $cgGeralDirectory
    ExecutedAt = (Get-Date).ToString("o")
    Passed = $failures.Count -eq 0
    SuccessCount = $successes.Count
    FailureCount = $failures.Count
    Successes = @($successes)
    Failures = @($failures)
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path -Parent $resolvedReportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    }
    $report | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $resolvedReportPath -Encoding UTF8
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) regressao(oes) encontrada(s) no CGGeral gerado."
}

Write-Host "$($successes.Count) testes de regressao do CGGeral passaram."
