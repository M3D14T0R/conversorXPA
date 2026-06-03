# Spec-Driven Development

Documento operacional para reduzir alucinacao e manter o `XpaConverterMvp` ancorado em evidencias verificaveis.

## Objetivo

Garantir que qualquer alteracao no conversor:

- parta de uma fonte de verdade identificavel;
- minimize heuristica nao comprovada;
- preserve compatibilidade com o comportamento esperado da conversao XPA;
- seja validada de forma reprodutivel;
- nao derive para "correcoes locais" que se perdem na proxima reconversao.

## Escopo

Este documento cobre:

- parser e semantic builder;
- writer de models, tasks, views, menus, printing, textio e assets compartilhados;
- estrategia de compatibilidade para lacunas entre XPA e Firefly/ENV;
- fluxo de investigacao e validacao em projetos como `OnlineSample`, `Data` e `Exemplo2`.

Nao cobre:

- mudancas arbitrarias no `ENV` original;
- refactors estéticos sem evidência funcional;
- "melhorias" de API baseadas apenas em gosto.

## Fontes De Verdade

Ordem obrigatoria de prioridade:

1. XML do projeto XPA
- fonte principal para estrutura, semantica e intencao.
- exemplos: `TaskLogic`, `Invoke`, `SnippetCode`, `DataObject`, `ObjectType`, `DotNetObjectExists`, `Select`, `Link`, `Range`, `Indexes`, `Form`.

2. Conversao original oficial
- usada quando existir para a pasta/task correspondente.
- serve para confirmar a traducao esperada, nao para substituir o XML.

3. Runtime real disponivel
- `ENV` original
- `XPARuntimeCore.Box`
- `ENV/UserMethods.cs`
- fontes locais do framework usados pela conversao

4. Documentacao oficial
- help do XPA
- documentacao Firefly
- contratos publicos do runtime

5. Heuristica minima
- so pode entrar quando os itens 1 a 4 nao forem suficientes.
- deve ser explicitamente documentada e isolada.

## Fontes Documentais

As fontes documentais devem ser tratadas como parte operacional do SDD, nao como apoio opcional.

### Documentacao Magic xpa

Usar para definir a semantica original do XPA quando o XML sozinho nao bastar para fechar a traducao.

Fontes padrao:

- `D:\Magic\Help\index.htm`
- topicos internos do help, por exemplo:
  - `DNSet.htm`
  - topicos de funcoes `JSON*`
  - topicos de `SharedVal*`
  - topicos de `Rq*`

Uso correto:

- responder o que a funcao/comando XPA significa;
- confirmar assinatura conceitual;
- confirmar efeito colateral esperado;
- confirmar quando algo deve retornar valor, modificar parametro ou atuar sobre estado externo.

### Documentacao e fontes Firefly / ENV

Usar para definir o que existe no runtime alvo da conversao.

Fontes padrao:

- `C:\Users\mfsnh\Downloads\Firefly`
- `C:\Users\mfsnh\Downloads\Application\ENV`
- `ENV/UserMethods.cs`
- contratos publicos reais de `XPARuntimeCore.Box`, `ENV`, `ENV.Data`, `ENV.UI`, `ENV.Advanced`

Uso correto:

- confirmar se ja existe API equivalente;
- confirmar a assinatura CLR real;
- confirmar tipo esperado de parametro e retorno;
- confirmar se o runtime suporta o comportamento desejado sem camada adicional.

### Regra De Leitura Das Fontes

As fontes documentais respondem perguntas diferentes:

1. XML
- diz o que o projeto realmente declarou.

2. Help do XPA
- diz o que o recurso significa no mundo XPA.

3. Firefly / ENV
- diz o que existe ou nao existe no runtime alvo.

4. Conversao original
- mostra a traducao oficial quando ela existir.

Se houver lacuna entre XPA e runtime:

- nao inventar API no `ENV` original;
- nao mascarar no projeto convertido como solucao definitiva;
- decidir entre:
  - traducao C# nativa;
  - asset compartilhado gerado pelo conversor;
  - comentario/unsupported, quando essa for a estrategia do original.

## Regra De Citacao De Evidencia

Toda decisao nao trivial deve registrar pelo menos uma destas evidencias:

1. XML
2. conversao original
3. help do XPA
4. fonte Firefly / ENV

O ideal e registrar duas ou mais quando houver risco semantico.

Formato esperado nas analises:

1. qual foi a evidencia usada;
2. caminho do arquivo ou topico;
3. o que ela prova;
4. como isso impacta o conversor.

## Regra De Ouro

Toda traducao precisa responder:

1. Qual e a evidencia?
2. Onde ela esta?
3. O que exatamente ela prova?

Se essa resposta nao estiver clara, a implementacao ainda nao esta pronta.

## Politica De Runtime

### ENV

Regras fixas:

- o `ENV` original deve permanecer preservado;
- nao corrigir o conversor alterando o `ENV` original;
- se uma compatibilidade extra for inevitavel, ela deve ficar fora do `ENV` original ou em camada explicitamente separada.

### SQLite

SQLite e uma excecao aceita de compatibilidade.

Regra:

- suporte a SQLite pode existir;
- isso nao autoriza alterar o `ENV` original como fonte de verdade.

### Lacunas Do Runtime

Se o XPA possui uma funcao e o Firefly/ENV nao possui:

1. confirmar a lacuna no XML + runtime + docs;
2. decidir se:
- a traducao correta e C# nativo;
- e necessario gerar asset compartilhado;
- e necessario marcar como unsupported, como faz a conversao original em varios casos.

Exemplos ja validados:

- `DNSet(...)`: traduzir para atribuicao C# nativa sobre objeto .NET real.
- funcoes JSON XPA: camada compat separada (`JsonCompat`) gerada pelo conversor.
- `SharedValPack/SharedValUnPack`: quando nao houver suporte no runtime, seguir a estrategia do original e emitir comentario de incompatibilidade em vez de inventar implementacao.

## Politica De Heuristica

Heuristica so e aceita quando:

- o XML for insuficiente para a traducao final;
- nao existir original correspondente;
- o runtime/docs nao derem resposta direta;
- a heuristica for pequena, localizada e reversivel.

Toda heuristica deve obedecer:

1. ser o menor passo possivel;
2. ter comentario ou nome que revele sua natureza;
3. ser substituivel por regra baseada em evidencia quando ela aparecer;
4. nao se espalhar para dezenas de casos por conveniencia.

### Regra Estrutural Para Expressoes

Correcao de expressao com efeito semantico deve ser estrutural, nao textual.

Isto significa:

- preferir parser, semantic model ou emissao orientada a contexto;
- quando a expressao depende de significado de funcoes como `Case`, `VarCurr`, `VarPrev`, `VarPic`, `Str`, `DStr`, `VariantGet`, `JSONGet`, `SharedValGet` ou similares, a regra deve atuar sobre estrutura ou contexto semantico;
- regex ou `string replace` sobre C# ja emitido so podem ser usados para:
  - tokens lexicos estaveis da linguagem XPA;
  - qualificacao superficial de namespace/metodo;
  - saneamento sintatico sem alterar semantica.

Proibido como solucao definitiva:

- corrigir semantica de tipos por correspondencia textual fragil;
- depender de shape exato do C# gerado para inserir casts;
- hardcode para uma task, classe ou exemplo quando a variacao do XML for previsivel.

Se a solucao falha com uma variacao plausivel do mesmo XML, ela nao atende o SDD.

### Regra De Compatibilidade Entre Variantes De XML

Projetos diferentes podem representar o mesmo conceito com:

- nomes de elemento diferentes;
- propriedades alternativas;
- nomenclatura de versoes diferentes do XML.

Regra:

- quando duas variantes representarem a mesma intencao semantica, o conversor deve aceitar ambas;
- essa compatibilidade deve ser implementada no ponto canonico de leitura/normalizacao daquele conceito;
- o restante do pipeline deve trabalhar com uma unica representacao semantica interna.

Obrigatorio:

- manter suporte para a variante antiga quando a nova for adicionada, desde que nao haja conflito semantico;
- evitar espalhar `if/else` por task, writer ou caso especifico para cada nome alternativo;
- documentar precedencia apenas quando duas variantes puderem coexistir e gerar ambiguidade.

Violacao do SDD:

- corrigir um projeto substituindo uma variante antiga por outra mais nova;
- adicionar suporte a uma nomenclatura nova quebrando a antiga;
- tratar variantes equivalentes em pontos diferentes do conversor, sem normalizacao canonica.

### Regra De Centralizacao De Tipagem E Coercao Por Target

Coercao de valor orientada ao target deve partir de uma leitura semantica unica do destino.

Regra:

- o conversor deve manter uma estrutura canonica de descricao do destino;
- essa estrutura deve concentrar:
  - resource alvo;
  - atributo/model attribute resolvido;
  - member alvo;
  - flags semanticas do destino, como:
    - `IsDotNet`
    - `IsArray`
    - `IsBlob`
    - `IsNumeric`
    - `IsBoolean`

Implementacao esperada:

- a classificacao do target deve ocorrer em um ponto unico;
- no estado atual, esse papel e de `TargetValueInfo` + `ResolveTargetValueInfo(...)`.

Corolario obrigatorio:

- a coerção de valor para o target nao deve ser reimplementada em cada rota do writer;
- deve existir um funil canonico de coerção;
- no estado atual, esse papel e de `CoerceAssignmentValueForTarget(...)`.

Essa coerção unica deve ser reutilizada por:

1. `Update`
2. `BindValue`
3. `Evaluate return`
4. `Invoke return`

Objetivo:

- evitar que a mesma semantica de tipo seja corrigida em varios lugares;
- impedir que uma familia de erro seja resolvida em `Update` e continue escapando por `BindValue`, `Evaluate` ou `Invoke`;
- reduzir correcoes paralelas e duplicadas.

Regra de revisao:

- se um ajuste de tipo/coercao exigir alterar mais de um caminho do writer, isso e sinal de que a regra ainda nao esta centralizada o suficiente;
- nesses casos, a prioridade deve ser mover a regra para a estrutura canonica de target e para o funil unico de coerção.

### Regra De Mapeamento Canonico De Comandos Internos

Mapeamento de `InternalEventID` deve existir em uma unica fonte canonica.

Problema estrutural a evitar:

- `SemanticBuilder` e `CodeWriter` manterem tabelas diferentes de `InternalEventID`;
- isso faz o mesmo evento interno ser reconhecido em menus mas nao em handlers ou views;
- o efeito pratico e divergencia de comportamento e emissao de `GAP` artificial.

Regra:

- o projeto deve ter uma tabela canonica de mapeamento de comandos internos;
- `SemanticBuilder` e `CodeWriter` nao podem divergir semanticamente nesse mapeamento;
- toda ampliacao de `InternalEventID` deve atualizar a fonte canonica e seus consumidores, nao apenas um lado.

Consequencia operacional:

- quando um `InternalEventID` e comprovado por XML/original/runtime, ele deve deixar de ser resolvido por heuristica local e passar para o mapeamento canonico;
- fallback por texto do controle so e aceitavel como complemento seguro, nao como substituto da tabela canonica.

Sinais de violacao do SDD:

1. um menu resolve o comando, mas a view correspondente ainda emite `GAP`;
2. o `CodeWriter` conhece um `InternalEventID`, mas o `SemanticBuilder` nao;
3. o mesmo `InternalEventID` produz comandos diferentes em partes distintas do conversor.

Diretriz de manutencao:

- antes de adicionar novo fallback de `view event binding`, verificar se o `InternalEventID` ja esta mapeado em outro ponto do conversor;
- se estiver, reutilizar ou extrair esse mapeamento;
- se nao estiver, a nova entrada deve nascer como parte do mapeamento canonico.

### Regra De Fix Tatico

Patch tatico so e aceito para destravar prova local quando:

- estiver explicitamente marcado como temporario;
- houver evidencia de que nao e uma solucao estrutural;
- ficar registrado como pendencia obrigatoria de refactor no conversor.

Patch tatico nao pode ser tratado como "caso resolvido" no conversor.

Heuristicas proibidas:

- renomeacao global sem base no XML;
- conversoes de tipo "parece texto", "parece numero" sem uso real comprovado;
- adaptacoes no `ENV` para mascarar erro do writer;
- alterar arquivos convertidos e considerar isso a solucao final.
- fix semantico por regex textual sobre expressao C# achatada, quando a regra correta depende da estrutura da expressao.

## Fluxo Obrigatorio De Trabalho

### 1. Identificar o problema

Coletar:

- arquivo gerado com erro;
- mensagem de compilacao ou divergencia funcional;
- task/pasta afetada;
- contexto no XML;
- se existir, o arquivo correspondente da conversao original.

### 2. Classificar a causa

O problema deve ser classificado em uma destas familias:

- parser/modelo XML;
- semantic builder;
- writer;
- runtime/lacuna de compatibilidade;
- referencia externa/materializacao;
- caso ainda nao determinado.

### 3. Encontrar a evidencia

Procurar nesta ordem:

1. XML
2. original
3. runtime/docs
4. somente depois inferencia minima

### 4. Validar em recorte pequeno

Antes de reconverter projeto grande:

- usar conversao por task ou por pasta quando viavel;
- produzir probe isolado;
- comparar o artefato gerado com a evidencia;
- medir build local quando o recorte compilar.

### 5. Corrigir no conversor

Regra fixa:

- patch no convertido e apenas para acelerar prova;
- correcao definitiva sempre no conversor.

Arquivos tipicos:

- [ProjectGenerator.cs](/c:/Users/mfsnh/Downloads/Application/tools/XpaConverterMvp/ProjectGenerator.cs)
- [SemanticBuilder.cs](/c:/Users/mfsnh/Downloads/Application/tools/XpaConverterMvp/SemanticBuilder.cs)
- [XpaParser.cs](/c:/Users/mfsnh/Downloads/Application/tools/XpaConverterMvp/XpaParser.cs)
- [Domain.cs](/c:/Users/mfsnh/Downloads/Application/tools/XpaConverterMvp/Domain.cs)

### 6. Revalidar

Sequencia esperada:

1. build do conversor;
2. probe isolado;
3. patch pontual no convertido atual, se isso economizar tempo;
4. build do projeto convertido;
5. quando o lote estiver consolidado, reconversao limpa.

## Politica De Reconversao

Reconversao completa e apropriada quando:

- a correcao afeta geracao estrutural;
- a correcao muda model names, namespaces, assets compartilhados ou templates;
- multiplos patches locais comecam a divergir do que o conversor geraria.

Nao reconverter imediatamente quando:

- ainda estamos provando uma regra pequena;
- o XML grande custa muito tempo;
- um patch local rapido permite confirmar a direcao.

## Politica De Crescimento Seguro Do Conversor

O `XpaConverterMvp` deve ser tratado como base acumulativa para varios projetos XPA, nao como um conjunto de fixes independentes por XML.

Regra central:

- toda mudanca nova deve preservar a capacidade que ja foi conquistada em projetos anteriores;
- corrigir um projeto quebrando outro e regressao, nao progresso;
- suporte novo deve ser aditivo e compativel, nao substitutivo.

### Corpus Fixo De Regressao

O projeto deve manter um corpus estavel de validacao, com XMLs e projetos convertidos que representem familias diferentes de semantica.

Exemplos atuais:

- `OnlineSample`
- `Northwind`
- `Exemplo2`
- `Data`
- `Exemplo6`
- projetos grandes reais, como `CGData`

Esses projetos formam o contrato vivo do conversor.

Regra:

- toda mudanca estrutural relevante deve ser avaliada contra esse corpus;
- o corpus nao e opcional nem descartavel quando um projeto novo trouxer pressao de prazo.

### Compatibilidade Acumulativa

Quando dois projetos representarem o mesmo conceito por:

- nomes de elementos diferentes;
- propriedades diferentes;
- combinacoes diferentes de atributos;

o conversor deve:

1. manter o suporte anterior;
2. adicionar o suporte novo;
3. normalizar ambos para a mesma semantica interna.

Violacao do SDD:

- alterar o conversor para atender `ProjetoB` quebrando o `ProjetoA`;
- substituir uma variante antiga por uma nova sem prova de equivalencia plena;
- fazer a semantica variar conforme o projeto em vez de variar conforme o XML.

### Regressao Como Criterio De Rejeicao

Se uma mudanca:

- reduz erros em um projeto novo;
- mas reabre erros ou perdas semanticas em projetos ja estabilizados;

entao a mudanca ainda nao esta pronta.

Nesse caso, o caminho correto e:

1. estreitar a regra;
2. mover a regra para o ponto canonico certo;
3. adicionar guardas ou suporte acumulativo;
4. revalidar no corpus.

### Ordem Correta De Escalada

Para fazer o conversor crescer sem retrabalho:

1. identificar a familia estrutural do problema;
2. provar no XML/original/runtime;
3. corrigir no parser, semantic builder ou writer canonico;
4. validar em probe pequeno;
5. validar em corpus pequeno estavel;
6. so depois medir no projeto grande.

Isso evita:

- abrir varias frentes de uma vez;
- corrigir sintomas locais;
- reconverter projetos gigantes a cada iteracao;
- reintroduzir regressao por mudancas amplas demais.

### Areas De Alto Risco

Mudancas nestas areas exigem revalidacao cruzada obrigatoria:

- tipagem e coercao por target;
- blob escalar vs vetor;
- coercao de argumentos de `Run(...)`;
- normalizacao de expressao;
- naming e nomes reservados;
- bindings de view;
- mapeamento de `InternalEventID`;
- locate/range/start row.

Regra:

- quanto mais central e mais generica for a mudanca, maior e a obrigacao de revalidar outros projetos.

### Politica De No Silent Drop

Perda silenciosa de semantica funcional e uma regressao grave, mesmo quando o projeto compila.

Regra:

1. se a semantica funcional existe no XML e o conversor sabe emitir, deve emitir;
2. se ainda nao sabe emitir, deve marcar `GAP`;
3. o que nao pode acontecer e:
   - a semantica existir no XML;
   - nao ser emitida;
   - e nao aparecer nenhum indicativo no gerado.

Isso e especialmente obrigatorio para:

- propriedades funcionais de tree;
- bindings de subform;
- data bindings de controles;
- eventos de view;
- propriedades estruturais de browser/dotnet controls.

### Regra De Aceitacao De Mudanca

Uma mudanca no conversor so deve ser considerada consolidada quando:

1. resolve a familia alvo com evidencia estrutural;
2. nao quebra o corpus ja conquistado;
3. nao substitui uma compatibilidade antiga por outra nova;
4. nao deixa perda semantica silenciosa;
5. pode ser explicada em termos de regra geral do XML/runtime, e nao de um projeto especifico.

## Checklist Operacional Por Rodada

Este checklist deve ser usado antes de considerar uma rodada de ajuste como concluida.

### 1. Triagem

- identificar o baseline atual do projeto afetado;
- agrupar erros ou divergencias por familia estrutural;
- escolher apenas uma familia principal por rodada.

### 2. Evidencia

- localizar o trecho correspondente no XML;
- quando existir, localizar o original convertido/oficial;
- confirmar no runtime ou na documentacao o contrato esperado;
- registrar claramente o que a evidencia prova.

### 3. Ponto Canonico

- decidir se a correcao pertence a:
  - `XpaParser`
  - `SemanticBuilder`
  - `CodeWriter`
- evitar duplicar a mesma regra em dois pontos diferentes;
- se a regra ja existir em outro caminho, centralizar antes de ampliar.

### 4. Compatibilidade

- listar explicitamente que outros projetos ou familias podem ser afetados;
- verificar se a mudanca preserva variantes antigas do XML;
- adicionar suporte novo sem remover o suporte ja existente.

### 5. Implementacao

- fazer a menor mudanca estrutural suficiente;
- evitar fix local no gerado como solucao definitiva;
- se houver semantica funcional nao emitida, garantir emissao ou `GAP`.

### 6. Prova Local

- compilar o conversor;
- reconverter probe pequeno por task ou recorte minimo;
- verificar se o artefato gerado nasce correto;
- se aplicavel, sobrepor so o arquivo validado no projeto convertido para medir impacto.

### 7. Revalidacao Cruzada

- validar em pelo menos um corpus pequeno estavel;
- validar no projeto principal da rodada;
- se a mudanca tocar area de alto risco, ampliar a checagem para mais de um projeto.

### 8. Decisao

- se melhorou o alvo e nao reabriu corpus estavel, manter;
- se melhorou o alvo mas piorou outro projeto, refinar a regra;
- se houver drop silencioso, a rodada nao esta pronta.

### 9. Fechamento

- atualizar o SDD quando a regra estrutural for nova e consolidada;
- registrar o novo baseline;
- definir a proxima familia com maior retorno e menor risco.

## Invariantes Da Solucao

### Invariantes de compatibilidade

- o XML manda na intencao semantica;
- o original manda quando existe e contradiz uma inferencia;
- o runtime nao deve ser alterado para esconder erro do conversor;
- nomes fisicos podem mudar menos do que APIs publicas;
- `namespace ENV` e mais sensivel que nome visual de projeto.
- variacoes equivalentes de nomenclatura no XML devem convergir para a mesma semantica interna.
- suporte novo nao pode quebrar suporte antigo que ja estava validado em outro projeto.

### Invariantes do writer

- nao transformar expressao dinamica em valor imediato sem prova;
- nao embrulhar tudo em lambda por heuristica global;
- diferenciar:
  - valor imediato
  - expressao diferida
  - filter
  - bind
- `ref/out` devem respeitar o tipo CLR esperado do parametro do snippet;
- `FIELD_BLOB` com `ObjectType` nao e `ByteArrayColumn`, e objeto .NET.

### Invariantes de assets compartilhados

- assets como `JsonCompat` nao podem existir so no projeto convertido se a reconversao os apaga;
- se um asset e necessario em mais de um projeto, o conversor deve materializa-lo.

## Estrategias Ja Consolidadas

### 1. JSON

Situacao:

- Firefly/ENV nao expoe funcoes XPA equivalentes para `JSONInsert`, `JSONModify`, `JSONDelete`, `JSONFind`, `JSONExist`, `JSONGet`, `JSONCnt`.

Regra:

- gerar `JsonCompat` como asset compartilhado do projeto convertido;
- nao fingir que existe equivalente pronto no runtime.

### 2. DotNet resources

Situacao:

- `FIELD_BLOB` com `ObjectType` e `DotNetObjectExists=Y` representa variavel `.NET`, nao blob arbitrario.

Regra:

- gerar tipo .NET real;
- `DNSet(...)` vira atribuicao C# nativa;
- snippets e invocacoes .NET devem usar o tipo CLR correto.

### 3. SharedValPack / SharedValUnPack

Situacao:

- o original comenta esses trechos como funcao desconhecida;
- o runtime possui `SharedValGetText`, mas nao `SharedValPack/UnPack`.

Regra:

- emitir comentario de incompatibilidade;
- nao inventar implementacao;
- usar helper tipado para `SharedValGet`.

### 4. DataObjects com mesmo nome logico

Situacao:

- o XML pode ter dois `DataObject`s distintos com o mesmo nome e `PhysicalName`/XSD diferentes.

Regra:

- o tipo gerado deve ser desambiguado por objeto real;
- nao colapsar por nome apenas.

## Criterios De Aceite Por Correcao

Uma correcao so esta aceita quando:

1. a evidencia foi localizada;
2. a mudanca foi aplicada no conversor;
3. o conversor compilou;
4. houve validacao em probe ou build;
5. o impacto foi descrito de forma objetiva.

## Template De Registro De Correcao

Usar este formato em revisoes, PRs ou notas de trabalho:

### Problema
- arquivo/projeto afetado
- sintoma observado

### Evidencia
- XML:
- original:
- runtime/docs:

### Regra decidida
- o que o conversor deve passar a fazer

### Onde foi implementado
- parser:
- semantic builder:
- writer:
- asset compartilhado:

### Validacao
- probe:
- build:
- reconversao:

## Anti-Padroes

Evitar:

- "funcionou no build, entao esta certo";
- corrigir output gerado e esquecer de portar para o conversor;
- usar o original como unica fonte quando o XML diz outra coisa;
- usar a Firefly como desculpa para alterar a semantica do XPA;
- criar wrappers no runtime sem primeiro provar a necessidade.
- aceitar como definitivo um fix que depende de um unico shape textual da expressao gerada.

## Uso Pratico Em Sessao

Ao responder ou implementar, seguir sempre:

1. citar a fonte de verdade usada;
2. dizer se a regra veio de XML, original, docs ou runtime;
3. preferir patch pequeno e reversivel;
4. consolidar no conversor antes da proxima reconversao completa.

## Resultado Esperado

Se este documento for seguido, a tendencia e:

- menos alucinacao;
- menos regressao silenciosa;
- menos correcao manual perdida;
- maior previsibilidade em reconversoes completas;
- e uma solucao que evolui por evidencia, nao por tentativa e erro.

