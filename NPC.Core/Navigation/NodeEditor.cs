using System.Collections.Generic;
using BepInEx.Configuration;
using NPC.Core.World;
using Space;
using UnityEngine;

namespace NPC.Core.Navigation
{
    /// <summary>
    /// In-game node editor: pooled LineRenderers for the 3D overlay, IMGUI for the
    /// panel. Toggle with 'node_editor' or the configured key (default F8); all keys
    /// live in the BepInEx config section "NodeEditor". docs/reference.md §3
    /// </summary>
    internal sealed class NodeEditor : MonoBehaviour
    {
        private int selectedNode = -1;
        private int lastSelectedNode = -1;
        private int markedNode = -1; // first node of a pending link
        private float lastPlaceTime = 0f;
        private float lastDeleteTime = 0f;
        private float visUpdateTimer = 0f;
        private bool showConnections = false;

        // Cached node positions (refreshed each frame while editor is active)
        private List<Vector3>? cachedNodes = null;
        private float cacheRefreshAt = 0f;
        private Vector3 lastPlayerPos = Vector3.zero;

        // Markers and edges only draw near the player, so a large graph does not paint
        // off-screen crosshairs and long-haul lines across the whole sector.
        private const float MaxDrawnEdgePlayerDist = 80f;
        private const float MaxDrawnNodePlayerDist = 80f;

        private readonly List<LineRenderer> nodeVisLines = [];
        private readonly List<LineRenderer> connVisLines = [];
        private readonly List<(Vector3 a, Vector3 b)> edgeLines = [];
        private Material? lrMaterial = null;

        /// <summary>
        /// Toggle the editor on/off.
        /// </summary>
        internal bool Active { get; set; }

        private static bool KeyDown(ConfigEntry<KeyboardShortcut> entry)
        {
            KeyboardShortcut shortcut = entry.Value;
            return shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey);
        }

        private static string KeyName(ConfigEntry<KeyboardShortcut> entry)
        {
            KeyboardShortcut shortcut = entry.Value;
            return shortcut.MainKey != KeyCode.None ? shortcut.MainKey.ToString() : "<unset>";
        }

        private void Update()
        {
            bool consoleOpen = NpcConsole.IsOpen;

            // Toggle key works even while the editor is closed (F8 by default).
            if (!consoleOpen && KeyDown(NpcCorePlugin.ConfigEditorToggleKey))
            {
                Active = !Active;
                NpcLog.Log.LogInfo("[editor] Editor " + (Active ? "opened" : "closed"));
            }

            if (!Active)
            {
                HideUnusedLines(nodeVisLines, 0);
                HideUnusedLines(connVisLines, 0);
                return;
            }

            if (GameManager.Instance == null || consoleOpen) return;

            Player? player = NpcPlayer.Pilot;
            if (player == null || player.Controller == null) return;

            Vector3 playerPos = player.Controller.CachedTransform.position;
            lastPlayerPos = playerPos;

            // Refresh cached node positions
            if (cachedNodes == null || Time.time >= cacheRefreshAt)
            {
                cachedNodes = NavGraph.GetAllNodesWorld();
                cacheRefreshAt = Time.time + 0.2f;

                // Nearest to the player on the same deck: without the height filter,
                // nodes a floor away steal the selection and Delete removes the wrong
                // one. docs/invariants.md#floor-to-floor
                selectedNode = -1;
                float bestDistanceSquared = 3f * 3f;
                for (int i = 0; i < cachedNodes.Count; i++)
                {
                    Vector3 p = cachedNodes[i];
                    if (Mathf.Abs(p.y - playerPos.y) > 2.5f) continue;
                    // A node in a room not built sits where that room will stand, often on top of a live one.
                    if (!NavGraph.IsNodeActive(i)) continue;

                    float distanceSquared = (p - playerPos).sqrMagnitude;
                    if (distanceSquared < bestDistanceSquared)
                    {
                        bestDistanceSquared = distanceSquared;
                        selectedNode = i;
                    }
                }
            }

            // Key: Place (Insert by default, or Numpad0) a node at player position
            if ((KeyDown(NpcCorePlugin.ConfigEditorPlaceKey) || Input.GetKeyDown(KeyCode.Keypad0)) &&
                Time.time - lastPlaceTime > 0.3f)
            {
                lastPlaceTime = Time.time;
                NavGraph.AddNode(playerPos);
                cachedNodes = NavGraph.GetAllNodesWorld();
                int placed = NavGraph.NodeCount - 1;
                NpcLog.Log.LogInfo("[editor] Node placed at " + playerPos.ToString("0.00") +
                                            " owner '" + NavGraph.GetNodeOwner(placed) +
                                            "' on '" + (NavGraph.GetNodeAnchor(placed) ?? "-") +
                                            "' (total " + NavGraph.NodeCount + ")");
            }

            // Key: Delete (by default) removes the nearest node
            if (KeyDown(NpcCorePlugin.ConfigEditorDeleteKey) && selectedNode >= 0 &&
                Time.time - lastDeleteTime > 0.3f)
            {
                lastDeleteTime = Time.time;
                Vector3 removed = NavGraph.GetNodeWorld(selectedNode);
                string? removedOwner = NavGraph.GetNodeOwner(selectedNode);
                NavGraph.RemoveNode(selectedNode);
                cachedNodes = NavGraph.GetAllNodesWorld();
                selectedNode = -1;
                NpcLog.Log.LogInfo("[editor] Node removed at " + removed.ToString("0.00") +
                                            " owner '" + removedOwner +
                                            "' (total " + NavGraph.NodeCount + ")");
            }

            // Key: L (by default) toggles connection line rendering
            if (KeyDown(NpcCorePlugin.ConfigEditorLinksKey))
            {
                showConnections = !showConnections;
                UpdateVisualization();
            }

            // Key: K (by default) - two-phase linking. First press marks the selected
            // node; the second press (at another node) creates or removes the link
            // between the marked and the currently selected node.
            HandleLinkKey(NpcCorePlugin.ConfigEditorForceLinkKey);

            // Key: U (by default) removes every link of the selected node in one go.
            if (KeyDown(NpcCorePlugin.ConfigEditorClearLinksKey) && selectedNode >= 0)
            {
                int cleared = NavGraph.ClearLinks(selectedNode);
                NpcLog.Log.LogInfo(cleared > 0
                    ? "[editor] Node #" + selectedNode + ": " + cleared + " link(s) removed"
                    : "[editor] Node #" + selectedNode + " has no links");
                UpdateVisualization();
            }

            // Key: T (by default) cycles the selected node's type Ground <-> Stair.
            if (KeyDown(NpcCorePlugin.ConfigEditorTypeKey) && selectedNode >= 0)
            {
                NodeType current = NavGraph.GetNodeType(selectedNode);
                NodeType next = current == NodeType.Stair
                    ? NodeType.Ground
                    : NodeType.Stair;
                NavGraph.SetNodeType(selectedNode, next);
                NpcLog.Log.LogInfo("[editor] Node #" + selectedNode + " type: " + next);
                UpdateVisualization();
            }

            // Key: F6 (by default) saves. F5 is the game's QuickSave - never bind it here.
            if (KeyDown(NpcCorePlugin.ConfigEditorSaveKey))
            {
                NavGraph.Save();
                NpcLog.Log.LogInfo("[editor] Nodes saved (" + NavGraph.NodeCount + " nodes)");
            }

            visUpdateTimer -= Time.deltaTime;
            if (visUpdateTimer <= 0f || selectedNode != lastSelectedNode)
            {
                visUpdateTimer = 0.5f;
                lastSelectedNode = selectedNode;
                UpdateVisualization();
            }
        }

        /// <summary>
        /// Two-phase linking: mark a node, walk to another one, press again to link them
        /// (or to remove the link, if pressed at an already-linked pair).
        /// </summary>
        private void HandleLinkKey(ConfigEntry<KeyboardShortcut> entry)
        {
            if (!KeyDown(entry)) return;

            if (markedNode < 0)
            {
                if (selectedNode < 0)
                {
                    NpcLog.Log.LogWarning("[editor] No node selected - stand closer to one first");
                    return;
                }
                markedNode = selectedNode;
                NpcLog.Log.LogInfo("[editor] Marked node #" + markedNode +
                                            " - now stand at another node and press " + KeyName(entry) +
                                            " to link (press on the same node to cancel)");
                UpdateVisualization();
                return;
            }

            if (selectedNode < 0 || selectedNode == markedNode)
            {
                markedNode = -1;
                NpcLog.Log.LogInfo("[editor] Link mark cancelled");
                UpdateVisualization();
                return;
            }

            bool created = NavGraph.ToggleLink(markedNode, selectedNode);
            NpcLog.Log.LogInfo(!created
                ? $"[editor] Link removed between #{markedNode} and #{selectedNode}"
                : $"[editor] Link created: #{markedNode} <-> #{selectedNode}" +
                  (NavGraph.LinkIsStageBound(markedNode, selectedNode)
                      ? " (holds only while the ship's rooms stand as they do now)"
                      : ""));
            markedNode = -1;
            UpdateVisualization();
        }

        private LineRenderer GetLineRenderer(List<LineRenderer> pool, int index, float width, Color color)
        {
            if (lrMaterial == null)
            {
                lrMaterial = new Material(Shader.Find("Sprites/Default"));
            }

            while (pool.Count <= index)
            {
                GameObject go = new("NPC.Core EditorLine")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                LineRenderer lr = go.AddComponent<LineRenderer>();
                lr.material = lrMaterial;
                lr.useWorldSpace = true;
                pool.Add(lr);
            }

            LineRenderer result = pool[index];
            result.gameObject.SetActive(true);
            result.startWidth = width;
            result.endWidth = width;
            result.startColor = color;
            result.endColor = color;
            return result;
        }

        private void HideUnusedLines(List<LineRenderer> pool, int usedCount)
        {
            for (int i = usedCount; i < pool.Count; i++)
            {
                if (pool[i] != null && pool[i].gameObject != null)
                {
                    pool[i].gameObject.SetActive(false);
                }
            }
        }

        private Color NodeColor(int index)
        {
            if (index == markedNode) return new Color(1f, 0.4f, 1f); // magenta: marked for a link
            if (index == selectedNode) return Color.red;
            if (!NavGraph.IsNodeActive(index)) return new Color(0.45f, 0.45f, 0.45f); // inactive or unbuilt
            if (NavGraph.GetNodeType(index) == NodeType.Stair) return new Color(1f, 0.55f, 0.1f);
            return Color.green;
        }

        private void UpdateVisualization()
        {
            if (cachedNodes == null || cachedNodes.Count == 0)
            {
                HideUnusedLines(nodeVisLines, 0);
                HideUnusedLines(connVisLines, 0);
                return;
            }

            int nodeVisIndex = 0;

            for (int i = 0; i < cachedNodes.Count; i++)
            {
                Vector3 p = cachedNodes[i];
                // Skip distant nodes, which would bloat the LineRenderer pool with
                // off-screen markers. Selected and marked nodes are always nearby.
                if ((p - lastPlayerPos).sqrMagnitude > MaxDrawnNodePlayerDist * MaxDrawnNodePlayerDist) continue;

                Color color = NodeColor(i);
                float size = i == selectedNode ? 0.25f : 0.12f;

                // Vertical
                LineRenderer lr1 = GetLineRenderer(nodeVisLines, nodeVisIndex++, 0.05f, color);
                lr1.positionCount = 2;
                lr1.SetPosition(0, p - Vector3.up * size);
                lr1.SetPosition(1, p + Vector3.up * size);

                // Horizontal
                LineRenderer lr2 = GetLineRenderer(nodeVisLines, nodeVisIndex++, 0.05f, color);
                lr2.positionCount = 2;
                lr2.SetPosition(0, p - Vector3.right * size);
                lr2.SetPosition(1, p + Vector3.right * size);

                // Forward
                LineRenderer lr3 = GetLineRenderer(nodeVisLines, nodeVisIndex++, 0.05f, color);
                lr3.positionCount = 2;
                lr3.SetPosition(0, p - Vector3.forward * size);
                lr3.SetPosition(1, p + Vector3.forward * size);
            }

            int connVisIndex = 0;
            if (showConnections)
            {
                // Draw the actual graph. Edges are only drawn near the player - long-haul
                // connections to a station across the sector would otherwise paint lines
                // everywhere.
                edgeLines.Clear();
                NavGraph.GetEdgeVisuals(edgeLines);

                foreach ((Vector3 a, Vector3 b) in edgeLines)
                {
                    if ((a - lastPlayerPos).sqrMagnitude > MaxDrawnEdgePlayerDist * MaxDrawnEdgePlayerDist ||
                        (b - lastPlayerPos).sqrMagnitude > MaxDrawnEdgePlayerDist * MaxDrawnEdgePlayerDist)
                    {
                        continue;
                    }

                    LineRenderer lr = GetLineRenderer(connVisLines, connVisIndex++, 0.025f, Color.yellow);
                    lr.positionCount = 2;
                    lr.SetPosition(0, a + Vector3.up * 0.5f);
                    lr.SetPosition(1, b + Vector3.up * 0.5f);
                }
            }

            HideUnusedLines(nodeVisLines, nodeVisIndex);
            HideUnusedLines(connVisLines, connVisIndex);
        }

        // ----------------------------------------------------------------
        // IMGUI overlay
        // ----------------------------------------------------------------

        private void OnGUI()
        {
            if (!Active) return;

            // Draw-only panel: docs/invariants.md#read-only-panels-build-on-repaint
            if (UnityEngine.Event.current.type != EventType.Repaint) return;

            // Sized to the lines drawn below, plus slack for a wrapped node summary.
            // Grow it here if you add a control line.
            float boxW = 380f;
            float boxH = 230f;
            float x = Screen.width - boxW - 10f;
            float y = 10f;

            GUI.Box(new Rect(x, y, boxW, boxH), "Node Editor");

            string text = "Nodes: " + NavGraph.DescribeNodes();

            if (markedNode >= 0) text += "\nMarked: #" + markedNode + " (magenta) - select another node, then K";

            if (selectedNode >= 0 && cachedNodes != null && selectedNode < cachedNodes.Count)
            {
                Player? player = NpcPlayer.Pilot;
                float dist = player != null && player.Controller != null
                    ? Vector3.Distance(player.Controller.CachedTransform.position, cachedNodes[selectedNode])
                    : 0f;
                string? anchor = NavGraph.GetNodeAnchor(selectedNode);
                text += "\nNearest: #" + selectedNode + " (" + dist.ToString("0.0") + "m)" +
                        " [" + NavGraph.GetNodeOwner(selectedNode) + (anchor != null ? "/" + anchor : "") +
                        (NavGraph.GetNodeType(selectedNode) == NodeType.Stair ? ", stair" : "") +
                        "]";
            }
            else
            {
                text += "\nNearest: none (walk closer)";
            }

            text += "\n\n--- Controls ---";
            text += "\n" + KeyName(NpcCorePlugin.ConfigEditorToggleKey) + ": Toggle editor";
            text += "\n" + KeyName(NpcCorePlugin.ConfigEditorPlaceKey) + "/Numpad0: Place node";
            text += "\n" + KeyName(NpcCorePlugin.ConfigEditorDeleteKey) + ": Remove nearest node";
            text += "\n" + KeyName(NpcCorePlugin.ConfigEditorLinksKey) + ": Toggle connections " + (showConnections ? "[ON]" : "[OFF]");
            text += "\n" + KeyName(NpcCorePlugin.ConfigEditorForceLinkKey) + ": Link nodes (2 presses)";
            text += "\n" + KeyName(NpcCorePlugin.ConfigEditorClearLinksKey) + ": Clear the node's links";
            text += "\n" + KeyName(NpcCorePlugin.ConfigEditorTypeKey) + ": Node type ground/stair";
            text += "\n" + KeyName(NpcCorePlugin.ConfigEditorSaveKey) + ": Save nodes";

            GUI.Label(new Rect(x + 10f, y + 22f, boxW - 20f, boxH - 30f), text);
        }
    }
}
