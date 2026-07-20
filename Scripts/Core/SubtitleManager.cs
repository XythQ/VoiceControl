using System;
using System.Collections;
using UnityEngine;
using XNPCVoiceControl;

namespace XNPCVoiceControl.UI
{
    /// <summary>
    /// Dedicated singleton for managing NPC subtitle display.
    /// Lives on DontDestroyOnLoad so auto-close coroutines survive UI lifecycle changes.
    /// Purely client-side — no server logic mixed in.
    ///
    /// The subtitle window is opened ONCE and never closed (only hidden via IsVisible).
    /// This prevents UILabel.OnEnable from re-firing on every show/hide cycle, which
    /// can crash Unity's native font system (Font_CUSTOM_RequestCharactersInTexture) when
    /// NGUIText.defaultFont is null after a close/reopen sequence.
    /// </summary>
    public class SubtitleManager : MonoBehaviour
    {
        private static SubtitleManager _instance;

        public static SubtitleManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("SubtitleManager");
                    _instance = go.AddComponent<SubtitleManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private GUIWindowManager _windowManager;
        private GUIWindowManager _openedOnWindowManager;  // tracks which manager instance we opened on

        /// <summary>
        /// Show a subtitle with the given NPC name and text. Auto-hides after <paramref name="duration"/> seconds.
        /// New calls overwrite any currently displayed subtitle and reset the hide timer.
        /// </summary>
        public void ShowSubtitle(string npcName, string text, float duration = 8f)
        {
            if (GameManager.IsDedicatedServer) return;
            EnsureWindowManager();
            if (_windowManager == null) return;

            // Open the subtitle window the first time, and again whenever a world/XUi reload
            // swaps in a new window manager (the old NPCSubtitlesGroup is gone). Opening once
            // per manager instance avoids the UILabel.OnEnable font crash from repeated reopens.
            if (!ReferenceEquals(_openedOnWindowManager, _windowManager))
            {
                _windowManager.Open("NPCSubtitlesGroup", false, false);
                _openedOnWindowManager = _windowManager;
            }

            UpdateControllerText(npcName, text);
            SetControllerVisible(true);

            StopAllCoroutines();
            StartCoroutine(AutoHideRoutine(duration));
        }

        private void EnsureWindowManager()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer() as EntityPlayerLocal;
            if (player == null) return;
            var playerUI = LocalPlayerUI.GetUIForPlayer(player);
            if (playerUI?.windowManager != null)
                _windowManager = playerUI.windowManager;
        }

        private void UpdateControllerText(string npcName, string text)
        {
            if (_windowManager == null) return;
            XUiWindowGroup group = _windowManager.GetWindow<XUiWindowGroup>("NPCSubtitlesGroup");
            if (group?.Controller == null) return;
            var subtitleController = group.Controller.GetChildByType<XUiC_NPCSubtitles>();
            subtitleController?.SetSubtitle(npcName, text);
        }

        private void SetControllerVisible(bool visible)
        {
            if (_windowManager == null) return;
            XUiWindowGroup group = _windowManager.GetWindow<XUiWindowGroup>("NPCSubtitlesGroup");
            if (group?.Controller == null) return;

            var subtitleController = group.Controller.GetChildByType<XUiC_NPCSubtitles>();

            // Managed safety net: if SetVisible throws (e.g. font access during D3D device loss),
            // log and defer rather than bubbling a crash.
            try
            {
                subtitleController?.SetVisible(visible);
            }
            catch (Exception ex)
            {
                Log.Warning($"SubtitleManager: SetVisible({visible}) threw, deferring — {ex.Message}");
                if (visible)
                    MainThreadDispatcher.Enqueue(() => SetControllerVisible(true));
            }
        }

        private IEnumerator AutoHideRoutine(float duration)
        {
            yield return new WaitForSeconds(duration);
            SetControllerVisible(false);
        }

        /// <summary>
        /// Immediately hide the subtitle (e.g., during conversation interruption).
        /// Does not close the window — preserves font state for next show.
        /// </summary>
        public void ClearSubtitle()
        {
            StopAllCoroutines();
            SetControllerVisible(false);
        }

        /// <summary>
        /// Incrementally update subtitle text without resetting auto-hide timer.
        /// Used by the typewriter effect to stream characters one at a time.
        /// </summary>
        public void UpdateSubtitle(string npcName, string incrementalText)
        {
            EnsureWindowManager();
            if (_windowManager == null) return;

            XUiWindowGroup group = _windowManager.GetWindow<XUiWindowGroup>("NPCSubtitlesGroup");
            if (group?.Controller == null) return;
            var subtitleController = group.Controller.GetChildByType<XUiC_NPCSubtitles>();
            subtitleController?.SetSubtitle(npcName, incrementalText);
        }
    }
}
