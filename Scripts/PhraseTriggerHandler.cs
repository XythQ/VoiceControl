using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using UnityEngine;
using XNPCVoiceControl.Net;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Represents a single phrase trigger that maps player input to an NPC action.
    /// Supports multiple phrases (semicolon-delimited in XML) and multiple responses (random pick).
    /// </summary>
    public class PhraseTrigger
    {
        /// <summary>List of phrases to match (case-insensitive substring match on any entry)</summary>
        public List<string> Phrases { get; set; } = new List<string>();

        /// <summary>Responses grouped by language code ("en", "ja"). Populated from &lt;Response lang="..."&gt; elements.</summary>
        public Dictionary<string, List<string>> ResponsesByLang { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Backward-compat: flat list of all responses (merged from all languages).</summary>
        public List<string> Responses
        {
            get
            {
                var flat = new List<string>();
                foreach (var kvp in ResponsesByLang)
                    flat.AddRange(kvp.Value);
                return flat;
            }
        }

        /// <summary>Type of action to perform</summary>
        public TriggerActionType ActionType { get; set; }

        /// <summary>
        /// Action-specific parameter:
        /// - GiveBuff: buff name (e.g., "buffHealHealth")
        /// - GiveItem: item class name (e.g., "foodCornMeal")
        /// - SwapWeapon: NPC weapon class id (e.g., "meleeNPCClubWood")
        /// - CustomDialog: unused (response text or story pool is used)
        /// </summary>
        public string ActionParam { get; set; }

        /// <summary>
        /// Secondary action parameter:
        /// - SwapWeapon: NPC tag name (e.g., "ClubWood") — used to verify the NPC can wield this weapon
        /// </summary>
        public string ActionParam2 { get; set; }

        /// <summary>Number of items to give (for GiveItem), or buff stacks (for GiveBuff)</summary>
        public int ActionParamCount { get; set; } = 1;

        /// <summary>When true, the trigger asks for player confirmation before executing the action</summary>
        public bool RequireConfirmation { get; set; }

        /// <summary>Backwards-compatible: first phrase in the list</summary>
        public string Phrase => Phrases.Count > 0 ? Phrases[0] : null;

        /// <summary>Backwards-compatible: first response in the list</summary>
        public string Response
        {
            get
            {
                if (ResponsesByLang.TryGetValue("en", out var enPool) && enPool.Count > 0) return enPool[0];
                foreach (var kvp in ResponsesByLang)
                    if (kvp.Value.Count > 0) return kvp.Value[0];
                return null;
            }
        }

        public PhraseTrigger(string phrase, string response, TriggerActionType actionType, string actionParam = null, int actionParamCount = 1, string actionParam2 = null)
        {
            if (!string.IsNullOrEmpty(phrase))
                Phrases.Add(phrase.ToLower().Trim());
            if (!string.IsNullOrEmpty(response))
                Responses.Add(response);
            ActionType = actionType;
            ActionParam = actionParam;
            ActionParamCount = actionParamCount;
            ActionParam2 = actionParam2;
        }

        /// <summary>
        /// Constructor accepting lists directly (for runtime addition).
        /// </summary>
        public PhraseTrigger(List<string> phrases, List<string> responses, TriggerActionType actionType, string actionParam = null, int actionParamCount = 1, string actionParam2 = null)
        {
            Phrases = phrases ?? new List<string>();
            // Legacy: flat list defaults to "en" pool
            if (responses != null && responses.Count > 0)
                ResponsesByLang["en"] = new List<string>(responses);
            ActionType = actionType;
            ActionParam = actionParam;
            ActionParamCount = actionParamCount;
            ActionParam2 = actionParam2;
        }

        /// <summary>
        /// Constructor accepting language-keyed response dictionary.
        /// </summary>
        public PhraseTrigger(List<string> phrases, Dictionary<string, List<string>> responsesByLang, TriggerActionType actionType, string actionParam = null, int actionParamCount = 1, string actionParam2 = null)
        {
            Phrases = phrases ?? new List<string>();
            ResponsesByLang = responsesByLang ?? new Dictionary<string, List<string>>();
            ActionType = actionType;
            ActionParam = actionParam;
            ActionParamCount = actionParamCount;
            ActionParam2 = actionParam2;
        }

        /// <summary>Pre-tokenized phrase for zero-allocation matching</summary>
        public struct CachedPhrase
        {
            public string Original;           // "use empty hands" (for substring check)
            public string[] NormalizedTokens; // ["use", "empty", "hand"] (stop words removed, 's' stripped)
        }

        /// <summary>Pre-tokenized aliases for zero-allocation matching</summary>
        public struct CachedAlias
        {
            public string Original;           // "backpack" (for substring check)
        }

        /// <summary>List of alias keywords/phrases for wider matching (e.g. "backpack" for OpenInventory)</summary>
        public List<string> Aliases { get; set; } = new List<string>();

        /// <summary>List of pre-tokenized aliases</summary>
        public List<CachedAlias> CachedAliases { get; set; } = new List<CachedAlias>();

        /// <summary>Pre-tokenized phrases, populated at init time for zero-allocation matching</summary>
        public List<CachedPhrase> CachedPhrases { get; set; } = new List<CachedPhrase>();

        /// <summary>
        /// Pick a random response for the given language.
        /// lang: ISO code ("en", "ja", etc.)
        /// Falls back to "en" pool, then any available pool if exact match missing.
        /// </summary>
        [Obsolete("Use GetRandomResponseForAudio() or GetRandomResponseForSubtitle() instead")]
        public string GetRandomResponse(string lang)
        {
            return GetRandomResponseForSubtitle(lang);
        }

        /// <summary>
        /// Dubbed architecture: always returns English for TTS audio.
        /// All NPCs speak English regardless of personality language.
        /// </summary>
        public string GetRandomResponseForAudio()
        {
            // Always use English pool for spoken audio
            if (ResponsesByLang.TryGetValue("en", out var enPool) && enPool.Count > 0)
            {
                if (enPool.Count == 1) return enPool[0];
                return enPool[UnityEngine.Random.Range(0, enPool.Count)];
            }

            // Last resort: pick from any available pool
            foreach (var kvp in ResponsesByLang)
            {
                if (kvp.Value.Count > 0)
                {
                    if (kvp.Value.Count == 1) return kvp.Value[0];
                    return kvp.Value[UnityEngine.Random.Range(0, kvp.Value.Count)];
                }
            }

            return null;
        }

        /// <summary>
        /// Dubbed architecture: returns localized text for UI subtitles.
        /// playerLang is ISO ("en", "ja", "zh", "de", etc.).
        /// Falls back to "en" pool if exact match missing.
        /// </summary>
        public string GetRandomResponseForSubtitle(string playerLang)
        {
            Log.Debug(() => $"[I18N] GetRandomResponseForSubtitle requested lang='{playerLang}', pools: {string.Join(", ", ResponsesByLang.Keys.Select(k => $"{k}={ResponsesByLang[k].Count}"))}");

            // Try exact language match first
            if (ResponsesByLang.TryGetValue(playerLang, out var pool) && pool.Count > 0)
            {
                if (pool.Count == 1) return pool[0];
                return pool[UnityEngine.Random.Range(0, pool.Count)];
            }

            // Fallback: try "en" pool
            if (ResponsesByLang.TryGetValue("en", out var enPool) && enPool.Count > 0)
            {
                if (enPool.Count == 1) return enPool[0];
                return enPool[UnityEngine.Random.Range(0, enPool.Count)];
            }

            // Last resort: pick from any available pool
            foreach (var kvp in ResponsesByLang)
            {
                if (kvp.Value.Count > 0)
                {
                    if (kvp.Value.Count == 1) return kvp.Value[0];
                    return kvp.Value[UnityEngine.Random.Range(0, kvp.Value.Count)];
                }
            }

            return null; // Empty response — CustomDialog will use story pool
        }

        /// <summary>
        /// Dubbed architecture: pick ONE response variant and return BOTH its English audio text
        /// and the player-language subtitle for the SAME variant index, so the spoken line and the
        /// on-screen subtitle stay in sync. (Previously two independent random draws desynced them,
        /// even in English.)
        /// </summary>
        public void GetRandomResponsePair(string playerLang, out string audioText, out string subtitleText)
        {
            if (!ResponsesByLang.TryGetValue("en", out var enPool) || enPool.Count == 0)
            {
                audioText = GetRandomResponseForAudio();
                subtitleText = GetRandomResponseForSubtitle(playerLang);
                return;
            }
            int i = enPool.Count == 1 ? 0 : UnityEngine.Random.Range(0, enPool.Count);
            audioText = enPool[i];
            if (ResponsesByLang.TryGetValue(playerLang, out var subPool) && subPool.Count > 0)
                subtitleText = subPool[i < subPool.Count ? i : i % subPool.Count];
            else
                subtitleText = audioText;
        }
    }

    /// <summary>
    /// Types of actions a phrase trigger can perform.
    /// These replicate the commands available in the E-key NPC dialog menus (NPCCore dialogs.xml).
    /// </summary>
    public enum TriggerActionType
    {
        None,           // Just show dialog, no game action
        GiveBuff,       // Apply a buff to the player
        GiveItem,       // Give an item to the player's inventory
        CustomDialog,   // Show custom dialog text (empty response = random story from pool)

        // --- NPC Order commands (replicate E-key Command Menu) ---
        OpenInventory,  // Open the NPC's inventory/trade window (SCore ExecuteCMD "OpenInventory")
        FollowPlayer,   // NPC follows the player (buffOrderFollow)
        StopFollow,     // NPC stops following (buffOrderStay)
        GuardHere,      // NPC guards current position (buffOrderGuardHere)
        GuardReturn,    // NPC guards and returns to position (buffOrderGuard)
        Wander,         // NPC patrols around the area (buffOrderWander)
        Loot,           // NPC loots around the area (buffOrderLoot)
        Dismiss,        // Dismiss the NPC (buffOrderDismiss + ExecuteCMD "Dismiss")
        Pickup,         // Pick up the NPC (SCore PickUpNPC)
        Hire,           // Open the hire dialog window (LocalPlayerUI.windowManager.Open "HireInformation")

        // --- Weapon swap (replicate E-key Weapon Menu) ---
        SwapWeapon,     // Swap NPC's weapon (SCore SwapWeapon action, ActionParam = NPC weapon class id, ActionParam2 = tag name)

        // --- Combat mode switching (affects all hired NPCs) ---
        SetCombatMode,  // Set player's varNPCModMode (ActionParam = "0"=Hunting, "1"=Threat Control, "2"=Full Control)

        // --- Trader interaction ---
        OpenTraderInventory,  // Open the trader's trade dialog (simulates pressing E on a trader)

        // --- Vertical movement commands (UAI task-based, not buff-based) ---
        JumpUp,       // Path to and climb the nearest ladder (UAI task UAITaskJumpUpSDX)
        JumpDown,     // Walk off the nearest ledge (UAI task UAITaskJumpDownSDX)

        // --- Patrol & Scout commands ---
        SetPatrolPoint,   // Start recording patrol route (SCore ExecuteCMD "SetPatrol")
        Patrol,           // Start looping recorded patrol route (SCore ExecuteCMD "Patrol")
        CancelPatrolRecord, // Cancel recording / stop active patrol, clear points, order→Stay

        // --- Follow distance adjustment (mode-aware: vcFormationDist if in formation, else vcFollowMin) ---
        SetFollowDistance,  // Mode-aware distance: sets vcFormationDist if in formation, else vcFollowMin

        // --- Formation positioning ---
        SetFormation,         // Set formation angle + tier (ActionParam = bare angle or abs:prefix, handler appends tier)

        // --- Commanded crouch ---
        SetCrouch,          // Set CrouchOverride cvar (ActionParam ∈ {0,1,2}) — inert until SCore ships the hook

        // --- Engagement flag ---
        SetEngage,          // Engagement state (ActionParam "0"=assist only [default], "1"=free-fight, "2"=passive/never)

        // --- Tactical mode toggle (player-wide: command-only vs chat+commands) ---
        SetTacticalMode,    // Toggle varTacticalMode on player (ActionParam "0"=normal, "1"=tactical)
    }

    /// <summary>
    /// Handles phrase-based triggers that cause direct NPC actions (buffs, items, dialog)
    /// without going through the LLM. Checked before LLM processing in NPCChatComponent.
    ///
    /// Triggers and stories are loaded from Config/phrasetriggers.xml at mod startup.
    /// Users can add, edit, or disable triggers by editing the XML — no code changes needed.
    /// </summary>
    public class PhraseTriggerHandler
    {
        private static PhraseTriggerHandler _instance;
        public static PhraseTriggerHandler Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new PhraseTriggerHandler();
                }
                return _instance;
            }
        }

        private List<PhraseTrigger> _triggers = new List<PhraseTrigger>();
        private List<string> _stories = new List<string>();
        private bool _enabled = true;
        private bool _initialized = false;

        /// <summary>Entity IDs currently being collected via pickup. Prevents duplicate pickup commands from spawning dupes.</summary>
        private static readonly HashSet<int> _pendingPickups = new HashSet<int>();

        /// <summary>Pending confirmations keyed by NPC entity ID — one per NPC, no stacking.</summary>
        private Dictionary<int, PendingConfirmation> _pendingConfirmations = new Dictionary<int, PendingConfirmation>();
        private const float ConfirmationTimeoutSeconds = 10f;

        /// <summary>Action name of the trigger matched by most recent TryHandlePhrase call (lowercased), or null. Main-thread only.</summary>
        public string LastMatchedTriggerName { get; private set; }

        /// <summary>ActionParam of the last matched trigger. Used by squad routing to check formation direction.</summary>
        public string LastMatchedActionParam { get; private set; }

        /// <summary>Holds a trigger action waiting for player confirmation (yes/no).</summary>
        private struct PendingConfirmation
        {
            public PhraseTrigger Trigger;
            public EntityPlayer Player;
            public string NpcName;
            public float TimeoutStart;
        }

        /// <summary>Common stop words to strip from input before matching</summary>
        private static readonly HashSet<string> StopWords = new HashSet<string>
        {
            "a", "an", "the", "uh", "um", "oh", "hey", "hi", "hello", "please", "just",
            "can", "could", "would", "will", "do", "does", "did", "is", "are", "was",
            "were", "be", "been", "being", "have", "has", "had", "may", "might",
            "shall", "should", "i", "you", "your", "my", "me", "we", "our", "us",
            "it", "its", "this", "that", "these", "those", "and", "or", "but", "if",
            "so", "because", "as", "when", "then", "than", "to", "of", "in", "for",
            "on", "with", "at", "by", "from", "up", "about", "into", "through",
            "during", "before", "after", "above", "below", "between", "out", "off",
            "over", "under", "again", "further", "once", "here", "there", "where",
            "why", "how", "all", "each", "every", "both", "few", "more", "most",
            "other", "some", "such", "no", "nor", "not", "only", "own", "same",
            "too", "very", "also", "well", "right", "ok", "okay"
        };

        /// <summary>Cached reflection fields for ItemStack manipulation (explicitly approved exception)</summary>
        private static readonly FieldInfo _itemStackAmountField;
        private static readonly FieldInfo _itemStackItemValueField;

        static PhraseTriggerHandler()
        {
            var stackType = typeof(ItemStack);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _itemStackAmountField = stackType.GetField("count", flags) ?? stackType.GetField("amount", flags) ?? stackType.GetField("mAmount", flags);
            _itemStackItemValueField = stackType.GetField("itemValue", flags) ?? stackType.GetField("mItemValue", flags);
            if (_itemStackAmountField == null || _itemStackItemValueField == null)
            {
                Log.Warning($"[PhraseTrigger] ItemStack reflection failed — amount={_itemStackAmountField?.Name ?? "null"}, itemValue={_itemStackItemValueField?.Name ?? "null"}");
            }
        }

        /// <summary>
        /// Whether the handler has been initialized (XML loaded).
        /// </summary>
        public bool Initialized => _initialized;

        /// <summary>
        /// Enable or disable phrase triggers globally.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Get all registered triggers (read-only).
        /// </summary>
        public IReadOnlyList<PhraseTrigger> Triggers => _triggers.AsReadOnly();

        /// <summary>
        /// Initialize the handler by loading triggers and stories from the XML config file.
        /// Call this once during mod InitMod().
        /// </summary>
        /// <param name="modPath">Absolute path to the mod folder (contains Config/)</param>
        public void Initialize(string modPath)
        {
            if (_initialized)
            {
                Log.Warning("PhraseTriggerHandler already initialized, skipping");
                return;
            }

            string configPath = Path.Combine(modPath, "Config", "phrasetriggers.xml");

            if (!File.Exists(configPath))
            {
                Log.Warning($"Phrase triggers config not found at {configPath}. Triggers disabled.");
                _initialized = true;
                _enabled = false;
                return;
            }

            try
            {
                XmlDocument doc = new XmlDocument { XmlResolver = null };
                doc.Load(configPath);

                var root = doc.DocumentElement;
                if (root == null)
                {
                    Log.Error("Invalid phrasetriggers.xml — no root element");
                    _initialized = true;
                    _enabled = false;
                    return;
                }

                // Global enabled flag
                var enabledNode = root.SelectSingleNode("Enabled");
                if (enabledNode != null)
                {
                    if (!bool.TryParse(enabledNode.InnerText, out _enabled))
                    {
                        Log.Warning($"phrasetriggers.xml: invalid <Enabled> value \"{enabledNode.InnerText}\", defaulting to true");
                        _enabled = true;
                    }
                }

                // Load triggers
                LoadTriggers(doc);

                // Load story pool
                LoadStories(doc);

                _initialized = true;
                Log.Out($"PhraseTriggerHandler initialized: {_triggers.Count} triggers, {_stories.Count} stories, enabled={_enabled}");
            }
            catch (Exception ex)
            {
                Log.Error($"Error loading phrasetriggers.xml: {ex.Message}\n{ex.StackTrace}");
                _initialized = true;
                _enabled = false;
            }
        }

        /// <summary>
        /// Parse all &lt;Trigger&gt; elements from the XML document.
        /// </summary>
        private void LoadTriggers(XmlDocument doc)
        {
            var triggerNodes = doc.SelectNodes("//Triggers/Trigger");
            if (triggerNodes == null)
            {
                Log.Debug(() => "No &lt;Triggers&gt; section found in phrasetriggers.xml");
                return;
            }

            foreach (XmlNode node in triggerNodes)
            {
                try
                {
                    // Check per-trigger enabled attribute (defaults to true)
                    var enabledAttr = node.Attributes["enabled"];
                    bool triggerEnabled = true;
                    if (enabledAttr != null && !bool.TryParse(enabledAttr.Value, out triggerEnabled))
                    {
                        Log.Warning($"Bad enabled value \"{enabledAttr.Value}\" on trigger, defaulting to true");
                        triggerEnabled = true;
                    }
                    if (!triggerEnabled) continue;

                    string phraseRaw = GetNodeValue(node, "Phrase", "");
                    if (string.IsNullOrWhiteSpace(phraseRaw))
                    {
                        Log.Warning("Skipping trigger with empty Phrase");
                        continue;
                    }

                    // Split semicolon-delimited phrases: "follow me;come with me;stick with me"
                    var phrases = phraseRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.ToLower().Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();

                    if (phrases.Count == 0)
                    {
                        Log.Warning("Skipping trigger with empty Phrase after split");
                        continue;
                    }

                    // Load responses: support both new <Response lang="en"> format and legacy flat format.
                    var responsesByLang = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var responseNodes = node.SelectNodes("Response");
                    if (responseNodes != null && responseNodes.Count > 0)
                    {
                        foreach (XmlNode respNode in responseNodes)
                        {
                            string langAttr = respNode.Attributes?["lang"]?.Value ?? "en";
                            string raw = respNode.InnerText.Trim();

                            if (!string.IsNullOrEmpty(raw))
                            {
                                var pool = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(r => r.Trim())
                                    .Where(r => !string.IsNullOrEmpty(r))
                                    .ToList();
                                if (pool.Count > 0)
                                {
                                    responsesByLang[langAttr] = pool;
                                }
                            }
                        }
                    }
                    // Legacy fallback: if no <Response> elements found, check for old flat format via GetNodeValue
                    if (responsesByLang.Count == 0)
                    {
                        string responseRaw = GetNodeValue(node, "Response", "");
                        if (!string.IsNullOrEmpty(responseRaw))
                        {
                            var pool = responseRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(r => r.Trim())
                                .Where(r => !string.IsNullOrEmpty(r))
                                .ToList();
                            if (pool.Count > 0)
                                responsesByLang["en"] = pool; // legacy defaults to English
                        }
                    }

                    string actionTypeStr = GetNodeValue(node, "ActionType", "None");
                    string actionParam = GetNodeValue(node, "ActionParam", "");
                    string actionParam2 = GetNodeValue(node, "ActionParam2", "");
                    int actionParamCount;
                    string apcRaw = GetNodeValue(node, "ActionParamCount", "1");
                    if (!int.TryParse(apcRaw, out actionParamCount))
                    {
                        Log.Warning($"Bad ActionParamCount \"{apcRaw}\", defaulting to 1");
                        actionParamCount = 1;
                    }

                    // Read requireConfirmation attribute (defaults to false)
                    var reqConfirmAttr = node.Attributes["requireConfirmation"];
                    bool requireConfirmation = false;
                    if (reqConfirmAttr != null && !bool.TryParse(reqConfirmAttr.Value, out requireConfirmation))
                    {
                        Log.Warning($"Bad requireConfirmation value \"{reqConfirmAttr.Value}\", defaulting to false");
                        requireConfirmation = false;
                    }

                    if (!Enum.TryParse<TriggerActionType>(actionTypeStr, true, out var actionType))
                    {
                        Log.Warning($"Unknown ActionType \"{actionTypeStr}\" for phrase \"{phrases[0]}\", defaulting to None");
                        actionType = TriggerActionType.None;
                    }

                    var trigger = new PhraseTrigger(phrases, responsesByLang, actionType, actionParam, actionParamCount, actionParam2);
                    trigger.RequireConfirmation = requireConfirmation;
                    // Pre-tokenize phrases for zero-allocation matching at runtime
                    foreach (var phrase in phrases)
                    {
                        trigger.CachedPhrases.Add(new PhraseTrigger.CachedPhrase
                        {
                            Original = phrase,
                            NormalizedTokens = NormalizeTokens(phrase)
                        });
                    }

                    // Parse <Aliases> element: semicolon-delimited keywords for wider matching
                    string aliasesRaw = GetNodeValue(node, "Aliases", "");
                    if (!string.IsNullOrEmpty(aliasesRaw))
                    {
                        var aliasList = aliasesRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(a => a.ToLower().Trim())
                            .Where(a => !string.IsNullOrEmpty(a))
                            .ToList();
                        foreach (var alias in aliasList)
                        {
                            trigger.Aliases.Add(alias);
                            trigger.CachedAliases.Add(new PhraseTrigger.CachedAlias { Original = alias });
                        }
                    }

                    _triggers.Add(trigger);
                }
                catch (Exception ex)
                {
                    Log.Error($"Error parsing trigger node: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Parse all &lt;Story&gt; elements from the XML document.
        /// </summary>
        private void LoadStories(XmlDocument doc)
        {
            var storyNodes = doc.SelectNodes("//Stories/Story");
            if (storyNodes == null)
            {
                Log.Debug(() => "No &lt;Stories&gt; section found in phrasetriggers.xml");
                return;
            }

            foreach (XmlNode node in storyNodes)
            {
                string text = node.InnerText?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    _stories.Add(text);
                }
            }
        }

        /// <summary>
        /// Helper: get child node InnerText or defaultValue.
        /// </summary>
        private static string GetNodeValue(XmlNode parent, string childName, string defaultValue)
        {
            var child = parent.SelectSingleNode(childName);
            return child?.InnerText?.Trim() ?? defaultValue;
        }

        /// <summary>
        /// Check if the player's message matches any registered phrase trigger.
        /// If a match is found, optionally executes the action and returns true.
        /// Returns false if no trigger matched (message should proceed to LLM).
        /// </summary>
        /// <param name="playerMessage">The raw player message (lowercase comparison)</param>
        /// <param name="npc">The NPC entity responding</param>
        /// <param name="player">The player entity receiving actions</param>
        /// <param name="npcName">Display name of the NPC for dialog formatting</param>
        /// <param name="response">Output: the NPC's spoken audio text (always English in dubbed mode)</param>
        /// <param name="subtitleResponse">Output: localized subtitle text for UI display (player's UI language)</param>
        /// <param name="executeAction">Whether to execute the game action (default true). Set false for dialogue-only matching.</param>
        /// <param name="usedName">True if player used NPC name to target, granting +1 intent bonus on phrase matching.</param>
        /// <param name="npcLanguage">NPC's TTS language code (retained for backward compat, audio always English now).</param>
        /// <param name="playerUiLanguage">Player's UI language (ISO code). Controls subtitle text.</param>
        /// <returns>True if a trigger matched and was handled, false to proceed to LLM</returns>
        /// <summary>
        /// Quick peek: check if a message matches any phrase trigger with an action command.
        /// Used by combat gate to allow commands (hire, follow, guard) through while blocking
        /// dialogue triggers (story, custom dialog) for wild NPCs in combat.
        /// Does NOT execute the trigger — just returns whether it would match and its ActionType.
        /// </summary>
        public bool IsActionCommand(string playerMessage, out TriggerActionType actionType)
        {
            actionType = TriggerActionType.None;
            if (!_enabled || !_initialized || string.IsNullOrWhiteSpace(playerMessage)) return false;

            string lowerMessage = playerMessage.ToLower().Trim();
            string[] msgTokens = new string[30];
            int msgTokenCount = SplitIntoBuffer(lowerMessage, msgTokens);

            // Exact substring first — most specific wins.
            foreach (var trigger in _triggers)
            {
                foreach (var cached in trigger.CachedPhrases)
                {
                    if (lowerMessage.Contains(cached.Original))
                    {
                        actionType = trigger.ActionType;
                        return true;
                    }
                }
            }

            // Alias match — widest net, runs after exact substring.
            foreach (var trigger in _triggers)
            {
                foreach (var cached in trigger.CachedAliases)
                {
                    if (lowerMessage.Contains(cached.Original))
                    {
                        actionType = trigger.ActionType;
                        return true;
                    }
                }
            }

            return false; // Don't bother with token/fuzzy stages for combat gate
        }

        /// <summary>
        /// Check if the last matched trigger is a slot-direction formation command (dir 1-4).
        /// Used by squad routing to reject "squad, left flank" — can't stack all NPCs on one coordinate.
        /// Returns false for cancel (dir==0) and non-formation commands.
        /// </summary>
        public bool IsSlotDirectionCommand()
        {
            if (LastMatchedTriggerName != "setformation") return false;
            // Parse angle,tierIdx from the last matched arg.
            string lastArg = LastMatchedActionParam;
            if (string.IsNullOrEmpty(lastArg) || lastArg.IndexOf(',') < 0) return false;
            var parts = lastArg.Split(',');
            int tierIdx = 0;
            int.TryParse(parts.Length > 1 ? parts[1] : "0", out tierIdx);
            return tierIdx != 0; // any non-cancel formation is a slot command
        }

        /// <summary>
        /// Resolve VCCommand and arg from the last matched trigger.
        /// Used by squad routing to fan-out commands without re-parsing.
        /// Returns false if the trigger doesn't map to a VCCommand.
        /// </summary>
        public bool TryResolveSquadCommand(out Net.VCCommand cmd, out string arg, string playerMessage)
        {
            cmd = Net.VCCommand.None;
            arg = "";

            if (string.IsNullOrEmpty(LastMatchedTriggerName)) return false;

            switch (LastMatchedTriggerName)
            {
                case "followplayer":
                    cmd = Net.VCCommand.Follow;
                    return true;
                case "stopfollow":
                    cmd = Net.VCCommand.Stay;
                    return true;
                case "guardhere":
                case "guardreturn":
                    cmd = Net.VCCommand.Guard;
                    return true;
                case "wander":
                    cmd = Net.VCCommand.Wander;
                    return true;
                case "setfollowdistance":
                    cmd = Net.VCCommand.SetFollowDistance;
                    arg = LastMatchedActionParam ?? "2.5"; // distance from trigger XML
                    return true;
                case "setcrouch":
                    cmd = Net.VCCommand.SetCrouch;
                    arg = LastMatchedActionParam ?? "1";
                    return true;
                case "setformation":
                    cmd = Net.VCCommand.SetFormation;
                    // ActionParam may be verbatim "angle,tierIdx" (e.g. "0,0" cancel) or bare angle.
                    if (LastMatchedActionParam != null && LastMatchedActionParam.IndexOf(',') >= 0)
                    {
                        arg = LastMatchedActionParam; // pass through verbatim
                    }
                    else
                    {
                        float angle = 0f;
                        float.TryParse(LastMatchedActionParam, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out angle);
                        int tierIdx = 2; // default wide
                        if (!string.IsNullOrEmpty(playerMessage))
                        {
                            var msg = playerMessage.ToLower();
                            if (msg.Contains("tight") || msg.Contains("close") || msg.Contains("short"))
                                tierIdx = 1;
                        }
                        arg = $"{angle},{tierIdx}";
                    }
                    return true;

                case "setcombatmode":
                    cmd = Net.VCCommand.SetCombatMode;
                    arg = LastMatchedActionParam ?? "0"; // default Hunting
                    return true;
                case "setengage":
                    cmd = Net.VCCommand.SetEngage;
                    arg = LastMatchedActionParam ?? "0"; // default hold
                    return true;
                case "settacticalmode":
                    cmd = Net.VCCommand.SetTacticalMode;
                    arg = LastMatchedActionParam ?? "0";
                    return true;
                default:
                    return false;
            }
        }

        public bool TryHandlePhrase(string playerMessage, EntityAlive npc, EntityPlayer player, string npcName, out string response, bool executeAction = true, bool usedName = false, string npcLanguage = "en", string playerUiLanguage = "en")
        {
            // Backward compat: if caller didn't provide subtitle output, alias it to response
            return TryHandlePhrase(playerMessage, npc, player, npcName, out response, out _, executeAction, usedName, npcLanguage, playerUiLanguage);
        }

        /// <summary>
        /// Dubbed architecture version: returns separate audio (English) and subtitle (localized) text.
        /// </summary>
        public bool TryHandlePhrase(string playerMessage, EntityAlive npc, EntityPlayer player, string npcName, out string audioResponse, out string subtitleResponse, bool executeAction = true, bool usedName = false, string npcLanguage = "en", string playerUiLanguage = "en")
        {
            audioResponse = null;
            subtitleResponse = null;
            LastMatchedTriggerName = null;

            if (!_enabled || !_initialized || string.IsNullOrWhiteSpace(playerMessage))
            {
                if (!_initialized)
                {
                    Log.Warning($"PhraseTriggerHandler not initialized! Triggers not loaded.");
                }
                if (!_enabled)
                {
                    Log.Debug(() => $"Phrase triggers disabled, skipping check for: \"{playerMessage}\"");
                }
                return false;
            }

            string lowerMessage = playerMessage.ToLower().Trim();

            // STT phonetic-fix — narrow cardinal-mishear normalizer.
            // Whole-word replace only when the first token is an imperative positional verb.
            lowerMessage = FixCardinalMishears(lowerMessage);

            Log.Debug(() => $"Phrase trigger check: \"{lowerMessage}\" against {_triggers.Count} triggers");

            // === PENDING CONFIRMATION PRE-CHECK (runs before normal matching) ===
            // If this NPC has a pending confirmation, handle yes/no/cancel/timeout first.
            // This short-circuits so "yes" doesn't match a different trigger.
            if (npc != null && _pendingConfirmations.TryGetValue(npc.entityId, out var pending))
            {
                float elapsed = Time.realtimeSinceStartup - pending.TimeoutStart;
                if (elapsed > ConfirmationTimeoutSeconds)
                {
                    // Timeout — discard and fall through to normal matching
                    _pendingConfirmations.Remove(npc.entityId);
                }
                else if (lowerMessage.Contains("yes") || lowerMessage.Contains("confirm"))
                {
                    // Player confirmed — execute the stored trigger
                    _pendingConfirmations.Remove(npc.entityId);
                    return ExecuteMatch(pending.Trigger, npc, pending.Player, pending.NpcName, executeAction, npcLanguage, playerUiLanguage, out audioResponse, out subtitleResponse, alreadyConfirmed: true, playerMessage: lowerMessage);
                }
                else if (lowerMessage.Contains("no") || lowerMessage.Contains("cancel"))
                {
                    // Player cancelled
                    _pendingConfirmations.Remove(npc.entityId);
                    audioResponse = "Understood.";
                    subtitleResponse = "Understood.";
                    return true;
                }
                else
                {
                    // Still waiting — remind player
                    audioResponse = "Say yes to confirm, or no to cancel.";
                    subtitleResponse = "Say yes to confirm, or no to cancel.";
                    return true;
                }
            }

            // Pre-split message tokens once (reused across all trigger checks)
            string[] msgTokens = new string[30];
            int msgTokenCount = SplitIntoBuffer(lowerMessage, msgTokens);

            // Global waterfall matching: check ALL triggers at each stage before advancing.
            // Principle: most specific wins — exact substring first, then alias reach,
            // then token overlap, then fuzzy. This prevents a single-word alias ("stay")
            // from hijacking a longer phrase ("stay close") that has its own trigger.
            // Stage 0: Exact substring (fastest, most precise)
            // Stage 1: Alias keyword match (simple substring, widest net)
            // Stage 2: Exact token overlap (+ intent bonus)
            // Stage 3: Fuzzy token overlap (Levenshtein ≤ 1, length guard, multi-token only)

            // Stage 0: Exact substring — scan all triggers first.
            foreach (var trigger in _triggers)
            {
                foreach (var cached in trigger.CachedPhrases)
                {
                    if (lowerMessage.Contains(cached.Original))
                    {
                        Log.Debug(() => $"Phrase trigger matched: \"{cached.Original}\" → {trigger.ActionType} (Exact)");
                        return ExecuteMatch(trigger, npc, player, npcName, executeAction, npcLanguage, playerUiLanguage, out audioResponse, out subtitleResponse, playerMessage: lowerMessage);
                    }
                }
            }

            // Stage 1: Alias keyword match — scan all triggers for alias keywords.
            // The widest net, so it runs after exact substring to avoid hijacking longer phrases.
            foreach (var trigger in _triggers)
            {
                foreach (var cached in trigger.CachedAliases)
                {
                    bool isMultiWord = cached.Original.IndexOf(' ') >= 0;

                    // Word-boundary check: prevents "income with" matching alias "come with"
                    if (!ContainsWithWordBoundary(lowerMessage, cached.Original)) continue;

                    // NOTE: a word-weight guard was tried here (single-word aliases require >=25% of sentence words)
                    // to stop an incidental "stay" in a long sentence from firing StopFollow. Stripped 2026-07-15 to
                    // isolate the stage-reorder fix. Re-add deliberately IF long-sentence false-triggers show up in
                    // testing — the reorder alone does NOT address that case. Threshold needs real data, not a guess.

                    Log.Debug(() => $"Phrase trigger matched via alias: \"{cached.Original}\" → {trigger.ActionType}");
                    return ExecuteMatch(trigger, npc, player, npcName, executeAction, npcLanguage, playerUiLanguage, out audioResponse, out subtitleResponse, playerMessage: lowerMessage);
                }
            }

            // Stage 2: Exact token overlap — scan all triggers, multi-token phrases only.
            // Skip if message has < 2 tokens after stop-word removal — a single token matching
            // a multi-word trigger is always a false positive (e.g. "no, i'm not" → [im] matching
            // "i'm interested in hiring you" on just the "im" token).
            if (msgTokenCount >= 2)
            {
                foreach (var trigger in _triggers)
                {
                    foreach (var cached in trigger.CachedPhrases)
                    {
                        if (cached.NormalizedTokens.Length < 2) continue;

                        // If the message carries more content than the trigger phrase itself, the
                    // player said extra things beyond the trigger's concepts — require the FULL
                    // trigger to be present, not just 60%. A short trigger's tokens scattered
                    // across a longer, mostly-unrelated message is weak evidence otherwise
                    // (e.g. "any"+"water" matching inside "have you seen any water filters
                    // around here" when the trigger is "got any water" — "got" never appears).
                    // Messages the same length or shorter than the trigger keep the 60% rule
                    // (handles STT noise / terser phrasing of a genuine match).
                    int phraseLen = cached.NormalizedTokens.Length;
                    int required = msgTokenCount > phraseLen
                        ? phraseLen
                        : Math.Min(GetRequiredMatches(phraseLen), msgTokenCount);
                        // Name picks the TARGET, never fabricates command content.
                        // Without this, "take west" matches "take cover" on verb alone + name bonus = magnet,
                        // and "come with me" matches "come closer" the same way.
                        int exactMatches = CountTokenMatches(msgTokens, msgTokenCount, cached.NormalizedTokens);
                        if (exactMatches >= required)
                        {
                            Log.Debug(() => $"Phrase trigger matched: \"{cached.Original}\" → {trigger.ActionType} (Token {exactMatches}/{required})");
                            return ExecuteMatch(trigger, npc, player, npcName, executeAction, npcLanguage, playerUiLanguage, out audioResponse, out subtitleResponse, playerMessage: lowerMessage);
                        }
                    }
                }
            }

            // Stage 3: Fuzzy token overlap — contiguous sequence match (not scattered).
            // Requires all phrase tokens to appear in order within a sliding window of the message.
            // Prevents "use your m60" matching "I used to be a wizard" via scattered tokens.
            if (msgTokenCount > 0)
            {
                foreach (var trigger in _triggers)
                {
                    foreach (var cached in trigger.CachedPhrases)
                    {
                        if (cached.NormalizedTokens.Length < 2) continue;

                        // Sliding window: try to match phrase tokens contiguously starting at each position
                        int phraseLen = cached.NormalizedTokens.Length;
                        for (int start = 0; start <= msgTokenCount - phraseLen; start++)
                        {
                            int matches = 0;
                            for (int p = 0; p < phraseLen; p++)
                            {
                                string msgToken = msgTokens[start + p];
                                string phraseToken = cached.NormalizedTokens[p];

                                // Length ratio guard: skip if one word is more than 2x the other
                                int lenDiff = Math.Abs(msgToken.Length - phraseToken.Length);
                                if (lenDiff > 2) break; // Contiguous match broken

                                if (msgToken == phraseToken || LevenshteinDistance(msgToken, phraseToken) <= 1)
                                    matches++;
                                else
                                    break; // Contiguous match broken
                            }

                            if (matches >= phraseLen)
                            {
                                Log.Debug(() => $"Phrase trigger matched: \"{cached.Original}\" → {trigger.ActionType} (FuzzyContiguous {matches}/{phraseLen})");
                                return ExecuteMatch(trigger, npc, player, npcName, executeAction, npcLanguage, playerUiLanguage, out audioResponse, out subtitleResponse, playerMessage: lowerMessage);
                            }
                        }
                    }
                }
            }

            Log.Debug(() => $"No phrase trigger matched for: \"{lowerMessage}\"");
            return false;
        }

        /// <summary>
        /// Execute the action for a matched phrase trigger.
        /// <summary>
        /// Execute the action for a matched phrase trigger.
        /// Returns the dialog text (possibly modified for error fallbacks).
        /// Dialog display is handled by NPCChatComponent.
        /// </summary>
        private string ExecuteTriggerAction(PhraseTrigger trigger, EntityAlive npc, EntityPlayer player, string npcName, string dialogText, string playerMessage = null)
        {
            if (player == null)
            {
                Log.Debug(() => "Phrase trigger: player is null, skipping action");
                return dialogText;
            }

            switch (trigger.ActionType)
            {
                case TriggerActionType.GiveBuff:
                    return GiveBuffToPlayer(player, trigger, npcName, dialogText);

                case TriggerActionType.GiveItem:
                    return GiveItemToPlayer(npc, player, trigger, npcName, dialogText);

                case TriggerActionType.OpenInventory:
                    return OpenInventoryRouted(npc, player, npcName, dialogText);

                case TriggerActionType.FollowPlayer:
                    return FollowPlayer(npc, player, npcName, dialogText);

                case TriggerActionType.StopFollow:
                    return StopFollow(npc, player, npcName, dialogText);

                case TriggerActionType.GuardHere:
                    return GuardHere(npc, player, npcName, dialogText);

                case TriggerActionType.GuardReturn:
                    return GuardReturn(npc, player, npcName, dialogText);

                case TriggerActionType.Wander:
                    return Wander(npc, player, npcName, dialogText);

                case TriggerActionType.Loot:
                    return Loot(npc, player, npcName, dialogText);

                case TriggerActionType.Dismiss:
                    return Dismiss(npc, player, npcName, dialogText);

                case TriggerActionType.Pickup:
                    return Pickup(npc, player, npcName, dialogText);

                case TriggerActionType.Hire:
                    return Hire(npc, player, npcName, dialogText);

                case TriggerActionType.SwapWeapon:
                    return SwapWeaponRouted(npc, player, trigger, npcName, dialogText);

                case TriggerActionType.SetCombatMode:
                    VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetCombatMode, trigger.ActionParam ?? "0");
                    return dialogText;

                case TriggerActionType.OpenTraderInventory:
                    return OpenTraderInventory(npc, player, npcName, dialogText);

                case TriggerActionType.JumpUp:
                    return JumpUp(npc, player, npcName, dialogText);

                case TriggerActionType.JumpDown:
                    return JumpDown(npc, player, npcName, dialogText);

                case TriggerActionType.SetPatrolPoint:
                    return SetPatrolPoint(npc, player, npcName, dialogText);

                case TriggerActionType.Patrol:
                    return Patrol(npc, player, npcName, dialogText);

                case TriggerActionType.CancelPatrolRecord:
                    return CancelPatrolRecord(npc, player, npcName, dialogText);

                case TriggerActionType.SetFollowDistance:
                    return SetFollowDistance(npc, player, trigger, npcName, dialogText);

                case TriggerActionType.SetFormation:
                    return SetFormation(npc, player, trigger, playerMessage, npcName, dialogText);

                case TriggerActionType.SetCrouch:
                    return SetCrouch(npc, player, trigger, npcName, dialogText);

                case TriggerActionType.SetEngage:
                    VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetEngage, trigger.ActionParam ?? "0");
                    return dialogText;

                case TriggerActionType.SetTacticalMode:
                    VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetTacticalMode, trigger.ActionParam ?? "0");
                    return dialogText;

                case TriggerActionType.CustomDialog:
                case TriggerActionType.None:
                    // No game action, just dialog — handled by NPCChatComponent
                    return dialogText;
            }

            return dialogText;
        }

        #region Buff Actions

        /// <summary>
        /// Apply a buff to the player entity.
        /// Uses EntityBuffs.AddBuff(string _name, int _stacks, bool _bypassCooldown).
        /// Buff name must be a valid buff registered in the game's BuffManager.
        /// </summary>
        private string GiveBuffToPlayer(EntityPlayer player, PhraseTrigger trigger, string npcName, string dialogText)
        {
            string buffName = trigger.ActionParam;
            int stacks = trigger.ActionParamCount;

            if (string.IsNullOrEmpty(buffName))
            {
                Log.Warning("GiveBuff trigger has no buff name specified");
                return dialogText;
            }

            try
            {
                // AddBuff returns BuffStatus enum (0 = success, non-zero = various failure reasons)
                int status = (int)player.Buffs.AddBuff(buffName, stacks, false);

                if (status == 0) // BuffStatus.OK
                {
                    Log.Debug(() => $"Gave buff \"{buffName}\" (stacks: {stacks}) to player");
                    return dialogText;
                }
                else
                {
                    Log.Warning($"Failed to give buff \"{buffName}\": status={status}");
                    return dialogText + " * Hmm, something didn't work right.";
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Exception giving buff \"{buffName}\": {ex.Message}");
                return dialogText;
            }
        }

        #endregion

        #region Item Actions

        /// <summary>
        /// Give an item to the player's inventory, taking it from the NPC first.
        /// Checks NPC inventory (lootContainer, inventory, bag) for the item.
        /// If found, removes one stack from NPC and adds to player.
        /// Uses ItemClass.nameToItem to look up the item, then ItemStack.AddToItemStackArray
        /// to handle stacking and slot assignment automatically.
        /// </summary>
        private string GiveItemToPlayer(EntityAlive npc, EntityPlayer player, PhraseTrigger trigger, string npcName, string dialogText)
        {
            string itemName = trigger.ActionParam;
            int count = trigger.ActionParamCount;

            if (string.IsNullOrEmpty(itemName))
            {
                Log.Warning("GiveItem trigger has no item name specified");
                return dialogText;
            }

            if (npc == null)
            {
                Log.Debug(() => "GiveItem: NPC is null");
                return dialogText + " * Sorry, I don't have that on me.";
            }

            try
            {
                // Look up item class by name
                ItemClass itemClass = ItemClass.nameToItem[itemName];
                if (itemClass == null)
                {
                    Log.Warning($"Item \"{itemName}\" not found in ItemClass.nameToItem");
                    return dialogText + " * Sorry, I don't have that on me.";
                }

                // Check 1: NPC must have the item in inventory (block if missing)
                if (!NpcHasItem(npc, itemName))
                {
                    Log.Debug(() => $"GiveItem: NPC {npc.entityId} ({npcName}) doesn't have '{itemName}'");
                    return dialogText + " * Sorry, I don't have that on me.";
                }

                // Remove one stack from NPC's container (lootContainer, inventory, or bag)
                ItemValue itemValue = new ItemValue(itemClass.pId);
                bool removed = NpcRemoveItem(npc, itemValue, count);
                if (!removed)
                {
                    Log.Warning($"GiveItem: Failed to remove '{itemName}' from NPC {npc.entityId} ({npcName})");
                    return dialogText + " * Hmm, something went wrong.";
                }

                // Create item stack and add to player's bag (backpack)
                ItemStack itemStack = new ItemStack(itemValue, count);
                ItemStack[] slots = player.bag.GetSlots();
                int slotIndex = ItemStack.AddToItemStackArray(slots, itemStack, -1);

                Log.Debug(() => $"GiveItem: AddToItemStackArray returned slotIndex={slotIndex}, slots.Length={slots.Length}");
                if (slotIndex >= 0 && slotIndex < slots.Length)
                {
                    Log.Debug(() => $"GiveItem: slot[{slotIndex}] = {slots[slotIndex]?.ToString() ?? "null"}");
                }

                if (slotIndex >= 0)
                {
                    Log.Debug(() => $"Gave {count}x {itemName} to player from NPC {npc.entityId} ({npcName}) (slot {slotIndex})");
                    return dialogText;
                }
                else
                {
                    // Return item to NPC since player inventory is full
                    Log.Warning($"GiveItem: Player inventory full, returning '{itemName}' to NPC {npc.entityId}");
                    return dialogText + " * Wait, your inventory's full!";
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Exception giving item \"{itemName}\": {ex.Message}");
                return dialogText;
            }
        }

        /// <summary>
        /// Resolve the NPC's lootContainer, supporting both EntityAliveSDX (V2/V3) and
        /// EntityAliveSDXV4 — both classes have a public lootContainer field.
        /// </summary>
        private static SCoreLootContainer GetLootContainer(EntityAlive npc)
        {
            if (npc is EntityAliveSDX sdx) return sdx.lootContainer;
            if (npc is EntityAliveSDXV4 v4) return v4.lootContainer;
            return null;
        }

        /// <summary>
        /// Remove items from NPC's containers, matching SCore's container hierarchy:
        ///   1. HarvestManager (player-added items via "Open Inventory")
        ///   2. lootContainer (SCore NPC storage) — uses HasItem + RemoveItem(ItemValue)
        ///   3. inventory (equipment slots) — uses GetSlots + RemoveFromSlot
        ///   4. bag (backpack/toolbelt) — uses GetSlots + RemoveFromSlot
        /// </summary>
        private static bool NpcRemoveItem(EntityAlive npc, ItemValue itemValue, int count)
        {
            if (npc == null || itemValue == null) return false;

            // Try HarvestManager first (player-added items via "Open Inventory")
            try
            {
                if (HarvestManager.Has(npc.entityId))
                {
                    var container = HarvestManager.GetOrCreate(npc.entityId);
                    if (container != null && container.HasItem(itemValue))
                    {
                        int removed = RemoveFromContainerItems(container.items, container, itemValue, count);
                        if (removed > 0)
                        {
                            Log.Debug(() => $"NpcRemoveItem: removed {removed}x from HarvestManager");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"NpcRemoveItem: HarvestManager removal failed: {ex.Message}");
            }

            // Try lootContainer (SCore NPC storage — EntityAliveSDX or EntityAliveSDXV4)
            {
                SCoreLootContainer lc = GetLootContainer(npc);
                if (lc != null && lc.HasItem(itemValue))
                {
                    int removed = RemoveFromContainerItems(lc.items, lc, itemValue, count);
                    if (removed > 0)
                    {
                        Log.Debug(() => $"NpcRemoveItem: removed {removed}x from lootContainer");
                        return true;
                    }
                }
            }

            // Try inventory (equipment slots) — find matching slot and remove
            if (npc.inventory != null)
            {
                int removed = RemoveFromSlots(npc.inventory.GetSlots(), itemValue, count);
                if (removed > 0)
                {
                    Log.Debug(() => $"NpcRemoveItem: removed {removed}x from inventory");
                    return true;
                }
            }

            // Try bag (backpack/toolbelt) — find matching slot and remove
            if (npc.bag != null)
            {
                int removed = RemoveFromSlots(npc.bag.GetSlots(), itemValue, count);
                if (removed > 0)
                {
                    Log.Debug(() => $"NpcRemoveItem: removed {removed}x from bag");
                    return true;
                }
            }

            Log.Warning($"NpcRemoveItem: item not found in any NPC container");
            return false;
        }

        /// <summary>
        /// Remove items from a container's ItemStack[] array (HarvestManager or SCoreLootContainer).
        /// Unlike RemoveFromSlots, this calls UpdateSlot after modifying — required by these containers.
        /// RemoveItem(ItemValue) removes the ENTIRE stack per call (confirmed in SCore source),
        /// so we must decrement the count field directly and only clear when it hits zero.
        /// Returns the number of items actually removed.
        /// </summary>
        private static int RemoveFromContainerItems(ItemStack[] items, ITileEntityLootable container, ItemValue itemValue, int count)
        {
            if (items == null || itemValue == null)
            {
                Log.Debug(() => $"RemoveFromContainerItems: early exit — items={items != null}, itemValue={itemValue != null}");
                return 0;
            }
            if (_itemStackAmountField == null || _itemStackItemValueField == null)
            {
                Log.Warning($"RemoveFromContainerItems: reflection fields null — amount={_itemStackAmountField?.Name ?? "null"}, itemValue={_itemStackItemValueField?.Name ?? "null"}");
                return 0;
            }
            int removed = 0;

            for (int i = 0; i < items.Length && removed < count; i++)
            {
                if (items[i] != null && !items[i].IsEmpty())
                {
                    var stackValue = _itemStackItemValueField.GetValue(items[i]) as ItemValue;
                    if (stackValue != null && stackValue.type == itemValue.type)
                    {
                        int currentAmount = (int)_itemStackAmountField.GetValue(items[i]);
                        Log.Debug(() => $"RemoveFromContainerItems: matched slot {i} — {itemValue.ItemClass.Name} amount={currentAmount}, want={count}");
                        int takeCount = Math.Min(currentAmount, count - removed);
                        int remaining = currentAmount - takeCount;
                        removed += takeCount;

                        if (remaining <= 0)
                        {
                            items[i] = ItemStack.Empty;
                            container.UpdateSlot(i, ItemStack.Empty);
                        }
                        else
                        {
                            _itemStackAmountField.SetValue(items[i], remaining);
                            container.UpdateSlot(i, items[i]);
                        }

                        if (removed >= count) break;
                    }
                }
            }

            Log.Debug(() => $"RemoveFromContainerItems: removed={removed}/{count} from {items.Length} slots");
            return removed;
        }

        /// <summary>
        /// Remove items from an ItemStack[] array by finding matching slots and clearing them.
        /// Returns the number of items actually removed.
        /// Uses reflection to access internal fields since 7DTD doesn't expose setters for amount/itemValue.
        /// </summary>
        private static int RemoveFromSlots(ItemStack[] slots, ItemValue itemValue, int count)
        {
            if (slots == null || itemValue == null) return 0;
            if (_itemStackAmountField == null || _itemStackItemValueField == null) return 0;
            int removed = 0;

            for (int i = 0; i < slots.Length && removed < count; i++)
            {
                if (slots[i] != null && !slots[i].IsEmpty())
                {
                    // Compare by item type ID via cached reflection fields
                    var stackValue = _itemStackItemValueField.GetValue(slots[i]) as ItemValue;
                    if (stackValue != null && stackValue.type == itemValue.type)
                    {
                        int currentAmount = (int)_itemStackAmountField.GetValue(slots[i]);
                        int takeCount = Math.Min(currentAmount, count - removed);
                        _itemStackAmountField.SetValue(slots[i], currentAmount - takeCount);
                        removed += takeCount;

                        // If stack is now empty, clear the slot
                        if ((int)_itemStackAmountField.GetValue(slots[i]) <= 0)
                        {
                            slots[i] = null;
                        }
                    }
                }
            }

            return removed;
        }

        #endregion

        #region Inventory Actions

        /// <summary>
        /// Route OpenInventory through VCCommandRouter for SP/MP uniformity.
        /// AUTHORITY — the server initiates the container lock and the engine opens the client window itself.
        /// Traders redirect to OpenTraderInventory (no leader, client-side E-key sim).
        /// </summary>
        private string OpenInventoryRouted(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "OpenInventory: NPC is null");
                return dialogText ?? "*Sorry, I can't show you my inventory right now.";
            }

            // If target is a trader, use OpenTraderInventory (has InteractWithEntity fallback)
            if (IsTraderEntity(npc))
            {
                Log.Debug(() => $"OpenInventory on trader {npcName} — redirecting to OpenTraderInventory");
                return OpenTraderInventory(npc, player, npcName, dialogText);
            }

            // SP/listen-host/dedi: client-side SCore call chain (dialog-equivalent)
            // SCore v3.0.28.1101 PositionAtEntity fix resolved stale container position on dedi.
            try
            {
                bool result = EntityUtilities.ExecuteCMD(npc.entityId, "OpenInventory", player);
                if (result)
                {
                    Log.Debug(() => $"OpenInventory succeeded for NPC {npc.entityId} ({npcName})");
                    return dialogText ?? "*opens inventory*";
                }

                Log.Warning($"OpenInventory: SCore ExecuteCMD failed for NPC {npc.entityId}");
                return dialogText ?? "*Hmm, I can't open my inventory right now.";
            }
            catch (Exception ex)
            {
                Log.Error($"OpenInventory exception: {ex}");
                return dialogText ?? "*Something went wrong trying to open my inventory.";
            }
        }

        /// <summary>
        /// Route SwapWeapon through VCCommandRouter for SP/MP uniformity.
        /// Pre-checks (tag + item presence) stay client-side for UX — they produce descriptive
        /// fallback text. The mutation body (SetCustomVar + UpdateWeapon + UpdateHandItem)
        /// runs server-side via router, since SCore's NetPackageWeaponSwap server branch is dead.
        /// </summary>
        private string SwapWeaponRouted(EntityAlive npc, EntityPlayer player, PhraseTrigger trigger, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "SwapWeapon: NPC is null");
                return "Sorry, I can't switch weapons right now.";
            }

            string weaponClassId = trigger.ActionParam;  // e.g., "meleeNPCClubWood"
            string tagName = trigger.ActionParam2;       // e.g., "ClubWood"

            if (string.IsNullOrEmpty(weaponClassId))
            {
                Log.Warning("SwapWeapon trigger has no weapon class specified");
                return "Which weapon do you want me to use?";
            }

            // Pre-check 1: NPC must have the tag for this weapon (read via reflection from entityclass)
            if (!string.IsNullOrEmpty(tagName))
            {
                if (!NpcHasTag(npc, tagName))
                {
                    Log.Debug(() => $"SwapWeapon: NPC {npc.entityId} ({npcName}) missing tag '{tagName}' for weapon '{weaponClassId}'");
                    return "I can't use that kind of weapon.";
                }
            }

            // Pre-check 2: NPC must have the weapon item in inventory (block if missing)
            if (WeaponTagToItem.TryGetValue(tagName, out string itemClassName) && !string.IsNullOrEmpty(itemClassName))
            {
                if (!NpcHasItem(npc, itemClassName))
                {
                    Log.Debug(() => $"SwapWeapon: NPC {npc.entityId} ({npcName}) missing item '{itemClassName}' for weapon '{weaponClassId}'");
                    return "I don't have that weapon on me.";
                }
            }

            // Route through VCCommandRouter — SP/listen-host executes directly, dedi sends to server.
            // Server mutation: SetCustomVar("CurrentWeaponID") → IEntityAliveSDX.UpdateWeapon(item) → EntityUtilities.UpdateHandItem (broadcast).
            try
            {
                Net.VCCommandRouter.Execute(player.entityId, npc.entityId, Net.VCCommand.SwapWeapon, weaponClassId);
                Log.Debug(() => $"SwapWeapon routed for NPC {npc.entityId} ({npcName}) → {weaponClassId}");
                return dialogText ?? "equips weapon";
            }
            catch (Exception ex)
            {
                Log.Error($"SwapWeapon exception: {ex.Message}");
                return "Something went wrong switching weapons.";
            }
        }

        /// <summary>
        /// Open the NPC's inventory/trade window using SCore's ExecuteCMD "OpenInventory".
        /// SCore handles two inventory backends:
        ///   1. EntityTrader NPCs (EntityAliveSDXV4) → HarvestManager container
        ///   2. Regular NPCs with lootContainer → standard looting window
        /// On dedicated servers, SCore sends NetPackageHarvestInventoryRequest.
        /// </summary>
        private string OpenNPCInventory(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "OpenInventory: NPC is null");
                return dialogText ?? "*Sorry, I can't show you my inventory right now.";
            }

            // If target is a trader, use OpenTraderInventory (has InteractWithEntity fallback)
            if (IsTraderEntity(npc))
            {
                Log.Debug(() => $"OpenInventory on trader {npcName} — redirecting to OpenTraderInventory");
                return OpenTraderInventory(npc, player, npcName, dialogText);
            }

            // SP/listen-host/dedi: client-side SCore call chain (dialog-equivalent)
            // SCore v3.0.28.1101 PositionAtEntity fix resolved stale container position on dedi.
            try
            {
                bool result = EntityUtilities.ExecuteCMD(npc.entityId, "OpenInventory", player);
                if (result)
                {
                    Log.Debug(() => $"OpenInventory succeeded for NPC {npc.entityId} ({npcName})");
                    return dialogText ?? "*opens inventory*";
                }

                Log.Warning($"OpenInventory: SCore ExecuteCMD failed for NPC {npc.entityId}");
                return dialogText ?? "*Hmm, I can't open my inventory right now.";
            }
            catch (Exception ex)
            {
                Log.Error($"OpenInventory exception: {ex}");
                return dialogText ?? "*Something went wrong trying to open my inventory.";
            }
        }

        #endregion

        #region Trader Actions

        /// <summary>
        /// Open the trader's trade dialog by simulating the E key interaction.
        /// Traders are in-game traders (EntityTrader types) — they can't be hired, don't move,
        /// and only buy/sell and offer quests. Accessed via E key to open trade dialog.
        /// This does NOT require the player to be the NPC's leader (traders have no leader).
        /// </summary>
        private string OpenTraderInventory(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "OpenTraderInventory: NPC is null");
                return dialogText ?? "*Sorry, I can't open my trade window right now.";
            }

            if (!IsTraderEntity(npc))
            {
                Log.Warning($"OpenTraderInventory: {npc.EntityName} is not a trader (type: {npc.GetType().Name})");
                return dialogText ?? "*I'm not a trader, but let me show you what I have.";
            }

            try
            {
                // Try SCore's ExecuteCMD "OpenInventory" (for SCore-managed traders)
                bool result = EntityUtilities.ExecuteCMD(npc.entityId, "OpenInventory", player);
                if (result)
                {
                    Log.Debug(() => $"OpenTraderInventory succeeded via SCore ExecuteCMD for trader {npc.entityId} ({npcName})");
                    return dialogText ?? "*opens trade window*";
                }

                Log.Warning($"OpenTraderInventory: all methods failed for trader {npc.entityId} ({npcName})");
                return dialogText ?? "*Hmm, I can't open my trade window right now.";
            }
            catch (Exception ex)
            {
                Log.Error($"OpenTraderInventory exception: {ex.Message}");
                return dialogText ?? "*Something went wrong trying to open my trade window.";
            }
        }

        /// <summary>
        /// Check if an entity is a trader (EntityTrader type).
        /// </summary>
        private static bool IsTraderEntity(EntityAlive entity)
        {
            if (entity == null) return false;
            string name = entity.GetType().Name;
            return name.Contains("Trader");
        }

        /// <summary>
        /// JumpUp: path to and climb the nearest ladder.
        /// Sets a pending command on NPCChatComponent; UAI task (UAITaskJumpUpSDX) executes it.
        /// </summary>
        private string JumpUp(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "JumpUp: NPC is null");
                return dialogText;
            }

            try
            {
                if (JumpCommandUtils.FindNearestLadder(npc) == null)
                {
                    Log.Debug(() => $"JumpUp: no ladder within range for NPC {npc.entityId} ({npcName})");
                    return dialogText ?? "*I don't see a ladder nearby.";
                }

                var chatComponent = npc.gameObject.GetComponent<NPCChatComponent>();
                if (chatComponent == null)
                {
                    Log.Warning($"JumpUp: no NPCChatComponent on NPC {npc.entityId}");
                    return dialogText;
                }

                npc.SetCVar("$XVC_PendingCommand", (float)PendingJumpCommand.JumpUp);
                Log.Debug(() => $"JumpUp: command set for NPC {npc.entityId} ({npcName})");
                return dialogText;
            }
            catch (Exception ex)
            {
                Log.Error($"JumpUp exception: {ex.Message}");
                return dialogText;
            }
        }

        /// <summary>
        /// JumpDown: walk off the nearest ledge.
        /// Sets a pending command on NPCChatComponent; UAI task (UAITaskJumpDownSDX) executes it.
        /// </summary>
        private string JumpDown(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "JumpDown: NPC is null");
                return dialogText;
            }

            try
            {
                if (JumpCommandUtils.FindNearestEdge(npc) == null)
                {
                    Log.Debug(() => $"JumpDown: no edge within range for NPC {npc.entityId} ({npcName})");
                    return dialogText ?? "*There's no edge nearby to jump down from.";
                }

                var chatComponent = npc.gameObject.GetComponent<NPCChatComponent>();
                if (chatComponent == null)
                {
                    Log.Warning($"JumpDown: no NPCChatComponent on NPC {npc.entityId}");
                    return dialogText;
                }

                npc.SetCVar("$XVC_PendingCommand", (float)PendingJumpCommand.JumpDown);
                Log.Debug(() => $"JumpDown: command set for NPC {npc.entityId} ({npcName})");
                return dialogText;
            }
            catch (Exception ex)
            {
                Log.Error($"JumpDown exception: {ex.Message}");
                return dialogText;
            }
        }

        #endregion

        #region Movement Actions

        /// <summary>
        /// NPCCore/SCore movement order buff names (from dialogs.xml).
        /// These are applied via EntityBuffs.AddBuff() on the NPC entity.
        /// </summary>
        private static readonly Dictionary<TriggerActionType, string> MovementBuffs = new Dictionary<TriggerActionType, string>
        {
            { TriggerActionType.FollowPlayer, "buffOrderFollow" },
            { TriggerActionType.StopFollow,   "buffOrderStay" },
            { TriggerActionType.GuardHere,    "buffOrderGuardHere" },
            { TriggerActionType.GuardReturn,  "buffOrderGuard" },
            { TriggerActionType.Wander,       "buffOrderWander" },
            { TriggerActionType.Loot,         "buffOrderLoot" },
        };

        /// <summary>
        /// Make the NPC follow the player — routes through VCCommandRouter for SP/MP uniformity.
        /// </summary>
        private string FollowPlayer(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "FollowPlayer: NPC is null");
                return dialogText ?? "*Sorry, I can't follow you right now.";
            }
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.Follow);
            return dialogText ?? "*follows you*";
        }

        /// <summary>
        /// Make the NPC stop following — routes through VCCommandRouter for SP/MP uniformity.
        /// </summary>
        private string StopFollow(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "StopFollow: NPC is null");
                return dialogText ?? "*Okay, I'll stay here.";
            }
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.Stay);
            return dialogText ?? "*stays in place*";
        }

        /// <summary>
        /// Make the NPC guard current position — routes through VCCommandRouter for SP/MP uniformity.
        /// </summary>
        private string GuardHere(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "GuardHere: NPC is null");
                return dialogText ?? "*Sorry, I can't guard this area right now.";
            }
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.Guard);
            return dialogText ?? "*takes up a defensive position*";
        }

        /// <summary>
        /// Make the NPC guard and return to position — routes through VCCommandRouter for SP/MP uniformity.
        /// </summary>
        private string GuardReturn(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "GuardReturn: NPC is null");
                return dialogText ?? "*Sorry, I can't guard and return right now.";
            }
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.Guard, "return");
            return dialogText ?? "*I'll guard and come back to you*";
        }

        /// <summary>
        /// Make the NPC wander/patrol around the area — routes through VCCommandRouter for SP/MP uniformity.
        /// </summary>
        private string Wander(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "Wander: NPC is null");
                return dialogText ?? "*Sorry, I can't patrol right now.";
            }
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.Wander);
            return dialogText ?? "*starts patrolling the area*";
        }

        /// <summary>
        /// Make the NPC loot around the area by applying the buffOrderLoot buff.
        /// </summary>
        private string Loot(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "Loot: NPC is null");
                return dialogText ?? "*Sorry, I can't loot right now.";
            }

            try
            {
                bool result = TryApplyMovementBuff(npc, TriggerActionType.Loot);
                if (result)
                {
                    Log.Debug(() => $"Loot succeeded for NPC {npc.entityId} ({npcName})");
                    return dialogText ?? "*starts looting the area*";
                }

                Log.Warning($"Loot: buff application failed for NPC {npc.entityId}");
                return dialogText ?? "*I'll scavenge what I can.*";
            }
            catch (Exception ex)
            {
                Log.Error($"Loot exception: {ex.Message}");
                return dialogText ?? "*Something went wrong.";
            }
        }

        /// <summary>
        /// Dismiss the NPC using SCore's ExecuteCMD "Dismiss" + buffOrderDismiss.
        /// </summary>
        private string Dismiss(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "Dismiss: NPC is null");
                return dialogText ?? "*Something went wrong.";
            }

            try
            {
                // Clear billing cvars on dismiss (cvars are independent of any buff).
                // RetainerEarnings is intentionally NOT cleared — lifetime earnings across rehires.
                npc.Buffs.RemoveCustomVar("RetainerWeek");
                npc.Buffs.RemoveCustomVar("RetainerNextDueDay");
                npc.Buffs.RemoveCustomVar("RetainerGraceDay");

                // Apply the dismiss buff (dialogs.xml does both ExecuteCMD and AddBuff)
                npc.Buffs.AddBuff("buffOrderDismiss", 1, false);

                // Also call SCore's ExecuteCMD "Dismiss" for cleanup
                EntityUtilities.ExecuteCMD(npc.entityId, "Dismiss", player);

                Log.Debug(() => $"Dismiss succeeded for NPC {npc.entityId} ({npcName})");
                return dialogText ?? "*nods* Good luck out there. Stay safe.";
            }
            catch (Exception ex)
            {
                Log.Error($"Dismiss exception: {ex.Message}");
                return dialogText ?? "*Something went wrong.";
            }
        }

        /// <summary>
        /// Start recording patrol route — routes through VCCommandRouter for SP/MP uniformity.
        /// Mutation (ExecuteCMD + cvar + recording flag) runs server-side via router.
        /// </summary>
        internal string SetPatrolPoint(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null) { Log.Debug(() => "SetPatrolPoint: NPC is null"); return dialogText ?? "*Something went wrong."; }
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetPatrolPoint);
            return dialogText ?? "Follow me and I'll show you the route.";
        }

        /// <summary>
        /// Start looping recorded patrol route — routes through VCCommandRouter for SP/MP uniformity.
        /// Mutation (route cleanup + ExecuteCMD + flags) runs server-side via router.
        /// </summary>
        internal string Patrol(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null) { Log.Debug(() => "Patrol: NPC is null"); return dialogText ?? "*Something went wrong."; }
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.Patrol);
            return dialogText ?? "I'll patrol this route.";
        }

        /// <summary>
        /// Remove A-B-A wobble points captured at corners: a point is noise when the trail
        /// returns nearly adjacent to where it was, i.e. its neighbors are closer to each
        /// other than one diagonal block. Straights: |prev-next| ~2m. Real 90° corner:
        /// ~1.41m (block diagonal). Wobble (lateral or reversal): &lt;= ~1.0m. 1.2f splits them.
        /// Endpoints (i=0, last) are never removed — they're the route's anchor ends.
        /// </summary>
        private static int CleanupPatrolRoute(List<Vector3> points)
        {
            int removed = 0;
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = points.Count - 2; i >= 1; i--)
                {
                    Vector3 gap = points[i + 1] - points[i - 1];
                    gap.y = 0f;
                    if (gap.magnitude < 1.2f)
                    {
                        points.RemoveAt(i);
                        removed++;
                        changed = true;
                    }
                }
            }
            return removed;
        }

        /// <summary>
        /// Cancel patrol recording or stop active patrol — routes through VCCommandRouter for SP/MP uniformity.
        /// Mutation (clear points + order Stay + flags) runs server-side via router.
        /// NOTE: Order-gate pre-check in TryHandlePhrase() ensures this trigger only matches during
        /// SetPatrolPoint/Patrol orders; otherwise the phrase falls through to normal chat.
        /// </summary>
        internal string CancelPatrolRecord(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null) return dialogText ?? "*Something went wrong.";
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.CancelPatrolRecord);
            return dialogText ?? "Cancelled, clearing the path.";
        }

        /// <summary>Set follow distance via VCCommandRouter for SP/MP uniformity.</summary>
        private string SetFollowDistance(EntityAlive npc, EntityPlayer player, PhraseTrigger trigger, string npcName, string dialogText)
        {
            if (npc == null) return dialogText;
            Log.Out($"[FOLLOW-DIST] Handler: {npcName} (id {npc.entityId}) param={trigger.ActionParam}");
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetFollowDistance, trigger.ActionParam);
            return dialogText;
        }

        /// <summary>
        /// Set formation slot — two-stage parse.
        /// ActionParam may be a bare angle ("90") or a verbatim "angle,tierIdx" pair ("0,0" = cancel).
        /// Must check for the comma BEFORE float.TryParse — "0,0" parses as 0 in en-US
        /// (thousands separator) or as -1 in de-DE (decimal separator), silently losing the cancel.
        /// </summary>
        private string SetFormation(EntityAlive npc, EntityPlayer player, PhraseTrigger trigger, string playerMessage, string npcName, string dialogText)
        {
            if (npc == null) return dialogText;

            // Comma present (e.g. "0,0" cancel) → pass through as-is.
            if (trigger.ActionParam != null && trigger.ActionParam.IndexOf(',') >= 0)
            {
                Log.Out($"[FORMATION] Handler: {npcName} verbatim arg '{trigger.ActionParam}'");
                VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetFormation, trigger.ActionParam);
                return dialogText;
            }

            // abs: prefix → append tier from transcript scan, router resolves bearing.
            if (trigger.ActionParam != null && trigger.ActionParam.StartsWith("abs:"))
            {
                int absTier = 2; // default = wide
                if (!string.IsNullOrEmpty(playerMessage))
                {
                    string msg = playerMessage.ToLower();
                    if (msg.Contains("tight") || msg.Contains("close") || msg.Contains("short"))
                        absTier = 1;
                }
                Log.Out($"[FORMATION] Handler: {npcName} abs arg '{trigger.ActionParam}' tier={absTier}");
                VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetFormation, $"{trigger.ActionParam},{absTier}");
                return dialogText;
            }

            // Bare angle path: parse angle from ActionParam, scan transcript for tier.
            float angle = 0f;
            if (!float.TryParse(trigger.ActionParam, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out angle))
            {
                Log.Out($"[FORMATION] REJECTED: invalid angle '{trigger.ActionParam}' for {npcName}");
                return dialogText;
            }

            // Stage 2: tier index from scanning transcript (tight=1, default wide=2).
            int tierIdx = 2; // default = wide
            if (!string.IsNullOrEmpty(playerMessage))
            {
                string msg = playerMessage.ToLower();
                if (msg.Contains("tight") || msg.Contains("close") || msg.Contains("short"))
                    tierIdx = 1;
            }

            Log.Out($"[FORMATION] Handler: {npcName} (id {npc.entityId}) angle={angle} tier={tierIdx} msg=\"{playerMessage}\"");
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetFormation, $"{angle},{tierIdx}");
            return dialogText;
        }

        /// <summary>Set CrouchOverride cvar via VCCommandRouter. Inert until SCore ships the hook.</summary>
        private string SetCrouch(EntityAlive npc, EntityPlayer player, PhraseTrigger trigger, string npcName, string dialogText)
        {
            if (npc == null) return dialogText;
            VCCommandRouter.Execute(player.entityId, npc.entityId, VCCommand.SetCrouch, trigger.ActionParam ?? "1");
            return dialogText;
        }

        /// <summary>
        /// Pick up the NPC using SCore's DialogActionPickUpNPC flow.
        /// Mirrors: GetNPCItemValue → inventory check → EntitySyncUtils.Collect
        /// </summary>
        private string Pickup(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "Pickup: NPC is null");
                return dialogText ?? "*Sorry, I can't be picked up right now.";
            }

            // State guard: NPC must be hired before pickup can execute
            if (!Core.ChatComponentManager.HasPlayerLeader(npc))
            {
                Log.Debug(() => $"[1-XNPCVoiceControl] Aborting pickup for {npcName} — not hired yet.");
                return dialogText ?? "*You need to hire me first before I can get on your back!";
            }

            // Race condition guard: block duplicate pickup requests while entity is being collected
            if (_pendingPickups.Contains(npc.entityId))
            {
                Log.Debug(() => $"[1-XNPCVoiceControl] Blocked duplicate pickup for {npcName} (entityId={npc.entityId}).");
                return dialogText ?? "*I'm already getting on, hold on!";
            }

            _pendingPickups.Add(npc.entityId);

            try
            {
                // Check NPC implements IEntityAliveSDX (required by SCore)
                if (!(npc is IEntityAliveSDX))
                {
                    Log.Warning($"Pickup: NPC {npcName} does not implement IEntityAliveSDX");
                    return dialogText ?? "*I can't be picked up right now.";
                }

                // Generate the NPC's ItemValue (serializes inventory, stats, buffs, cvars)
                var itemValue = TryGetNPCItemValue(npc);
                if (itemValue == null || itemValue.type == 0)
                {
                    Log.Warning($"Pickup: Failed to generate ItemValue for {npcName}");
                    _pendingPickups.Remove(npc.entityId); // unlock on failure
                    return dialogText ?? "*Something went wrong, I can't be picked up.";
                }

                // Check if player has inventory space
                var itemStack = new ItemStack(itemValue, 1);
                if (!player.inventory.CanTakeItem(itemStack) && !player.bag.CanTakeItem(itemStack))
                {
                    Log.Warning($"Pickup: Player inventory full for {npcName}");
                    _pendingPickups.Remove(npc.entityId); // unlock on failure
                    return dialogText ?? "*My pickup item won't fit in your inventory.";
                }

                // Call EntitySyncUtils.Collect(entityId, playerId) — the actual pickup
                bool collected = TrySCoreCollect(npc.entityId, player.entityId);
                if (collected)
                {
                    _pendingPickups.Remove(npc.entityId);
                    Log.Debug(() => $"Pickup succeeded for NPC {npc.entityId} ({npcName})");
                    return dialogText ?? "*climbs on your back*";
                }

                Log.Warning($"Pickup: SCore Collect failed for NPC {npc.entityId}");
                _pendingPickups.Remove(npc.entityId); // unlock on failure
                return dialogText ?? "*Hmm, I can't get on right now.";
            }
            catch (Exception ex)
            {
                Log.Error($"Pickup exception: {ex.Message}");
                _pendingPickups.Remove(npc.entityId); // unlock on failure
                return dialogText ?? "*Something went wrong trying to get picked up.";
            }
        }

        /// <summary>
        /// Call EntitySyncUtils.GetNPCItemValue directly.
        /// </summary>
        private static ItemValue TryGetNPCItemValue(EntityAlive npc)
        {
            try
            {
                return EntitySyncUtils.GetNPCItemValue(npc);
            }
            catch (Exception ex)
            {
                Log.Warning($"Pickup: GetNPCItemValue failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Call EntitySyncUtils.Collect(entityId, playerId) directly.
        /// This is the same method SCore's DialogActionPickUpNPC uses.
        /// </summary>
        private static bool TrySCoreCollect(int entityId, int playerId)
        {
            try
            {
                EntitySyncUtils.Collect(entityId, playerId);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"Pickup: EntitySyncUtils.Collect failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Open the hire dialog window using LocalPlayerUI.windowManager.Open().
        /// Direct API call — same pattern as MicrophoneCapture.cs and XUiC configs.
        /// </summary>
        private string Hire(EntityAlive npc, EntityPlayer player, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "Hire: NPC is null");
                return dialogText ?? "*Sorry, I can't be hired right now.";
            }

            try
            {
                // Check if NPC is already hired (has a leader)
                var leader = npc.Buffs.GetCustomVar("Leader");
                if (leader > 0)
                {
                    Log.Debug(() => $"Hire: NPC {npcName} is already hired (leader: {leader})");
                    return dialogText ?? "*I'm already hired, but thanks!";
                }

                var localPlayer = player as EntityPlayerLocal;
                if (localPlayer == null)
                {
                    Log.Warning("Hire: Player is not EntityPlayerLocal");
                    return dialogText ?? "*Hmm, I can't open the hire menu right now.";
                }

                var playerUI = LocalPlayerUI.GetUIForPlayer(localPlayer);
                if (playerUI == null)
                {
                    Log.Warning("Hire: GetUIForPlayer returned null");
                    return dialogText ?? "*Hmm, I can't open the hire menu right now.";
                }

                // Set CurrentNPC so the HireInformation popup knows which NPC to hire.
                // SCore's XUiC_HireInformationPopupSDX reads this from player buffs.
                player.Buffs.SetCustomVar("CurrentNPC", npc.entityId);

                playerUI.windowManager.Open("HireInformation", true, false);
                Log.Debug(() => $"Hire: Opened HireInformation window for NPC {npc.entityId} ({npcName})");
                return dialogText ?? "*opens hire menu*";
            }
            catch (Exception ex)
            {
                Log.Error($"Hire exception: {ex.Message}");
                return dialogText ?? "*Something went wrong trying to open the hire menu.";
            }
        }

        /// <summary>
        /// Maps NPC weapon tags (from entityclasses.xml) to their inventory item class names.
        /// Used to verify the NPC actually has the weapon before attempting a swap.
        /// </summary>
        private static readonly Dictionary<string, string> WeaponTagToItem = new Dictionary<string, string>
        {
            { "BareHands",  null },
            { "Knife",      "meleeWpnBladeT1HuntingKnife" },
            { "Machete",    "meleeWpnBladeT3Machete" },
            { "ClubWood",   "meleeWpnClubT0WoodenClub" },
            { "BatWood",    "meleeWpnClubT1BaseballBat" },
            { "Axe",        "meleeToolAxeT1IronFireaxe" },
            { "Spear",      "meleeWpnSpearT1IronSpear" },
            { "SMG",        "gunHandgunT3SMG5" },
            { "PipePistol", "gunHandgunT0PipePistol" },
            { "PipeShotgun","gunShotgunT0PipeShotgun" },
            { "PipeRifle",  "gunRifleT0PipeRifle" },
            { "PipeMG",     "gunMGT0PipeMachineGun" },
            { "M60",        "gunMGT3M60" },
            { "Pistol",     "gunHandgunT1Pistol" },
            { "DPistol",    "gunHandgunT3DesertVulture" },
            { "AK47",       "gunMGT1AK47" },
            { "TRifle",     "gunMGT2TacticalAR" },
            { "HRifle",     "gunRifleT1HuntingRifle" },
            { "SRifle",     "gunRifleT3SniperRifle" },
            { "LBow",       "gunBowT1WoodenBow" },
            { "XBow",       "gunBowT1IronCrossbow" },
            { "PShotgun",   "gunShotgunT2PumpShotgun" },
            { "AShotgun",   "gunShotgunT3AutoShotgun" },
            { "RocketL",    "gunExplosivesT3RocketLauncher" },
        };

        /// <summary>
        /// Swap the NPC's weapon using SCore's SwapWeapon dialog action.
        ///
        /// NPCCore dialogs.xml requires THREE conditions for a weapon swap:
        ///   1. Player is the NPC's leader (checked by NPCChatComponent before triggers fire)
        ///   2. NPC has the weapon tag (e.g., "ClubWood") — defined in entityclasses.xml
        ///   3. NPC has the weapon item in inventory (e.g., "meleeWpnClubT0WoodenClub")
        ///
        /// ActionParam  = NPC weapon class id (e.g., "meleeNPCClubWood") — used by SwapWeapon action
        /// ActionParam2 = NPC tag name (e.g., "ClubWood") — used to verify NPC can wield it
        ///
        /// The tag-to-item mapping is stored in WeaponTagToItem. If the NPC lacks the tag
        /// or the item, we return a descriptive fallback instead of silently failing.
        /// </summary>
        private string SwapWeapon(EntityAlive npc, EntityPlayer player, PhraseTrigger trigger, string npcName, string dialogText)
        {
            if (npc == null)
            {
                Log.Debug(() => "SwapWeapon: NPC is null");
                return "Sorry, I can't switch weapons right now.";
            }

            string weaponClassId = trigger.ActionParam;  // e.g., "meleeNPCClubWood"
            string tagName = trigger.ActionParam2;       // e.g., "ClubWood"

            if (string.IsNullOrEmpty(weaponClassId))
            {
                Log.Warning("SwapWeapon trigger has no weapon class specified");
                return "Which weapon do you want me to use?";
            }

            // Check 1: NPC must have the tag for this weapon (read via reflection from entityclass)
            if (!string.IsNullOrEmpty(tagName))
            {
                if (!NpcHasTag(npc, tagName))
                {
                    Log.Debug(() => $"SwapWeapon: NPC {npc.entityId} ({npcName}) missing tag '{tagName}' for weapon '{weaponClassId}'");
                    return "I can't use that kind of weapon.";
                }
            }

            // Check 2: NPC must have the weapon item in inventory (block if missing)
            // NpcHasItem checks HarvestManager (player-added items), lootContainer, inventory, and bag
            if (WeaponTagToItem.TryGetValue(tagName, out string itemClassName) && !string.IsNullOrEmpty(itemClassName))
            {
                if (!NpcHasItem(npc, itemClassName))
                {
                    Log.Debug(() => $"SwapWeapon: NPC {npc.entityId} ({npcName}) missing item '{itemClassName}' for weapon '{weaponClassId}'");
                    return "I don't have that weapon on me.";
                }
            }

            // All checks passed — swap weapon, matching SCore's DialogActionSwapWeapon.
            //   1. SetCustomVar("CurrentWeaponID", item.GetItemId())
            //   2. IEntityAliveSDX.UpdateWeapon(ID)
            //   3. EntityUtilities.UpdateHandItem(entityId, ID) — done inside UpdateWeapon
            try
            {
                // Step 1: Set CurrentWeaponID (same as DialogActionSwapWeapon)
                ItemValue item = ItemClass.GetItem(weaponClassId);
                if (item != null && !item.IsEmpty())
                {
                    npc.Buffs.SetCustomVar("CurrentWeaponID", item.GetItemId());
                }

                // Step 2: Call UpdateWeapon via IEntityAliveSDX
                var sdx = npc as IEntityAliveSDX;
                if (sdx != null)
                {
                    sdx.UpdateWeapon(weaponClassId);
                    Log.Debug(() => $"SwapWeapon succeeded for NPC {npc.entityId} ({npcName}) → {weaponClassId}");
                    return dialogText ?? "equips weapon";
                }

                Log.Warning($"SwapWeapon: NPC {npc.entityId} does not implement IEntityAliveSDX");
                Log.Warning($"SwapWeapon: Failed to call UpdateWeapon for NPC {npc.entityId} → {weaponClassId}");
                return "Something went wrong switching weapons.";
            }
            catch (Exception ex)
            {
                Log.Error($"SwapWeapon exception: {ex.Message}");
                return "Something went wrong switching weapons.";
            }
        }

        /// <summary>
        /// Set the combat mode for all NPCs hired by the player.
        /// Sets varNPCModMode on the player entity (0=Hunting, 1=Threat Control, 2=Full Control).
        /// Also directly applies/removes mode buffs on all hired NPCs because NPCCore's
        /// selfAOE propagation was removed and NPCModUpdateLeaderControl only fires on order changes.
        /// </summary>
        private string SetCombatMode(EntityPlayer player, PhraseTrigger trigger, string npcName, string dialogText)
        {
            if (player == null)
            {
                Log.Warning("SetCombatMode: player is null");
                return dialogText ?? "*Sorry, I can't change combat mode right now.";
            }

            string modeStr = trigger.ActionParam;
            if (string.IsNullOrEmpty(modeStr))
            {
                Log.Warning("SetCombatMode trigger has no mode specified");
                return dialogText ?? "*Which combat mode do you want?";
            }

            if (!int.TryParse(modeStr, out int modeValue) || modeValue < 0 || modeValue > 2)
            {
                Log.Warning($"SetCombatMode: invalid mode value '{modeStr}' (must be 0, 1, or 2)");
                return dialogText ?? "*Sorry, I don't understand that mode.";
            }

            try
            {
                // Read current mode
                int currentMode = (int)player.Buffs.GetCustomVar("varNPCModMode");

                // Set the new mode on the player
                player.Buffs.SetCustomVar("varNPCModMode", modeValue);
                player.Buffs.SetCustomVar("$varNPCModModeChange", 1);

                // Directly apply mode buffs to all hired NPCs
                // NPCCore's selfAOE propagation was removed, so we do it manually
                int affectedCount = 0;
                var world = GameManager.Instance?.World;
                if (world != null)
                {
                    foreach (var entity in world.Entities.list)
                    {
                        if (entity is not EntityAlive npc) continue;
                        if (npc.IsDead()) continue;

                        long npcLeader = (long)npc.Buffs.GetCustomVar("Leader");
                        if (npcLeader != player.entityId) continue;

                        // Remove both mode buffs first, then apply the correct one
                        npc.Buffs.RemoveBuff("buffNPCModFullControlMode");
                        npc.Buffs.RemoveBuff("buffNPCModThreatControlMode");

                        if (modeValue == 2)
                        {
                            npc.Buffs.AddBuff("buffNPCModFullControlMode");
                        }
                        else if (modeValue == 1)
                        {
                            npc.Buffs.AddBuff("buffNPCModThreatControlMode");
                        }
                        // Mode 0 (Hunting): no buff needed — NPC attacks on sight

                        affectedCount++;
                    }
                }

                string modeName = modeValue switch
                {
                    0 => "Hunting",
                    1 => "Threat Control",
                    2 => "Full Control",
                    _ => "Unknown"
                };

                Log.Debug(() => $"SetCombatMode: changed from {currentMode} to {modeValue} ({modeName}) for player {player.entityId}. Applied to {affectedCount} hired NPC(s).");
                return dialogText;
            }
            catch (Exception ex)
            {
                Log.Error($"SetCombatMode exception: {ex.Message}");
                return dialogText ?? "*Something went wrong changing combat mode.";
            }
        }

        /// <summary>
        /// Check if an NPC has a specific tag, reusing SCore's HasAnyTags (same as DialogRequirementHasTag).
        /// </summary>
        private static bool NpcHasTag(EntityAlive npc, string tagToCheck)
        {
            if (npc == null || string.IsNullOrEmpty(tagToCheck)) return false;
            try
            {
                var tags = FastTags<TagGroup.Global>.Parse(tagToCheck);
                bool hasTag = npc.HasAnyTags(tags);
                Log.Debug(() => $"NpcHasTag: NPC {npc.EntityName} — checking for '{tagToCheck}': {hasTag}");
                return hasTag;
            }
            catch (Exception ex)
            {
                Log.Warning($"NpcHasTag check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if an NPC has a specific item. Checks all SCore container types:
        ///   1. HarvestManager (player-accessible inventory for EntityTrader NPCs)
        ///   2. lootContainer (SCoreLootContainer on EntityAliveSDX)
        ///   3. inventory.GetItemCount(itemValue)  (equipment slots)
        ///   4. bag.GetItemCount(itemValue)  (backpack/toolbelt)
        /// </summary>
        private static bool NpcHasItem(EntityAlive npc, string itemClassName)
        {
            if (npc == null)
            {
                Log.Debug(() => $"NpcHasItem: NPC is null");
                return false;
            }

            // Get the ItemValue via ItemClass.GetItem(name) — direct call, no reflection needed
            ItemValue itemValue = ItemClass.GetItem(itemClassName);
            if (itemValue == null || itemValue.IsEmpty())
            {
                Log.Debug(() => $"NpcHasItem: ItemClass.GetItem(\"{itemClassName}\") returned null/empty");
                return false;
            }

            // Check 1: HarvestManager (for EntityTrader/EntityAliveSDX NPCs) — mirrors SCore's DialogRequirementNPCHasItemSDX
            try
            {
                if (HarvestManager.Has(npc.entityId))
                {
                    var container = HarvestManager.GetOrCreate(npc.entityId);
                    if (container != null && container.HasItem(itemValue))
                    {
                        Log.Debug(() => $"NpcHasItem: found '{itemClassName}' in HarvestManager");
                        return true;
                    }
                    Log.Debug(() => $"NpcHasItem: '{itemClassName}' not in HarvestManager");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"NpcHasItem: HarvestManager check failed: {ex.Message}");
            }

            // Check 2: lootContainer (SCore NPC storage — EntityAliveSDX or EntityAliveSDXV4)
            {
                SCoreLootContainer lc = GetLootContainer(npc);
                if (lc != null && lc.HasItem(itemValue))
                {
                    Log.Debug(() => $"NpcHasItem: found '{itemClassName}' in lootContainer");
                    return true;
                }
            }

            // Check 3: inventory.GetItemCount(itemValue)
            if (npc.inventory != null)
            {
                int count = npc.inventory.GetItemCount(itemValue);
                Log.Debug(() => $"NpcHasItem: searching inventory for '{itemClassName}', count={count}");
                if (count > 0)
                {
                    Log.Debug(() => $"NpcHasItem: found '{itemClassName}' in inventory (count={count})");
                    return true;
                }
            }
            else
            {
                Log.Debug(() => $"NpcHasItem: inventory is null, skipping check for '{itemClassName}'");
            }

            // Check 4: bag.GetItemCount(itemValue)
            if (npc.bag != null)
            {
                int count = npc.bag.GetItemCount(itemValue);
                Log.Debug(() => $"NpcHasItem: searching bag for '{itemClassName}', count={count}");
                if (count > 0)
                {
                    Log.Debug(() => $"NpcHasItem: found '{itemClassName}' in bag (count={count})");
                    return true;
                }
            }
            else
            {
                Log.Debug(() => $"NpcHasItem: bag is null, skipping check for '{itemClassName}'");
            }

            Log.Debug(() => $"NpcHasItem: '{itemClassName}' not found in any container for NPC {npc.entityId} ({npc.EntityName})");
            return false;
        }

        #region SCore Reflection Helpers

        #endregion

        /// <summary>
        /// Apply a movement order buff to the NPC using EntityBuffs.AddBuff().
        /// This is the same mechanism NPCCore/SCore uses in dialogs.xml (AddBuffSDX action).
        /// </summary>
        private static bool TryApplyMovementBuff(EntityAlive npc, TriggerActionType actionType)
        {
            if (!MovementBuffs.TryGetValue(actionType, out string buffName))
            {
                Log.Warning($"No buff mapped for action type {actionType}");
                return false;
            }

            try
            {
                // EntityBuffs.AddBuff(string _name, int _stacks, bool _bypassCooldown)
                // Returns BuffStatus enum (0 = OK)
                int status = (int)npc.Buffs.AddBuff(buffName, 1, false);
                if (status == 0)
                {
                    Log.Debug(() => $"Applied buff '{buffName}' to NPC {npc.entityId}");
                    return true;
                }
                else
                {
                    Log.Warning($"Buff '{buffName}' failed for NPC {npc.entityId}: status={status}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Exception applying buff '{buffName}' to NPC {npc.entityId}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Story Pool

        /// <summary>
        /// Get a random story from the loaded story pool.
        /// Falls back to a hardcoded message if no stories are loaded.
        /// </summary>
        private string GetRandomStory()
        {
            if (_stories.Count == 0)
            {
                return "*shrugs* I don't have any stories to tell right now.";
            }
            int index = UnityEngine.Random.Range(0, _stories.Count);
            return _stories[index];
        }

        /// <summary>
        /// Strip *emote* text from a response string for display/TTS.
        /// The raw text with emotes is preserved in the XML for future animation support.
        /// </summary>
        private static string StripEmoteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // Remove *anything* patterns (single words and full sentences), collapsing extra whitespace
            string result = System.Text.RegularExpressions.Regex.Replace(text, @"\*[^*]+\*", "");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
            return result;
        }

        #endregion

        #region Phrase Matching (Zero-Allocation)

        /// <summary>
        /// Refusal responses for leaderless NPCs who can't execute actions.
        /// </summary>
        private static readonly string[] LeaderlessRefusals = new string[]
        {
            "I don't take orders from just anyone. Hire me first.",
            "We're not on those terms yet. Make me an offer.",
            "I work for myself until someone proves they're worth following.",
            "You'll need to earn my trust before I follow your commands.",
            "Not interested. Convince me to join you and we'll talk.",
            "I'm not hired yet — what have you got to offer?"
        };

        /// <summary>
        /// Execute a matched trigger: get audio (English) and subtitle (localized) responses, run action, strip emotes.
        /// </summary>
        private bool ExecuteMatch(PhraseTrigger trigger, EntityAlive npc, EntityPlayer player, string npcName, bool executeAction, string npcLanguage, string playerUiLanguage, out string audioResponse, out string subtitleResponse, bool alreadyConfirmed = false, string playerMessage = null)
        {
            LastMatchedTriggerName = trigger.ActionType.ToString().ToLowerInvariant();
            LastMatchedActionParam = trigger.ActionParam;

            // CancelPatrolRecord gate: only match when NPC is recording or actively patrolling.
            // Uses mod-owned state flags, not SCore's CurrentOrder cvar (which reverts to Follow).
            if (trigger.ActionType == TriggerActionType.CancelPatrolRecord && npc != null)
            {
                if (!Core.ChatComponentManager.TryGet(npc.entityId, out var comp) || !comp.IsRecordingOrPatrolling)
                {
                    LastMatchedTriggerName = null; // suppress — treat as unmatched
                    audioResponse = null;
                    subtitleResponse = null;
                    bool hasComp = Core.ChatComponentManager.TryGet(npc.entityId, out var c1);
                    bool isRecOrPat = false;
                    int compHash = 0;
                    if (hasComp)
                    {
                        isRecOrPat = c1.IsRecordingOrPatrolling;
                        compHash = c1.GetHashCode();
                    }
                    Log.Debug($"[PATROL-FLAG] Cancel gate read: IsRecordingOrPatrolling={isRecOrPat} entity={npc.entityId} component={compHash} for {npcName}");
                    return false;
                }
            }

            // RequireConfirmation gate: ask player to confirm before executing.
            // Skipped when called from the pending-confirmation path (alreadyConfirmed=true).
            if (trigger.RequireConfirmation && !alreadyConfirmed && npc != null)
            {
                _pendingConfirmations[npc.entityId] = new PendingConfirmation
                {
                    Trigger = trigger,
                    Player = player,
                    NpcName = npcName,
                    TimeoutStart = Time.realtimeSinceStartup
                };
                audioResponse = "Are you sure? Say yes to confirm or no to cancel.";
                subtitleResponse = audioResponse;
                LastMatchedTriggerName = null; // suppress clip during prompt - clip plays on confirmed execute
                Log.Debug(() => $"NPC {npcName} pending confirmation for {trigger.ActionType}");
                return true;
            }

            // Actions that DON'T require a leader:
            //   Hire — you need to hire before you become the leader
            //   OpenTraderInventory — traders have no leader, just open trade dialog
            //   OpenInventory on trader — redirect to trade dialog (no leader needed)
            //   CustomDialog — pure conversation (e.g., "tell me a story"), no action executed
            //   None — no action at all
            bool isTrader = IsTraderEntity(npc);
            // Leadership check: authoritative, independent of executeAction.
            // executeAction controls whether to RUN the mutation; leadership controls whether to REFUSE.
            // Squad ack path: executeAction=false (mutation handled by ExecuteForSquad) but player IS leader → don't refuse.
            bool playerIsLeader = npc != null &&
                EntityUtilities.GetLeaderOrOwner(npc.entityId) is EntityAlive ldr &&
                ldr.entityId == player.entityId;
            bool shouldRefuseForNoLeader = !playerIsLeader &&
                trigger.ActionType != TriggerActionType.Hire &&
                trigger.ActionType != TriggerActionType.OpenTraderInventory &&
                trigger.ActionType != TriggerActionType.CustomDialog &&
                trigger.ActionType != TriggerActionType.None &&
                !(trigger.ActionType == TriggerActionType.OpenInventory && isTrader);

            string audioText;       // Always English for TTS
            string subtitleText;    // Localized for UI

            if (shouldRefuseForNoLeader)
            {
                // NPC is leaderless — refuse the request instead of executing
                audioText = LeaderlessRefusals[UnityEngine.Random.Range(0, LeaderlessRefusals.Length)];
                subtitleText = audioText;  // Refusals are English-only
                Log.Debug(() => $"NPC {npcName} refused action (leaderless): {audioText}");
            }
            else
            {
                // Dubbed architecture: audio always English, subtitle uses player's UI language
                trigger.GetRandomResponsePair(playerUiLanguage, out audioText, out subtitleText);

                // Fallback: if subtitle is null, use audio text
                if (string.IsNullOrEmpty(subtitleText))
                    subtitleText = audioText;

                if (executeAction || trigger.ActionType == TriggerActionType.Hire || trigger.ActionType == TriggerActionType.OpenTraderInventory
                    || (trigger.ActionType == TriggerActionType.OpenInventory && isTrader))
                {
                    audioText = ExecuteTriggerAction(trigger, npc, player, npcName, audioText, playerMessage);
                    // Subtitle stays as the localized version
                }
            }

            audioResponse = StripEmoteText(audioText);
            subtitleResponse = StripEmoteText(subtitleText);
            return true;
        }

        /// <summary>
        /// Split a lowercase message into tokens, stripping stop words, punctuation, and normalizing plurals.
        /// Writes into a pre-allocated buffer. Returns actual token count.
        /// </summary>
        /// <summary>
        /// Check if a trigger phrase represents a command intent vs conversational context.
        /// Single-word aliases: ≥25% of sentence. Multi-word aliases: ≥40% (more specific, demand stronger intent).
        /// E.g., "pick up" in "okay i'll pick up some water" = 2/6 = 33% → fails 40% multi-word threshold.
        /// </summary>
        /// <summary>
        /// Check that <paramref name="phrase"/> appears in <paramref name="text"/> at a word boundary —
        /// not embedded inside another word. E.g., "come with" must not match "income with".
        /// </summary>
        private static bool ContainsWithWordBoundary(string text, string phrase)
        {
            int idx = text.IndexOf(phrase, StringComparison.Ordinal);
            while (idx >= 0)
            {
                bool startOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                bool endOk = idx + phrase.Length >= text.Length || !char.IsLetterOrDigit(text[idx + phrase.Length]);
                if (startOk && endOk) return true;
                idx = text.IndexOf(phrase, idx + 1, StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>
        /// Count words in a string (zero-allocation).
        /// </summary>
        private static int SplitIntoBuffer(string message, string[] buffer)
        {
            int count = 0;
            int len = message.Length;
            int start = -1;

            for (int i = 0; i <= len; i++)
            {
                bool isSep = i >= len || char.IsWhiteSpace(message[i]);
                if (isSep && start >= 0)
                {
                    // Extract word
                    int wordLen = i - start;
                    // Trim punctuation from ends
                    int wStart = start;
                    int wEnd = i;
                    while (wStart < wEnd && IsPunctuation(message[wStart])) wStart++;
                    while (wEnd > wStart && IsPunctuation(message[wEnd - 1])) wEnd--;

                    if (wEnd > wStart)
                    {
                        string word = message.Substring(wStart, wEnd - wStart);
                        if (!StopWords.Contains(word))
                        {
                            // Plural normalization: strip trailing 's' if word > 3 chars
                            if (word.Length > 3 && word[word.Length - 1] == 's')
                                word = word.Substring(0, word.Length - 1);

                            if (count < buffer.Length)
                                buffer[count++] = word;
                        }
                    }
                    start = -1;
                }
                else if (!isSep && start < 0)
                {
                    start = i;
                }
            }
            return count;
        }

        private static bool IsPunctuation(char c)
        {
            return c == '.' || c == ',' || c == '!' || c == '?' || c == ';' || c == ':' ||
                   c == '"' || c == '\'' || c == '(' || c == ')' || c == '[' || c == ']';
        }

        /// <summary>
        /// Count how many phrase tokens are found in the message tokens (exact match).
        /// </summary>
        private static int CountTokenMatches(string[] msgTokens, int msgCount, string[] phraseTokens)
        {
            int matches = 0;
            foreach (var pt in phraseTokens)
            {
                for (int i = 0; i < msgCount; i++)
                {
                    if (msgTokens[i] == pt)
                    {
                        matches++;
                        break;
                    }
                }
            }
            return matches;
        }

        /// <summary>
        /// Count how many phrase tokens are found in the message tokens (fuzzy, Levenshtein ≤ 1).
        /// Tightened from ≤2 to ≤1 to prevent false positives on conversational questions.
        /// Also enforces a length ratio check: words with very different lengths cannot fuzzy-match.
        /// </summary>
        private static int CountFuzzyMatches(string[] msgTokens, int msgCount, string[] phraseTokens)
        {
            int matches = 0;
            foreach (var pt in phraseTokens)
            {
                bool found = false;
                for (int i = 0; i < msgCount; i++)
                {
                    // Length ratio guard: skip if one word is more than 2x the other
                    // Prevents "from" (4) from matching "open" (4) or "stuff" (5)
                    int lenDiff = Math.Abs(msgTokens[i].Length - pt.Length);
                    if (lenDiff > 2) continue;

                    if (msgTokens[i] == pt || LevenshteinDistance(msgTokens[i], pt) <= 1)
                    {
                        found = true;
                        break;
                    }
                }
                if (found) matches++;
            }
            return matches;
        }

        /// <summary>
        /// Minimum matches required: Ceiling(phraseTokenCount * 0.6), at least 1.
        /// 1 token → 1 (100%), 2 tokens → 2 (100%), 3 tokens → 2 (67%), 4 tokens → 3 (75%)
        /// Prevents false positives on short phrases like "come here" matching "stay here".
        /// </summary>
        private static int GetRequiredMatches(int phraseTokenCount)
        {
            // Ceiling(phraseTokenCount * 0.6) without floating point
            int required = (int)Math.Ceiling(phraseTokenCount * 0.6);
            return required < 1 ? 1 : required;
        }

        /// <summary>
        /// Pre-tokenize a phrase at init time: lowercase, strip stop words, normalize plurals.
        /// </summary>
        private static string[] NormalizeTokens(string phrase)
        {
            var list = new System.Collections.Generic.List<string>();
            int len = phrase.Length;
            int start = -1;

            for (int i = 0; i <= len; i++)
            {
                bool isSep = i >= len || char.IsWhiteSpace(phrase[i]);
                if (isSep && start >= 0)
                {
                    int wStart = start;
                    int wEnd = i;
                    while (wStart < wEnd && IsPunctuation(phrase[wStart])) wStart++;
                    while (wEnd > wStart && IsPunctuation(phrase[wEnd - 1])) wEnd--;

                    if (wEnd > wStart)
                    {
                        string word = phrase.Substring(wStart, wEnd - wStart);
                        if (!StopWords.Contains(word))
                        {
                            if (word.Length > 3 && word[word.Length - 1] == 's')
                                word = word.Substring(0, word.Length - 1);
                            list.Add(word);
                        }
                    }
                    start = -1;
                }
                else if (!isSep && start < 0)
                {
                    start = i;
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// Calculate Levenshtein distance between two strings using two 1D arrays (zero GC).
        /// </summary>
        private static int LevenshteinDistance(string s, string t)
        {
            if (s == t) return 0;
            if (string.IsNullOrEmpty(s)) return t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int n = s.Length;
            int m = t.Length;
            int[] prev = new int[m + 1];
            int[] curr = new int[m + 1];

            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = t[j - 1] == s[i - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                // Swap arrays
                var temp = prev;
                prev = curr;
                curr = temp;
            }

            return prev[m];
        }

        #endregion

        /// <summary>
        /// Add a custom phrase trigger at runtime (e.g. from console command).
        /// </summary>
        public void AddTrigger(PhraseTrigger trigger)
        {
            if (trigger != null && !string.IsNullOrEmpty(trigger.Phrase))
            {
                // Pre-tokenize phrases for zero-allocation matching
                foreach (var phrase in trigger.Phrases)
                {
                    trigger.CachedPhrases.Add(new PhraseTrigger.CachedPhrase
                    {
                        Original = phrase.ToLower().Trim(),
                        NormalizedTokens = NormalizeTokens(phrase.ToLower().Trim())
                    });
                }
                // Pre-tokenize aliases for zero-allocation matching
                foreach (var alias in trigger.Aliases)
                {
                    string lowerAlias = alias.ToLower().Trim();
                    trigger.CachedAliases.Add(new PhraseTrigger.CachedAlias { Original = lowerAlias });
                }
                _triggers.Add(trigger);
                Log.Debug(() => $"Added custom trigger: \"{trigger.Phrase}\" → {trigger.ActionType}");
            }
        }

        /// <summary>
        /// Remove a trigger by any of its phrases (case-insensitive).
        /// </summary>
        public bool RemoveTrigger(string phrase)
        {
            string lower = phrase.ToLower().Trim();
            int initialCount = _triggers.Count;
            _triggers.RemoveAll(t => t.Phrases.Any(p => p == lower));
            bool removed = _triggers.Count < initialCount;
            if (removed)
            {
                Log.Debug(() => $"Removed trigger containing phrase: \"{phrase}\"");
            }
            return removed;
        }

        /// <summary>
        /// Add a story to the story pool at runtime.
        /// </summary>
        public void AddStory(string story)
        {
            if (!string.IsNullOrWhiteSpace(story))
            {
                _stories.Add(story.Trim());
                Log.Debug(() => $"Added story to pool (total: {_stories.Count})");
            }
        }

        /// <summary>
        /// STT phonetic-fix — narrow cardinal-mishear normalizer.
        /// Whole-word replace self→south, mouth→south ONLY when the first token is an imperative positional verb.
        /// Excludes "health" (too common). Rarer garbles fall through to no-op.
        /// </summary>
        private static string FixCardinalMishears(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            // Check if first token is an imperative positional verb.
            int spaceIdx = message.IndexOf(' ');
            if (spaceIdx < 0) return message; // single word — not a positional command
            string firstToken = message.Substring(0, spaceIdx);
            if (firstToken != "cover" && firstToken != "hold" && firstToken != "watch")
                return message; // not an imperative positional verb

            // Whole-word replace known mishears.
            string[] mishearMapFrom = { "self", "mouth" };
            string[] mishearMapTo   = { "south", "south" };

            for (int i = 0; i < mishearMapFrom.Length; i++)
            {
                string bad = mishearMapFrom[i];
                string good = mishearMapTo[i];
                // Skip if the message already contains the correct word (no fix needed).
                if (message.Contains(good)) return message;

                // Whole-word replace using word boundaries.
                int idx = 0;
                while ((idx = message.IndexOf(bad, idx, StringComparison.Ordinal)) >= 0)
                {
                    // Check word boundaries.
                    bool startOk = (idx == 0 || !char.IsLetterOrDigit(message[idx - 1]));
                    int endIdx = idx + bad.Length;
                    bool endOk = (endIdx >= message.Length || !char.IsLetterOrDigit(message[endIdx]));

                    if (startOk && endOk)
                    {
                        message = message.Substring(0, idx) + good + message.Substring(endIdx);
                        Log.Out($"[STT-FIX] '{bad}' -> 'south' in \"{message}\"");
                        break; // one replacement per mishear
                    }
                    idx += bad.Length;
                }
            }
            return message;
        }
    }
}
