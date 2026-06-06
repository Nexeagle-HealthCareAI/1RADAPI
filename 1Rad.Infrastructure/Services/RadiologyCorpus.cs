using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Loads the RadLex + curated radiology term corpus and the wrong→correct map
/// from Resources/Radiology (copied beside the app). Singleton — built once on
/// first use. See <see cref="IRadiologyCorpus"/>.
/// </summary>
public sealed class RadiologyCorpus : IRadiologyCorpus
{
    private readonly ILogger<RadiologyCorpus> _logger;
    private readonly object _gate = new();
    private bool _loaded;
    private bool _available;

    private HashSet<string> _terms = new(StringComparer.Ordinal);
    private string[] _sorted = Array.Empty<string>();
    private Dictionary<char, List<string>> _byFirst = new();
    private Dictionary<string, string> _corrections = new(StringComparer.Ordinal);

    // Letter-only protected words (numbers/units are caught separately by the
    // digit check, so they aren't even tokenised by the spell pass).
    private static readonly Regex ProtectedWord = new(
        @"^(?:left|right|bilateral|no|not|without|absent|nil|none|flair|dwi|adc|stir)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RadiologyCorpus(ILogger<RadiologyCorpus> logger) => _logger = logger;

    public bool IsAvailable { get { EnsureLoaded(); return _available; } }

    public bool IsTerm(string word)
    {
        EnsureLoaded();
        return !string.IsNullOrWhiteSpace(word) && _terms.Contains(word.Trim().ToLowerInvariant());
    }

    public string? Correction(string word)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(word)) return null;
        return _corrections.TryGetValue(word.Trim().ToLowerInvariant(), out var fix) ? fix : null;
    }

    public bool IsProtected(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;
        var t = token.Trim();
        return Regex.IsMatch(t, @"\d") || ProtectedWord.IsMatch(t);
    }

    public string? NearestTerm(string word, int maxDistance)
    {
        EnsureLoaded();
        if (!_available || string.IsNullOrWhiteSpace(word)) return null;
        var w = word.Trim().ToLowerInvariant();
        if (w.Length < 4) return null;                 // too short to fuzzy-fix safely
        if (_terms.Contains(w)) return w;
        if (!_byFirst.TryGetValue(w[0], out var bucket)) return null;
        string? best = null;
        var bestD = maxDistance + 1;
        foreach (var cand in bucket)
        {
            if (Math.Abs(cand.Length - w.Length) > maxDistance) continue;
            if (cand.IndexOf(' ') >= 0) continue;       // single-word fixes only
            var d = Levenshtein(w, cand, bestD);
            if (d < bestD) { bestD = d; best = cand; if (d == 1) break; }
        }
        return bestD <= maxDistance ? best : null;
    }

    public IReadOnlyList<string> Suggest(string prefix, int limit)
    {
        EnsureLoaded();
        if (!_available || string.IsNullOrWhiteSpace(prefix) || limit <= 0) return Array.Empty<string>();
        var p = prefix.Trim().ToLowerInvariant();
        if (p.Length < 2) return Array.Empty<string>();
        var i = LowerBound(_sorted, p);
        var res = new List<string>(limit);
        for (; i < _sorted.Length && res.Count < limit; i++)
        {
            if (!_sorted[i].StartsWith(p, StringComparison.Ordinal)) break;
            res.Add(_sorted[i]);
        }
        return res;
    }

    // ── loading ──────────────────────────────────────────────────────────────
    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Radiology");
                var terms = new HashSet<string>(StringComparer.Ordinal);

                var radlexPath = Path.Combine(dir, "radlex_terms.json");
                if (File.Exists(radlexPath) && JsonNode.Parse(File.ReadAllText(radlexPath))?["terms"] is JsonArray arr)
                    foreach (var t in arr) AddTerm(terms, (string?)t);

                var kwPath = Path.Combine(dir, "radiology_keywords.json");
                if (File.Exists(kwPath) && JsonNode.Parse(File.ReadAllText(kwPath)) is JsonObject kw)
                    foreach (var (key, val) in kw)
                    {
                        if (key.StartsWith("_")) continue;
                        if (val is JsonArray a) foreach (var t in a) AddTerm(terms, (string?)t);
                    }

                var corrPath = Path.Combine(dir, "radiology_corrections.json");
                if (File.Exists(corrPath) && JsonNode.Parse(File.ReadAllText(corrPath)) is JsonObject corr
                    && corr["corrections"] is JsonObject cmap)
                    foreach (var (bad, good) in cmap)
                    {
                        var g = (string?)good;
                        if (!string.IsNullOrWhiteSpace(bad) && !string.IsNullOrWhiteSpace(g))
                            _corrections[bad.ToLowerInvariant()] = g!;
                    }

                _terms = terms;
                _sorted = terms.ToArray();
                Array.Sort(_sorted, StringComparer.Ordinal);
                _byFirst = _sorted.Where(s => s.Length > 0).GroupBy(s => s[0]).ToDictionary(g => g.Key, g => g.ToList());
                _available = _terms.Count > 0;
                _logger.LogInformation("[RadiologyCorpus] loaded {Terms} terms, {Corr} corrections (available={A})",
                    _terms.Count, _corrections.Count, _available);
            }
            catch (Exception ex)
            {
                _available = false;
                _logger.LogWarning(ex, "[RadiologyCorpus] failed to load; spell pass + suggest disabled.");
            }
            finally { _loaded = true; }
        }
    }

    private static void AddTerm(HashSet<string> set, string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return;
        var s = t.Trim().ToLowerInvariant();
        if (s.Length is >= 2 and <= 80) set.Add(s);
    }

    private static int LowerBound(string[] arr, string key)
    {
        int lo = 0, hi = arr.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (string.CompareOrdinal(arr[mid], key) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // Levenshtein with an early-exit ceiling (returns ceiling+1 once it's clear
    // the distance can't beat the best-so-far).
    private static int Levenshtein(string a, string b, int ceiling)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                if (curr[j] < rowMin) rowMin = curr[j];
            }
            if (rowMin > ceiling) return ceiling + 1;
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
