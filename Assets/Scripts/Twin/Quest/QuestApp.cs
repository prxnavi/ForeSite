using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Keystone.Voice;
using Keystone.XR;
using UnityEngine;

namespace CbreTwin
{
    /// <summary>
    /// The Meta Quest experience. Runs instead of the desktop UI when a headset is active.
    ///
    /// Controls (Touch controllers):
    ///   Left stick  – walk (collides with walls)      Right stick ← → – snap turn 30°
    ///   Right trigger – click the panel or system you point at, or teleport to the floor
    ///   A – next room     X – previous room     B – ask the agent about this room
    ///   Hold left trigger – talk (Meta Voice SDK): notes, "next room", "fix the roof", questions
    ///   Menu / Y – back to the home picker
    /// </summary>
    public class QuestApp : MonoBehaviour
    {
        public BlueprintBuilder Builder;
        IXRRig xr;
        TwinConnection net;

        CharacterController body;
        LineRenderer ray;
        Transform reticle, fadeQuad, hud, lobby, wrist, infoCard, sayCard, agentCard, notesRoot;
        TextMesh hudText, wristTitle, wristSub;
        Material fadeMat;
        float fadeAlpha, fadeTarget;
        System.Action fadeAction;

        uint bp;
        List<TourStop> stops = new();
        int stopIndex;
        float floorY;
        bool turnReady = true;
        VRButton hovered;
        uint hoveredSystem, cardSystem;
        float hudUntil;
        bool inLobby;
        int answerBaseline = -1;
        bool agentWaiting;
        readonly List<GameObject> targets = new();

        public void Init(BlueprintBuilder builder, IXRRig rig)
        {
            Builder = builder;
            xr = rig;
        }

        void Start()
        {
            net = TwinConnection.I;
            xr.Spawn();
            var root = xr.Root;

            body = root.gameObject.AddComponent<CharacterController>();
            body.radius = 0.2f; body.height = 1.5f; body.center = new Vector3(0, 0.85f, 0);
            body.stepOffset = 0.05f; body.skinWidth = 0.02f; body.minMoveDistance = 0;
            root.gameObject.layer = BlueprintBuilder.WallLayer;

            var lamp = new GameObject("Head lamp").AddComponent<Light>();
            lamp.type = LightType.Point; lamp.range = 7f; lamp.intensity = 0.9f; lamp.color = new Color(1f, 0.96f, 0.9f);
            lamp.transform.SetParent(xr.Head, false);
            lamp.transform.localPosition = new Vector3(0, 0.4f, 0);

            var cam = xr.EyeCamera;
            if (cam != null) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Palette.Sky3D; }

            // Pointer
            ray = new GameObject("Pointer").AddComponent<LineRenderer>();
            ray.material = VRUI.Flat(new Color(1f, 1f, 1f, 0.7f));
            ray.startWidth = 0.004f; ray.endWidth = 0.002f; ray.positionCount = 2; ray.useWorldSpace = true;
            reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
            Destroy(reticle.GetComponent<Collider>());
            reticle.localScale = Vector3.one * 0.025f;
            reticle.GetComponent<Renderer>().sharedMaterial = VRUI.Flat(Palette.Violet);

            // Fade quad in front of the eyes (comfort)
            var fq = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(fq.GetComponent<Collider>());
            fadeQuad = fq.transform;
            fadeQuad.SetParent(xr.Head, false);
            fadeQuad.localPosition = new Vector3(0, 0, 0.08f);
            fadeQuad.localScale = new Vector3(0.6f, 0.6f, 1f);
            fadeMat = VRUI.Flat(new Color(0.07f, 0.06f, 0.16f, 0.99f));
            fq.GetComponent<Renderer>().sharedMaterial = fadeMat;
            fadeMat.renderQueue = 4000;
            fadeAlpha = 1; fadeTarget = 0;

            // Head-locked status line
            hud = VRUI.Panel("HUD", null, new Vector2(0.62f, 0.09f), new Color(1f, 0.31f, 0.64f, 0.95f));
            hudText = VRUI.Text(hud, "", Vector2.zero, 0.028f, Color.white, TextAnchor.MiddleCenter, 44, true);
            hud.gameObject.SetActive(false);

            notesRoot = new GameObject("Notes").transform;

            // Voice
            if (!string.IsNullOrEmpty(TwinConnection.Config.witToken)) VoiceHub.Configure(TwinConnection.Config.witToken);
            VoiceHub.Partial += t => Hud("“" + t + "”", 3f);
            VoiceHub.Final += OnVoice;
            VoiceHub.Failed += e => Hud("Voice: " + e, 4f);

            net.OnReducerError += e => Hud(e, 4f);
            net.OnReady += OnReady;
            net.Connect(net.ServerUrl, net.DbName);
            Hud("Connecting…", 30f);
        }

        void OnReady()
        {
            if (net.Me == null)
            {
                var name = string.IsNullOrEmpty(TwinConnection.Config.playerName) ? "Quest visitor" : TwinConnection.Config.playerName;
                net.Conn.Reducers.RegisterUser(name, "real_estate_agent", "Keystone");
            }
            ShowLobby();
        }

        // ───────────────────────── Lobby ─────────────────────────

        void ShowLobby()
        {
            inLobby = true;
            ClearHouse();
            Builder.Clear();
            Hud("", 0);
            if (lobby != null) Destroy(lobby.gameObject);
            lobby = VRUI.Panel("Lobby", null, new Vector2(1.3f, 0.95f), VRUI.PanelBg);
            VRUI.Text(lobby, "Keystone", new Vector2(-0.58f, 0.42f), 0.075f, new Color(0.73f, 0.69f, 1f), TextAnchor.UpperLeft, 0, true);
            VRUI.Text(lobby, "Pick a home to walk through", new Vector2(-0.58f, 0.3f), 0.04f, Color.white);
            var homes = net.Blueprints();
            for (int i = 0; i < homes.Count && i < 4; i++)
            {
                var b = homes[i];
                var loc = net.Location(b.LocationId);
                var id = b.Id;
                float x = i % 2 == 0 ? -0.3f : 0.3f, y = i < 2 ? 0.05f : -0.25f;
                var btn = VRUI.Button(lobby, b.Name, new Vector2(x, y), new Vector2(0.56f, 0.24f), Palette.Hex(b.AccentColor), Color.white, () => LoadHome(id));
                VRUI.Text(btn.transform, $"{loc?.City} · {(b.Stage == "existing" ? b.YearBuilt.ToString() : "new build")}", new Vector2(0, -0.075f), 0.024f, new Color(1, 1, 1, 0.9f), TextAnchor.MiddleCenter);
            }
            VRUI.Text(lobby, "Point with the right controller, pull the trigger to choose", new Vector2(0, -0.42f), 0.026f, new Color(1, 1, 1, 0.7f), TextAnchor.MiddleCenter);

            // Stand the visitor in a neutral spot and float the lobby in front of them
            PlaceRoot(Vector3.zero, 0);
            var head = xr.Head;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            lobby.position = head.position + fwd * 1.6f;
            VRUI.Face(lobby, head.position);
            if (wrist != null) wrist.gameObject.SetActive(false);
            if (sayCard != null) sayCard.gameObject.SetActive(false);
            if (agentCard != null) agentCard.gameObject.SetActive(false);
            if (infoCard != null) infoCard.gameObject.SetActive(false);
        }

        void LoadHome(uint id)
        {
            bp = id;
            net.Conn.Reducers.SelectBlueprint(id);
            FadeThen(() =>
            {
                inLobby = false;
                if (lobby != null) Destroy(lobby.gameObject);
                Builder.PlanMode = false;
                Builder.ShowRoof = true;
                Builder.ShowStructural = true;
                Builder.ShowSystems = true;
                Builder.ShowCascade = true;
                Builder.MaxFloor = 99;
                Builder.Build(id, animate: false);
                stops = TourPlanner.Plan(Builder, net, id);
                BuildTargets();
                BuildNotes();
                BuildWrist();
                stopIndex = 0;
                ArriveAt(0);
                answerBaseline = net.Messages(bp).Count;
            });
        }

        void ClearHouse()
        {
            foreach (var t in targets) if (t) Destroy(t);
            targets.Clear();
            foreach (Transform c in notesRoot) Destroy(c.gameObject);
        }

        void BuildTargets()
        {
            foreach (var t in targets) if (t) Destroy(t);
            targets.Clear();
            foreach (var kv in Builder.ComponentWorld)
            {
                var go = new GameObject($"Target {kv.Key}");
                go.transform.position = kv.Value;
                var s = go.AddComponent<SphereCollider>(); s.radius = 0.16f; s.isTrigger = true;
                go.AddComponent<VRTarget>().ComponentId = kv.Key;
                targets.Add(go);
            }
        }

        void BuildNotes()
        {
            foreach (Transform c in notesRoot) Destroy(c.gameObject);
            if (bp == 0) return;
            foreach (var n in net.Notes(bp).Take(30))
            {
                var p = VRUI.Panel("Note", notesRoot, new Vector2(0.42f, 0.12f), VRUI.Card);
                var stripe = VRUI.Panel("Stripe", p, new Vector2(0.012f, 0.12f), Palette.Hex(n.Color));
                stripe.localPosition = new Vector3(-0.205f, 0, -0.002f);
                VRUI.Text(p, n.Text, new Vector2(-0.18f, 0.045f), 0.022f, VRUI.Ink, TextAnchor.UpperLeft, 34);
                p.position = new Vector3(n.X, n.Y, n.Z);
            }
        }

        // ───────────────────────── Tour ─────────────────────────

        void ArriveAt(int i)
        {
            if (stops.Count == 0) { Hud("No walkable rooms in this plan", 4f); return; }
            stopIndex = Mathf.Clamp(i, 0, stops.Count - 1);
            var s = stops[stopIndex];
            var yaw = Quaternion.LookRotation(Vector3.ProjectOnPlane(s.LookAt - s.Stand, Vector3.up).normalized + Vector3.forward * 0.0001f).eulerAngles.y;
            PlaceRoot(s.Stand, yaw);
            UpdateWrist();
            ShowSay(s);
            xr.Haptic(true, 0.4f);
        }

        void GoTo(int i)
        {
            if (stops.Count == 0) return;
            if (i >= stops.Count) { Hud("That's the whole house!", 3f); return; }
            if (i < 0) return;
            FadeThen(() => ArriveAt(i));
        }

        /// <summary>Move the rig so the user's head lands on the target (feet) and faces yaw.</summary>
        void PlaceRoot(Vector3 feet, float yaw)
        {
            var root = xr.Root;
            var head = xr.Head;
            float headYaw = head.eulerAngles.y;
            root.RotateAround(head.position, Vector3.up, yaw - headYaw);
            var headOffset = head.position - root.position; headOffset.y = 0;
            body.enabled = false;
            root.position = new Vector3(feet.x, feet.y, feet.z) - headOffset;
            body.enabled = true;
            floorY = feet.y;
        }

        void ShowSay(TourStop s)
        {
            if (sayCard != null) Destroy(sayCard.gameObject);
            sayCard = VRUI.Panel("Room card", null, new Vector2(0.62f, 0.42f), VRUI.PanelBg);
            VRUI.Text(sayCard, $"ROOM {stopIndex + 1} OF {stops.Count}", new Vector2(-0.28f, 0.18f), 0.022f, new Color(1, 1, 1, 0.6f), TextAnchor.UpperLeft, 0, true);
            VRUI.Text(sayCard, s.Title, new Vector2(-0.28f, 0.145f), 0.05f, Color.white, TextAnchor.UpperLeft, 0, true);
            var color = s.Severity == "critical" ? Palette.Coral : s.Severity == "watch" ? Palette.Amber : Palette.Teal;
            var bar = VRUI.Panel("Bar", sayCard, new Vector2(0.56f, 0.008f), color);
            bar.localPosition = new Vector3(0, 0.07f, -0.004f);
            var text = s.Narration.Split(new[] { "\n\n" }, System.StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            VRUI.Text(sayCard, text, new Vector2(-0.28f, 0.045f), 0.026f, Color.white, TextAnchor.UpperLeft, 46);
            var head = xr.Head;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var left = Quaternion.Euler(0, -32, 0) * fwd;
            sayCard.position = head.position + left * 1.3f + Vector3.down * 0.12f;
            VRUI.Face(sayCard, head.position);
        }

        void BuildWrist()
        {
            if (wrist != null) Destroy(wrist.gameObject);
            wrist = VRUI.Panel("Wrist menu", null, new Vector2(0.3f, 0.24f), VRUI.PanelBg);
            wristTitle = VRUI.Text(wrist, "", new Vector2(-0.135f, 0.105f), 0.028f, Color.white, TextAnchor.UpperLeft, 0, true);
            wristSub = VRUI.Text(wrist, "", new Vector2(-0.135f, 0.068f), 0.017f, new Color(1, 1, 1, 0.65f));
            VRUI.Button(wrist, "Next (A)", new Vector2(-0.072f, 0.005f), new Vector2(0.135f, 0.05f), Palette.Violet, Color.white, () => GoTo(stopIndex + 1));
            VRUI.Button(wrist, "Back (X)", new Vector2(0.072f, 0.005f), new Vector2(0.135f, 0.05f), new Color(1, 1, 1, 0.15f), Color.white, () => GoTo(stopIndex - 1));
            VRUI.Button(wrist, "Ask (B)", new Vector2(-0.072f, -0.055f), new Vector2(0.135f, 0.05f), Palette.Teal, Color.white, AskAboutRoom);
            VRUI.Button(wrist, "Homes", new Vector2(0.072f, -0.055f), new Vector2(0.135f, 0.05f), new Color(1, 1, 1, 0.15f), Color.white, ShowLobby);
            VRUI.Text(wrist, "Hold left trigger to talk", new Vector2(0, -0.1f), 0.015f, new Color(1, 0.6f, 0.82f), TextAnchor.MiddleCenter);
        }

        void UpdateWrist()
        {
            if (wrist == null || stops.Count == 0) return;
            wristTitle.text = stops[stopIndex].Title;
            wristSub.text = $"{net.Blueprint(bp)?.Name}  ·  {stopIndex + 1}/{stops.Count}";
        }

        void AskAboutRoom()
        {
            if (stops.Count == 0) return;
            Ask($"I'm standing in the {stops[stopIndex].Title.ToLowerInvariant()}. What should I know about this room?");
        }

        void Ask(string q)
        {
            if (bp == 0 || !net.Ready) return;
            answerBaseline = net.Messages(bp).Count;
            agentWaiting = true;
            ShowAgentCard("Thinking…");
            net.Conn.Procedures.AskAgent(bp, q, (ctx, result) =>
            {
                agentWaiting = false;
                if (!result.IsSuccess) Hud("Agent error: " + result.Error?.Message, 4f);
                net.DirtyFlags |= Dirty.Agent;
            });
        }

        void ShowAgentCard(string text)
        {
            if (agentCard != null) Destroy(agentCard.gameObject);
            agentCard = VRUI.Panel("Agent card", null, new Vector2(0.7f, 0.62f), VRUI.Card);
            VRUI.Text(agentCard, "AGENT", new Vector2(-0.32f, 0.28f), 0.022f, Palette.Violet, TextAnchor.UpperLeft, 0, true);
            var clean = Regex.Replace(text.Replace("**", ""), @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
            if (clean.Length > 700) clean = clean.Substring(0, 700) + "…";
            VRUI.Text(agentCard, clean, new Vector2(-0.32f, 0.24f), 0.022f, VRUI.Ink, TextAnchor.UpperLeft, 58);
            VRUI.Button(agentCard, "Close", new Vector2(0.27f, -0.27f), new Vector2(0.12f, 0.05f), Palette.Violet, Color.white, () => agentCard.gameObject.SetActive(false));
            var head = xr.Head;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var right = Quaternion.Euler(0, 30, 0) * fwd;
            agentCard.position = head.position + right * 1.3f;
            VRUI.Face(agentCard, head.position);
        }

        void ShowSystemCard(uint id)
        {
            var c = net.Conn.Db.Component.Id.Find(id);
            if (c == null) return;
            var r = net.Risk(id);
            cardSystem = id;
            if (infoCard != null) Destroy(infoCard.gameObject);
            infoCard = VRUI.Panel("System card", null, new Vector2(0.46f, 0.3f), VRUI.Card);
            VRUI.Text(infoCard, c.Name, new Vector2(-0.21f, 0.125f), 0.03f, VRUI.Ink, TextAnchor.UpperLeft, 28, true);
            if (r != null)
            {
                VRUI.Text(infoCard, $"{r.CascadeProb * 100:0}%", new Vector2(-0.21f, 0.07f), 0.06f, Palette.Tier(r.Tier), TextAnchor.UpperLeft, 0, true);
                VRUI.Text(infoCard, "chance it fails\nthis year", new Vector2(-0.05f, 0.06f), 0.018f, new Color(0.45f, 0.44f, 0.6f));
                VRUI.Text(infoCard, $"Fixing now saves {Palette.Money(r.PreventionValue)}", new Vector2(-0.21f, -0.025f), 0.02f, VRUI.Ink);
            }
            VRUI.Text(infoCard, $"{c.SensorLabel}: {c.SensorValue:0.#} {c.SensorUnit}", new Vector2(-0.21f, -0.055f), 0.017f, new Color(0.45f, 0.44f, 0.6f));
            bool open = net.WorkOrders(bp).Any(w => w.ComponentId == id && w.Status != "done");
            if (!open) VRUI.Button(infoCard, "Fix it", new Vector2(-0.11f, -0.11f), new Vector2(0.16f, 0.05f), Palette.Coral, Color.white, () => { net.Conn.Reducers.CreateWorkOrder(id, "Preventive repair or replacement"); Hud($"Booked: {c.Name}", 3f); ShowSystemCard(id); });
            else VRUI.Text(infoCard, "Repair booked", new Vector2(-0.19f, -0.1f), 0.02f, Palette.Teal, TextAnchor.UpperLeft, 0, true);
            VRUI.Button(infoCard, "Ask", new Vector2(0.08f, -0.11f), new Vector2(0.12f, 0.05f), Palette.Violet, Color.white, () => Ask($"How risky is the {c.Name.ToLowerInvariant()}, and what happens if we wait a year?"));
            VRUI.Button(infoCard, "×", new Vector2(0.19f, 0.12f), new Vector2(0.05f, 0.05f), new Color(0.9f, 0.9f, 0.95f), VRUI.Ink, () => { infoCard.gameObject.SetActive(false); cardSystem = 0; });
            var at = Builder.ComponentWorld.TryGetValue(id, out var w) ? w : xr.Head.position + xr.Head.forward;
            var toHead = (xr.Head.position - at); toHead.y = 0;
            infoCard.position = at + Vector3.up * 0.3f + toHead.normalized * 0.25f;
            VRUI.Face(infoCard, xr.Head.position);
        }

        // ───────────────────────── Voice ─────────────────────────

        void OnVoice(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            var lower = text.ToLowerInvariant().TrimEnd('.', '!', '?');
            if (inLobby)
            {
                var home = net.Blueprints().FirstOrDefault(b => lower.Contains(b.Name.ToLowerInvariant().Split(' ')[0]));
                if (home != null) { LoadHome(home.Id); return; }
                Hud("Say the name of a home, like “Willow Bend”", 3f);
                return;
            }
            if (Regex.IsMatch(lower, @"^(next( room)?|go on|keep going)$")) { GoTo(stopIndex + 1); return; }
            if (Regex.IsMatch(lower, @"^(previous( room)?|go back|back)$")) { GoTo(stopIndex - 1); return; }
            if (Regex.IsMatch(lower, @"^(exit|home|homes|lobby|leave)$")) { ShowLobby(); return; }
            var fix = Regex.Match(lower, @"^(fix|repair|book|schedule)( a fix for| a repair for)? (the )?(.+)$");
            if (fix.Success)
            {
                var want = fix.Groups[4].Value;
                var c = net.Components(bp).FirstOrDefault(x => x.Name.ToLowerInvariant().Contains(want) || Palette.KindLabel(x.Kind).ToLowerInvariant().Contains(want) || want.Contains(Palette.KindLabel(x.Kind).ToLowerInvariant()));
                if (c != null) { net.Conn.Reducers.CreateWorkOrder(c.Id, "Repair requested by voice"); Hud($"Booked: {c.Name}", 3f); return; }
            }
            var ask = Regex.Match(text, @"^(hey keystone|ask|agent)[,:]?\s+(.+)$", RegexOptions.IgnoreCase);
            if (ask.Success) { Ask(ask.Groups[2].Value); return; }
            if (Regex.IsMatch(lower, @"^(what|how|why|should|is|are|can|could|does|do|which|when|where)\b")) { Ask(text); return; }

            // Otherwise: a note pinned where you're looking
            var head = xr.Head;
            var pos = head.position + Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized * 0.9f;
            var room = stops.Count > 0 ? stops[stopIndex].RoomId : 0u;
            var feet = new Vector3(head.position.x, floorY, head.position.z);
            foreach (var r in Builder.RoomsById.Values)
            {
                var ft = feet / BlueprintBuilder.FT;
                if (ft.x >= r.X && ft.x <= r.X + r.W && ft.z >= r.Z && ft.z <= r.Z + r.D && Mathf.Abs(ft.y - Builder.LevelBaseFt((int)r.Floor)) < 3) { room = r.Id; break; }
            }
            net.Conn.Reducers.AddNote(bp, room, text, "voice", pos.x, pos.y, pos.z);
            Hud("Note saved", 2.5f);
        }

        // ───────────────────────── Frame loop ─────────────────────────

        void Update()
        {
            if (xr == null || xr.Root == null) return;
            UpdateFade();
            UpdatePointer();
            if (!inLobby && bp != 0) UpdateLocomotion();
            UpdateButtons();
            UpdateFloating();
            ConsumeData();
            if (hud.gameObject.activeSelf && Time.unscaledTime > hudUntil && !VoiceHub.Listening) hud.gameObject.SetActive(false);
        }

        void UpdateButtons()
        {
            if (xr.Down(XRButton.LeftTrigger)) { if (VoiceHub.Ready) { Hud("Listening…", 30f); VoiceHub.Begin(); } else Hud("Voice needs a Wit.ai token (see README)", 4f); xr.Haptic(false, 0.3f); }
            if (xr.Up(XRButton.LeftTrigger)) VoiceHub.End();
            if (inLobby) return;
            if (xr.Down(XRButton.A)) GoTo(stopIndex + 1);
            if (xr.Down(XRButton.X)) GoTo(stopIndex - 1);
            if (xr.Down(XRButton.B)) AskAboutRoom();
            if (xr.Down(XRButton.Y) || xr.Down(XRButton.Menu)) ShowLobby();
        }

        void UpdateLocomotion()
        {
            var head = xr.Head;
            var root = xr.Root;
            // keep the collision capsule under the head as the user moves in their play space
            var local = root.InverseTransformPoint(head.position);
            body.center = new Vector3(local.x, 0.85f, local.z);

            var stick = xr.LeftStick;
            if (stick.sqrMagnitude > 0.04f)
            {
                var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                var right = Vector3.Cross(Vector3.up, fwd);
                var move = (fwd * stick.y + right * stick.x) * 1.4f * Time.deltaTime;
                move.y = floorY - root.position.y;
                body.Move(move);
            }
            var turn = xr.RightStick.x;
            if (Mathf.Abs(turn) > 0.7f && turnReady)
            {
                turnReady = false;
                root.RotateAround(head.position, Vector3.up, Mathf.Sign(turn) * 30f);
            }
            if (Mathf.Abs(turn) < 0.3f) turnReady = true;
        }

        void UpdatePointer()
        {
            var hand = xr.RightHand;
            if (hand == null) return;
            var origin = hand.position;
            var dir = hand.forward;
            float length = 6f;
            VRButton btn = null;
            uint sys = 0;
            Vector3? floor = null;
            bool hitWall = false;

            var hits = Physics.RaycastAll(origin, dir, 12f, ~0, QueryTriggerInteraction.Collide).OrderBy(h => h.distance);
            foreach (var h in hits)
            {
                if (h.collider == body) continue;
                length = h.distance;
                btn = h.collider.GetComponent<VRButton>();
                if (btn != null) break;
                var t = h.collider.GetComponent<VRTarget>();
                if (t != null) { sys = t.ComponentId; break; }
                if (h.collider.GetComponent<RoomTag>() != null && h.normal.y > 0.7f) { floor = h.point; break; }
                hitWall = true;
                break;
            }
            ray.SetPosition(0, origin);
            ray.SetPosition(1, origin + dir * length);
            reticle.position = origin + dir * length;
            var col = btn != null ? Palette.Violet : sys != 0 ? Palette.Amber : floor != null ? Palette.Teal : hitWall ? Palette.Coral : Color.white;
            ray.material.color = new Color(col.r, col.g, col.b, 0.75f);
            reticle.GetComponent<Renderer>().sharedMaterial.color = col;

            if (hovered != btn) { hovered?.SetHover(false); btn?.SetHover(true); if (btn != null) xr.Haptic(true, 0.15f); hovered = btn; }
            if (sys != hoveredSystem) { hoveredSystem = sys; if (sys != 0) xr.Haptic(true, 0.2f); }

            if (xr.Down(XRButton.RightTrigger))
            {
                if (btn != null) { btn.OnClick?.Invoke(); xr.Haptic(true, 0.5f); }
                else if (sys != 0) ShowSystemCard(sys);
                else if (floor != null && !inLobby)
                {
                    var p = floor.Value;
                    FadeThen(() => { var head = xr.Head; PlaceRoot(p, head.eulerAngles.y); });
                }
            }
        }

        void UpdateFloating()
        {
            var head = xr.Head;
            if (wrist != null && !inLobby)
            {
                var lh = xr.LeftHand;
                wrist.gameObject.SetActive(lh != null);
                if (lh != null)
                {
                    wrist.position = lh.position + Vector3.up * 0.16f;
                    VRUI.Face(wrist, head.position);
                }
            }
            if (hud.gameObject.activeSelf)
            {
                var target = head.position + head.forward * 1.1f + Vector3.down * 0.32f;
                hud.position = Vector3.Lerp(hud.position, target, 1f - Mathf.Exp(-6f * Time.deltaTime));
                VRUI.Face(hud, head.position);
            }
            foreach (Transform n in notesRoot) VRUI.Face(n, head.position);
        }

        void ConsumeData()
        {
            var d = net.DirtyFlags;
            if (d == Dirty.None || bp == 0) return;
            net.DirtyFlags = Dirty.None;
            if ((d & Dirty.Annotations) != 0) BuildNotes();
            if ((d & (Dirty.Risk | Dirty.Structural)) != 0 && !inLobby) { Builder.RefreshOverlays(); BuildTargets(); if (cardSystem != 0 && infoCard != null && infoCard.gameObject.activeSelf) ShowSystemCard(cardSystem); }
            if ((d & Dirty.Agent) != 0 && answerBaseline >= 0)
            {
                var answer = net.Messages(bp).Skip(answerBaseline).LastOrDefault(m => m.Role != "user");
                if (answer != null) { ShowAgentCard(answer.Content); answerBaseline = -1; xr.Haptic(true, 0.4f); }
                else if (agentWaiting)
                {
                    var run = net.LatestRun(bp);
                    var step = run != null ? net.Steps(run.Id).LastOrDefault() : null;
                    if (step != null) Hud($"Agent: {step.Title}", 3f);
                }
            }
            if ((d & Dirty.Ops) != 0)
            {
                var a = net.Alerts(bp).FirstOrDefault();
                if (a != null && a.Level == "critical") Hud(a.Message, 4f);
            }
        }

        // ───────────────────────── Helpers ─────────────────────────

        void Hud(string text, float seconds)
        {
            if (hud == null) return;
            if (string.IsNullOrEmpty(text)) { hud.gameObject.SetActive(false); return; }
            hudText.text = VRUI.Wrap(text, 44);
            if (!hud.gameObject.activeSelf && xr.Head != null) hud.position = xr.Head.position + xr.Head.forward * 1.1f + Vector3.down * 0.32f;
            hud.gameObject.SetActive(true);
            hudUntil = Time.unscaledTime + seconds;
        }

        void FadeThen(System.Action a) { fadeAction = a; fadeTarget = 1; }

        void UpdateFade()
        {
            fadeAlpha = Mathf.MoveTowards(fadeAlpha, fadeTarget, Time.unscaledDeltaTime / 0.15f);
            var c = fadeMat.color; c.a = fadeAlpha; fadeMat.color = c; fadeMat.SetColor("_BaseColor", c);
            fadeQuad.gameObject.SetActive(fadeAlpha > 0.001f);
            if (fadeTarget >= 1f && fadeAlpha >= 1f) { var a = fadeAction; fadeAction = null; a?.Invoke(); fadeTarget = 0; }
        }
    }
}
