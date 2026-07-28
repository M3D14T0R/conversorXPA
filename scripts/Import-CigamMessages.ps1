[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)]
    [string]$XmlPath,

    [Parameter(Mandatory)]
    [string]$ConnectionString,

    [string]$TableName = "dbo.GEMSG",

    [ValidateRange(100, 10000)]
    [int]$BatchSize = 2000,

    [ValidateRange(0, [int]::MaxValue)]
    [int]$ExpectedRows = 0,

    [string]$AuditUser = "A00"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $XmlPath -PathType Leaf)) {
    throw "Arquivo de mensagens não encontrado: $XmlPath"
}

if ($TableName -notmatch '^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$') {
    throw "TableName deve estar no formato schema.tabela e conter somente identificadores SQL simples."
}

$tableParts = $TableName.Split(".")
$schemaName = $tableParts[0]
$physicalTableName = $tableParts[1]
$qualifiedTable = "[$schemaName].[$physicalTableName]"

Add-Type -AssemblyName System.Data
Add-Type -AssemblyName System.Xml.Linq

function Add-StageColumn {
    param(
        [Parameter(Mandatory)][System.Data.DataTable]$Table,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][Type]$Type,
        [int]$MaxLength = -1
    )

    $column = [System.Data.DataColumn]::new($Name, $Type)
    if ($Type -eq [string] -and $MaxLength -gt 0) {
        $column.MaxLength = $MaxLength
    }
    [void]$Table.Columns.Add($column)
}

function Get-ElementText {
    param(
        [Parameter(Mandatory)][System.Xml.Linq.XElement]$Element,
        [Parameter(Mandatory)][string]$Name
    )

    $child = $Element.Element($Name)
    if ($null -eq $child) {
        return ""
    }
    return [string]$child.Value
}

function Convert-ToDecimal {
    param(
        [string]$Value,
        [Parameter(Mandatory)][string]$FieldName
    )

    $number = [decimal]0
    if (-not [decimal]::TryParse(
            $Value,
            [System.Globalization.NumberStyles]::Number,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$number)) {
        throw "Valor numérico inválido em '$FieldName': '$Value'."
    }
    return $number
}

$stage = [System.Data.DataTable]::new("CigamMessages")
Add-StageColumn -Table $stage -Name "Sequencia" -Type ([decimal])
Add-StageColumn -Table $stage -Name "Seq_mensagem" -Type ([decimal])
Add-StageColumn -Table $stage -Name "Idioma" -Type ([string]) -MaxLength 3
Add-StageColumn -Table $stage -Name "Modulo" -Type ([string]) -MaxLength 2
Add-StageColumn -Table $stage -Name "Divisao" -Type ([string]) -MaxLength 2
Add-StageColumn -Table $stage -Name "Sub_divisao" -Type ([string]) -MaxLength 2
Add-StageColumn -Table $stage -Name "Descricao" -Type ([string]) -MaxLength 256
Add-StageColumn -Table $stage -Name "Tipo" -Type ([string]) -MaxLength 2
Add-StageColumn -Table $stage -Name "Nivel" -Type ([string]) -MaxLength 1
Add-StageColumn -Table $stage -Name "Link_protocolo" -Type ([string]) -MaxLength 16
Add-StageColumn -Table $stage -Name "Link_endereco" -Type ([string]) -MaxLength 240
Add-StageColumn -Table $stage -Name "Titulo" -Type ([string]) -MaxLength 30
Add-StageColumn -Table $stage -Name "Botoes" -Type ([string]) -MaxLength 3
Add-StageColumn -Table $stage -Name "Botao_inicial" -Type ([decimal])
Add-StageColumn -Table $stage -Name "Botao_1" -Type ([string]) -MaxLength 20
Add-StageColumn -Table $stage -Name "Botao_2" -Type ([string]) -MaxLength 20
Add-StageColumn -Table $stage -Name "Botao_3" -Type ([string]) -MaxLength 20
Add-StageColumn -Table $stage -Name "Campo41" -Type ([bool])
Add-StageColumn -Table $stage -Name "Texto_exibicao" -Type ([string]) -MaxLength 240

$connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
$transaction = $null
$reader = $null
$bulkCopy = $null
$parsedRows = 0
$insertedRows = 0
$updatedRows = 0
$keys = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

try {
    $connection.Open()
    $transaction = $connection.BeginTransaction(
        [System.Data.IsolationLevel]::Serializable)

    $validateCommand = $connection.CreateCommand()
    $validateCommand.Transaction = $transaction
    $validateCommand.CommandText = @"
IF OBJECT_ID(@QualifiedName, N'U') IS NULL
    THROW 50001, 'Tabela de mensagens não encontrada.', 1;

DECLARE @MissingColumns nvarchar(max);
WITH RequiredColumns(Name) AS
(
    SELECT N'XPA_ROW_ID' UNION ALL SELECT N'Sequencia' UNION ALL
    SELECT N'Seq_mensagem' UNION ALL SELECT N'Idioma' UNION ALL
    SELECT N'Modulo' UNION ALL SELECT N'Divisao' UNION ALL
    SELECT N'Sub_divisao' UNION ALL SELECT N'Descricao' UNION ALL
    SELECT N'Tipo' UNION ALL SELECT N'Nivel' UNION ALL
    SELECT N'Link_protocolo' UNION ALL SELECT N'Link_endereco' UNION ALL
    SELECT N'Titulo' UNION ALL SELECT N'Botoes' UNION ALL
    SELECT N'Botao_inicial' UNION ALL SELECT N'Botao_1' UNION ALL
    SELECT N'Botao_2' UNION ALL SELECT N'Botao_3' UNION ALL
    SELECT N'Campo41' UNION ALL SELECT N'Texto_exibicao'
)
SELECT @MissingColumns = STRING_AGG(r.Name, N', ')
FROM RequiredColumns r
WHERE NOT EXISTS
(
    SELECT 1
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(@QualifiedName)
      AND c.name = r.Name
);

IF @MissingColumns IS NOT NULL
    THROW 50002, 'A tabela de mensagens não possui todas as colunas esperadas.', 1;

IF COLUMNPROPERTY(OBJECT_ID(@QualifiedName), N'XPA_ROW_ID', 'IsIdentity') <> 1
    THROW 50003, 'A coluna XPA_ROW_ID da tabela de mensagens deve ser IDENTITY.', 1;
"@
    [void]$validateCommand.Parameters.Add(
        "@QualifiedName",
        [System.Data.SqlDbType]::NVarChar,
        260)
    $validateCommand.Parameters["@QualifiedName"].Value = "$schemaName.$physicalTableName"
    [void]$validateCommand.ExecuteNonQuery()

    $createStageCommand = $connection.CreateCommand()
    $createStageCommand.Transaction = $transaction
    $createStageCommand.CommandText = @"
CREATE TABLE #CigamMessagesStage
(
    Sequencia decimal(18,4) NOT NULL,
    Seq_mensagem decimal(18,4) NOT NULL,
    Idioma nvarchar(3) NOT NULL,
    Modulo nvarchar(2) NOT NULL,
    Divisao nvarchar(2) NOT NULL,
    Sub_divisao nvarchar(2) NOT NULL,
    Descricao nvarchar(256) NOT NULL,
    Tipo nvarchar(2) NOT NULL,
    Nivel nvarchar(1) NOT NULL,
    Link_protocolo nvarchar(16) NOT NULL,
    Link_endereco nvarchar(240) NOT NULL,
    Titulo nvarchar(30) NOT NULL,
    Botoes nvarchar(3) NOT NULL,
    Botao_inicial decimal(18,4) NOT NULL,
    Botao_1 nvarchar(20) NOT NULL,
    Botao_2 nvarchar(20) NOT NULL,
    Botao_3 nvarchar(20) NOT NULL,
    Campo41 bit NOT NULL,
    Texto_exibicao nvarchar(240) NOT NULL
);
"@
    [void]$createStageCommand.ExecuteNonQuery()

    $bulkCopy = [System.Data.SqlClient.SqlBulkCopy]::new(
        $connection,
        [System.Data.SqlClient.SqlBulkCopyOptions]::TableLock,
        $transaction)
    $bulkCopy.DestinationTableName = "#CigamMessagesStage"
    $bulkCopy.BatchSize = $BatchSize
    $bulkCopy.BulkCopyTimeout = 120
    foreach ($column in $stage.Columns) {
        [void]$bulkCopy.ColumnMappings.Add($column.ColumnName, $column.ColumnName)
    }

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.IgnoreComments = $true
    $settings.IgnoreProcessingInstructions = $true
    $settings.IgnoreWhitespace = $true
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create(
        [System.IO.Path]::GetFullPath($XmlPath),
        $settings)

    while (-not $reader.EOF) {
        if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element -or
            $reader.LocalName -ne "registro") {
            [void]$reader.Read()
            continue
        }

        $record = [System.Xml.Linq.XElement]::ReadFrom($reader)
        $sequenceText = Get-ElementText -Element $record -Name "sequencia"
        $messageSequenceText = Get-ElementText -Element $record -Name "seqMensagem"
        $sequence = Convert-ToDecimal -Value $sequenceText -FieldName "sequencia"
        $messageSequence = Convert-ToDecimal -Value $messageSequenceText -FieldName "seqMensagem"
        $module = Get-ElementText -Element $record -Name "modulo"
        $division = Get-ElementText -Element $record -Name "divisao"
        $subDivision = Get-ElementText -Element $record -Name "subDivisao"
        $type = Get-ElementText -Element $record -Name "tipo"
        $level = Get-ElementText -Element $record -Name "nivel"
        $linkProtocol = Get-ElementText -Element $record -Name "linkProtocolo"
        $linkAddress = Get-ElementText -Element $record -Name "linkEndereco"
        $buttons = Get-ElementText -Element $record -Name "botoes"
        $initialButton = Convert-ToDecimal `
            -Value (Get-ElementText -Element $record -Name "botaoInicial") `
            -FieldName "botaoInicial"
        $allowDoNotShowText = Get-ElementText -Element $record -Name "permiteNaoMostrar"
        $allowDoNotShow = $allowDoNotShowText -in @("1", "S", "Y", "true", "True")
        $displayText = Get-ElementText -Element $record -Name "textoExibicao"

        foreach ($translation in $record.Elements("traducao")) {
            $language = Get-ElementText -Element $translation -Name "idioma"
            $key = "{0}|{1}|{2}" -f $sequence, $messageSequence, $language
            if (-not $keys.Add($key)) {
                throw "Chave duplicada no XML de mensagens: $key"
            }

            $row = $stage.NewRow()
            $row["Sequencia"] = $sequence
            $row["Seq_mensagem"] = $messageSequence
            $row["Idioma"] = $language
            $row["Modulo"] = $module
            $row["Divisao"] = $division
            $row["Sub_divisao"] = $subDivision
            $row["Descricao"] = Get-ElementText -Element $translation -Name "descricao"
            $row["Tipo"] = $type
            $row["Nivel"] = $level
            $row["Link_protocolo"] = $linkProtocol
            $row["Link_endereco"] = $linkAddress
            $row["Titulo"] = Get-ElementText -Element $translation -Name "titulo"
            $row["Botoes"] = $buttons
            $row["Botao_inicial"] = $initialButton
            $row["Botao_1"] = Get-ElementText -Element $translation -Name "botao1"
            $row["Botao_2"] = Get-ElementText -Element $translation -Name "botao2"
            $row["Botao_3"] = Get-ElementText -Element $translation -Name "botao3"
            $row["Campo41"] = $allowDoNotShow
            $row["Texto_exibicao"] = $displayText
            [void]$stage.Rows.Add($row)
            $parsedRows++

            if ($stage.Rows.Count -ge $BatchSize) {
                $bulkCopy.WriteToServer($stage)
                $stage.Clear()
            }
        }
    }

    if ($stage.Rows.Count -gt 0) {
        $bulkCopy.WriteToServer($stage)
        $stage.Clear()
    }

    if ($parsedRows -eq 0) {
        throw "O XML não contém mensagens para importar."
    }
    if ($ExpectedRows -gt 0 -and $parsedRows -ne $ExpectedRows) {
        throw "Quantidade inesperada de mensagens no XML: esperado $ExpectedRows, encontrado $parsedRows."
    }

    $auditUserValue = if ([string]::IsNullOrWhiteSpace($AuditUser)) {
        "A00"
    }
    else {
        $AuditUser.Trim()
    }
    if ($auditUserValue.Length -gt 3) {
        throw "AuditUser deve possuir no máximo 3 caracteres."
    }

    $applyCommand = $connection.CreateCommand()
    $applyCommand.Transaction = $transaction
    $applyCommand.CommandTimeout = 120
    $applyCommand.CommandText = @"
DECLARE @Today date = CONVERT(date, GETDATE());
DECLARE @Now time(0) = CONVERT(time(0), GETDATE());
DECLARE @Updated int = 0;
DECLARE @Inserted int = 0;

UPDATE target
SET
    Modulo = source.Modulo,
    Divisao = source.Divisao,
    Sub_divisao = source.Sub_divisao,
    Descricao = source.Descricao,
    Tipo = source.Tipo,
    Nivel = source.Nivel,
    Link_protocolo = source.Link_protocolo,
    Link_endereco = source.Link_endereco,
    Titulo = source.Titulo,
    Botoes = source.Botoes,
    Botao_inicial = source.Botao_inicial,
    Botao_1 = source.Botao_1,
    Botao_2 = source.Botao_2,
    Botao_3 = source.Botao_3,
    Usuario_modificacao = @AuditUser,
    Data_modificacao = @Today,
    Hora_modificacao = @Now,
    Campo41 = source.Campo41,
    Texto_exibicao = source.Texto_exibicao
FROM $qualifiedTable target WITH (UPDLOCK, HOLDLOCK)
INNER JOIN #CigamMessagesStage source
    ON source.Sequencia = target.Sequencia
   AND source.Seq_mensagem = target.Seq_mensagem
   AND source.Idioma = target.Idioma;

SET @Updated = @@ROWCOUNT;

;WITH MissingRows AS
(
    SELECT source.*
    FROM #CigamMessagesStage source
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM $qualifiedTable target WITH (UPDLOCK, HOLDLOCK)
        WHERE target.Sequencia = source.Sequencia
          AND target.Seq_mensagem = source.Seq_mensagem
          AND target.Idioma = source.Idioma
    )
)
INSERT INTO $qualifiedTable
(
    Sequencia, Seq_mensagem, Idioma, Modulo, Divisao,
    Sub_divisao, Descricao, Tipo, Nivel, Link_protocolo, Link_endereco,
    Titulo, Botoes, Botao_inicial, Botao_1, Botao_2, Botao_3,
    Usuario_criacao, Data_criacao, Hora_criacao,
    Usuario_modificacao, Data_modificacao, Hora_modificacao,
    Campo24, Campo25, Campo26, Campo27,
    Campo28, Campo29, Campo30, Campo31,
    Campo32, Campo33, Campo34, Campo35, Campo36, Campo37,
    Campo38, Campo39, Campo40, Campo41, Campo42, Campo43,
    Campo44, Campo45, Campo46, Campo47,
    Campo48, Campo49, Campo50, Campo51,
    Campo52, Campo53, Campo54, Campo55,
    Usrmsg1, Usrmsg2, Usrmsg3, Texto_exibicao
)
SELECT
    Sequencia, Seq_mensagem, Idioma, Modulo, Divisao,
    Sub_divisao, Descricao, Tipo, Nivel, Link_protocolo, Link_endereco,
    Titulo, Botoes, Botao_inicial, Botao_1, Botao_2, Botao_3,
    @AuditUser, @Today, @Now,
    @AuditUser, @Today, @Now,
    NULL, NULL, NULL, NULL,
    N'', N'', N'', N'',
    N'', N'', N'', N'', N'', N'',
    N'', N'', N'', Campo41, 0, 0,
    0, 0, 0, 0,
    0, 0, 0, 0,
    0, 0, 0, 0,
    N'', NULL, 0, Texto_exibicao
FROM MissingRows;

SET @Inserted = @@ROWCOUNT;

SELECT @Inserted AS InsertedRows, @Updated AS UpdatedRows;
"@
    [void]$applyCommand.Parameters.Add(
        "@AuditUser",
        [System.Data.SqlDbType]::NVarChar,
        3)
    $applyCommand.Parameters["@AuditUser"].Value = $auditUserValue

    if ($PSCmdlet.ShouldProcess(
            "$qualifiedTable via $($connection.DataSource)/$($connection.Database)",
            "Importar $parsedRows mensagens do XML")) {
        $resultReader = $applyCommand.ExecuteReader()
        try {
            if (-not $resultReader.Read()) {
                throw "A carga não retornou as quantidades inseridas e atualizadas."
            }
            $insertedRows = [int]$resultReader["InsertedRows"]
            $updatedRows = [int]$resultReader["UpdatedRows"]
        }
        finally {
            $resultReader.Dispose()
        }
        $transaction.Commit()
        $transaction = $null
    }
    else {
        $transaction.Rollback()
        $transaction = $null
    }
}
catch {
    if ($null -ne $transaction) {
        try {
            $transaction.Rollback()
        }
        catch {
            # Preserve the original failure.
        }
    }
    throw
}
finally {
    if ($null -ne $reader) {
        $reader.Dispose()
    }
    if ($null -ne $bulkCopy) {
        $bulkCopy.Dispose()
    }
    $connection.Dispose()
}

[pscustomobject]@{
    XmlPath = [System.IO.Path]::GetFullPath($XmlPath)
    Table = $qualifiedTable
    ParsedRows = $parsedRows
    InsertedRows = $insertedRows
    UpdatedRows = $updatedRows
    Applied = -not $WhatIfPreference
}
