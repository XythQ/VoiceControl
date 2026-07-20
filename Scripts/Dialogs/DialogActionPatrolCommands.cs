using UnityEngine;
using XNPCVoiceControl;
using XNPCVoiceControl.Net;

/// <summary>
/// Dialog actions for patrol commands — callable from dialogs.xml clicks, not just phrase triggers.
/// Mirrors SCore's DialogActionExecuteCommandSDX pattern (extends DialogActionAddBuff, ActionType.AddBuff).
/// Routes mutations through VCCommandRouter for SP/MP uniformity.
/// Discovery names: SetPatrolPoint, 1-XNPCVoiceControl / Patrol, 1-XNPCVoiceControl / CancelPatrolRecord, 1-XNPCVoiceControl
/// </summary>

public class DialogActionSetPatrolPoint : DialogActionAddBuff
{
    public override BaseDialogAction.ActionTypes ActionType => BaseDialogAction.ActionTypes.AddBuff;

    public override void PerformAction(EntityPlayer player)
    {
        Log.Out($"[PATROL-DIALOG] SetPatrolPoint dialog action fired, HasCustomVar(CurrentNPC)={player.Buffs.HasCustomVar("CurrentNPC")}");
        int npcEntityId = player.Buffs.HasCustomVar("CurrentNPC") ? (int)player.Buffs.GetCustomVar("CurrentNPC") : -1;
        if (npcEntityId == -1) return;
        VCCommandRouter.Execute(player.entityId, npcEntityId, VCCommand.SetPatrolPoint);
    }
}

public class DialogActionPatrol : DialogActionAddBuff
{
    public override BaseDialogAction.ActionTypes ActionType => BaseDialogAction.ActionTypes.AddBuff;

    public override void PerformAction(EntityPlayer player)
    {
        Log.Out($"[PATROL-DIALOG] Patrol dialog action fired, HasCustomVar(CurrentNPC)={player.Buffs.HasCustomVar("CurrentNPC")}");
        int npcEntityId = player.Buffs.HasCustomVar("CurrentNPC") ? (int)player.Buffs.GetCustomVar("CurrentNPC") : -1;
        if (npcEntityId == -1) return;
        VCCommandRouter.Execute(player.entityId, npcEntityId, VCCommand.Patrol);
    }
}

public class DialogActionCancelPatrolRecord : DialogActionAddBuff
{
    public override BaseDialogAction.ActionTypes ActionType => BaseDialogAction.ActionTypes.AddBuff;

    public override void PerformAction(EntityPlayer player)
    {
        Log.Out($"[PATROL-DIALOG] CancelPatrolRecord dialog action fired, HasCustomVar(CurrentNPC)={player.Buffs.HasCustomVar("CurrentNPC")}");
        int npcEntityId = player.Buffs.HasCustomVar("CurrentNPC") ? (int)player.Buffs.GetCustomVar("CurrentNPC") : -1;
        if (npcEntityId == -1) return;
        VCCommandRouter.Execute(player.entityId, npcEntityId, VCCommand.CancelPatrolRecord);
    }
}
