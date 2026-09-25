using System.Collections.Generic;

namespace NPC.Core.Interaction
{
    /// <summary>
    /// What the player can say to an NPC through the talk window, and what it answers. Register one per
    /// NPC with <see cref="NpcInteraction.Register"/>. docs/interaction.md
    /// </summary>
    public interface INpcConversation
    {
        /// <summary>
        /// The NPC this conversation is with: what the player looks at, and whose log the lines go to.
        /// </summary>
        INpc Npc { get; }

        /// <summary>
        /// Whether the player may open the window on it now. NPC.Core already checks that it lives and is
        /// loaded; add your own: asleep, hidden, your mod's setting.
        /// </summary>
        bool CanTalk { get; }

        /// <summary>
        /// The window's title, read when it opens.
        /// </summary>
        string Title { get; }

        /// <summary>
        /// Said the first time the window opens on this NPC.
        /// </summary>
        string Greeting { get; }

        /// <summary>
        /// The commands page: each entry is sent as if typed. In the order drawn.
        /// </summary>
        IReadOnlyList<string> Commands { get; }

        /// <summary>
        /// The NPC's answer to what the player typed or picked.
        /// </summary>
        string Answer(string text);

        /// <summary>
        /// The window opened (true) or closed (false) on this NPC. While it is open the NPC should hold
        /// still and face the player: walking off mid-sentence is not a conversation.
        /// </summary>
        void SetOpen(bool open);
    }
}
