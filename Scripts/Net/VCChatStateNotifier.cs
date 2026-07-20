namespace XNPCVoiceControl.Net
{
    /// <summary>
    /// Static entry point for chat-state sync (Phase 1b).
    /// Self-routing: on server (SP/listen-host) → no-op, local component IS the authority.
    /// On dedi client → send NetPackageVCChatState to server.
    /// </summary>
    public static class VCChatStateNotifier
    {
        /// <summary>
        /// Notify the server that a conversation with this NPC has started or ended.
        /// In SP/listen-host (IsServer==true) this is a no-op — zero packets, zero change.
        /// </summary>
        public static void Notify(int npcEntityId, bool active)
        {
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                return; // SP/listen-host: local component state IS the server state

            SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                NetPackageManager.GetPackage<NetPackageVCChatState>()
                    .Setup(npcEntityId, active), false);
        }
    }
}
