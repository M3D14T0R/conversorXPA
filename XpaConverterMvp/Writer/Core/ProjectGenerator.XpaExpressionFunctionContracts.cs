using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private enum XpaFunctionArgumentContractUsage
    {
        SourceAnalysis,
        Emission
    }

    private static long _xpaFunctionContractReturnLookupCount;
    private static long _xpaFunctionContractReturnHitCount;
    private static long _xpaFunctionContractReturnCacheHitCount;
    private static long _xpaFunctionContractArgumentLookupCount;
    private static long _xpaFunctionContractArgumentHitCount;
    private static long _xpaFunctionContractArgumentCacheHitCount;
    private static long _xpaFunctionContractSourceArgumentLookupCount;
    private static long _xpaFunctionContractSourceArgumentHitCount;
    private static long _xpaFunctionContractSourceArgumentCacheHitCount;
    private static long _xpaFunctionContractEmissionArgumentLookupCount;
    private static long _xpaFunctionContractEmissionArgumentHitCount;
    private static long _xpaFunctionContractEmissionArgumentCacheHitCount;
    private static long _xpaFunctionContractNameCacheHitCount;
    private static readonly ConcurrentDictionary<string, string> NormalizedXpaFunctionContractNameCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> XpaFunctionReturnContractCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> XpaFunctionArgumentContractCache = new(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> KnownXpaFunctionReturnTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DNEXCEPTIONOCCURRED"] = "Bool",
            ["IMAGERELOAD"] = "Bool",
            ["STAT"] = "Bool",
            ["FILEEXIST"] = "Bool",
            ["CLIENTFILEEXIST"] = "Bool",
            ["FILEDELETE"] = "Bool",
            ["GETBOOLPARAM"] = "Bool",
            ["LOGGING"] = "Bool",
            ["IN"] = "Bool",
            ["NOT"] = "Bool",
            ["ISNULL"] = "Bool",
            ["RANGE"] = "Bool",
            ["ISDEFAULT"] = "Bool",
            ["ISCOMPONENT"] = "Bool",
            ["EOF"] = "Bool",
            ["EOP"] = "Bool",
            ["DBEXIST"] = "Bool",
            ["DBXMLEXIST"] = "Bool",
            ["XMLEXIST"] = "Bool",
            ["XMLVALIDATE"] = "Bool",
            ["INTRANS"] = "Bool",
            ["ISFIRSTRECORDCYCLE"] = "Bool",
            ["LOCK"] = "Number",
            ["BLB2FILE"] = "Bool",
            ["CLIENTBLB2FILE"] = "Bool",
            ["SETCRSR"] = "Bool",
            ["DRAGSETDATA"] = "Bool",
            ["DRAGSETCRSR"] = "Bool",
            ["DBDEL"] = "Bool",
            ["CNDCTRL"] = "Bool",
            ["CNDRANGE"] = "Bool",
            ["WINHWND"] = "Number",
            ["VECSET"] = "Bool",
            ["VARSET"] = "Bool",
            ["SETPARAM"] = "Bool",
            ["RANGEADD"] = "Bool",
            ["RANGEEXPADD"] = "Bool",
            ["LOCATEADD"] = "Bool",
            ["DIFDATETIME"] = "Bool",
            ["SORTADD"] = "Bool",
            ["TREENODEGOTO"] = "Bool",
            ["CASE"] = "object",
            ["CASEUNTYPED"] = "object",
            ["CASTTOTEXT"] = "Text",
            ["CASTTONUMBER"] = "Number",
            ["CASTTODATE"] = "Date",
            ["CASTTOTIME"] = "Time",
            ["CASTTOBOOL"] = "Bool",
            ["CASTTOBYTEARRAY"] = "byte[]",
            ["BYTEARRAYTOTEXT"] = "Text",
            ["TOTEXT"] = "Text",
            ["TONUMBER"] = "Number",
            ["TODATE"] = "Date",
            ["TOTIME"] = "Time",
            ["TOBOOL"] = "Bool",
            ["TOBYTEARRAY"] = "byte[]",
            ["RIGHTS"] = "Bool",
            ["BOM"] = "Date",
            ["EOM"] = "Date",

            ["DATAVIEWTODNDATATABLE"] = "System.Data.DataTable",
            ["DATAVIEWVARS"] = "Text[]",
            ["DATAVIEWVARSINDEX"] = "Number[]",
            ["FILELISTGET"] = "Text[]",
            ["CLIENTFILELISTGET"] = "Text[]",
            ["FILEINFO"] = "object",
            ["CLIENTFILEINFO"] = "object",
            ["HTTPGET"] = "object",
            ["HTTPPOST"] = "object",
            ["HTTPCALL"] = "object",
            ["CALLDLL"] = "object",
            ["CALLDLLF"] = "object",
            ["UDF"] = "object",
            ["VARCURR"] = "object",
            ["VARCURRN"] = "object",
            ["VARPREV"] = "object",
            ["VARIANTGET"] = "object",
            ["VECGET"] = "object",
            ["JGET"] = "object",
            ["JCALL"] = "object",
            ["JCALLSTATIC"] = "object",
            ["JAVACOMPAT.JGETSTATIC"] = "object",
            ["SHAREDVALGET"] = "object",
            ["NULL"] = "object",

            ["DATAVIEWTOTEXT"] = "Bool",
            ["DATAVIEWTOHTML"] = "Bool",
            ["DATAVIEWTOXML"] = "Bool",
            ["GETPARAM"] = "object",
            ["GETTEXTPARAM"] = "Text",
            ["GETPARAMATTR"] = "Text",
            ["GETPARAMNAMES"] = "Text",
            ["GETVARNAME"] = "Text",
            ["USER"] = "Text",
            ["TRIM"] = "Text",
            ["LTRIM"] = "Text",
            ["RTRIM"] = "Text",
            ["UPPER"] = "Text",
            ["LOWER"] = "Text",
            ["FLIP"] = "Text",
            ["LEFT"] = "Text",
            ["RIGHT"] = "Text",
            ["MID"] = "Text",
            ["STR"] = "Text",
            ["ASTR"] = "Text",
            ["DSTR"] = "Text",
            ["TSTR"] = "Text",
            ["MTSTR"] = "Text",
            ["REPSTR"] = "Text",
            ["STRTOKEN"] = "Text",
            ["STATUSBARSETTEXT"] = "Text",
            ["CLIPREAD"] = "Text",
            ["BROWSERGETCONTENT"] = "Text",
            ["HTTPLASTHEADER"] = "Text",
            ["ASCIICHR"] = "Text",
            ["HSTR"] = "Text",
            ["TRANSLATE"] = "Text",
            ["XMLGET"] = "Text",
            ["JSONGET"] = "Text",
            ["BLOBTOBASE64"] = "Text",
            ["JEXCEPTION"] = "Text",
            ["JEXCEPTIONTEXT"] = "Text",
            ["CIGAM.UTILS.CRYPTAFR.ENCRYPTSTRING"] = "Text",
            ["CIGAM.UTILS.CRYPTAFR.DECRYPTSTRING"] = "Text",
            ["CIGAM.UTILS.CRYPTAAU2A.ENCRYPT"] = "Text",
            ["CIGAM.AMBIENTE.MAGICSETTINGS.CHANGEPASSWORD"] = "Number",
            ["CIGAM.UTILS.MAIL.MAILBODY.PROCESSLINKS"] = "Text",
            ["SYSTEM.DRAWING.COLOR.FROMNAME"] = "System.Drawing.Color",
            ["SYSTEM.DRAWING.COLOR.FROMARGB"] = "System.Drawing.Color",
            ["ANSI2OEM"] = "Text",
            ["OEM2ANSI"] = "Text",
            ["UNICODEFROMANSI"] = "Text",
            ["UNICODETOANSI"] = "byte[]",
            ["UTF8FROMANSI"] = "byte[]",
            ["UTF8TOANSI"] = "byte[]",
            ["DEL"] = "Text",
            ["FILL"] = "Text",
            ["INS"] = "Text",
            ["REP"] = "Text",
            ["APPNAME"] = "Text",
            ["PROJECTDIR"] = "Text",
            ["PUBLICNAME"] = "Text",
            ["CDOW"] = "Text",
            ["CMONTH"] = "Text",
            ["GETCOMPONENTNAME"] = "Text",
            ["GETGUID"] = "Text",
            ["RQHTTPHEADER"] = "Text",
            ["RQRTINF"] = "Text",
            ["RQRTAPP"] = "Text",
            ["RQREQINF"] = "Text",
            ["RQRTCTX"] = "Text",
            ["RQLOAD"] = "Text",
            ["EDITGET"] = "object",
            ["MARKEDTEXTGET"] = "Text",
            ["INIGET"] = "Text",
            ["INIGETLN"] = "Text",
            ["OSENVGET"] = "Text",
            ["CTXGETNAME"] = "Text",
            ["GETLANG"] = "Text",
            ["DATAVIEWINDEXNAMES"] = "Text",
            ["DATAVIEWINDEXSEGMENTNAMES"] = "Text",
            ["VARNAME"] = "Text",
            ["VARDISPLAYNAME"] = "Text",
            ["VARPIC"] = "Text",
            ["VARDBNAME"] = "Text",
            ["ERRDATABASENAME"] = "Text",
            ["ERRDBMSMESSAGE"] = "Text",
            ["ERRMAGICNAME"] = "Text",
            ["ERRTABLENAME"] = "Text",
            ["MENUIDX"] = "Number",

            ["GETNUMBERPARAM"] = "Number",
            ["ABS"] = "Number",
            ["ACOS"] = "Number",
            ["ASIN"] = "Number",
            ["ATAN"] = "Number",
            ["COS"] = "Number",
            ["EXP"] = "Number",
            ["FIX"] = "Number",
            ["LOG"] = "Number",
            ["MOD"] = "Number",
            ["RAND"] = "Number",
            ["ROUND"] = "Number",
            ["SIN"] = "Number",
            ["TAN"] = "Number",
            ["VAL"] = "Number",
            ["LEN"] = "Number",
            ["INSTR"] = "Number",
            ["ASCIIVAL"] = "Number",
            ["COMHANDLEGET"] = "Number",
            ["STRNUM"] = "Number",
            ["STRTOKENCNT"] = "Number",
            ["STRTOKENIDX"] = "Number",
            ["DOW"] = "Number",
            ["YEAR"] = "Number",
            ["MONTH"] = "Number",
            ["DAY"] = "Number",
            ["HOUR"] = "Number",
            ["MINUTE"] = "Number",
            ["SECOND"] = "Number",
            ["NDOW"] = "Number",
            ["NMONTH"] = "Text",
            ["BOY"] = "Date",
            ["EOY"] = "Date",
            ["DBNAME"] = "Text",
            ["INDEX"] = "Number",
            ["TASKINSTANCE"] = "Number",
            ["VARINDEX"] = "Number",
            ["VARMOD"] = "Bool",
            ["RQRTTRM"] = "Bool",
            ["RQRTTRMEX"] = "Bool",
            ["RQRTRESUME"] = "Bool",
            ["RQRTBLOCK"] = "Bool",
            ["RQHTTPSTATUSCODE"] = "Bool",
            ["RQEXE"] = "Bool",
            ["VARINP"] = "Number",
            ["DBRECS"] = "Number",
            ["DBSIZE"] = "Number",
            ["DBVIEWROWIDX"] = "Number",
            ["DBVIEWSIZE"] = "Number",
            ["ERRDBMSCODE"] = "Number",
            ["ERRPOSITION"] = "Number",
            ["COUNTER"] = "Number",
            ["LOOPCOUNTER"] = "Number",
            ["MAINLEVEL"] = "Number",
            ["TDEPTH"] = "Number",
            ["TREELEVEL"] = "Number",
            ["TREEVALUE"] = "object",
            ["VECSIZE"] = "Number",
            ["RQRTS"] = "Number",
            ["RQRTAPPS"] = "Number",
            ["RQREQLST"] = "Number",
            ["RQRTCTXS"] = "Number",
            ["RQTRMTIMEOUT"] = "Number",
            ["RQQUELST"] = "object",
            ["RQSTAT"] = "Number",
            ["RQQUEDEL"] = "Number",
            ["RQQUEPRI"] = "Number",
            ["MAILCONNECT"] = "Number",
            ["MAILDISCONNECT"] = "Number",
            ["MAILMSGDEL"] = "Number",
            ["MAILBOXSET"] = "Number",
            ["MAILMSGFILES"] = "Number",
            ["MAILFILESAVE"] = "Number",
            ["MAILLASTRC"] = "Number",
            ["IOCURR"] = "Number",
            ["LINE"] = "Number",
            ["PAGE"] = "Number",
            ["MMCOUNT"] = "Number",
            ["MMCURR"] = "Number",
            ["JSONINSERT"] = "Number",
            ["XMLINSERT"] = "Number",
            ["JSONMODIFY"] = "Number",
            ["JSONDELETE"] = "Number",
            ["JSONFIND"] = "Number",
            ["JSONCNT"] = "Number",

            ["JSONEXIST"] = "Bool",

            ["MAILMSGDATE"] = "Text",
            ["MAILMSGFROM"] = "Text",
            ["MAILMSGTO"] = "Text",
            ["MAILMSGSUBJ"] = "Text",
            ["MAILMSGHEADER"] = "Text",
            ["MAILMSGCC"] = "Text",
            ["MAILMSGBCC"] = "Text",
            ["MAILMSGREPLYTO"] = "Text",
            ["MAILMSGTEXT"] = "Text",
            ["MAILMSGFILE"] = "Text",
            ["MAILERROR"] = "Text",

            ["DATE"] = "Date",
            ["DVAL"] = "Date",
            ["ADDDATE"] = "Date",
            ["MDATE"] = "Date",
            ["UTCDATE"] = "Date",

            ["TIME"] = "Time",
            ["TVAL"] = "Time",
            ["ADDTIME"] = "Time",
            ["MTIME"] = "Time",
            ["UTCMTIME"] = "Time",
            ["UTCTIME"] = "Time",

            ["FILE2BLB"] = "byte[]",
            ["BLOBFROMFILE"] = "byte[]"
        };

    private static readonly IReadOnlySet<string> KnownXpaStatementFunctionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SETPARAM",
            "SHAREDVALSET",
            "DENYUNDOFOR"
        };

    private static bool IsKnownXpaStatementFunction(string functionName)
    {
        var normalizedFunction = NormalizeXpaFunctionContractName(functionName);
        return !string.IsNullOrWhiteSpace(normalizedFunction) &&
               KnownXpaStatementFunctionNames.Contains(normalizedFunction);
    }

    private static bool TryResolveKnownXpaFunctionReturnType(
        string functionName,
        IReadOnlyList<string> args,
        out string returnType)
    {
        Interlocked.Increment(ref _xpaFunctionContractReturnLookupCount);
        returnType = "";
        var normalizedFunction = NormalizeXpaFunctionContractName(functionName);
        if (string.IsNullOrWhiteSpace(normalizedFunction))
            return false;

        var cacheKey = BuildXpaFunctionReturnContractCacheKey(normalizedFunction, args);
        if (XpaFunctionReturnContractCache.TryGetValue(cacheKey, out var cachedReturnType))
        {
            Interlocked.Increment(ref _xpaFunctionContractReturnCacheHitCount);
            if (string.IsNullOrWhiteSpace(cachedReturnType))
                return false;

            returnType = cachedReturnType;
            Interlocked.Increment(ref _xpaFunctionContractReturnHitCount);
            return true;
        }

        if ((string.Equals(normalizedFunction, "FILEINFO", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalizedFunction, "CLIENTFILEINFO", StringComparison.OrdinalIgnoreCase)) &&
            args.Count >= 2 &&
            TryGetLiteralNumberValue(args[1], out var fileInfoType))
        {
            returnType = fileInfoType switch
            {
                1 or 2 or 3 or 4 => "Text",
                5 => "Number",
                6 or 8 or 10 => "Date",
                7 or 9 or 11 => "Time",
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(returnType))
            {
                XpaFunctionReturnContractCache.TryAdd(cacheKey, "");
                return false;
            }

            XpaFunctionReturnContractCache.TryAdd(cacheKey, returnType);
            Interlocked.Increment(ref _xpaFunctionContractReturnHitCount);
            return true;
        }

        if (normalizedFunction.EndsWith(".RUNBYPUBLICNAME", StringComparison.OrdinalIgnoreCase))
        {
            returnType = "object";
            XpaFunctionReturnContractCache.TryAdd(cacheKey, returnType);
            Interlocked.Increment(ref _xpaFunctionContractReturnHitCount);
            return true;
        }

        if (IsJavaGetStaticFunctionName(normalizedFunction) &&
            args.Count >= 2 &&
            TryResolveJavaSignatureReturnType(args[1], out returnType))
        {
            XpaFunctionReturnContractCache.TryAdd(cacheKey, returnType);
            Interlocked.Increment(ref _xpaFunctionContractReturnHitCount);
            return true;
        }

        if (!KnownXpaFunctionReturnTypes.TryGetValue(normalizedFunction, out var knownReturnType) ||
            string.IsNullOrWhiteSpace(knownReturnType))
        {
            XpaFunctionReturnContractCache.TryAdd(cacheKey, "");
            return false;
        }

        returnType = knownReturnType;
        XpaFunctionReturnContractCache.TryAdd(cacheKey, returnType);
        Interlocked.Increment(ref _xpaFunctionContractReturnHitCount);
        return true;
    }

    private static bool TryResolveJavaSignatureReturnType(string signatureExpression, out string returnType)
    {
        returnType = "";
        if (!TryGetWholeCSharpStringLiteralValue(signatureExpression, out var signature) ||
            string.IsNullOrWhiteSpace(signature))
            return false;

        var normalized = signature.Trim();
        returnType = normalized switch
        {
            "Z" => "Bool",
            "B" or "S" or "I" or "J" or "F" or "D" => "Number",
            "C" => "Text",
            "Ljava/lang/String;" => "Text",
            _ => ""
        };

        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryGetWholeCSharpStringLiteralValue(string expression, out string value)
    {
        value = "";
        if (!TryGetWholeCSharpStringLiteral(expression, out var literalCode))
            return false;

        if (literalCode.StartsWith("@\"", StringComparison.Ordinal) &&
            literalCode.EndsWith("\"", StringComparison.Ordinal) &&
            literalCode.Length >= 3)
        {
            value = literalCode[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            return true;
        }

        if (!literalCode.StartsWith("\"", StringComparison.Ordinal) ||
            !literalCode.EndsWith("\"", StringComparison.Ordinal) ||
            literalCode.Length < 2)
            return false;

        var sb = new System.Text.StringBuilder(literalCode.Length);
        for (var i = 1; i < literalCode.Length - 1; i++)
        {
            var ch = literalCode[i];
            if (ch != '\\' || i + 1 >= literalCode.Length - 1)
            {
                sb.Append(ch);
                continue;
            }

            var escaped = literalCode[++i];
            sb.Append(escaped switch
            {
                '"' => '"',
                '\\' => '\\',
                '0' => '\0',
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                _ => escaped
            });
        }

        value = sb.ToString();
        return true;
    }

    private static bool IsJavaGetStaticFunctionName(string normalizedFunction)
        => string.Equals(normalizedFunction, "JGETSTATIC", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(normalizedFunction, "JAVACOMPAT.JGETSTATIC", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveXpaFunctionSourceArgumentReturnTypeContract(
        string functionName,
        int argumentIndex,
        int argumentCount,
        out string returnType)
        => TryResolveXpaFunctionArgumentReturnTypeContract(
            functionName,
            argumentIndex,
            argumentCount,
            XpaFunctionArgumentContractUsage.SourceAnalysis,
            out returnType);

    private static bool TryResolveXpaFunctionEmissionArgumentReturnTypeContract(
        string functionName,
        int argumentIndex,
        int argumentCount,
        out string returnType)
        => TryResolveXpaFunctionArgumentReturnTypeContract(
            functionName,
            argumentIndex,
            argumentCount,
            XpaFunctionArgumentContractUsage.Emission,
            out returnType);

    private static bool TryResolveXpaFunctionArgumentReturnTypeContract(
        string functionName,
        int argumentIndex,
        int argumentCount,
        XpaFunctionArgumentContractUsage usage,
        out string returnType)
    {
        _ = argumentCount;
        Interlocked.Increment(ref _xpaFunctionContractArgumentLookupCount);
        IncrementXpaFunctionArgumentContractLookup(usage);
        returnType = "";
        var normalizedFunction = NormalizeXpaFunctionContractName(functionName);
        if (string.IsNullOrWhiteSpace(normalizedFunction))
            return false;

        var cacheKey = BuildXpaFunctionArgumentContractCacheKey(normalizedFunction, argumentIndex, argumentCount, usage);
        if (XpaFunctionArgumentContractCache.TryGetValue(cacheKey, out var cachedReturnType))
        {
            Interlocked.Increment(ref _xpaFunctionContractArgumentCacheHitCount);
            IncrementXpaFunctionArgumentContractCacheHit(usage);
            if (string.IsNullOrWhiteSpace(cachedReturnType))
                return false;

            returnType = cachedReturnType;
            Interlocked.Increment(ref _xpaFunctionContractArgumentHitCount);
            IncrementXpaFunctionArgumentContractHit(usage);
            return true;
        }

        returnType = normalizedFunction switch
        {
            "STR" when argumentIndex == 0 => "Number",
            "ASTR" when argumentIndex is 0 or 1 => "Text",
            "DSTR" when argumentIndex == 0 => "Date",
            "TSTR" when argumentIndex == 0 => "Time",
            "NOT" when argumentIndex == 0 => "Bool",
            "KBPUT" when argumentIndex == 0 => "Text",
            "DBNAME" when argumentIndex is 0 or 1 => "Number",

            "TRIM" or "LTRIM" or "RTRIM" or "UPPER" or "LOWER" or "FLIP" or "LEN"
                when argumentIndex == 0 => "Text",

            "DRAGSETDATA" when argumentIndex is 0 or 2 => "Text",
            "DRAGSETDATA" when argumentIndex == 1 => "Number",
            "DRAGSETCRSR" when argumentIndex == 0 => "Number",
            "DRAGSETCRSR" when argumentIndex == 1 => "Text",

            "DIFDATETIME" when argumentIndex is 0 or 2 => "Date",
            "DIFDATETIME" when argumentIndex is 1 or 3 => "Time",
            "DIFDATETIME" when argumentIndex is 4 or 5 => "Number",
            "ADDDATE" when argumentIndex == 0 => "Date",
            "ADDDATE" when argumentIndex is 1 or 2 or 3 => "Number",
            "CDOW" or "CMONTH" when argumentIndex == 0 => "Date",
            "CTRLGOTO" when argumentIndex == 0 => "Text",
            "CTRLGOTO" when argumentIndex is 1 or 2 => "Number",
            "USER" when argumentIndex == 0 => "Number",

            "FILEINFO" when argumentIndex == 0 => "Text",
            "FILEINFO" when argumentIndex == 1 => "Number",
            "FILELISTGET" or "CLIENTFILELISTGET" when argumentIndex is 0 or 1 => "Text",
            "FILELISTGET" or "CLIENTFILELISTGET" when argumentIndex == 2 => "Bool",
            "FILEEXIST" or "FILE2BLB" when argumentIndex == 0 => "Text",
            "IMAGERELOAD" when argumentIndex == 0 => "Text",
            "BLB2FILE" when argumentIndex == 0 => "byte[]",
            "BLB2FILE" when argumentIndex == 1 => "Text",
            "BLOBTOBASE64" when argumentIndex == 0 => "byte[]",

            "MAILCONNECT" when argumentIndex == 0 => "Number",
            "MAILCONNECT" when argumentIndex >= 1 && argumentIndex <= 3 => "Text",
            "MAILDISCONNECT" when argumentIndex == 0 => "Number",
            "MAILDISCONNECT" when argumentIndex == 1 => "Bool",
            "MAILMSGDATE" or "MAILMSGFROM" or "MAILMSGTO" or "MAILMSGSUBJ" or
            "MAILMSGCC" or "MAILMSGBCC" or "MAILMSGREPLYTO" or "MAILMSGTEXT" or
            "MAILMSGFILES" when argumentIndex == 0 => "Number",
            "MAILMSGHEADER" when argumentIndex == 0 => "Number",
            "MAILMSGHEADER" when argumentIndex == 1 => "Text",
            "MAILMSGFILE" when argumentIndex is 0 or 1 => "Number",
            "MAILFILESAVE" when argumentIndex is 0 or 1 => "Number",
            "MAILFILESAVE" when argumentIndex == 2 => "Text",
            "MAILFILESAVE" when argumentIndex == 3 => "Bool",
            "MAILERROR" when argumentIndex == 0 => "Number",

            "RQRTS" when argumentIndex is 0 or 1 => "Text",
            "RQRTINF" when argumentIndex == 0 => "Text",
            "RQRTINF" when argumentIndex == 1 => "Number",
            "RQRTAPPS" when argumentIndex == 0 => "Text",
            "RQRTAPPS" when argumentIndex == 1 => "Number",
            "RQRTAPPS" when argumentIndex == 2 => "Text",
            "RQRTAPP" when argumentIndex == 0 => "Text",
            "RQRTAPP" when argumentIndex == 1 => "Number",
            "RQRTCTXS" when argumentIndex == 0 => "Text",
            "RQRTCTXS" when argumentIndex == 1 => "Number",
            "RQRTCTX" when argumentIndex == 0 => "Text",
            "RQRTCTX" when argumentIndex == 1 => "Number",
            "RQRTTRM" or "RQRTRESUME" or "RQRTBLOCK"
                when argumentIndex == 0 => "Text",
            "RQRTTRM" or "RQRTRESUME" or "RQRTBLOCK"
                when argumentIndex == 1 => "Number",
            "RQRTTRM" or "RQRTRESUME" or "RQRTBLOCK"
                when argumentIndex == 2 => "Text",
            "RQRTBLOCK" when argumentIndex == 3 => "Bool",
            "RQRTTRMEX" when argumentIndex == 0 => "Text",
            "RQRTTRMEX" when argumentIndex == 1 => "Number",
            "RQRTTRMEX" when argumentIndex == 2 => "Text",
            "RQRTTRMEX" when argumentIndex == 3 => "Number",
            "RQREQLST" when argumentIndex is 0 or 1 or 2 or 3 => "Text",
            "RQREQINF" when argumentIndex == 0 => "Text",
            "RQREQINF" when argumentIndex == 1 => "Number",
            "RQQUELST" when argumentIndex is 0 or 1 => "Text",
            "RQQUEDEL" when argumentIndex is 0 or 1 or 2 => "Text",
            "RQQUEPRI" when argumentIndex is 0 or 1 or 3 => "Text",
            "RQQUEPRI" when argumentIndex == 2 => "Number",
            "RQSTAT" when argumentIndex is 0 or 1 or 2 => "Text",
            "RQLOAD" when argumentIndex is 0 or 1 => "Text",
            "RQEXE" when argumentIndex is 0 or 1 or 2 or 3 => "Text",
            "RQHTTPHEADER" when argumentIndex == 0 => "Text",
            "RQHTTPSTATUSCODE" when argumentIndex == 0 => "Number",
            "RQHTTPSTATUSCODE" when argumentIndex == 1 => "Text",

            "VAL" when argumentIndex is 0 or 1 => "Text",
            "DVAL" when argumentIndex is 0 or 1 => "Text",
            "TVAL" when argumentIndex is 0 or 1 => "Text",
            "DBNAME" when argumentIndex is 0 or 1 => "Number",
            "ABS" or "ACOS" or "ASIN" or "ATAN" or "COS" or "EXP" or "LOG" or "SIN" or "TAN"
                when argumentIndex == 0 => "Number",
            "MOD" when argumentIndex is 0 or 1 => "Number",
            "ROUND" when argumentIndex >= 0 && argumentIndex <= 2 => "Number",
            "FIX" when argumentIndex >= 0 && argumentIndex <= 2 => "Number",
            "MTSTR" when argumentIndex == 0 => "Number",
            "MTSTR" when argumentIndex == 1 => "Text",
            "HSTR" when argumentIndex == 0 => "Number",
            "COMHANDLEGET" when argumentIndex == 0 => "XPARuntimeCore.Box.Data.Advanced.ColumnBase",
            "REPSTR" when argumentIndex >= 0 && argumentIndex <= 2 => "Text",
            "DATAVIEWTODNDATATABLE" when usage == XpaFunctionArgumentContractUsage.SourceAnalysis &&
                                       argumentIndex == 0 => "Number",
            "DATAVIEWTODNDATATABLE" when usage == XpaFunctionArgumentContractUsage.SourceAnalysis &&
                                       argumentIndex is 1 or 2 => "Text",
            "DATAVIEWTOTEXT" when argumentIndex == 0 =>
                "Number",
            "DATAVIEWTOTEXT" when argumentIndex >= 1 && argumentIndex <= 5 =>
                "Text",
            "DATAVIEWTOTEXT" when argumentIndex == 6 =>
                "Number",
            "CNDRANGE" when usage == XpaFunctionArgumentContractUsage.SourceAnalysis &&
                            argumentIndex == 0 => "Bool",
            "IF" when argumentIndex == 0 => "Bool",
            "DATAVIEWTOHTML" when argumentIndex == 0 =>
                "Number",
            "DATAVIEWTOHTML" when argumentIndex >= 1 && argumentIndex <= 4 =>
                "Text",
            "DATAVIEWTOHTML" when argumentIndex == 5 =>
                "Number",
            "DATAVIEWTOXML" when argumentIndex == 0 =>
                "Number",
            "DATAVIEWTOXML" when argumentIndex >= 1 && argumentIndex <= 5 =>
                "Text",
            "DATAVIEWTOXML" when argumentIndex == 6 =>
                "Number",
            "NOT" when usage == XpaFunctionArgumentContractUsage.SourceAnalysis &&
                       argumentIndex == 0 => "Bool",
            "RIGHTS" when argumentIndex == 0 => "Text",
            "INSTR" when argumentIndex is 0 or 1 => "Text",
            "TRANSLATENR" when argumentIndex == 0 => "Text",
            "FILEEXIST" or "CLIENTFILEEXIST"
                when usage == XpaFunctionArgumentContractUsage.SourceAnalysis &&
                     argumentIndex == 0 => "Text",
            "GETPARAM" or "GETTEXTPARAM"
                when usage == XpaFunctionArgumentContractUsage.SourceAnalysis &&
                     argumentIndex == 0 => "Text",
            "SETPARAM" when argumentIndex == 0 => "Text",
            "STATUSBARSETTEXT" when argumentIndex == 0 => "Text",
            "VARCURR" when argumentIndex == 0 => "Number",
            "VARCURRN" when argumentIndex == 0 => "Text",
            "VARPREV" when argumentIndex == 0 => "Number",
            "VARSET" when argumentIndex == 0 => "Number",
            "SHAREDVALGET" when argumentIndex == 0 => "Text",
            "ASCIICHR" when argumentIndex == 0 => "Number",
            "TRANSLATE" when argumentIndex >= 0 && argumentIndex <= 2 => "Text",
            "STRBUILD" when argumentIndex >= 0 => "Text",

            "DOW" when argumentIndex == 0 => "Date",
            "YEAR" or "MONTH" or "DAY" or "BOY" or "EOY" when argumentIndex == 0 => "Date",

            "LEFT" or "RIGHT" when argumentIndex == 0 => "Text",
            "LEFT" or "RIGHT" when argumentIndex == 1 => "Number",
            "MID" when argumentIndex == 0 => "Text",
            "MID" when argumentIndex is 1 or 2 => "Number",
            "DEL" when argumentIndex is 1 or 2 => "Number",
            "INS" when argumentIndex is 0 or 1 => "Text",
            "INS" when argumentIndex is 2 or 3 => "Number",
            "TREEVALUE" when argumentIndex == 0 => "Number",
            "UTF8FROMANSI" when argumentIndex == 0 => "Text",
            "UTF8TOANSI" when argumentIndex == 0 => "byte[]",
            "UNICODEFROMANSI" when argumentIndex == 0 => "byte[]",
            "UNICODETOANSI" when argumentIndex == 0 => "Text",
            "UNICODETOANSI" when argumentIndex == 1 => "Number",

            "STRTOKEN" when argumentIndex is 0 or 2 => "Text",
            "STRTOKEN" when argumentIndex == 1 => "Number",
            "STRTOKENCNT" when argumentIndex is 0 or 1 => "Text",
            "STRTOKENIDX" when argumentIndex is 0 or 1 or 2 => "Text",

            "XMLINSERT" when argumentIndex is 0 or 1 => "Number",
            "XMLINSERT" when argumentIndex >= 2 && argumentIndex <= 4 => "Text",

            "JSONINSERT" or "JSONMODIFY" or "JSONDELETE" or "JSONEXIST" or "JSONGET" or "JSONCNT" or "JSONFIND"
                when argumentIndex == 0 => "Number",
            "JSONINSERT" when argumentIndex is 1 or 2 => "Text",
            "JSONMODIFY" or "JSONDELETE" or "JSONEXIST" or "JSONGET" or "JSONCNT"
                when argumentIndex == 1 => "Text",
            "JSONFIND" when argumentIndex is 1 or 2 => "Text",
            "JSONFIND" when argumentIndex == 4 => "Number",

            "BUFSETALPHA" or "BUFSETNUM" or "BUFSETDATE" or "BUFSETTIME" or "BUFSETLOG" or
            "BUFGETALPHA" or "BUFGETNUM" or "BUFGETDATE" or "BUFGETTIME" or "BUFGETLOG" or
            "BUFSETBLOB" or "BUFGETBLOB" or "BUFSETVARIANT" or "BUFGETVARIANT"
                when argumentIndex == 0 => "Number",

            "CIGAM.UTILS.CRYPTAAU2A.ENCRYPT" when argumentIndex == 0 => "Text",
            "CIGAM.UTILS.CRYPTAAU2A.ENCRYPT" when argumentIndex == 1 => "Number",
            "SYSTEM.DRAWING.COLOR.FROMNAME" when argumentIndex == 0 => "Text",
            "SYSTEM.DRAWING.COLOR.FROMARGB" when argumentIndex >= 0 && argumentIndex <= 3 => "Number",
            "SYSTEM.WINDOWS.FORMS.FORM.FROMHANDLE" when argumentIndex == 0 => "System.IntPtr",
            "FORM.FROMHANDLE" when argumentIndex == 0 => "System.IntPtr",
            "GETVARNAME" when argumentIndex == 0 => "Number",
            "GETVARNAME" when argumentIndex == 1 => "Text",
            "GETVARNAME" when argumentIndex == 2 => "byte[]",

            _ => ""
        };

        if (string.IsNullOrWhiteSpace(returnType))
        {
            XpaFunctionArgumentContractCache.TryAdd(cacheKey, "");
            return false;
        }

        XpaFunctionArgumentContractCache.TryAdd(cacheKey, returnType);
        Interlocked.Increment(ref _xpaFunctionContractArgumentHitCount);
        IncrementXpaFunctionArgumentContractHit(usage);
        return true;
    }

    private static void IncrementXpaFunctionArgumentContractLookup(XpaFunctionArgumentContractUsage usage)
    {
        if (usage == XpaFunctionArgumentContractUsage.SourceAnalysis)
            Interlocked.Increment(ref _xpaFunctionContractSourceArgumentLookupCount);
        else
            Interlocked.Increment(ref _xpaFunctionContractEmissionArgumentLookupCount);
    }

    private static void IncrementXpaFunctionArgumentContractHit(XpaFunctionArgumentContractUsage usage)
    {
        if (usage == XpaFunctionArgumentContractUsage.SourceAnalysis)
            Interlocked.Increment(ref _xpaFunctionContractSourceArgumentHitCount);
        else
            Interlocked.Increment(ref _xpaFunctionContractEmissionArgumentHitCount);
    }

    private static void IncrementXpaFunctionArgumentContractCacheHit(XpaFunctionArgumentContractUsage usage)
    {
        if (usage == XpaFunctionArgumentContractUsage.SourceAnalysis)
            Interlocked.Increment(ref _xpaFunctionContractSourceArgumentCacheHitCount);
        else
            Interlocked.Increment(ref _xpaFunctionContractEmissionArgumentCacheHitCount);
    }

    private static string BuildXpaFunctionReturnContractCacheKey(string normalizedFunction, IReadOnlyList<string> args)
    {
        if ((string.Equals(normalizedFunction, "FILEINFO", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalizedFunction, "CLIENTFILEINFO", StringComparison.OrdinalIgnoreCase)) &&
            args.Count >= 2)
            return normalizedFunction + "|" + args[1].Trim();

        return normalizedFunction;
    }

    private static string BuildXpaFunctionArgumentContractCacheKey(
        string normalizedFunction,
        int argumentIndex,
        int argumentCount,
        XpaFunctionArgumentContractUsage usage)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{usage}|{normalizedFunction}|{argumentIndex}|{argumentCount}");

    private static string NormalizeXpaFunctionContractName(string functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return "";

        if (NormalizedXpaFunctionContractNameCache.TryGetValue(functionName, out var cached))
        {
            Interlocked.Increment(ref _xpaFunctionContractNameCacheHitCount);
            return cached;
        }

        var normalized = functionName.Trim();
        if (normalized.StartsWith("u.", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        if (normalized.StartsWith("DotNet.", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["DotNet.".Length..];
        if (normalized.StartsWith("UserMethods.", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["UserMethods.".Length..];
        if (normalized.StartsWith("JavaCompat.", StringComparison.OrdinalIgnoreCase))
            normalized = "JavaCompat." + normalized["JavaCompat.".Length..];

        normalized = normalized.ToUpperInvariant();
        NormalizedXpaFunctionContractNameCache.TryAdd(functionName, normalized);
        return normalized;
    }

    private static void ResetXpaFunctionContractTelemetryCounters()
    {
        Interlocked.Exchange(ref _xpaFunctionContractReturnLookupCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractReturnHitCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractReturnCacheHitCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractArgumentLookupCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractArgumentHitCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractArgumentCacheHitCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractSourceArgumentLookupCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractSourceArgumentHitCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractSourceArgumentCacheHitCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractEmissionArgumentLookupCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractEmissionArgumentHitCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractEmissionArgumentCacheHitCount, 0);
        Interlocked.Exchange(ref _xpaFunctionContractNameCacheHitCount, 0);
    }

    private static void LogXpaFunctionContractTelemetrySummary()
    {
        var returnLookups = Interlocked.Read(ref _xpaFunctionContractReturnLookupCount);
        var returnHits = Interlocked.Read(ref _xpaFunctionContractReturnHitCount);
        var returnCacheHits = Interlocked.Read(ref _xpaFunctionContractReturnCacheHitCount);
        var argumentLookups = Interlocked.Read(ref _xpaFunctionContractArgumentLookupCount);
        var argumentHits = Interlocked.Read(ref _xpaFunctionContractArgumentHitCount);
        var argumentCacheHits = Interlocked.Read(ref _xpaFunctionContractArgumentCacheHitCount);
        var sourceArgumentLookups = Interlocked.Read(ref _xpaFunctionContractSourceArgumentLookupCount);
        var sourceArgumentHits = Interlocked.Read(ref _xpaFunctionContractSourceArgumentHitCount);
        var sourceArgumentCacheHits = Interlocked.Read(ref _xpaFunctionContractSourceArgumentCacheHitCount);
        var emissionArgumentLookups = Interlocked.Read(ref _xpaFunctionContractEmissionArgumentLookupCount);
        var emissionArgumentHits = Interlocked.Read(ref _xpaFunctionContractEmissionArgumentHitCount);
        var emissionArgumentCacheHits = Interlocked.Read(ref _xpaFunctionContractEmissionArgumentCacheHitCount);
        var nameCacheHits = Interlocked.Read(ref _xpaFunctionContractNameCacheHitCount);
        if (returnLookups == 0 &&
            returnHits == 0 &&
            returnCacheHits == 0 &&
            argumentLookups == 0 &&
            argumentHits == 0 &&
            argumentCacheHits == 0 &&
            sourceArgumentLookups == 0 &&
            sourceArgumentHits == 0 &&
            sourceArgumentCacheHits == 0 &&
            emissionArgumentLookups == 0 &&
            emissionArgumentHits == 0 &&
            emissionArgumentCacheHits == 0 &&
            nameCacheHits == 0)
            return;

        ConversionTelemetry.Log(
            "XPA_FUNCTION_CONTRACT",
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"returnLookups={returnLookups} returnHits={returnHits} returnCacheHits={returnCacheHits} argumentLookups={argumentLookups} argumentHits={argumentHits} argumentCacheHits={argumentCacheHits} sourceArgumentLookups={sourceArgumentLookups} sourceArgumentHits={sourceArgumentHits} sourceArgumentCacheHits={sourceArgumentCacheHits} emissionArgumentLookups={emissionArgumentLookups} emissionArgumentHits={emissionArgumentHits} emissionArgumentCacheHits={emissionArgumentCacheHits} nameCacheHits={nameCacheHits}"));
    }
}
