using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using NPC.Core;
using NPC.Core.Agents;
using NPC.Core.Interaction;
using NPC.Core.Navigation;
using NPC.Core.World;
using Space;
using UnityEngine;

namespace WanderingNpc
{
    /// <summary>
    /// The smallest NPC mod on NPC.Core: a plush toy that wanders the nav graph. It shares the world with
    /// every other NPC mod through NPC.Core, and owns nothing NPC.Core owns. See README.md.
    /// </summary>
    [BepInPlugin(Guid, ModName, "1.0.0")]
    [BepInDependency(NpcCorePlugin.Guid, NpcCorePlugin.Version)]
    [BepInProcess("Isolated Inhale.exe")]
    public sealed class WanderingNpcPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.bytenull1.wanderingnpc";
        internal const string ModName = "WanderingNpc";

        private static readonly List<NpcAgent> Wanderers = [];

        internal static ManualLogSource Log = null!; // set in Awake, before anything reads it
        internal static ConfigEntry<bool> ConfigShowOnLifecare = null!; // set in Awake

        private void Awake()
        {
            Log = Logger;
            ConfigShowOnLifecare = Config.Bind("General", "ShowOnLifecare", false,
                "Draw wanderers on the ship's lifecare terminal. Read at every scan.");

            NpcConsole.Register(ModName, "wanderer_spawn", Spawn);
            NpcConsole.Register(ModName, "wanderer_kill", _ => ForEach(agent => agent.Die(Vector3.zero)));
            NpcConsole.Register(ModName, "wanderer_clear", _ => ForEach(agent => Destroy(agent.gameObject)));
            // NPC.Core owns it: refused and logged, and the core's command keeps working.
            if (!NpcConsole.Register(ModName, "debug_level", _ => { })) Log.LogInfo("[mod] debug_level is NPC.Core's, as it should be");

            // A new scene destroyed the old wanderers with it.
            NpcEvents.WorldReset += Wanderers.Clear;
        }

        /// <summary>
        /// `wanderer_spawn [count]`: in front of the player.
        /// </summary>
        private static void Spawn(string[] args)
        {
            Player? player = NpcPlayer.Pilot;
            if (player == null || player.Controller == null)
            {
                NpcConsole.Print("No player");
                return;
            }

            int count = args.Length > 0 && int.TryParse(args[0], out int n) ? Mathf.Clamp(n, 1, 8) : 1;
            Transform eye = player.Controller.CachedTransform;
            Vector3 ahead = Vector3.ProjectOnPlane(eye.forward, Vector3.up).normalized;
            for (int i = 0; i < count; i++)
            {
                Vector3 spot = eye.position + ahead * (1.2f + 0.6f * i);
                if (!NavProbe.TryFloorHeight(spot, out float floorY))
                {
                    NpcConsole.Print("No floor in front of you");
                    return;
                }
                NpcAgent agent = WandererBody.Build(new Vector3(spot.x, floorY + 0.05f, spot.z),
                    Quaternion.LookRotation(-ahead), FreeNumber());
                Wanderers.Add(agent);
                NpcConsole.Print("Spawned " + agent.Name);
            }
        }

        private static void ForEach(System.Action<NpcAgent> act)
        {
            Wanderers.RemoveAll(agent => agent == null);
            foreach (NpcAgent agent in Wanderers.ToArray())
            {
                using NpcRegistry.ActingScope _ = NpcRegistry.Acting(agent);
                act(agent);
            }
        }

        private static int FreeNumber()
        {
            Wanderers.RemoveAll(agent => agent == null);
            int number = 1;
            while (Wanderers.Exists(agent => agent.Number == number)) number++;
            return number;
        }
    }

    /// <summary>
    /// A body NPC.Core can drive: a CharacterController the player's size on the player's layer, one of the
    /// game's plush toys blown up to see, and a second copy as the ragdoll that <see cref="NpcAgent.Die"/>
    /// lets fall. docs/agent.md#1-attaching-an-agent
    /// </summary>
    internal static class WandererBody
    {
        /// <summary>
        /// The player's controller when there is none to copy. The graph's doorways and stairs fit it.
        /// </summary>
        private const float FallbackHeight = 1f;
        private const float FallbackRadius = 0.22f;
        /// <summary>
        /// The box a model is scaled into. Doorways are 0.85 m wide and 1.65 m high.
        /// </summary>
        private const float ModelHeight = 1.1f;
        private const float ModelWidth = 0.8f;
        private const float RagdollMass = 15f;
        /// <summary>
        /// The plush meshes face -Z; the agent walks along +Z.
        /// </summary>
        private static readonly Quaternion FaceForward = Quaternion.Euler(0f, 180f, 0f);

        /// <summary>
        /// Models under the game's Resources, one picked at random per wanderer.
        /// </summary>
        private static readonly string[] Plushes =
        [
            "Models/RainPlushie/RainPlushie",
            "Models/LordPlush/LordPlushie",
            "Models/HyenaPlush/HyenaPlush",
            "Models/DieterPlush/DieterPlushie",
            "Models/WypherPlushie/WypherPlushie"
        ];

        private static Material? _material;

        internal static NpcAgent Build(Vector3 feet, Quaternion rotation, int number)
        {
            string name = "Wanderer " + number;
            GameObject root = new(WanderingNpcPlugin.ModName + " " + name);
            // Inactive until registered: OnEnable must find the other NPCs to pass through.
            root.SetActive(false);
            root.transform.SetPositionAndRotation(feet, rotation);

            // The graph is built for a walker the player's size that collides with what the player does.
            CharacterController? playerCc = NpcPlayer.Controller;
            root.layer = playerCc != null ? playerCc.gameObject.layer : LayerMask.NameToLayer("Player");
            CharacterController cc = root.AddComponent<CharacterController>();
            cc.height = playerCc != null ? playerCc.height : FallbackHeight;
            cc.radius = playerCc != null ? playerCc.radius : FallbackRadius;
            cc.center = new Vector3(0f, cc.height / 2f, 0f);
            if (playerCc != null)
            {
                cc.slopeLimit = playerCc.slopeLimit;
                cc.stepOffset = playerCc.stepOffset;
                cc.skinWidth = playerCc.skinWidth;
            }

            string plush = Plushes[Random.Range(0, Plushes.Length)];
            GameObject model = Model("Model", plush, root.transform);
            foreach (Collider part in model.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(part);

            GameObject ragdoll = Model("Ragdoll", plush, root.transform);
            // The player's ragdoll layer: Default passes through the floor. docs/agent.md#1-attaching-an-agent
            ragdoll.layer = LayerMask.NameToLayer("Grabbable");
            // Fitted to the mesh by Unity when added.
            if (ragdoll.GetComponent<Collider>() == null) ragdoll.AddComponent<BoxCollider>();
            Rigidbody ragdollBody = ragdoll.AddComponent<Rigidbody>();
            ragdollBody.isKinematic = true;
            ragdollBody.mass = RagdollMass;
            ragdoll.SetActive(false);

            WandererBrain brain = new();
            NpcBody body = new()
            {
                Ragdoll = ragdoll,
                RagdollBody = ragdollBody,
                AnimatedModel = model,
                FootstepEvents = NpcPlayer.FootstepEvents()
            };
            NpcAgent agent = NpcAgent.Attach(root, new NpcIdentity(WanderingNpcPlugin.ModName, name, number), body,
                WandererSettings.Instance, brain);
            brain.Agent = agent;
            NpcRegistry.Register(agent);
            NpcInteraction.Register(brain);
            root.SetActive(true);

            // Stand in the frame of the station it is on; the player ship needs none.
            FloorOwnership floor = NpcVessels.FloorOwner(feet, out string? owner, out Transform? anchor);
            if (floor == FloorOwnership.Elsewhere && owner != null && anchor != null) agent.RideOwner(owner, anchor);

            using NpcRegistry.ActingScope _ = NpcRegistry.Acting(agent);
            NpcLog.Log.LogInfo($"[mod] {name} spawned at {feet} on '{owner ?? "?"}' as {model.name}, " +
                               $"controller {cc.height:0.00} x {cc.radius:0.00}m");
            return agent;
        }

        /// <summary>
        /// The plush at `path`, or an orange capsule when the game has none there, standing on the root's
        /// origin and scaled to fit ModelHeight x ModelWidth.
        /// </summary>
        private static GameObject Model(string role, string path, Transform parent)
        {
            GameObject? prefab = Resources.Load<GameObject>(path);
            GameObject model;
            if (prefab != null)
            {
                model = Object.Instantiate(prefab);
                model.name = role + " " + prefab.name;
            }
            else
            {
                WanderingNpcPlugin.Log.LogWarning($"[mod] No model at Resources '{path}' - using a capsule");
                model = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                model.name = role + " Capsule";
                if (BodyMaterial() is { } material) model.GetComponent<Renderer>().sharedMaterial = material;
            }
            model.layer = 0;
            model.transform.SetParent(parent, false);
            model.transform.localRotation = FaceForward;

            MeshFilter? filter = model.GetComponentInChildren<MeshFilter>();
            Bounds bounds = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            float footprint = Mathf.Max(bounds.size.x, bounds.size.z, 0.01f);
            float scale = Mathf.Min(ModelHeight / Mathf.Max(bounds.size.y, 0.01f), ModelWidth / footprint);
            model.transform.localScale = Vector3.one * scale;
            // Bottom of the mesh on the feet, centred over them.
            Vector3 bottom = new(bounds.center.x, bounds.min.y, bounds.center.z);
            model.transform.localPosition = FaceForward * -bottom * scale;
            return model;
        }

        /// <summary>
        /// The game renders with URP: the primitive's built-in material would draw nothing, or magenta.
        /// </summary>
        private static Material? BodyMaterial()
        {
            if (_material != null) return _material;

            Shader? shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;

            _material = new Material(shader) { name = "Wanderer" };
            _material.SetColor("_BaseColor", new Color(0.95f, 0.55f, 0.15f));
            return _material;
        }
    }

    /// <summary>
    /// Read live by the agent: docs/agent.md#1-attaching-an-agent
    /// </summary>
    internal sealed class WandererSettings : NpcAgentSettings
    {
        internal static readonly WandererSettings Instance = new();

        public override float MoveSpeed => 2.5f;
        public override bool ShowOnLifecare => WanderingNpcPlugin.ConfigShowOnLifecare.Value;
    }
}
