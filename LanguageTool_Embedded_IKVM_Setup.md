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

- **.NET 8 SDK** (you already have it).
- Build-time access to **NuGet** and **Maven Central** (the build downloads LanguageTool from Maven).
- **Do NOT** run `dotnet tool install --global IKVM` — the `IKVM` package is **not** a global .NET tool, so the install fails with a `tools` `DirectoryNotFoundException` and leaves a broken store entry. If you tried it, clean up first:
  ```powershell
  dotnet tool uninstall --global ikvm   # may error; if so:
  Remove-Item -Recurse -Force "$env:USERPROFILE\.dotnet\tools\.store\ikvm"
  ```
  The supported modern path is **MSBuild integration** (`IKVM` + `IKVM.Maven.Sdk` packages with a `MavenReference`). You do **not** run `ikvmc` by hand (in 8.5+ it requires fiddly `-runtime`/`-reference` args).

---

## 3. Generate the assemblies via MSBuild (recommended)

Create a tiny class library that pulls LanguageTool from Maven and IKVM-compiles it on build. `1Rad.LanguageTool/1Rad.LanguageTool.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <!-- IKVM + the reflection adapter need the full runtime; never trim / AOT. -->
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="IKVM" Version="8.*" />
    <PackageReference Include="IKVM.Maven.Sdk" Version="1.*" />
    <!-- Pulls languagetool-core + the English module + ALL transitive deps from
         Maven and IKVM-compiles them (rules/dictionaries embedded automatically). -->
    <MavenReference Include="org.languagetool:language-en" Version="6.4" />
  </ItemGroup>
</Project>
```

Build it:
```
dotnet build 1Rad.LanguageTool/1Rad.LanguageTool.csproj -c Release
```

Output (in the project's `bin/Release/net8.0/`): the IKVM-compiled managed assemblies — e.g. `org.languagetool.languagetool-core.dll`, `org.languagetool.language-en.dll`, transitive-dependency DLLs — plus the **IKVM runtime DLLs**. No jar/resource juggling: `MavenReference` handles resolution and IKVM embeds the resources.

> **Pin the version** (6.4 shown). If a release fails to IKVM-compile, step to a nearby one and record what worked.

---

## 4. Wire it into the API — two ways

**Option 1 — project-reference it (simplest).** Add to the API project (`1RadAPI.csproj`):
```xml
<ProjectReference Include="..\1Rad.LanguageTool\1Rad.LanguageTool.csproj" />
```
On build, all the IKVM + LanguageTool DLLs are copied into the API's output. The adapter finds the types by **scanning loaded assemblies**, so you can leave `AssemblyPath` empty.

**Option 2 — copy the DLLs.** Copy the whole build output (every produced DLL + the IKVM runtime DLLs, kept together) into the API's `bin/` or a folder. If not in `bin/`, set `AssemblyPath` to the full path of `org.languagetool.languagetool-core.dll` (keep the sibling DLLs next to it so dependencies resolve).

> **Build settings:** do **not** enable trimming or Native AOT on the API — IKVM + the reflection adapter need the full runtime and dynamic loading.

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

If you see `Embedded LanguageTool failed to load`, the most likely causes are: the IKVM/LanguageTool DLLs aren't in the API output (use the project-reference in step 4 Option 1), the IKVM runtime DLLs aren't beside the LanguageTool DLLs, or the build didn't run (rebuild `1Rad.LanguageTool`). The adapter scans loaded assemblies first, so a project reference is the most reliable wiring.

---

## 7. Operational notes & tuning

- **Memory & startup:** loading English rules costs a few hundred MB and the **first** check is slow (rules load on first use; the adapter caches the engine afterward). Subsequent checks are fast. (Optional **n-gram** data for their/there-style confusions is large/GBs — skip unless you want it.)
- **Thread-safety:** `JLanguageTool` is not thread-safe; the adapter serialises checks on a single cached instance (a lock). For higher throughput later, pool several instances.
- **Scale-out:** the engine is per-process, so each API instance loads its own copy (fine — it's stateless). No shared infra needed.
- **Medical tuning (recommended):** LanguageTool's default rules will fight telegraphic report style. The clean next step is to disable the offending rule IDs (e.g. sentence-fragment, uppercase-after-period) — LanguageTool exposes `JLanguageTool.disableRule(id)` / `disableCategory(id)`. Say the word and I'll add a `LanguageTool:DisabledRules` config list the adapter applies on init, so you can curate it without code changes.
- **Reflection signature check:** the adapter expects the standard LanguageTool API — `JLanguageTool.check(String)`, `RuleMatch.getFromPos()/getToPos()/getMessage()/getSuggestedReplacements()/getRule()`, `Rule.isDictionaryBasedSpellingRule()`. These are stable, but if your IKVM build maps `check(String)` to a non-`System.String` parameter, tell me and I'll add a coercion.
- **License:** LanguageTool is LGPL 2.1+ — embedding/calling it as a library is fine; you only owe source for modifications to LanguageTool itself. Check separate terms for any non-default dictionaries / n-gram data.
```
