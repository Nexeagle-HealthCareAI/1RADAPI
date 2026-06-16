using System.Reflection;
using System.Text.Json;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// IN-PROCESS LanguageTool (Option A): calls the LanguageTool Java engine that
/// has been cross-compiled to a .NET assembly with IKVM, so grammar runs inside
/// this API process — no LLM, no separate server, no Docker, no third party.
///
/// Deliberately uses REFLECTION (no compile-time reference to the IKVM assembly)
/// so the solution builds even before the DLL is generated. It activates only
/// when LanguageTool:Mode = "embedded" AND the IKVM assembly loads at runtime;
/// otherwise IsConfigured is false and the controller falls back per config.
///
/// Config (section "LanguageTool"):
///   Mode          = "embedded"
///   AssemblyPath  = full path to the IKVM-generated DLL (e.g. .../bin/LanguageTool.dll),
///                   or just the assembly name if it's in the app's bin/ probing path.
///   Language      = "en-US" (default) or "en-GB"
///
/// See docs: LanguageTool_Embedded_IKVM_Setup.md for how to generate the DLL.
/// </summary>
public class EmbeddedLanguageToolService : ILanguageToolService
{
    private readonly ILogger<EmbeddedLanguageToolService> _logger;
    private readonly string _assemblyPath;
    private readonly string _languageClass;

    private readonly object _gate = new();
    private bool _initTried;
    private bool _ready;
    private object? _jlt;            // org.languagetool.JLanguageTool instance
    private MethodInfo? _checkM;     // JLanguageTool.check(String) -> java.util.List<RuleMatch>

    // Cached reflection handles, resolved off the first result objects.
    private MethodInfo? _listSize, _listGet;
    private MethodInfo? _getFromPos, _getToPos, _getMessage, _getSuggested, _getRule, _ruleIsSpelling;

    public EmbeddedLanguageToolService(IConfiguration configuration, ILogger<EmbeddedLanguageToolService> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("LanguageTool");
        _assemblyPath = (section["AssemblyPath"] ?? string.Empty).Trim().Trim('"');
        var lang = (section["Language"] ?? "en-US").Trim();
        _languageClass = lang.Equals("en-GB", StringComparison.OrdinalIgnoreCase)
            ? "org.languagetool.language.BritishEnglish"
            : "org.languagetool.language.AmericanEnglish";
    }

    public bool IsConfigured
    {
        get { EnsureInit(); return _ready; }
    }

    private void EnsureInit()
    {
        if (_initTried) return;
        lock (_gate)
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                var asm = LoadAssembly();
                var jltType = asm.GetType("org.languagetool.JLanguageTool", throwOnError: true)!;
                var langType = asm.GetType(_languageClass, throwOnError: true)!;
                var langObj = Activator.CreateInstance(langType);
                _jlt = Activator.CreateInstance(jltType, new[] { langObj! });

                // Prefer check(string); fall back to the single-arg overload.
                _checkM = jltType.GetMethods().FirstOrDefault(m =>
                              m.Name == "check" && m.GetParameters().Length == 1 &&
                              m.GetParameters()[0].ParameterType == typeof(string))
                          ?? jltType.GetMethods().First(m => m.Name == "check" && m.GetParameters().Length == 1);

                _ready = _jlt != null && _checkM != null;
                if (_ready)
                    _logger.LogInformation("Embedded LanguageTool loaded ({Lang}).", _languageClass);
            }
            catch (Exception ex)
            {
                _ready = false;
                _logger.LogError(ex, "Embedded LanguageTool failed to load — embedded grammar disabled.");
            }
        }
    }

    private Assembly LoadAssembly()
    {
        if (!string.IsNullOrWhiteSpace(_assemblyPath) && File.Exists(_assemblyPath))
            return Assembly.LoadFrom(_assemblyPath);
        var name = string.IsNullOrWhiteSpace(_assemblyPath)
            ? "LanguageTool"
            : Path.GetFileNameWithoutExtension(_assemblyPath);
        return Assembly.Load(name);   // must resolve from the app's probing path
    }

    public Task<string> CheckAsync(string text, string language, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(text ?? string.Empty), cancellationToken);

    private string Check(string text)
    {
        EnsureInit();
        if (!_ready || _jlt == null || _checkM == null)
            throw new InvalidOperationException("Embedded LanguageTool is not available.");

        // JLanguageTool is NOT thread-safe — serialise checks on the single instance.
        lock (_gate)
        {
            var resultList = _checkM.Invoke(_jlt, new object[] { text });
            var matches = new List<object>();
            if (resultList != null)
            {
                var listType = resultList.GetType();
                _listSize ??= Find(listType, "size", 0);
                _listGet ??= Find(listType, "get", 1);
                int n = Convert.ToInt32(_listSize!.Invoke(resultList, null));
                for (int i = 0; i < n; i++)
                {
                    var rm = _listGet!.Invoke(resultList, new object[] { i });
                    if (rm == null) continue;
                    var rmType = rm.GetType();
                    _getFromPos ??= Find(rmType, "getFromPos", 0);
                    _getToPos ??= Find(rmType, "getToPos", 0);
                    _getMessage ??= Find(rmType, "getMessage", 0);
                    _getSuggested ??= Find(rmType, "getSuggestedReplacements", 0);
                    _getRule ??= Find(rmType, "getRule", 0);

                    int from = Convert.ToInt32(_getFromPos!.Invoke(rm, null));
                    int to = Convert.ToInt32(_getToPos!.Invoke(rm, null));
                    var message = _getMessage?.Invoke(rm, null)?.ToString() ?? string.Empty;
                    var replacements = ReadStringList(_getSuggested?.Invoke(rm, null));

                    bool isSpelling = false;
                    var rule = _getRule?.Invoke(rm, null);
                    if (rule != null)
                    {
                        try
                        {
                            _ruleIsSpelling ??= rule.GetType().GetMethod("isDictionaryBasedSpellingRule", Type.EmptyTypes);
                            if (_ruleIsSpelling != null)
                                isSpelling = Convert.ToBoolean(_ruleIsSpelling.Invoke(rule, null));
                        }
                        catch { /* leave as grammar */ }
                    }

                    matches.Add(new
                    {
                        offset = from,
                        length = Math.Max(0, to - from),
                        message,
                        replacements = replacements.Select(v => new { value = v }).ToArray(),
                        rule = new { issueType = isSpelling ? "misspelling" : "grammar", id = "LT_EMBEDDED" }
                    });
                }
            }
            return JsonSerializer.Serialize(new { matches });
        }
    }

    private static MethodInfo Find(Type t, string name, int argc)
        => t.GetMethods().First(m => m.Name == name && m.GetParameters().Length == argc);

    private static List<string> ReadStringList(object? list)
    {
        var outp = new List<string>();
        if (list == null) return outp;
        var lt = list.GetType();
        var size = Find(lt, "size", 0);
        var get = Find(lt, "get", 1);
        int n = Convert.ToInt32(size.Invoke(list, null));
        for (int i = 0; i < n; i++)
        {
            var v = get.Invoke(list, new object[] { i });
            if (v != null) outp.Add(v.ToString()!);
        }
        return outp;
    }
}
