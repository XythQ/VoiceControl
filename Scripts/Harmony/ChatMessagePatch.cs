using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace XNPCVoiceControl.Harmony
{
    /// <summary>
    /// Applies and removes all Harmony patches for 1-XNPCVoiceControl.
    /// Call Apply() during mod init, Unapply() during shutdown.
    /// Each patch is applied individually so failures are logged with the specific method name.
    /// </summary>
    public static class ChatMessagePatch
    {
        private static HarmonyLib.Harmony _harmony;

        /// <summary>Apply all patches. Call once during mod init.</summary>
        public static void Apply()
        {
            _harmony = new HarmonyLib.Harmony("com.xnpcvoicecontrol.patches");
            int applied = 0;

            // 1. GameManager.ChatMessageServer — intercept @-commands
            try
            {
                var original = AccessTools.Method(typeof(GameManager), nameof(GameManager.ChatMessageServer));
                if (original == null)
                    throw new InvalidOperationException("Method not found — signature may have changed in game update");
                _harmony.Patch(original, prefix: new HarmonyMethod(typeof(ChatMessageServerPatch).GetMethod("Prefix")));
                applied++;
            }
            catch (Exception ex)
            {
                Log.Error($"[Harmony] Failed to patch GameManager.ChatMessageServer: {ex.Message}");
            }

            // 2. World.RemoveEntity — clean up chat components on entity removal
            try
            {
                var original = AccessTools.Method(typeof(World), nameof(World.RemoveEntity));
                if (original == null)
                    throw new InvalidOperationException("Method not found — signature may have changed in game update");
                _harmony.Patch(original, prefix: new HarmonyMethod(typeof(RemoveEntityPatch).GetMethod("Prefix")));
                applied++;
            }
            catch (Exception ex)
            {
                Log.Error($"[Harmony] Failed to patch World.RemoveEntity: {ex.Message}");
            }

            // 3. Application.Quit — ensure servers are killed on hard exit
            try
            {
                var original = AccessTools.Method(typeof(Application), nameof(Application.Quit), new Type[] { });
                if (original == null)
                    throw new InvalidOperationException("Method not found — signature may have changed in game update");
                _harmony.Patch(original, prefix: new HarmonyMethod(typeof(ApplicationQuitPatch).GetMethod("Prefix")));
                applied++;
            }
            catch (Exception ex)
            {
                Log.Error($"[Harmony] Failed to patch Application.Quit: {ex.Message}");
            }

            // 4. XUiC_InGameMenuWindow.OnOpen — wire our pause menu button (3.0 grid approach)
            try
            {
                var original = AccessTools.Method(typeof(XUiC_InGameMenuWindow), "OnOpen");
                if (original == null)
                    throw new InvalidOperationException("Method not found — signature may have changed in game update");
                _harmony.Patch(original, postfix: new HarmonyMethod(typeof(InGameMenuPatch).GetMethod("Postfix")));
                applied++;
            }
            catch (Exception ex)
            {
                Log.Error($"[Harmony] Failed to patch XUiC_InGameMenuWindow.OnOpen: {ex.Message}");
            }

            // 5. Entity.OnPushEntity — asymmetric push (hired NPC yields to leader, leader never displaced)
            try
            {
                var original = AccessTools.Method(typeof(Entity), nameof(Entity.OnPushEntity));
                if (original == null)
                    throw new InvalidOperationException("Method not found — signature may have changed in game update");
                _harmony.Patch(original, prefix: new HarmonyMethod(typeof(LeaderPushPatch).GetMethod("Prefix")));
                applied++;
            }
            catch (Exception ex)
            {
                Log.Error($"[Harmony] Failed to patch Entity.OnPushEntity: {ex.Message}");
            }

            Log.Out($"[Harmony] Applied {applied}/5 patches");
        }

        /// <summary>Remove all patches. Call during mod shutdown.</summary>
        public static void Unapply() => _harmony?.UnpatchSelf();
    }

    /// <summary>
    /// Patch the game's chat system to intercept @-prefixed NPC-directed messages.
    /// </summary>
    public class ChatMessageServerPatch
    {
        // Fragile: GameManager.ChatMessageServer signature may change on game update
        public static bool Prefix(ClientInfo _cInfo, EChatType _chatType, int _senderEntityId, string _msg,
            List<int> _recipientEntityIds, EMessageSender _msgSender)
        {
            if (!string.IsNullOrEmpty(_msg) && _msg.StartsWith("@"))
            {
                EntityPlayer player = GameManager.Instance.World?.GetEntity(_senderEntityId) as EntityPlayer;
                if (player != null)
                {
                    Core.ChatComponentManager.ProcessAtCommand(player, _msg.Substring(1).Trim());
                    return false; // Don't process as normal chat
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Clean up chat components when entities are removed from the world.
    /// </summary>
    public class RemoveEntityPatch
    {
        // Fragile: World.RemoveEntity(int) signature may change on game update
        public static void Prefix(int _entityId) => Core.ChatComponentManager.OnEntityRemoved(_entityId);
    }

    /// <summary>
    /// Patch Application.Quit to ensure servers are killed even on hard game exit.
    /// ModEvents.GameShutdown may not fire reliably (e.g., Alt+F4 from main menu).
    /// </summary>
    public class ApplicationQuitPatch
    {
        public static void Prefix()
        {
            try
            {
                // 1. Save all pending buffers BEFORE killing servers (async LLM calls need llama-server alive)
                if (NPCMemoryManager.Instance != null)
                    NPCMemoryManager.Instance.SaveAllPendingBuffers();

                // 2. Give in-flight consolidations a moment to complete
                System.Threading.Thread.Sleep(2000);

                // 3. Now save the consolidated memories
                if (NPCMemoryManager.Instance != null)
                    NPCMemoryManager.Instance.SaveAndWait();

                // 4. Finally kill servers
                ServerManager.StopServers();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[XNPCVoiceControl] Application.Quit cleanup error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 3.0 pause menu button wiring.
    /// Patching OnOpen (not Init) because Init fires during XUi loading — before
    /// GameStartDone and before Harmony patches are applied.
    /// OnOpen fires every time the player opens the pause menu, which is always
    /// after our patches are live. _wired guards against re-subscribing.
    /// </summary>
    public class InGameMenuPatch
    {
        // Track the last wired button instance to avoid double-subscribing after UI rebuilds.
        // After resume save, the old XUiC_SimpleButton is destroyed — new instance detected via != null check.
        private static XUiC_SimpleButton _lastWiredButton;

        public static void Postfix(XUiC_InGameMenuWindow __instance)
        {
            try
            {
                var btn = __instance.GetChildById("btnXNPCVoiceControlConfig") as XUiC_SimpleButton;
                if (btn == null)
                {
                    Log.Warning("[XNPCVoiceControl] InGameMenuPatch: button not found in DOM");
                    return;
                }
                // Unity null-check: if _lastWiredButton was destroyed (world reload) it compares as null.
                // If same instance, skip to avoid stacking handlers.
                if (_lastWiredButton != null && _lastWiredButton == btn)
                    return;

                btn.OnPressed += (XUiController _sender, int _mouseButton) =>
                {
                    // Open as modal — openInternal calls CloseAllOpenModalWindows internally,
                    // closing ingameMenu in the correct sequence without a race window.
                    __instance.xui.playerUI.windowManager.Open("XNPCVoiceControlConfigGroup", true);
                };
                _lastWiredButton = btn;
                Log.Out("[XNPCVoiceControl] InGameMenuPatch: button wired");
            }
            catch (Exception ex)
            {
                Log.Error($"[XNPCVoiceControl] InGameMenuPatch.OnOpen error: {ex.Message}");
            }
        }
    }
}
