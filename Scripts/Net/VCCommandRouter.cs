using System;
using System.Collections.Generic;
using UnityEngine;
using XNPCVoiceControl.Actions;
using XNPCVoiceControl.Core;

namespace XNPCVoiceControl.Net
{
    /// <summary>
    /// State-mutating voice/dialog commands execute here.
    /// SCore self-routing pattern: in SP/listen-host IsServer==true → direct execution, zero packets.
    /// On dedi client → send NetPackageVCCommand to server; server executes authoritatively.
    /// </summary>
    public enum VCCommand : byte
    {
        None = 0,
        Follow = 1,
        Stay = 2,
        Guard = 3,
        Wander = 4,

        SetPatrolPoint = 10,
        Patrol = 11,
        CancelPatrolRecord = 12,
        // BLOCKED — SCore gap, dedi denial shipped:
        // OpenInventory = 13,   client-side dialog-equivalent + dedi denial (see SCORE_OPENINVENTORY_DEDI_GAP.md)
        SwapWeapon = 14,         // SCore NetPackageWeaponSwap server branch is dead; we route via router
        SetFollowDistance = 15,  // arg = distance as string (e.g., "1", "2.5", "5")

        // Formations (extend from 20)
        SetFormation = 20,       // arg = "dir,dist" (e.g., "3,1" = left flank tight)

        // Commanded crouch — sets CrouchOverride cvar; SCore must ship the hook to honour it.
        SetCrouch = 21,          // arg ∈ {0,1,2} — 1=crouch, 2=stand, 0=resume leader mirroring

        // Combat mode — player-wide mode toggle (0=Hunting, 1=Threat Control, 2=Full Control).
        // Sets varNPCModMode on the player + applies buffs to all hired NPCs.
        // DEFERRED — replaced by vcDisengage engagement primitive; retained for future richer-mode work.
        SetCombatMode = 22,      // arg = "0", "1", or "2"

        // Engagement flag. arg "1"=engage (free-fight), "0"=disengage (hold + return). Per-NPC, networks.
        SetEngage = 23,          // arg = "0" or "1"

        // Tactical mode — player-wide toggle (0=normal chat, 1=command-only). Miss → clarification, no LLM/RAG.
        SetTacticalMode = 24,    // arg = "0" or "1"
    }

    public static class VCCommandRouter
    {
        /// <summary>
        /// Self-routing entry point. SP/listen-host: IsServer==true → ExecuteAuthoritative directly.
        /// Dedi client: apply client mirror + send to server.
        /// </summary>
        public static void Execute(int playerEntityId, int npcEntityId, VCCommand cmd, string arg = "")
        {
            if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                ApplyClientMirror(npcEntityId, cmd);
                SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(
                    NetPackageManager.GetPackage<NetPackageVCCommand>()
                        .Setup(playerEntityId, npcEntityId, (byte)cmd, arg), false);
                return;
            }

            ExecuteAuthoritative(playerEntityId, npcEntityId, cmd, arg);
        }

        /// <summary>
        /// Client mirror flags — UX gates read local component flags (e.g. voice-cancel's
        /// IsRecordingOrPatrolling). On remote clients the authoritative flags live server-side,
        /// so we set LOCAL chat-component flags on command send.
        /// 
        /// Documented drift risk: if the server rejects the command, the client mirror is
        /// optimistically wrong until the next command. Phase 2 adds rejection feedback.
        /// </summary>
        private static void ApplyClientMirror(int npcEntityId, VCCommand cmd)
        {
            if (ChatComponentManager.TryGet(npcEntityId, out var comp))
            {
                switch (cmd)
                {
                    case VCCommand.SetPatrolPoint:
                        comp.SetPatrolRecording(true);
                        break;
                    case VCCommand.Patrol:
                        comp.SetPatrolRecording(false);
                        comp.SetActivelyPatrolling(true);
                        break;
                    case VCCommand.CancelPatrolRecord:
                        comp.SetPatrolRecording(false);
                        comp.SetActivelyPatrolling(false);
                        break;
                }
            }
        }

        /// <summary>
        /// Execute a command for all NPCs in the squad roster.
        /// Resolves the VCCommand from triggerActionName, loops roster, executes per-NPC.
        /// Nearest NPC (ackResponderId) produces voice/subtitle; others comply silently.
        /// </summary>
        internal static void ExecuteForSquad(int playerEntityId, int ackResponderId,
            List<EntityAlive> roster, VCCommand cmd, string arg)
        {
            if (roster == null || roster.Count == 0)
            {
                Log.Out("[VC-NET] Squad command rejected: empty roster");
                return;
            }

            Log.Out($"[VC-NET] Squad {cmd} → {roster.Count} NPC(s)");

            // Player-wide commands — execute once, not N-loop.
            if (cmd == VCCommand.SetCombatMode || cmd == VCCommand.SetTacticalMode)
            {
                Execute(playerEntityId, roster[0].entityId, cmd, arg);
                return;
            }

            foreach (var npc in roster)
            {
                Execute(playerEntityId, npc.entityId, cmd, arg);
            }
        }

        /// <summary>
        /// Map a trigger action name to its VCCommand equivalent.


        /// <summary>
        /// Authoritative execution — validates then dispatches. Called on server (or SP/listen-host).
        /// All failures log "[VC-NET] Command {cmd} rejected: {reason}" — never silent.
        /// </summary>
        internal static void ExecuteAuthoritative(int playerEntityId, int npcEntityId, VCCommand cmd, string arg = "")
        {
            // Validate NPC exists and is a chat target
            var npc = GameManager.Instance?.World?.GetEntity(npcEntityId) as EntityAlive;
            if (npc == null)
            {
                Log.Out($"[VC-NET] Command {cmd} rejected: NPC entity {npcEntityId} not found");
                return;
            }
            if (!ChatComponentManager.IsChatTarget(npc))
            {
                Log.Out($"[VC-NET] Command {cmd} rejected: {npc.EntityName} is not a chat target");
                return;
            }

            // Validate player entity exists
            var player = GameManager.Instance?.World?.GetEntity(playerEntityId) as EntityPlayer;
            if (player == null)
            {
                Log.Out($"[VC-NET] Command {cmd} rejected: player entity {playerEntityId} not found");
                return;
            }

            // Validate player is the NPC's leader OR within 15m.
            // Leadership proves authority at any range (e.g. recalling a distant scout).
            // Non-leaders still need to be close — distance is a proxy for "talking to a stranger".
            var leader = EntityUtilities.GetLeaderOrOwner(npcEntityId);
            bool isLeader = leader != null && leader.entityId == playerEntityId;

            if (!isLeader)
            {
                float hDist = Vector3.Distance(new Vector3(npc.position.x, 0f, npc.position.z),
                                               new Vector3(player.position.x, 0f, player.position.z));
                if (hDist >= 15f)
                {
                    Log.Out($"[VC-NET] Command {cmd} rejected: player too far ({hDist:F1}m) from {npc.EntityName}");
                    return;
                }
            }

            // Sync server-side chat component leader from the already-validated player.
            // Patrol recording, billing reactor, and follow-assist all read _leaderEntityId;
            // on dedi the component was created by the attach pass with leader=-1 (cold start).
            var comp = ChatComponentManager.GetOrCreate(npc);
            if (comp != null)
                comp.SetLeader(player);

            // Dispatch to mutation body
            Log.Out($"[VC-NET] Executing {cmd} on {npc.EntityName} (id {npc.entityId}) for player {playerEntityId}");
            try
            {
                Dispatch(npc, player, npc.EntityName, cmd, arg);
            }
            catch (Exception ex)
            {
                Log.Error($"[VC-NET] Command {cmd} threw: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void Dispatch(EntityAlive npc, EntityPlayer player, string npcName, VCCommand cmd, string arg)
        {
            switch (cmd)
            {
                case VCCommand.Follow:
                    MutateFollow(npc);
                    break;
                case VCCommand.Stay:
                    MutateStay(npc);
                    break;
                case VCCommand.Guard:
                    MutateGuard(npc, arg);
                    break;
                case VCCommand.Wander:
                    MutateWander(npc);
                    break;
                case VCCommand.SetPatrolPoint:
                    MutateSetPatrolPoint(npc, player);
                    break;
                case VCCommand.Patrol:
                    MutatePatrol(npc, player, npcName);
                    break;
                case VCCommand.CancelPatrolRecord:
                    MutateCancelPatrolRecord(npc);
                    break;
                case VCCommand.SwapWeapon:
                    MutateSwapWeapon(npc, arg);
                    break;
                case VCCommand.SetFollowDistance:
                    MutateSetFollowDistance(npc, arg);
                    break;
                case VCCommand.SetFormation:
                    MutateSetFormation(npc, player, arg);
                    break;
                case VCCommand.SetCrouch:
                    MutateSetCrouch(npc, arg);
                    break;
                case VCCommand.SetCombatMode:
                    MutateSetCombatMode(player, arg);
                    break;
                case VCCommand.SetEngage:
                    MutateSetEngage(npc, arg);
                    break;
                case VCCommand.SetTacticalMode:
                    MutateSetTacticalMode(player, arg);
                    break;
            }
        }

        #region Mutation Bodies (extracted from PhraseTriggerHandler)

        /// <summary>Apply buffOrderFollow to NPC.</summary>
        internal static void MutateFollow(EntityAlive npc)
        {
            npc.Buffs.AddBuff("buffOrderFollow", 1, false);
        }

        /// <summary>Apply buffOrderStay to NPC.
        /// NOTE: base game removes party indicator on Stay (SCore dialog wheel does same). Not our bug.</summary>
        internal static void MutateStay(EntityAlive npc)
        {
            npc.Buffs.AddBuff("buffOrderStay", 1, false);
        }

        /// <summary>Apply buffOrderGuardHere to NPC (GuardHere) or buffOrderGuard (GuardReturn).</summary>
        internal static void MutateGuard(EntityAlive npc, string arg)
        {
            // arg="return" → GuardReturn (buffOrderGuard), else GuardHere (buffOrderGuardHere)
            string buffName = !string.IsNullOrEmpty(arg) && string.Equals(arg, "return", StringComparison.OrdinalIgnoreCase)
                ? "buffOrderGuard" : "buffOrderGuardHere";
            npc.Buffs.AddBuff(buffName, 1, false);
        }

        /// <summary>Apply buffOrderWander to NPC.</summary>
        internal static void MutateWander(EntityAlive npc)
        {
            npc.Buffs.AddBuff("buffOrderWander", 1, false);
        }

        /// <summary>
        /// Start recording patrol route — calls SCore ExecuteCMD "SetPatrol" which sets order to SetPatrolPoint.
        /// NPC follows player while mod-side CheckPatrolRecording() records waypoints on the 2s tick.
        /// </summary>
        internal static void MutateSetPatrolPoint(EntityAlive npc, EntityPlayer player)
        {
            EntityUtilities.ExecuteCMD(npc.entityId, "SetPatrol", player);
            // SCore's SetCurrentOrder() switch doesn't handle SetPatrolPoint — set the cvar directly.
            npc.Buffs.SetCustomVar("CurrentOrder", (float)EntityUtilities.Orders.SetPatrolPoint);
            // Set mod-owned recording flag so CheckPatrolRecording() doesn't rely on SCore's cvar.
            var comp = ChatComponentManager.GetOrCreate(npc);
            if (comp == null)
            {
                Log.Out($"[VC-NET] SetPatrolPoint: could not get/create chat component for {npc.EntityName}");
                return;
            }
            comp.SetPatrolRecording(true);
        }

        /// <summary>
        /// Start looping recorded patrol route — enforces >=2-point minimum, calls SCore ExecuteCMD "Patrol".
        /// </summary>
        internal static void MutatePatrol(EntityAlive npc, EntityPlayer player, string npcName)
        {
            if (!(npc is IEntityOrderReceiverSDX receiver) || receiver.PatrolCoordinates.Count < 2)
            {
                Log.Out($"[VC-NET] Patrol rejected: {npcName} has no recorded route (needs >=2 points)");
                return;
            }

            // Post-recording cleanup: remove A-B-A wobble points captured at corners.
            int removed = CleanupPatrolRoute(receiver.PatrolCoordinates);
            int count = receiver.PatrolCoordinates.Count;

            EntityUtilities.ExecuteCMD(npc.entityId, "Patrol", player);
            // Set mod-owned patrolling flag; clear recording.
            var comp = ChatComponentManager.GetOrCreate(npc);
            if (comp == null)
            {
                Log.Out($"[VC-NET] Patrol: could not get/create chat component for {npc.EntityName}");
                return;
            }
            comp.SetPatrolRecording(false);
            comp.SetActivelyPatrolling(true);
            Log.Out($"[PATROL] {npcName} starting patrol, {count} points ({removed} wobble points cleaned)");
        }

        /// <summary>
        /// Cancel patrol recording or stop active patrol — clears patrol points, sets order to Stay.
        /// </summary>
        internal static void MutateCancelPatrolRecord(EntityAlive npc)
        {
            if (npc is IEntityOrderReceiverSDX receiver)
                receiver.PatrolCoordinates.Clear();
            EntityUtilities.SetCurrentOrder(npc.entityId, EntityUtilities.Orders.Stay);
            // Clear mod-owned patrol flags.
            var comp = ChatComponentManager.GetOrCreate(npc);
            if (comp == null)
            {
                Log.Out($"[VC-NET] CancelPatrolRecord: could not get/create chat component for {npc.EntityName}");
                return;
            }
            comp.SetPatrolRecording(false);
            comp.SetActivelyPatrolling(false);
        }

        /// <summary>
        /// Swap NPC weapon — server-authoritative (SCore NetPackageWeaponSwap server branch is dead).
        /// arg = weaponClassId (e.g. "meleeNPCEmptyHand").
        /// Three steps: SetCustomVar for persistence, UpdateWeapon for real swap, UpdateHandItem for visual broadcast.
        /// </summary>
        internal static void MutateSwapWeapon(EntityAlive npc, string weaponClassId)
        {
            if (string.IsNullOrEmpty(weaponClassId))
            {
                Log.Out($"[VC-NET] SwapWeapon rejected: empty weaponClassId for {npc.EntityName}");
                return;
            }

            ItemValue item = ItemClass.GetItem(weaponClassId);
            if (item == null || item.IsEmpty())
            {
                Log.Out($"[VC-NET] SwapWeapon rejected: ItemClass.GetItem({weaponClassId}) returned null/empty for {npc.EntityName}");
                return;
            }

            // Step 1: SetCustomVar for persistence (survives save/load)
            npc.Buffs.SetCustomVar("CurrentWeaponID", item.GetItemId());

            // Step 2: IEntityAliveSDX.UpdateWeapon — server entity swaps the weapon for real
            // Interface takes string name (not ItemValue); concrete class has ItemValue overload but interface doesn't.
            if (!(npc is IEntityAliveSDX sdx))
            {
                Log.Out($"[VC-NET] SwapWeapon rejected: {npc.EntityName} does not implement IEntityAliveSDX");
                return;
            }
            sdx.UpdateWeapon(weaponClassId);

            // Step 3: EntityUtilities.UpdateHandItem — server→all-clients visual broadcast
            EntityUtilities.UpdateHandItem(npc.entityId, weaponClassId);

            Log.Out($"[VC-NET] SwapWeapon: {npc.EntityName} → {weaponClassId}");
        }

        /// <summary>Set vcFormationAngle + vcFormationDist cvars on NPC. arg = "angle,tierIdx".
        /// angle may be prefixed "abs:" for absolute compass bearing, else resolved from player yaw.</summary>
        internal static void MutateSetFormation(EntityAlive npc, EntityPlayer player, string arg)
        {
            if (string.IsNullOrEmpty(arg) || arg.IndexOf(',') < 0)
            {
                Log.Out($"[VC-NET] SetFormation rejected: invalid arg '{arg}' for {npc.EntityName}");
                return;
            }

            var parts = arg.Split(',');
            string angleStr = parts[0].Trim();
            int tierIdx = 0;
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out tierIdx))
            {
                Log.Out($"[VC-NET] SetFormation rejected: non-numeric tier in arg '{arg}' for {npc.EntityName}");
                return;
            }

            // Positional command — NPC must be on Follow order for DynamicFollow to read the cvars.
            // If guarding/staying, switch to Follow so the formation slot actually takes effect.
            if (EntityUtilities.GetCurrentOrder(npc.entityId) != EntityUtilities.Orders.Follow)
            {
                MutateFollow(npc);
                Log.Out($"[VC-NET] SetFormation: {npc.EntityName} switched to Follow (positional command)");
            }

            // tierIdx == 0 → cancel formation (plain follow).
            if (tierIdx == 0)
            {
                npc.Buffs.SetCustomVar("vcFormationAngle", 0f);
                npc.Buffs.SetCustomVar("vcFormationDist", 0f);
                Log.Out($"[VC-NET] SetFormation: {npc.EntityName} → cancel (plain follow)");
                return;
            }

            // Validate tier against config and resolve to metres.
            var formation = XNPCVoiceControlMod.Formation;
            if (formation == null || !formation.Distances.ContainsKey(tierIdx))
            {
                string valid = "(none)";
                if (formation != null && formation.Distances.Keys.Count > 0)
                {
                    var keys = new List<int>(formation.Distances.Keys);
                    keys.Sort();
                    valid = string.Join(", ", keys);
                }
                Log.Out($"[VC-NET] SetFormation rejected: tier index {tierIdx} not configured (valid: {valid}) for {npc.EntityName}");
                return;
            }

            float metres = formation.GetDistance(tierIdx);

            // Resolve angle to compass bearing.
            float bearing;
            if (angleStr.StartsWith("abs:"))
            {
                // Absolute compass bearing — snap to 45° grid.
                float rawBearing;
                if (!float.TryParse(angleStr.Substring(4), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rawBearing))
                {
                    Log.Out($"[VC-NET] SetFormation rejected: non-numeric bearing in '{angleStr}' for {npc.EntityName}");
                    return;
                }
                bearing = FormationUtils.Snap45(rawBearing);
            }
            else
            {
                // Relative angle (existing phrase convention: fwd 0, rear 180, left +90, right -90).
                // Resolve to world bearing using player yaw at command time.
                float relAngle;
                if (!float.TryParse(angleStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out relAngle))
                {
                    Log.Out($"[VC-NET] SetFormation rejected: non-numeric angle in '{angleStr}' for {npc.EntityName}");
                    return;
                }
                bearing = FormationUtils.ResolveRelativeToBearing(player.rotation.y, relAngle);
            }

            // Adjacency allocator — remap bearing to a free slot when another NPC occupies the same cardinal.
            // Only applies to abs: path (cardinal commands). Relative resolver already yields unique bearings.
            if (angleStr.StartsWith("abs:"))
            {
                float requested = bearing;
                var world = GameManager.Instance?.World;
                HashSet<float> occupancy = null;
                if (world != null)
                {
                    occupancy = new HashSet<float>();
                    foreach (var entity in world.Entities.list)
                    {
                        if (entity is EntityAlive other && Core.ChatComponentManager.IsChatTarget(other))
                        {
                            if (other.entityId == npc.entityId) continue; // exclude self
                            var leader = EntityUtilities.GetLeaderOrOwner(other.entityId);
                            if (leader != null && leader.entityId == player.entityId)
                            {
                                float otherDist = other.Buffs.GetCustomVar("vcFormationDist");
                                if (otherDist > 0f)
                                {
                                    float otherAngle = other.Buffs.GetCustomVar("vcFormationAngle");
                                    occupancy.Add(FormationUtils.Snap45(otherAngle));
                                }
                            }
                        }
                    }
                }

                float chosen = requested;
                if (occupancy != null && occupancy.Contains(requested))
                {
                    float tryClockwise = ((requested + 45f) % 360f + 360f) % 360f;
                    float tryCounterClockwise = ((requested + 315f) % 360f + 360f) % 360f;
                    if (!occupancy.Contains(tryClockwise))
                    {
                        chosen = tryClockwise;
                    }
                    else if (!occupancy.Contains(tryCounterClockwise))
                    {
                        chosen = tryCounterClockwise;
                    }
                    // else all three occupied — stack on requested
                }

                if (chosen != requested)
                {
                    Log.Out($"[FORMATION-ALLOC] {npc.EntityName} requested {requested:F0}\u00b0 ({CardinalName(requested)}) → {chosen:F0}\u00b0 ({CardinalName(chosen)})");
                }
                else if (occupancy != null && occupancy.Contains(requested))
                {
                    Log.Out($"[FORMATION-ALLOC] {npc.EntityName} requested {requested:F0}\u00b0 ({CardinalName(requested)}) → stacked");
                }
                bearing = chosen;
            }

            // Position order clears stance — don't leave a crouch pinned when the NPC is about to move.
            npc.Buffs.SetCustomVar("CrouchOverride", 0);

            npc.Buffs.SetCustomVar("vcFormationAngle", bearing);
            npc.Buffs.SetCustomVar("vcFormationDist", metres);

            Log.Out($"[VC-NET] SetFormation: {npc.EntityName} → socket {bearing:F0}\u00b0 ({CardinalName(bearing)}), dist {metres}m (tier {tierIdx})");
        }

        /// <summary>Map a compass bearing to an 8-way cardinal name.</summary>
        private static string CardinalName(float b)
        {
            float snapped = FormationUtils.Snap45(b);
            if (snapped < 22.5f) return "N";
            if (snapped < 67.5f) return "NE";
            if (snapped < 112.5f) return "E";
            if (snapped < 157.5f) return "SE";
            if (snapped < 202.5f) return "S";
            if (snapped < 247.5f) return "SW";
            if (snapped < 292.5f) return "W";
            return "NW";
        }

        /// <summary>Set CrouchOverride cvar on NPC. arg ∈ {0,1,2}.</summary>
        /// NOTE: this is inert until SCore ships the CrouchOverride hook in follow/idle tasks.
        /// The cvar is set correctly; SCore's per-tick leader mirror still wins until then.
        internal static void MutateSetCrouch(EntityAlive npc, string arg)
        {
            if (!int.TryParse(arg, out int val) || val < 0 || val > 2)
            {
                Log.Out($"[VC-NET] SetCrouch rejected: invalid arg '{arg}' for {npc.EntityName} (must be 0,1,2)");
                return;
            }

            npc.Buffs.SetCustomVar("CrouchOverride", val);

            string label = val switch { 1 => "crouch", 2 => "stand", 0 => "mirror" };
            Log.Out($"[VC-NET] SetCrouch: {npc.EntityName} → CrouchOverride={val} ({label})");
        }

        /// <summary>Mode-aware distance adjustment.
        /// If NPC is locked in a formation slot (vcFormationDist > 0), adjusts the SOCKET RADIUS;
        /// otherwise adjusts the plain-follow distance. One vocabulary for both modes.
        /// Does NOT change order — passive preference only.</summary>
        internal static void MutateSetFollowDistance(EntityAlive npc, string distanceStr)
        {
            if (!float.TryParse(distanceStr, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float dist))
            {
                Log.Out($"[DIST] REJECTED: invalid distance '{distanceStr}' for {npc.EntityName}");
                return;
            }
            if (dist < 0.5f || dist > 10f)
            {
                Log.Out($"[DIST] REJECTED: {dist} out of range (0.5-10) for {npc.EntityName}");
                return;
            }
            // Mode-aware: an NPC locked in a formation slot (vcFormationDist > 0) has its SOCKET RADIUS
            // adjusted; a free-following NPC has its plain-follow distance adjusted. The system knows the
            // mode, so the player only needs ONE vocabulary. Does NOT change order (passive preference).
            float formDist = EntityUtilities.GetCVarValue(npc.entityId, "vcFormationDist");
            string targetCvar = formDist > 0f ? "vcFormationDist" : "vcFollowMin";
            float before = EntityUtilities.GetCVarValue(npc.entityId, targetCvar);
            npc.Buffs.SetCustomVar(targetCvar, dist);
            float after = EntityUtilities.GetCVarValue(npc.entityId, targetCvar);
            Log.Out($"[DIST] SET {npc.EntityName} ({targetCvar}, in-formation={formDist > 0f}): before={before} -> set={dist} -> after={after}");
        }

        /// <summary>
        /// Set combat mode on the player (varNPCModMode) + apply buffs to all hired NPCs.
        /// arg = "0"=Hunting, "1"=Threat Control, "2"=Full Control.
        /// Player-wide — executes once even in squad fan-out (handled in ExecuteForSquad).
        /// </summary>
        internal static void MutateSetCombatMode(EntityPlayer player, string arg)
        {
            if (!int.TryParse(arg, out int modeValue) || modeValue < 0 || modeValue > 2)
            {
                Log.Out($"[VC-NET] SetCombatMode rejected: invalid arg '{arg}' (must be 0,1,2)");
                return;
            }

            // Set mode on player
            player.Buffs.SetCustomVar("varNPCModMode", modeValue);
            player.Buffs.SetCustomVar("$varNPCModModeChange", 1);

            // Apply mode buffs to all hired NPCs
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
                    // Mode 0 (Hunting): no buff needed

                    affectedCount++;
                }
            }

            string modeName = modeValue switch { 0 => "Hunting", 1 => "Threat Control", 2 => "Full Control" };
            Log.Out($"[VC-NET] SetCombatMode: player {player.entityId} → {modeName} (mode {modeValue}), applied to {affectedCount} hired NPC(s)");
        }

        /// <summary>
        /// Engagement flag. arg = "1"=engage (free-fight), "0"=disengage (hold + return).
        /// Disengaging also clears attack targets, cancels formation, and forces Follow order.
        /// Per-NPC (not player-wide like SetCombatMode).
        /// </summary>
        internal static void MutateSetEngage(EntityAlive npc, string arg)
        {
            if (!int.TryParse(arg, out int val) || val < 0 || val > 2)
            {
                Log.Out($"[VC-NET] SetEngage rejected: invalid arg '{arg}' for {npc.EntityName}");
                return;
            }

            npc.Buffs.SetCustomVar("vcEngage", val);

            if (val == 0 || val == 2)
            {
                // Stop fighting now (disengage or passive): drop target, cancel formation, return to leader on Follow.
                EntityUtilities.ClearAttackTargets(npc.entityId);
                npc.Buffs.SetCustomVar("vcFormationAngle", 0f);
                npc.Buffs.SetCustomVar("vcFormationDist", 0f);
                EntityUtilities.SetCurrentOrder(npc.entityId, EntityUtilities.Orders.Follow);
            }

            string state = val switch { 1 => "engaged (free-fight)", 2 => "passive (never engage)", _ => "assist only" };
            Log.Out($"[VC-NET] SetEngage: {npc.EntityName} (id {npc.entityId}) \u2192 {state}");
        }

        /// <summary>
        /// Tactical mode toggle. arg = "1"=tactical (command-only), "0"=normal (chat + commands).
        /// Player-wide — sets varTacticalMode on the player entity.
        /// </summary>
        internal static void MutateSetTacticalMode(EntityPlayer player, string arg)
        {
            if (!int.TryParse(arg, out int val) || val < 0 || val > 1)
            {
                Log.Out($"[VC-NET] SetTacticalMode rejected: invalid arg '{arg}' (must be 0 or 1)");
                return;
            }

            player.Buffs.SetCustomVar("varTacticalMode", val);
            string mode = val == 1 ? "tactical (command-only)" : "normal (chat + commands)";
            Log.Out($"[VC-NET] SetTacticalMode: player {player.entityId} -> {mode}");
        }

        /// <summary>
        /// Remove A-B-A wobble points captured at corners: a point is noise when the trail
        /// returns nearly adjacent to where it was, i.e. its neighbors are closer to each
        /// other than one diagonal block. Straights: |prev-next| ~2m. Real 90° corner:
        /// ~1.41m (block diagonal). Wobble (lateral or reversal): <= ~1.0m. 1.2f splits them.
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

        #endregion
    }
}
