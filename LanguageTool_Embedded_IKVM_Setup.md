# Embedded LanguageTool (Option A) — IKVM setup runbook

_Goal: run the LanguageTool grammar engine **inside the .NET API process** — no LLM, no separate server, no Docker, no third party. We cross-compile the LanguageTool Java JARs to a .NET assembly with **IKVM**, and the API calls it in-process via reflection (`EmbeddedLanguageToolService`). PHI never leaves the box._

The code is already in place and **builds without the DLL** (the adapter is reflection-based). This runbook is the one-time step to generate the DLL and switch it on.

---

## 1. What's already wired in the codebase

- `1Rad.Infrastructure/Services/EmbeddedLanguageToolService.cs` — reflection adapter that loads the IKVM assembly, builds a `org.languagetool.JLanguageTool` for the configured language, runs `check(text)`, and returns the standard `{ matches: [...] }` JSON the editor already parses.
- `DependencyInjection.cs` — registers the embedded service when `LanguageTool:Mode = "embedded"`, otherwise the HTTP proxy.
- `ReportingController.GrammarCheck` — uses whichever `ILanguageToolService` is registered; LLM fallback is **opt-out** via `LanguageTool:GrammarLlmFallback`.
- `appsettings.json` — `LanguageTool` config block.

So once the DLL exists and config is set, grammar runs embedded with zero further code changes.

---

## 2. Prerequisites

- **IKVM** toolchain. Use the maintained IKVM (IKVM 8 / the `ikvm` .NET tool) that targets modern .NET and bundles OpenJDK:
  ```
  dotnet tool install --global IKVM            # provides ikvmc (Java JAR → .NET DLL)
  ```
  (If you use the classic IKVM.NET distribution, you get `ikvmc.exe` directly. Either works; commands below use `ikvmc`.)
- **LanguageTool standalone build** matching a version you pin. Download `LanguageTool-<version>.zip` from languagetool.org and unzip. You need `languagetool-core.jar`, the English module (`org/languagetool/language-module/...` → in standalone it's bundled), and all dependency JARs in the unzipped `libs/` folder.
- A **JDK** is only needed if your IKVM build requires it; the maintained IKVM bundles its own OpenJDK.

> **Pin the version.** IKVM compiles cleanly against some LanguageTool versions and not others. Start with a known-good line (LanguageTool 5.x has the most community IKVM mileage; the current 6.x works with the maintained IKVM but test it). Record the exact version you used.

---

## 3. Generate the .NET assembly

From the unzipped LanguageTool folder (where the JARs live):

```bash
# Compile the whole JAR set into one library DLL. Including ALL jars ensures the
# rule/dictionary RESOURCES inside the jars are embedded into the assembly.
ikvmc -target:library -out:LanguageTool.dll languagetool-core.jar libs/*.jar
```

Notes / gotchas:
- **Resource embedding is the classic failure mode.** If rules fail to load at runtime ("no rules found"), the resources weren't embedded — make sure you pass the JARs themselves (not an extracted classpath) so `ikvmc` packs `org/languagetool/rules/...` and the dictionaries. With the maintained IKVM this is automatic when you pass the jars.
- The command also produces/needs the **IKVM runtime assemblies** (e.g. `IKVM.Runtime.dll`, `IKVM.Java.dll` and native runtime bits). Keep them next to `LanguageTool.dll`.
- You can trim to just English to cut size: include `languagetool-core.jar` + the english language jar + its dependency jars instead of `libs/*.jar`.

Output: `LanguageTool.dll` (+ IKVM runtime DLLs).

---

## 4. Deploy the DLL with the API

Put `LanguageTool.dll` **and the IKVM runtime DLLs** somewhere the API process can load them — simplest is the API's output folder (`bin/`), so `Assembly.Load("LanguageTool")` resolves and its IKVM dependencies sit alongside it. Otherwise use an absolute path via `AssemblyPath` (then ensure the IKVM runtime DLLs are in that same folder so dependency probing finds them).

> **Build settings:** do **not** enable trimming or Native AOT for the API — IKVM + the reflection adapter need the full runtime and dynamic loading.

---

## 5. Turn it on (config)

In `appsettings.json` / App Service configuration / env vars:

```json
"LanguageTool": {
  "Mode": "embedded",
  "AssemblyPath": "LanguageTool.dll",     // or full path, e.g. D:\\home\\site\\lt\\LanguageTool.dll
  "Language": "en-US",                      // or "en-GB"
  "GrammarLlmFallback": false               // false = NEVER use the LLM for grammar
}
```

- `GrammarLlmFallback: false` is what guarantees "no LLM": if the embedded engine somehow isn't available, the endpoint returns `503 GRAMMAR_DISABLED` (the editor shows "grammar isn't enabled yet") instead of silently calling Claude.
- (Linux App Service uses `__` for nesting in env-var form: `LanguageTool__Mode=embedded`, etc.)

---

## 6. Validate

1. Start the API and check logs for: `Embedded LanguageTool loaded (org.languagetool.language.AmericanEnglish).`
2. In the editor, run Grammar Check → underlines appear, sourced entirely in-process.
3. Confirm no outbound network/LLM call happens (the request stays inside the API).

If you see `Embedded LanguageTool failed to load`, the most likely causes are: DLL not on the probing path, missing IKVM runtime DLLs, or unembedded resources (re-run step 3 passing the jars).

---

## 7. Operational notes & tuning

- **Memory & startup:** loading English rules costs a few hundred MB and the **first** check is slow (rules load on first use; the adapter caches the engine afterward). Subsequent checks are fast. (Optional **n-gram** data for their/there-style confusions is large/GBs — skip unless you want it.)
- **Thread-safety:** `JLanguageTool` is not thread-safe; the adapter serialises checks on a single cached instance (a lock). For higher throughput later, pool several instances.
- **Scale-out:** the engine is per-process, so each API instance loads its own copy (fine — it's stateless). No shared infra needed.
- **Medical tuning (recommended):** LanguageTool's default rules will fight telegraphic report style. The clean next step is to disable the offending rule IDs (e.g. sentence-fragment, uppercase-after-period) — LanguageTool exposes `JLanguageTool.disableRule(id)` / `disableCategory(id)`. Say the word and I'll add a `LanguageTool:DisabledRules` config list the adapter applies on init, so you can curate it without code changes.
- **Reflection signature check:** the adapter expects the standard LanguageTool API — `JLanguageTool.check(String)`, `RuleMatch.getFromPos()/getToPos()/getMessage()/getSuggestedReplacements()/getRule()`, `Rule.isDictionaryBasedSpellingRule()`. These are stable, but if your IKVM build maps `check(String)` to a non-`System.String` parameter, tell me and I'll add a coercion.
- **License:** LanguageTool is LGPL 2.1+ — embedding/calling it as a library is fine; you only owe source for modifications to LanguageTool itself. Check separate terms for any non-default dictionaries / n-gram data.
```
