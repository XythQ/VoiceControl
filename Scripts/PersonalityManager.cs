using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using UnityEngine;

namespace XNPCVoiceControl
{
    public enum VoiceMode { Tts, Clips, Mixed }

    /// <summary>
    /// Per-character facial-animation overrides.
    /// All fields are nullable — a null value means "use the global TTSConfig default".
    /// Parsed from &lt;FaceOverride&gt; inside a &lt;Personality&gt; block in personalities.xml.
    /// </summary>
    public class FaceOverride
    {
        // Jaw tuning
        public float? OpenAngle { get; set; }
        public float? LowerMaxFrac { get; set; }
        public float? ForwardMinFrac { get; set; }
        public float? HingeYFrac { get; set; }
        public float? HingeZFrac { get; set; }
        public bool? TestHold { get; set; }

        // Blink tuning
        public float? BlinkEyeYFrac { get; set; }
        public float? BlinkBandHeightFrac { get; set; }
        public float? BlinkBandWidthFrac { get; set; }
        public float? BlinkCloseAmount { get; set; }
        public float? BlinkForwardMinFrac { get; set; }
        public string BlinkWinkMode { get; set; }   // null = use default
        public float? BlinkWinkChance { get; set; }

        /// <summary>True if any field is set (non-null).</summary>
        public bool HasAnyOverride =>
            OpenAngle.HasValue || LowerMaxFrac.HasValue || ForwardMinFrac.HasValue ||
            HingeYFrac.HasValue || HingeZFrac.HasValue || TestHold.HasValue ||
            BlinkEyeYFrac.HasValue || BlinkBandHeightFrac.HasValue || BlinkBandWidthFrac.HasValue ||
            BlinkCloseAmount.HasValue || BlinkForwardMinFrac.HasValue ||
            !string.IsNullOrEmpty(BlinkWinkMode) || BlinkWinkChance.HasValue;
    }

    /// <summary>
    /// Represents a single personality definition loaded from personalities.xml
    /// </summary>
    public class PersonalityDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Traits { get; set; }
        public string MaleVoice { get; set; }
        public string FemaleVoice { get; set; }

        public VoiceMode VoiceMode { get; set; } = VoiceMode.Tts;
        public string VoiceClipFolder { get; set; }
        public bool UsesClips => VoiceMode != VoiceMode.Tts;
        public bool LlmDisabled => VoiceMode == VoiceMode.Clips;

        /// <summary>
        /// ISO TTS language code for this personality.
        /// Defaults to "en" (American English). Use "ja" for Japanese, "en-GB" for British,
        /// "es" for Spanish, "hi" for Hindi, "en-IE" for Irish, "fi" for Finnish,
        /// "pl" for Polish, "zh" for Chinese. Must match the voice's language family.
        /// </summary>
        public string TTSLanguage { get; set; } = "en";

        /// <summary>
        /// Greetings grouped by ISO language code ("en", "ja", "de", etc.).
        /// Populated from &lt;Greeting lang="..."&gt; elements in personalities.xml.
        /// </summary>
        public Dictionary<string, List<string>> GreetingsByLang { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// When true, this personality is excluded from random assignment (tiers 2/3 fallback).
        /// It will only be used when explicitly set via entity class "personality" property.
        /// Used for non-American personalities to keep random NPCs Arizona-appropriate.
        /// </summary>
        public bool ExcludeFromRandom { get; set; }

        /// <summary>
        /// Forces this NPC's dialogue subtitle language regardless of the player's selection.
        /// ISO-style code (e.g. "ja", "zh", "es"). Empty string means no override — use normal player-driven behavior.
        /// </summary>
        public string ForceLanguage { get; set; } = "";

        /// <summary>
        /// The character's canonical backstory — their personal history the LLM draws on
        /// when generating stories. Separate from Traits (personality/behavior).
        /// Also serves as offline fallback text if the LLM is unavailable.
        /// </summary>
        public string Backstory { get; set; } = "";

        /// <summary>
        /// Random story pool for CustomDialog ("tell me a story"). Picked randomly each time.
        /// Falls back to Backstory if empty. Loaded from &lt;Stories&gt;/&lt;Story&gt; in personalities.xml.
        /// </summary>
        public List<string> Stories { get; set; } = new List<string>();

        /// <summary>
        /// Get a random story from the pool, falling back to Backstory text.
        /// Never returns null or empty — last resort is hardcoded fallback.
        /// </summary>
        public string GetRandomStory()
        {
            if (Stories.Count > 0)
                return Stories[UnityEngine.Random.Range(0, Stories.Count)];
            if (!string.IsNullOrEmpty(Backstory))
                return Backstory;
            return "I've seen a lot of things out here... but I'd rather not talk about it.";
        }

        /// <summary>
        /// Probability (0.0–1.0) that this NPC will proactively greet the player on approach.
        /// 0.0 = never greets, 1.0 = always greets. Defaults to 0 (opt-in via personalities.xml).
        /// </summary>
        public float ProactiveGreetingChance { get; set; } = 0f;

        /// <summary>
        /// Alt currency item ID (raw items.xml ID) for weekly billing.
        /// Empty string = primary-only (no alt payment). Example: "goldOre".
        /// </summary>
        public string AltPaymentItem { get; set; } = "";

        /// <summary>
        /// How many primary-currency coins one alt item is worth. Default 1.0.
        /// E.g., if baseCost=1000 and AltPaymentRatio=5, player can pay with 200 alt items.
        /// </summary>
        public float AltPaymentRatio { get; set; } = 1f;

        /// <summary>
        /// Per-character facial-animation overrides. Null if no &lt;FaceOverride&gt; block in XML.
        /// When non-null, any non-empty field overrides the corresponding global TTSConfig default
        /// for this NPC's procedural lip-sync bake.
        /// </summary>
        public FaceOverride FaceOverride { get; set; }

        /// <summary>
        /// Get a random greeting matching the requested language, or null if none defined.
        /// Falls back to English greetings if no match for the requested language.
        /// </summary>
        public string GetRandomGreeting(string playerLang = "en")
        {
            Log.Debug(() => $"[I18N] GetRandomGreeting requested lang='{playerLang}', pools: {string.Join(", ", GreetingsByLang.Keys.Select(k => $"{k}={GreetingsByLang[k].Count}"))}");

            // Try exact language match first
            if (GreetingsByLang.TryGetValue(playerLang, out var pool) && pool.Count > 0)
            {
                if (pool.Count == 1) return pool[0];
                return pool[UnityEngine.Random.Range(0, pool.Count)];
            }

            // Fallback: try "en" pool
            if (GreetingsByLang.TryGetValue("en", out var enPool) && enPool.Count > 0)
            {
                if (enPool.Count == 1) return enPool[0];
                return enPool[UnityEngine.Random.Range(0, enPool.Count)];
            }

            // Last resort: pick from any available pool
            foreach (var kvp in GreetingsByLang)
            {
                if (kvp.Value.Count > 0)
                {
                    if (kvp.Value.Count == 1) return kvp.Value[0];
                    return kvp.Value[UnityEngine.Random.Range(0, kvp.Value.Count)];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Manages NPC personality definitions and assignment.
    /// 
    /// Personalities are loaded from personalities.xml at mod init.
    /// Assignment mirrors how 7DTD assigns NPC names: the entity class
    /// can define a "personality" property with a comma-separated list
    /// of personality IDs. On spawn, one is randomly selected by the
    /// game engine. We read that selected value and apply the matching
    /// personality definition.
    /// 
    /// If no "personality" property exists on the entity class, we fall
    /// back to type-based matching (trader, bandit, companion, survivor).
    /// </summary>
    public class PersonalityManager
    {
        private static PersonalityManager _instance;
        public static PersonalityManager Instance => _instance ??= new PersonalityManager();

        // All loaded personalities keyed by ID
        private Dictionary<string, PersonalityDefinition> _personalities = new Dictionary<string, PersonalityDefinition>();

        // Grouped by type prefix for fallback matching
        private Dictionary<string, List<string>> _personalitiesByType = new Dictionary<string, List<string>>();

        /// <summary>Number of loaded personalities (for diagnostics).</summary>
        public int LoadedCount => _personalities.Count;

        /// <summary>Number of personalities with a FaceOverride block (for diagnostics).</summary>
        public int FaceOverrideCount
        {
            get
            {
                int c = 0;
                foreach (var kvp in _personalities)
                    if (kvp.Value.FaceOverride != null) c++;
                return c;
            }
        }

        // Track which personality was assigned to which NPC (by name — stable across respawns)
        // NOTE: keyed by display name deliberately - assignments survive entityId changes across
        // respawns/reloads. Tradeoff: two distinct NPCs sharing a display name share one
        // personality assignment (first one wins). Do not change the key format without a
        // migration plan for existing saves.
        private Dictionary<string, string> _assignedPersonalities = new Dictionary<string, string>();

        // Lock for thread-safe access to _assignedPersonalities (Loaded Chamber bg thread vs main thread)
        private readonly object _personalityLock = new object();

        /// <summary>
        /// Load personalities from the XML config file.
        /// Call once during mod initialization.
        /// </summary>
        public void LoadPersonalities(string modPath)
        {
            string configPath = Path.Combine(modPath, "Config", "personalities.xml");

            if (!File.Exists(configPath))
            {
                Log.Warning($"Personalities file not found at {configPath}");
                return;
            }

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var personalityNodes = doc.SelectNodes("//Personality");
                if (personalityNodes == null)
                {
                    Log.Warning("No <Personality> nodes found in personalities.xml");
                    return;
                }

                foreach (XmlNode node in personalityNodes)
                {
                    var def = new PersonalityDefinition();

                    // Required: id attribute
                    var idAttr = node.Attributes["id"];
                    if (idAttr == null || string.IsNullOrEmpty(idAttr.Value))
                    {
                        Log.Warning("Skipping personality node without 'id' attribute");
                        continue;
                    }
                    def.Id = idAttr.Value;

                    // Name
                    var nameNode = node.SelectSingleNode("Name");
                    def.Name = nameNode?.InnerText ?? def.Id;

                    // Traits (the personality description injected into the LLM system prompt)
                    var traitsNode = node.SelectSingleNode("Traits");
                    def.Traits = traitsNode?.InnerText ?? "";

                    // Backstory (character's personal history — grounds LLM stories, offline fallback)
                    var backstoryNode = node.SelectSingleNode("Backstory");
                    def.Backstory = backstoryNode?.InnerText ?? "";

                    // Stories pool (random stories for CustomDialog "tell me a story" trigger)
                    var storiesNodes = node.SelectNodes("Stories/Story");
                    if (storiesNodes != null)
                    {
                        foreach (XmlNode s in storiesNodes)
                        {
                            string text = s.InnerText?.Trim();
                            if (!string.IsNullOrEmpty(text))
                                def.Stories.Add(text);
                        }
                    }

                    // Voice (TTS voice IDs — gender-specific, selected by entity tags)
                    var maleVoiceNode = node.SelectSingleNode("MaleVoice");
                    def.MaleVoice = maleVoiceNode?.InnerText ?? null;

                    var femaleVoiceNode = node.SelectSingleNode("FemaleVoice");
                    def.FemaleVoice = femaleVoiceNode?.InnerText ?? null;

                    // TTSLanguage: ISO language code (en=American, ja=Japanese, en-GB=British, etc.)
                    var ttsLangNode = node.SelectSingleNode("TTSLanguage");
                    if (ttsLangNode != null && !string.IsNullOrEmpty(ttsLangNode.InnerText.Trim()))
                        def.TTSLanguage = ttsLangNode.InnerText.Trim();

                    // ExcludeFromRandom: when true, personality is only used if explicitly
                    // set via entity class "personality" property (not picked randomly)
                    var excludeAttr = node.Attributes["excludeFromRandom"];
                    def.ExcludeFromRandom = excludeAttr != null && bool.TryParse(excludeAttr.Value, out bool excl) && excl;

                    // ProactiveGreetingChance: probability (0.0–1.0) of greeting on approach
                    var chanceNode = node.SelectSingleNode("ProactiveGreetingChance");
                    if (chanceNode != null && !string.IsNullOrEmpty(chanceNode.InnerText.Trim()))
                    {
                        if (float.TryParse(chanceNode.InnerText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float chance))
                        {
                            def.ProactiveGreetingChance = Mathf.Clamp01(chance);
                        }
                    }

                    // AltPaymentItem: alt currency item ID for weekly billing (e.g. "goldOre")
                    var altItemNode = node.SelectSingleNode("AltPaymentItem");
                    if (altItemNode != null && !string.IsNullOrEmpty(altItemNode.InnerText.Trim()))
                        def.AltPaymentItem = altItemNode.InnerText.Trim();

                    // AltPaymentRatio: how many primary coins one alt item is worth
                    var altRatioNode = node.SelectSingleNode("AltPaymentRatio");
                    if (altRatioNode != null && !string.IsNullOrEmpty(altRatioNode.InnerText.Trim()))
                    {
                        if (float.TryParse(altRatioNode.InnerText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ratio))
                        {
                            def.AltPaymentRatio = Mathf.Max(0.1f, ratio);
                        }
                    }

                    // ForceLanguage: forces subtitle language for this NPC regardless of player setting
                    var forceLangNode = node.SelectSingleNode("ForceLanguage");
                    if (forceLangNode != null && !string.IsNullOrEmpty(forceLangNode.InnerText.Trim()))
                        def.ForceLanguage = forceLangNode.InnerText.Trim().ToLowerInvariant();

                    var voiceModeNode = node.SelectSingleNode("VoiceMode");
                    if (voiceModeNode != null && !string.IsNullOrEmpty(voiceModeNode.InnerText.Trim()))
                    {
                        string vm = voiceModeNode.InnerText.Trim().ToLowerInvariant();
                        switch (vm)
                        {
                            case "clips": def.VoiceMode = VoiceMode.Clips; break;
                            case "mixed": def.VoiceMode = VoiceMode.Mixed; break;
                            case "tts":   def.VoiceMode = VoiceMode.Tts;   break;
                            default:
                                Log.Warning($"Unknown VoiceMode '{vm}' for personality {def.Id} - defaulting to tts");
                                def.VoiceMode = VoiceMode.Tts;
                                break;
                        }
                    }
                    var clipFolderNode = node.SelectSingleNode("VoiceClipFolder");
                    def.VoiceClipFolder = clipFolderNode?.InnerText?.Trim();

                    if (def.UsesClips && string.IsNullOrEmpty(def.VoiceClipFolder))
                        Log.Warning($"Personality {def.Id} has VoiceMode={def.VoiceMode} but no VoiceClipFolder - clips will not load");

                    // Greetings (optional, for initial NPC dialogue)
                    // Each <Greeting> can have a lang attribute: "en", "ja", "zh"
                    // Defaults to English if no lang specified
                    var greetingsNode = node.SelectSingleNode("Greetings");
                    if (greetingsNode != null)
                    {
                        var greetingNodes = greetingsNode.SelectNodes("Greeting");
                        if (greetingNodes != null)
                        {
                            foreach (XmlNode gNode in greetingNodes)
                            {
                                string lang = gNode.Attributes["lang"]?.Value ?? "en";
                                if (!def.GreetingsByLang.ContainsKey(lang.ToLower()))
                                    def.GreetingsByLang[lang.ToLower()] = new List<string>();
                                def.GreetingsByLang[lang.ToLower()].Add(gNode.InnerText);
                            }
                        }
                    }

                    // FaceOverride: per-character facial-animation tuning (optional)
                    ParseFaceOverride(def, node);

                    _personalities[def.Id] = def;

                    // Index by type prefix (everything before the first underscore)
                    string type = def.Id.Contains("_") ? def.Id.Substring(0, def.Id.IndexOf('_')) : def.Id;
                    if (!_personalitiesByType.ContainsKey(type))
                    {
                        _personalitiesByType[type] = new List<string>();
                    }
                    _personalitiesByType[type].Add(def.Id);

                    int totalGreetings = 0;
                    foreach (var kvp in def.GreetingsByLang) totalGreetings += kvp.Value.Count;
                    Log.Debug(() => $"Loaded personality: {def.Id} ({def.Name}) - {totalGreetings} greetings");
                }

                Log.Debug(() => $"Loaded {_personalities.Count} personalities in {_personalitiesByType.Count} type groups");
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading personalities: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Parse optional &lt;FaceOverride&gt; block from a &lt;Personality&gt; XML node.
        /// All fields are optional — only present values override the global defaults.
        /// </summary>
        private void ParseFaceOverride(PersonalityDefinition def, XmlNode personalityNode)
        {
            var foNode = personalityNode.SelectSingleNode("FaceOverride");
            if (foNode == null) return;

            var fo = new FaceOverride();

            // Jaw tuning
            fo.OpenAngle = TryReadFloat(foNode, "FaceLipSyncProcOpenAngle");
            fo.LowerMaxFrac = TryReadFloat(foNode, "FaceLipSyncProcLowerMaxFrac");
            fo.ForwardMinFrac = TryReadFloat(foNode, "FaceLipSyncProcForwardMinFrac");
            fo.HingeYFrac = TryReadFloat(foNode, "FaceLipSyncProcHingeYFrac");
            fo.HingeZFrac = TryReadFloat(foNode, "FaceLipSyncProcHingeZFrac");
            fo.TestHold = TryReadBool(foNode, "FaceLipSyncProcTestHold");

            // Blink tuning
            fo.BlinkEyeYFrac = TryReadFloat(foNode, "FaceLipSyncProcBlinkEyeYFrac");
            fo.BlinkBandHeightFrac = TryReadFloat(foNode, "FaceLipSyncProcBlinkBandHeightFrac");
            fo.BlinkBandWidthFrac = TryReadFloat(foNode, "FaceLipSyncProcBlinkBandWidthFrac");
            fo.BlinkCloseAmount = TryReadFloat(foNode, "FaceLipSyncProcBlinkCloseAmount");
            fo.BlinkForwardMinFrac = TryReadFloat(foNode, "FaceLipSyncProcBlinkForwardMinFrac");

            var winkModeNode = foNode.SelectSingleNode("FaceLipSyncProcBlinkWinkMode");
            if (winkModeNode != null && !string.IsNullOrEmpty(winkModeNode.InnerText.Trim()))
                fo.BlinkWinkMode = winkModeNode.InnerText.Trim().ToLowerInvariant();

            fo.BlinkWinkChance = TryReadFloat(foNode, "FaceLipSyncProcBlinkWinkChance");

            if (fo.HasAnyOverride)
            {
                def.FaceOverride = fo;
                Log.Debug(() => $"Loaded FaceOverride for personality {def.Id}");
            }
        }

        private static float? TryReadFloat(XmlNode parent, string childName)
        {
            var node = parent.SelectSingleNode(childName);
            if (node == null || string.IsNullOrEmpty(node.InnerText.Trim())) return null;
            if (float.TryParse(node.InnerText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                return v;
            Log.Warning($"Invalid float for {childName} in FaceOverride: '{node.InnerText.Trim()}'");
            return null;
        }

        private static bool? TryReadBool(XmlNode parent, string childName)
        {
            var node = parent.SelectSingleNode(childName);
            if (node == null || string.IsNullOrEmpty(node.InnerText.Trim())) return null;
            if (bool.TryParse(node.InnerText.Trim(), out bool v))
                return v;
            Log.Warning($"Invalid bool for {childName} in FaceOverride: '{node.InnerText.Trim()}'");
            return null;
        }

        /// <summary>
        /// Assign a personality to an NPC based on its entity class properties.
        /// 
        /// This mirrors how 7DTD assigns names:
        /// 1. Check if the entity class has a "personality" property
        /// 2. If yes, read the game-selected value (or pick randomly from the list)
        /// 3. If no, fall back to type-based matching from entity class name
        /// 
        /// Returns the assigned PersonalityDefinition, or null if none could be assigned.
        /// </summary>
        public PersonalityDefinition AssignPersonality(EntityAlive npc)
        {
            if (npc == null) return null;

            // Cache key: NPC name (stable across respawns, unlike entityId)
            string npcName = npc.EntityName ?? npc.EntityClass?.entityClassName ?? "unknown";

            // --- 1. Thread-safe cache read ---
            lock (_personalityLock)
            {
                if (_assignedPersonalities.TryGetValue(npcName, out string cachedId))
                {
                    Log.Debug(() => $"[PERSONALITY] Cache hit for NPC {npcName}: '{cachedId}' (entityId: {npc.entityId})");
                    return GetPersonality(cachedId);
                }
            }

            // --- 2. Determine personality outside the lock (avoids blocking main thread) ---
            string personalityId = DeterminePersonalityId(npc);

            // --- 3. Thread-safe write with double-check ---
            if (!string.IsNullOrEmpty(personalityId))
            {
                lock (_personalityLock)
                {
                    // Another thread may have assigned while we were computing
                    if (!_assignedPersonalities.ContainsKey(npcName))
                    {
                        _assignedPersonalities[npcName] = personalityId;
                    }
                    else
                    {
                        // Use the ID another thread already stored
                        personalityId = _assignedPersonalities[npcName];
                    }
                }
                Log.Out($"[PERSONALITY] Assigned '{personalityId}' to NPC {npcName} (entityId: {npc.entityId})");
                return GetPersonality(personalityId);
            }

            return null;
        }

        /// <summary>
        /// Determine the personality ID for an NPC. Called outside the lock.
        /// Mirrors the old AssignPersonality logic (Steps 1-3).
        /// </summary>
        private string DeterminePersonalityId(EntityAlive npc)
        {
            string personalityId = null;

            // --- Step 1: Check entity class "personality" property ---
            // The game's entityclasses.xml can define:
            //   <property name="personality" value="trader_gruff,trader_friendly"/>
            //   <property name="personality" value="Gruff Trader"/>
            // On spawn, the engine picks one value and stores it in the property.
            // The value can be either a personality ID ("trader_gruff") or a display Name ("Gruff Trader").
            string entityPropertyValue = GetPersonalityFromEntityProperty(npc);
            Log.Debug(() => $"AssignPersonality: entity property value = '{entityPropertyValue}' for NPC {npc.EntityName} (class: {npc.EntityClass?.entityClassName ?? "null"})");

            // Try matching by ID first, then by Name
            if (!string.IsNullOrEmpty(entityPropertyValue))
            {
                if (_personalities.ContainsKey(entityPropertyValue))
                {
                    personalityId = entityPropertyValue; // matched by ID
                    Log.Debug(() => $"AssignPersonality: matched by ID '{personalityId}'");
                }
                else
                {
                    personalityId = GetPersonalityIdByName(entityPropertyValue); // try matching by Name
                    if (personalityId != null)
                    {
                        Log.Debug(() => $"AssignPersonality: matched by Name '{entityPropertyValue}' → ID '{personalityId}'");
                    }
                }
            }

            // --- Step 2: Fallback to type-based matching ---
            if (string.IsNullOrEmpty(personalityId) || !_personalities.ContainsKey(personalityId))
            {
                personalityId = GetPersonalityFromEntityType(npc);
            }

            // --- Step 3: Fallback to any random personality ---
            if (string.IsNullOrEmpty(personalityId) || !_personalities.ContainsKey(personalityId))
            {
                personalityId = GetRandomPersonalityId();
            }

            return personalityId;
        }

        /// <summary>
        /// Read the "personality" property from the NPC's entity class.
        /// Uses EntityClass.Properties.Values[key] — same as SCore.
        /// </summary>
        private string GetPersonalityFromEntityProperty(EntityAlive npc)
        {
            if (npc.EntityClass == null) return null;

            // EntityClass.Properties.Values is a public Dictionary<string, string>
            // Same pattern SCore uses: Properties.Values["key"]
            if (npc.EntityClass.Properties.Values.ContainsKey("personality"))
            {
                string value = npc.EntityClass.Properties.Values["personality"];
                Log.Debug(() => $"GetPersonalityFromEntityProperty: read '{value}' for {npc.EntityName}");
                return value;
            }

            Log.Debug(() => $"GetPersonalityFromEntityProperty: no 'personality' property found for {npc.EntityName}");
            return null;
        }

        /// <summary>
        /// Look up a personality ID by its display Name.
        /// Entity class properties may store the Name ("Gruff Trader") instead of the ID ("trader_gruff").
        /// </summary>
        private string GetPersonalityIdByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var kvp in _personalities)
            {
                if (kvp.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug(() => $"GetPersonalityIdByName: matched '{name}' → ID '{kvp.Key}'");
                    return kvp.Key;
                }
            }
            Log.Debug(() => $"GetPersonalityIdByName: no personality found with Name '{name}'");
            return null;
        }

        /// <summary>
        /// Match a personality based on the NPC's entity class name.
        /// E.g., entity class "zombieTrader" → type "trader" → pick from trader_* personalities
        /// </summary>
        private string GetPersonalityFromEntityType(EntityAlive npc)
        {
            string entityClass = npc.EntityClass?.entityClassName?.ToLower() ?? "";
            string typeName = npc.GetType().Name.ToLower();

            // Check entity class name for type keywords
            if (entityClass.Contains("trader"))
            {
                return PickRandomFromType("trader");
            }
            else if (entityClass.Contains("bandit") || entityClass.Contains("hostile"))
            {
                return PickRandomFromType("bandit");
            }
            else if (entityClass.Contains("companion") || entityClass.Contains("hire") || entityClass.Contains("follow"))
            {
                return PickRandomFromType("companion");
            }
            else if (entityClass.Contains("wanderer"))
            {
                return PickRandomFromType("wanderer");
            }

            // Check C# type name for additional hints
            if (typeName.Contains("trader"))
            {
                return PickRandomFromType("trader");
            }
            else if (typeName.Contains("bandit"))
            {
                return PickRandomFromType("bandit");
            }

            // Default: pick from survivor personalities
            return PickRandomFromType("survivor");
        }

        /// <summary>
        /// Pick a random personality ID from a type group (e.g., "trader" → trader_gruff or trader_friendly)
        /// Only considers personalities not excluded from random assignment.
        /// </summary>
        private string PickRandomFromType(string type)
        {
            if (!_personalitiesByType.TryGetValue(type, out List<string> ids))
                return null;

            // Filter to only non-excluded personalities
            var eligible = new List<string>();
            foreach (string id in ids)
            {
                if (_personalities.TryGetValue(id, out var def) && !def.ExcludeFromRandom)
                {
                    eligible.Add(id);
                }
            }

            if (eligible.Count > 0)
            {
                return eligible[UnityEngine.Random.Range(0, eligible.Count)];
            }
            return null;
        }

        /// <summary>
        /// Pick any random personality from all loaded definitions.
        /// Only considers personalities not excluded from random assignment.
        /// </summary>
        private string GetRandomPersonalityId()
        {
            var eligible = new List<string>();
            foreach (var kvp in _personalities)
            {
                if (!kvp.Value.ExcludeFromRandom)
                {
                    eligible.Add(kvp.Key);
                }
            }

            if (eligible.Count == 0) return null;
            return eligible[UnityEngine.Random.Range(0, eligible.Count)];
        }

        /// <summary>
        /// Get a personality definition by ID
        /// </summary>
        public PersonalityDefinition GetPersonality(string id)
        {
            _personalities.TryGetValue(id, out var def);
            return def;
        }

        /// <summary>
        /// Clear cached assignments (e.g., on world reload)
        /// </summary>
        public void ClearAssignments()
        {
            lock (_personalityLock)
            {
                _assignedPersonalities.Clear();
            }
        }

        /// <summary>
        /// Remove a specific NPC's assignment (on entity despawn)
        /// </summary>
        public void RemoveAssignment(int entityId)
        {
            // No-op: assignments are now keyed by NPC name, not entityId.
            // They persist across respawns intentionally.
        }
    }
}
