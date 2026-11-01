using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DepthRim;
using HarmonyLib;

// 內容一致性（比照 DMSE 的 check_bvr_world.py，但寫成 C#、直接對真實組件做反射）：
// XML 能解析、DefOf 都有對應的 Def、XML 引用的類別存在、翻譯鍵兩種語言都有、Harmony patch 目標在 1.7 仍存在。
static partial class Test
{
    static readonly Assembly DepthRimAssembly = typeof(DepthRimDefOf).Assembly;

    static IEnumerable<string> DefFiles => Directory.GetFiles(Path.Combine(ModRoot, "1.7", "Defs"), "*.xml", SearchOption.AllDirectories);

    static IEnumerable<string> SourceFiles => Directory.GetFiles(Path.Combine(ModRoot, ".source", "DepthRim"), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    static void ContentXml()
    {
        int files = 0;
        foreach (string file in Directory.GetFiles(Path.Combine(ModRoot, "1.7"), "*.xml", SearchOption.AllDirectories)
                     .Concat(new[] { Path.Combine(ModRoot, "About", "About.xml"), Path.Combine(ModRoot, "LoadFolders.xml") }))
        {
            files++;
            try { XDocument.Load(file); Check(true, "parses " + Path.GetFileName(file)); }
            catch (Exception e) { Check(false, $"{Path.GetRelativePath(ModRoot, file)} does not parse: {e.Message}"); }
        }
        Check(files >= 5, $"found mod XML files ({files})");
    }

    /// <summary>
    /// Def 的第一層子元素都對應到該 Def 型別（含父類別）的欄位：遊戲載入時的「doesn't correspond to any field」錯誤。
    /// 只檢查第一層；巢狀物件（comps、stages 等）由驗收測試的 Player.log 把關。
    /// </summary>
    static void ContentDefFields()
    {
        Assembly game = typeof(Verse.Def).Assembly;
        // Def 型別不一定在 RimWorld / Verse 命名空間（例如 1.7 的 ShaderDef），以型別名稱查
        Dictionary<string, Type> defTypes = game.GetTypes()
            .Where(t => typeof(Verse.Def).IsAssignableFrom(t))
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());
        // 模組自己的 Def 型別（例如 WaterVisualTierDef）：XML 用完整型別名當元素名
        foreach (Type t in DepthRimAssembly.GetTypes().Where(t => typeof(Verse.Def).IsAssignableFrom(t)))
        {
            defTypes[t.Name] = t;
        }
        int checkedDefs = 0;
        foreach (string file in DefFiles)
        {
            foreach (XElement def in XDocument.Load(file).Root.Elements())
            {
                // <DepthRim.WaterVisualTierDef> → WaterVisualTierDef
                string typeName = def.Name.LocalName;
                int dot = typeName.LastIndexOf('.');
                if (dot >= 0) { typeName = typeName.Substring(dot + 1); }
                defTypes.TryGetValue(typeName, out Type type);
                if (!Check(type != null && typeof(Verse.Def).IsAssignableFrom(type), $"{Path.GetFileName(file)}: {typeName} is a Def type")) { continue; }
                checkedDefs++;
                foreach (XElement child in def.Elements())
                {
                    string field = child.Name.LocalName;
                    Check(AccessTools.Field(type, field) != null, $"{Path.GetFileName(file)}: {typeName} {def.Element("defName")?.Value} field <{field}> exists");
                }
            }
        }
        Check(checkedDefs > 0, $"checked Def fields ({checkedDefs} defs)");
    }

    /// <summary>
    /// ShaderDef 引用的 GLSL 檔案存在：模組自己的在 1.7/Shaders，沿用原版的在遊戲的 Data/Core/Shaders。
    /// 模組著色器檔名全域共用，必須加 DepthRim_ 前綴。
    /// </summary>
    static void ContentShaders()
    {
        string modShaders = Path.Combine(ModRoot, "1.7", "Shaders");
        string gameShaders = Path.Combine(ModRoot, "..", "..", "Data", "Core", "Shaders");
        int refs = 0;
        foreach (string file in DefFiles)
        {
            foreach (XElement def in XDocument.Load(file).Root.Elements("ShaderDef"))
            {
                foreach (XElement e in def.Descendants().Where(x => x.Name.LocalName.EndsWith("ShaderFileName")))
                {
                    refs++;
                    string name = e.Value.Trim();
                    bool own = File.Exists(Path.Combine(modShaders, name));
                    bool vanilla = Directory.Exists(gameShaders) && Directory.GetFiles(gameShaders, name, SearchOption.AllDirectories).Length > 0;
                    Check(own || vanilla, $"{def.Element("defName")?.Value}: shader file {name} exists");
                    if (own) { Check(name.StartsWith("DepthRim_"), $"mod shader file {name} has the DepthRim_ prefix"); }
                }
            }
        }
        Check(refs > 0, $"found shader file references ({refs})");
        // 全域 uniform 由引擎提供，不算材質參數
        HashSet<string> globals = new HashSet<string> { "_GameSeconds" };
        const int maxUniformBytes = 512; // MaterialLayout.ValidateSlots 的硬上限
        int blocks = 0;
        foreach (string glsl in Directory.Exists(modShaders) ? Directory.GetFiles(modShaders, "*.glsl") : new string[0])
        {
            string name = Path.GetFileName(glsl);
            string text = File.ReadAllText(glsl);
            Check(text.TrimStart().StartsWith("#version 450"), $"{name} starts with #version 450");
            Check(text.Count(ch => ch == '{') == text.Count(ch => ch == '}'), $"{name} has balanced braces");

            // A.4：MaterialUniforms 區塊裡的每個 float／vec4 都要有 @default，材質才不必逐一設定
            bool inBlock = false;
            int floats = 0;
            int vec4s = 0;
            foreach (string raw in File.ReadAllLines(glsl))
            {
                string line = raw.Trim();
                if (!inBlock)
                {
                    if (line.Contains("uniform MaterialUniforms"))
                    {
                        inBlock = true;
                        floats = 0;
                        vec4s = 0;
                        blocks++;
                    }
                    continue;
                }
                if (line.StartsWith("}"))
                {
                    inBlock = false;
                    int bytes = vec4s * 16 + floats * 4;
                    int padded = (bytes + 15) / 16 * 16;
                    Check(padded <= maxUniformBytes, $"{name}: material uniform block is {padded} bytes (limit {maxUniformBytes})");
                    continue;
                }
                Match m = Regex.Match(line, "^(float|vec4)\\s+(\\w+)\\s*;");
                if (!m.Success) { continue; }
                string fieldName = m.Groups[2].Value;
                if (m.Groups[1].Value == "vec4") { vec4s++; } else { floats++; }
                if (globals.Contains(fieldName)) { continue; }
                Check(line.Contains("@default"), $"{name}: uniform {fieldName} has @default");
            }
            Check(!inBlock, $"{name}: material uniform block is closed");
        }
        Check(blocks >= 2, $"checked material uniform blocks ({blocks})");
    }

    /// <summary>
    /// 水體視覺層級（實作指南 C.1、C.5、C.7）：四個層級各一個 Def、沒有重複、數值都在範圍內。
    /// 直接讀 XML，因為單元測試不啟動遊戲、沒有 DefDatabase。
    /// </summary>
    static void ContentWaterTiers()
    {
        string file = Path.Combine(ModRoot, "1.7", "Defs", "Rendering", "WaterVisualTiers.xml");
        if (!Check(File.Exists(file), "WaterVisualTiers.xml exists")) { return; }
        List<XElement> defs = XDocument.Load(file).Root.Elements()
            .Where(e => e.Name.LocalName.EndsWith("WaterVisualTierDef")).ToList();
        Check(defs.Count == 4, $"exactly four water visual tiers ({defs.Count})");
        Check(defs.All(d => d.Name.LocalName == "DepthRim.WaterVisualTierDef"), "tiers use the DepthRim.WaterVisualTierDef element name");

        string[] required =
        {
            "defName", "tier", "waterColor", "deepColor", "fogColor", "clearAlpha", "murkAlpha",
            "ambientReach",
            "rippleScale", "rippleStrength", "causticStrength", "lightShaftStrength", "snowDensity",
            "snowSpeed", "biolumDensity", "biolumColor", "hazeStrength", "wobbleStrength", "compartmentEffects"
        };
        HashSet<string> names = new HashSet<string>();
        HashSet<DepthTier> tiers = new HashSet<DepthTier>();
        foreach (XElement def in defs)
        {
            string defName = def.Element("defName")?.Value;
            Check(defName != null && names.Add(defName), $"defName {defName} is unique");
            foreach (string field in required)
            {
                Check(def.Element(field) != null, $"{defName} defines {field}");
            }
            XElement tierElement = def.Element("tier");
            if (!Check(tierElement != null, $"{defName} defines a tier")) { continue; }
            if (!Check(Enum.TryParse(tierElement.Value.Trim(), out DepthTier tier), $"{defName} tier is a DepthTier")) { continue; }
            Check(tiers.Add(tier), $"{defName} tier {tier} is used exactly once");
            // 二期 P2-6：艙室層海洋雪由 Def 決定。淺海／變溫層保持關閉（艙室看得到就好），
            // 深海與海床開啟（那兩層的艙室裡也該有懸浮物）。
            bool compartmentOn = def.Element("compartmentEffects").Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            bool shouldBeOn = tier == DepthTier.Deep || tier == DepthTier.Seabed;
            Check(compartmentOn == shouldBeOn, $"{defName} compartmentEffects = {shouldBeOn} (P2-6)");
            foreach (string field in new[]
                     {
                         "clearAlpha", "murkAlpha", "ambientReach", "rippleStrength", "causticStrength", "lightShaftStrength",
                         "snowDensity", "biolumDensity", "hazeStrength", "wobbleStrength"
                     })
            {
                float v = TierValue(def, field);
                Check(v >= 0f && v <= 1f, $"{defName} {field} = {v} is within 0–1");
            }
            Check(TierValue(def, "rippleScale") > 0f, $"{defName} rippleScale is positive");
            Check(TierValue(def, "snowSpeed") >= 0f, $"{defName} snowSpeed is not negative");
            foreach (string field in new[] { "waterColor", "deepColor", "fogColor", "biolumColor" })
            {
                CheckColor(def, field);
            }
        }
        for (DepthTier t = DepthTier.Shallow; t <= DepthTier.Seabed; t++)
        {
            Check(tiers.Contains(t), $"tier {t} has a Def (C.1)");
        }

        // C.5 的初始值抽查
        XElement shallow = defs.First(d => d.Element("defName")?.Value == "DepthRim_WaterTier_Shallow");
        XElement thermocline = defs.First(d => d.Element("defName")?.Value == "DepthRim_WaterTier_Thermocline");
        XElement deep = defs.First(d => d.Element("defName")?.Value == "DepthRim_WaterTier_Deep");
        XElement seabed = defs.First(d => d.Element("defName")?.Value == "DepthRim_WaterTier_Seabed");
        Near(TierValue(shallow, "rippleStrength"), 0.8f, "C.5: shallows keep the A-stage ripples");
        Near(TierValue(shallow, "causticStrength"), 0.3f, "C.5: shallows caustics");
        Near(TierValue(shallow, "lightShaftStrength"), 0.15f, "C.5: only the shallows have light shafts");
        Near(TierValue(thermocline, "wobbleStrength"), 0.6f, "C.5: thermocline wobble");
        Check(TierValue(deep, "rippleStrength") == 0f && TierValue(deep, "causticStrength") == 0f, "C.5: deep sea has no surface ripples");
        Near(TierValue(deep, "snowDensity"), 0.6f, "C.5: deep sea marine snow");
        Near(TierValue(seabed, "hazeStrength"), 0.5f, "C.5: seabed sediment haze");
        Check(TierValue(shallow, "lightShaftStrength") > 0f && TierValue(thermocline, "lightShaftStrength") == 0f, "C.5: light shafts only in the shallows");
        // 混濁度上下限：越深越濁（C.5）
        Check(TierValue(shallow, "murkAlpha") < TierValue(seabed, "murkAlpha"), "C.5: deeper tiers are murkier");
        Check(TierValue(shallow, "clearAlpha") <= TierValue(seabed, "clearAlpha"), "C.5: deeper tiers are no clearer when lit");
        // 日光穿透：淺海要「有日光就算有光」，越深越拿不到日光（D9）
        Near(TierValue(shallow, "ambientReach"), 1f, "D9: the shallows count daylight as light");
        Check(TierValue(shallow, "ambientReach") > TierValue(thermocline, "ambientReach")
              && TierValue(thermocline, "ambientReach") > TierValue(deep, "ambientReach")
              && TierValue(deep, "ambientReach") > TierValue(seabed, "ambientReach"),
            "D9: daylight reaches less as it gets deeper");
        Check(TierValue(seabed, "ambientReach") > 0f, "D9: even the seabed keeps a little daylight");
    }

    static float TierValue(XElement def, string field)
    {
        XElement e = def.Element(field);
        return e == null ? float.NaN : float.Parse(e.Value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }

    static void CheckColor(XElement def, string field)
    {
        string defName = def.Element("defName")?.Value;
        XElement e = def.Element(field);
        if (!Check(e != null, $"{defName} defines {field}")) { return; }
        string[] parts = e.Value.Trim().TrimStart('(').TrimEnd(')').Split(',');
        if (!Check(parts.Length == 3, $"{defName} {field} has three components")) { return; }
        foreach (string part in parts)
        {
            bool ok = float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)
                      && v >= 0f && v <= 1f;
            Check(ok, $"{defName} {field} channel {part.Trim()} is within 0–1");
        }
    }

    /// <summary>每個 DepthRimDefOf 欄位都在 1.7/Defs 裡有同型別、同名的 Def。</summary>
    static void ContentDefOf()
    {
        Dictionary<string, HashSet<string>> defsByType = new Dictionary<string, HashSet<string>>();
        foreach (string file in DefFiles)
        {
            foreach (XElement def in XDocument.Load(file).Root.Elements())
            {
                string defName = def.Element("defName")?.Value;
                if (defName == null) { continue; }
                if (!defsByType.TryGetValue(def.Name.LocalName, out HashSet<string> set)) { defsByType[def.Name.LocalName] = set = new HashSet<string>(); }
                set.Add(defName);
            }
        }
        foreach (FieldInfo field in typeof(DepthRimDefOf).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            string type = field.FieldType.Name;
            Check(defsByType.TryGetValue(type, out HashSet<string> set) && set.Contains(field.Name), $"DefOf {type} {field.Name} defined in XML");
        }
    }

    /// <summary>XML 裡引用的 DepthRim / DMSE 類別都存在。</summary>
    static void ContentTypes()
    {
        Regex classAttr = new Regex("Class=\"((?:DepthRim|DMSE)\\.[\\w.]+)\"");
        Regex classElem = new Regex("<(?:thingClass|workerClass|worldObjectClass|compClass|hediffClass|layerType|tileType)>((?:DepthRim|DMSE)\\.[\\w.]+)</");
        Assembly dmse = typeof(DMSE.TransferEnvironmentExtension).Assembly;
        int refs = 0;
        foreach (string file in DefFiles)
        {
            string text = File.ReadAllText(file);
            foreach (Match m in classAttr.Matches(text).Cast<Match>().Concat(classElem.Matches(text)))
            {
                refs++;
                string name = m.Groups[1].Value;
                Assembly asm = name.StartsWith("DMSE.") ? dmse : DepthRimAssembly;
                Check(asm.GetType(name) != null, $"{Path.GetFileName(file)}: type {name} exists");
            }
        }
        Check(refs > 0, $"found class references in Defs ({refs})");
    }

    /// <summary>程式碼用到的翻譯鍵在英文與繁中都有，兩種語言的鍵集合一致。</summary>
    static void ContentKeyed()
    {
        HashSet<string> used = new HashSet<string>();
        Regex literal = new Regex("\"(DepthRim\\.[A-Za-z0-9_.]+)\"\\s*\\.Translate");
        foreach (string file in SourceFiles)
        {
            foreach (Match m in literal.Matches(File.ReadAllText(file))) { used.Add(m.Groups[1].Value); }
        }
        // DepthTierUtility.Label 以 "DepthRim.DepthTier." + 列舉名稱 組出鍵
        foreach (string tier in Enum.GetNames(typeof(DepthTier))) { used.Add("DepthRim.DepthTier." + tier); }
        Check(used.Count >= 5, $"found translation keys in source ({used.Count})");

        Dictionary<string, HashSet<string>> languages = new Dictionary<string, HashSet<string>>();
        foreach (string dir in Directory.GetDirectories(Path.Combine(ModRoot, "1.7", "Languages")))
        {
            HashSet<string> keys = new HashSet<string>();
            string keyed = Path.Combine(dir, "Keyed");
            if (Directory.Exists(keyed))
            {
                foreach (string file in Directory.GetFiles(keyed, "*.xml"))
                {
                    foreach (XElement e in XDocument.Load(file).Root.Elements()) { keys.Add(e.Name.LocalName); }
                }
            }
            languages[Path.GetFileName(dir)] = keys;
        }
        Check(languages.ContainsKey("English"), "English Keyed exists");
        Check(languages.Keys.Any(k => k.StartsWith("ChineseTraditional")), "Traditional Chinese Keyed exists");
        foreach (KeyValuePair<string, HashSet<string>> lang in languages)
        {
            foreach (string key in used) { Check(lang.Value.Contains(key), $"{lang.Key} has {key}"); }
            if (languages.TryGetValue("English", out HashSet<string> en) && lang.Key != "English")
            {
                foreach (string extra in lang.Value.Except(en)) { Check(false, $"{lang.Key} has {extra} but English does not"); }
                foreach (string missing in en.Except(lang.Value)) { Check(false, $"{lang.Key} is missing {missing}"); }
            }
        }
    }

    /// <summary>
    /// DefInjected 翻譯（二期 P2-2）：每個語言都要有 DefInjected 目錄，而且每個鍵
    /// <c>&lt;defName&gt;.&lt;欄位&gt;</c> 指的 Def 與欄位都真的存在（欄位用 Def XML 的子元素名稱判斷，
    /// <c>stages.N.label</c> 只檢查到 <c>stages</c>）。
    /// 這樣 Def 改名或拿掉欄位時，翻譯不會靜默失效。
    /// </summary>
    static void ContentDefInjected()
    {
        Dictionary<string, HashSet<string>> defs = new Dictionary<string, HashSet<string>>();
        string defsRoot = Path.Combine(ModRoot, "1.7", "Defs");
        foreach (string file in Directory.GetFiles(defsRoot, "*.xml", SearchOption.AllDirectories))
        {
            foreach (XElement def in XDocument.Load(file).Root.Elements())
            {
                string name = def.Element("defName")?.Value;
                if (name == null) { continue; }
                // 同一個 defName 可能出現在不同 Def 型別（例：冰洋同時是 PlanetLayerDef 與 BiomeDef），
                // 子元素取聯集，避免檔案讀取順序影響結果。
                if (!defs.TryGetValue(name, out HashSet<string> fields))
                {
                    fields = new HashSet<string>();
                    defs[name] = fields;
                }
                foreach (XElement field in def.Elements()) { fields.Add(field.Name.LocalName); }
            }
        }
        Check(defs.Count >= 15, $"found Def definitions to cross-check ({defs.Count})");

        int keys = 0;
        foreach (string dir in Directory.GetDirectories(Path.Combine(ModRoot, "1.7", "Languages")))
        {
            string language = Path.GetFileName(dir);
            string injected = Path.Combine(dir, "DefInjected");
            bool isSourceLanguage = language.StartsWith("English", StringComparison.Ordinal);
            if (!Directory.Exists(injected))
            {
                // 英文是來源語言：Def XML 裡的 label/description 本身就是英文，
                // 不需要（慣例上也不會）再放一份 DefInjected。其他語言則必須有。
                if (!isSourceLanguage)
                {
                    Check(false, $"{language} has a DefInjected folder");
                }
                continue;
            }
            // 原版 LoadedLanguage 用資料夾名稱呼叫 GenTypes.GetTypeInAnyAssembly 找 Def 型別：只會自動補上原版命名空間，
            // 模組自訂的型別（例如 DepthRim.WaterVisualTierDef）必須寫完整名稱，否則整個資料夾被當成載入錯誤跳過。
            foreach (string typeDir in Directory.GetDirectories(injected))
            {
                string folder = Path.GetFileName(typeDir);
                Type defType = ResolveDefInjectedFolder(folder);
                Check(defType != null, $"{language}: DefInjected folder {folder} resolves to a Def type the way the game resolves it");
            }
            int languageKeys = 0;
            foreach (string file in Directory.GetFiles(injected, "*.xml", SearchOption.AllDirectories))
            {
                foreach (XElement e in XDocument.Load(file).Root.Elements())
                {
                    string key = e.Name.LocalName;
                    languageKeys++;
                    keys++;
                    int dot = key.IndexOf('.');
                    if (!Check(dot > 0, $"{language}: {key} looks like <defName>.<field>")) { continue; }
                    string defName = key.Substring(0, dot);
                    string root = key.Substring(dot + 1).Split('.')[0];
                    if (!Check(defs.ContainsKey(defName), $"{language}: {defName} exists (from {key})")) { continue; }
                    Check(defs[defName].Contains(root) || root == "stages",
                        $"{language}: {defName} has field {root} (from {key})");
                }
            }
            Check(languageKeys >= 15, $"{language} translates enough Def fields ({languageKeys})");
        }
        Check(keys >= 30, $"DefInjected keys exist across languages ({keys})");
    }

    /// <summary>
    /// 比照原版 <c>GenTypes.GetTypeInAnyAssembly</c> 解析 DefInjected 資料夾名稱：先照原樣找（完整名稱），
    /// 再補原版命名空間，最後比照 <c>LoadedLanguage</c> 去掉結尾一個字元再試一次。只接受 Def 的子類。
    /// </summary>
    static Type ResolveDefInjectedFolder(string folder)
    {
        Assembly[] assemblies = { typeof(Verse.Def).Assembly, DepthRimAssembly, typeof(DMSE.TransferEnvironmentExtension).Assembly };
        string[] namespaces = { "Verse", "RimWorld", "RimWorld.Planet", "Verse.AI", "RimWorld.QuestGen" };
        foreach (string name in new[] { folder, folder.Length > 3 ? folder.Substring(0, folder.Length - 1) : folder })
        {
            foreach (Assembly asm in assemblies)
            {
                Type t = asm.GetType(name, throwOnError: false, ignoreCase: true);
                if (t == null)
                {
                    foreach (string ns in namespaces)
                    {
                        t = asm.GetType(ns + "." + name, throwOnError: false, ignoreCase: true);
                        if (t != null) { break; }
                    }
                }
                if (t != null && typeof(Verse.Def).IsAssignableFrom(t)) { return t; }
            }
        }
        return null;
    }

    /// <summary>
    /// 每個 [HarmonyPatch] 的目標方法在目前的 1.7 組件裡存在，patch 方法的參數名稱也對得上
    /// （<c>___欄位</c> 要有該欄位、一般參數要和原方法參數同名）。遊戲改版時最先壞的就是這些。
    /// </summary>
    static void HarmonyTargets()
    {
        int patches = 0;
        foreach (Type type in DepthRimAssembly.GetTypes())
        {
            HarmonyPatch attr = type.GetCustomAttributes<HarmonyPatch>().FirstOrDefault();
            if (attr == null) { continue; }
            patches++;
            Type declaring = attr.info.declaringType;
            MethodBase target = AccessTools.Method(declaring, attr.info.methodName, attr.info.argumentTypes);
            Check(target != null, $"{type.Name}: target {declaring?.Name}.{attr.info.methodName} exists");
            if (target == null) { continue; }
            HashSet<string> targetParams = new HashSet<string>(target.GetParameters().Select(p => p.Name));
            foreach (MethodInfo patch in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                bool isPatch = patch.IsDefined(typeof(HarmonyPrefix)) || patch.IsDefined(typeof(HarmonyPostfix))
                               || patch.Name == "Prefix" || patch.Name == "Postfix";
                if (!isPatch) { continue; }
                foreach (ParameterInfo p in patch.GetParameters())
                {
                    if (p.Name == "__instance" || p.Name == "__result" || p.Name == "__state") { continue; }
                    if (p.Name.StartsWith("___"))
                    {
                        Check(AccessTools.Field(declaring, p.Name.Substring(3)) != null, $"{type.Name}.{patch.Name}: field {p.Name.Substring(3)} exists on {declaring.Name}");
                    }
                    else
                    {
                        Check(targetParams.Contains(p.Name), $"{type.Name}.{patch.Name}: parameter {p.Name} exists on {declaring.Name}.{target.Name}");
                    }
                }
            }
        }
        Check(patches >= 3, $"found Harmony patch classes ({patches})");
    }
}
