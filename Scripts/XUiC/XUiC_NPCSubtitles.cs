using UnityEngine;
using XNPCVoiceControl;

// XUI controllers must be in the global namespace for 7DTD to find them
/// <summary>
/// Controller for the NPC subtitle window.
/// Shows NPC name + dialogue text at bottom-center of screen, auto-closes after duration.
/// Text is set by SubtitleManager (DontDestroyOnLoad singleton).
/// </summary>
public class XUiC_NPCSubtitles : XUiController
{
    private XUiV_Label _lblName;
    private XUiV_Label _lblText;
    private XUiV_Panel _bgPanel;

    public override void Init()
    {
        base.Init();

        var labels = GetChildrenByViewType<XUiV_Label>();
        foreach (var label in labels)
        {
            if (label.ID == "lblName") _lblName = label;
            else if (label.ID == "lblText") _lblText = label;
        }

        var panels = GetChildrenByViewType<XUiV_Panel>();
        foreach (var panel in panels)
        {
            if (panel.ID == "bg") { _bgPanel = panel; break; }
        }

    }

    /// <summary>
    /// Set the subtitle text. Called by SubtitleManager.
    /// </summary>
    public void SetSubtitle(string npcName, string text)
    {
        if (_lblName != null) _lblName.Text = npcName;
        if (_lblText != null) _lblText.Text = text;
    }

    /// <summary>
    /// Show or hide all subtitle elements without closing the window.
    /// Prevents UILabel.OnEnable re-triggering the font crash on every show/hide cycle.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_lblName != null) _lblName.IsVisible = visible;
        if (_lblText != null) _lblText.IsVisible = visible;
        if (_bgPanel != null) _bgPanel.IsVisible = visible;
    }

    /// <summary>
    /// Update only the dialogue text (not NPC name). Used by typewriter effect.
    /// </summary>
    public void UpdateText(string text)
    {
        if (_lblText != null) _lblText.Text = text;
    }
}
