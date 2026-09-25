using System.Collections.Generic;
using NPC.Core;
using NPC.Core.Agents;
using NPC.Core.Interaction;
using Space;
using UnityEngine;

namespace WanderingNpc
{
    /// <summary>
    /// Wanders any active node, or waits where it stands when told to; holds still facing the player while
    /// talked to. docs/agent.md#3-the-brain, docs/interaction.md
    /// </summary>
    internal sealed class WandererBrain : INpcBrain, INpcConversation
    {
        private static readonly string[] Orders = ["wait", "walk"];

        private bool waiting;
        private bool talking;

        internal NpcAgent Agent { get; set; } = null!; // set by WandererBody.Build right after Attach

        // INpcBrain: the agent calls these inside the NPC's acting scope.

        public void SlowPhase(int phase, Player player) { }

        public bool OverrideMovement(Player player, out Vector3 move, out bool wantMove)
        {
            move = Vector3.zero;
            wantMove = false;
            if (!talking) return false;

            Agent.FacePlayer(player);
            return true;
        }

        public Vector3 Steer(Player player, out bool wantMove) =>
            waiting ? Agent.Stay(out wantMove) : Agent.Wander(null, null, out wantMove);

        public Vector3 Constrain(Vector3 desired, ref bool wantMove) => desired;

        public NpcActivity Activity => waiting || talking
            ? new NpcActivity(NpcRecovery.None, false, true, true)
            : new NpcActivity(NpcRecovery.DropPlanAndPause, true, true, true);

        public string ActivityName => talking ? "Talk" : waiting ? "Wait" : "Wander";

        public void TryIdleFacing(Player player) { }

        public void OnWalkAbandoned(string why) => NpcLog.Log.LogInfo("[ai] Gave up a walk: " + why);

        public bool Holds(Transform t) => false;

        public bool Sheltered => false;

        public void OnInterrupted(string why) => talking = false;

        public void OnDied() { }

        public bool OnShipRebuilt() => false;

        // INpcConversation: NPC.Core's talk window, opened by looking at it and pressing Interact.

        public INpc Npc => Agent;
        public bool CanTalk => true;
        public string Title => Agent.Name;
        public string Greeting => "Just walking.";
        public IReadOnlyList<string> Commands => Orders;

        public string Answer(string text)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "wait":
                    waiting = true;
                    return "I'll wait here.";
                case "walk":
                    waiting = false;
                    return "Off I go.";
                default:
                    return "I only know: " + string.Join(", ", Orders) + ".";
            }
        }

        public void SetOpen(bool open) => talking = open;
    }
}
