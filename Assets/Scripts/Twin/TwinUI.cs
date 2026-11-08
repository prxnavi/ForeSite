using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.UIElements;

namespace CbreTwin
{
    /// <summary>Runtime-built UI Toolkit interface: login → agent workspace over the 3D twin.</summary>
    public class TwinUI : MonoBehaviour
    {
        public static bool IsTyping;

        BlueprintBuilder builder;
        CameraRig rig;
        PresenceSync presence;
        TwinConnection net;

        UIDocument doc;
        VisualElement root, loginLayer, appLayer, overlay, markerLayer, labelLayer, popover;
        Label loginStatus, liveLabel, simClock, agentModel, buildLabel, viewHint, toast;
        VisualElement liveDot, avatars, bpList, kpiRow, hudTitleBox, buildFill, drawer, drawerContent, tabBar, chatBox, traceBox, agentBody, quickRow, thinking;
        TextField nameField, companyField, serverField, dbField, promptField;
        Button simBtn;
        ScrollView chatScroll, traceScroll;

        string role = "real_estate_agent";
        uint selectedBp;
        string drawerTab = "structural";
        string agentTab = "chat";
        bool drawerOpen = true;
        bool waitingForProfile;
        string pendingName;
        bool showLabels = true, showTeammates = true;
        float nextRefresh, lastOverlayRefresh, toastUntil;
        int lastMessageCount = -1;
        ulong lastAlertSeen;
        readonly List<(VisualElement el, uint id)> markerEls = new();
        readonly List<(VisualElement el, uint id)> roomLabels = new();
        readonly Dictionary<string, VisualElement> nameTags = new();
        readonly List<(VisualElement el, Vector3 pos)> pinEls = new();
        bool agentBusy;

        public void Init(BlueprintBuilder b, CameraRig r, PresenceSync p)
        {
            builder = b;
            rig = r;
            presence = p;
        }

        // ───────────── Setup ─────────────

        void Start()
        {
            net = TwinConnection.I;
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UI/TwinTheme");
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            ps.sortingOrder = 10;

            var go = new GameObject("Twin UI");
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            doc = go.AddComponent<UIDocument>();
            doc.panelSettings = ps;
            go.SetActive(true);

            root = doc.rootVisualElement;
            var uss = Resources.Load<StyleSheet>("UI/Twin");
            if (uss != null) root.styleSheets.Add(uss);
            root.AddToClassList("root");
            root.pickingMode = PickingMode.Ignore;

            BuildLogin();
            BuildApp();
            appLayer.style.display = DisplayStyle.None;

            toast = new Label { name = "toast" };
            toast.AddToClassList("toast");
            toast.style.display = DisplayStyle.None;
            toast.pickingMode = PickingMode.Ignore;
            root.Add(toast);

            rig.IsOverUI = IsOverUI;
            rig.OnClick = HandleWorldClick;

            net.OnStatus += s =>
            {
                if (loginStatus != null)
                {
                    loginStatus.text = s;
                    loginStatus.EnableInClassList("status-line--err", s.StartsWith("Could not") || s.Contains("error"));
                }
                if (liveLabel != null) liveLabel.text = s;
            };
            net.OnReady += OnNetReady;
            net.OnReducerError += msg => Toast("Error: " + msg);

            // Auto-connect so the login page can show live status and restore the profile.
            net.Connect(net.ServerUrl, net.DbName);
        }

        bool IsOverUI(Vector2 screen)
        {
            if (root?.panel == null) return false;
            var p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
            var picked = root.panel.Pick(p);
            return picked != null && picked != root && picked != root.panel.visualTree && picked != root.parent;
        }

        void OnNetReady()
        {
            var me = net.Me;
            if (me != null && loginLayer.style.display == DisplayStyle.Flex)
            {
                if (string.IsNullOrEmpty(nameField.value)) nameField.value = me.Name;
                if (string.IsNullOrEmpty(companyField.value)) companyField.value = me.Company;
                SetRole(me.Role);
            }
            if (waitingForProfile && pendingName != null) Register();
        }

        // ───────────── Login ─────────────

        VisualElement roleCm, roleAgent;

        void BuildLogin()
        {
            loginLayer = new VisualElement();
            loginLayer.AddToClassList("fill");
            loginLayer.style.backgroundImage = new StyleBackground(Gradient(new[] { Palette.Hex("#7B61FF"), Palette.Hex("#4DA3FF"), Palette.Hex("#2EC4B6"), Palette.Hex("#FF8FB1") }));
            loginLayer.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Cover));
            loginLayer.style.flexDirection = FlexDirection.Row;
            loginLayer.style.alignItems = Align.Center;
            loginLayer.style.justifyContent = Justify.Center;
            root.Add(loginLayer);

            // Hero
            var hero = new VisualElement();
            hero.style.width = 620; hero.style.marginRight = 70;
            var kicker = new Label("HACKUTD 2026 · CBRE CHALLENGE");
            kicker.style.color = new Color(1, 1, 1, 0.85f); kicker.style.unityFontStyleAndWeight = FontStyle.Bold; kicker.style.fontSize = 13; kicker.style.letterSpacing = 2;
            hero.Add(kicker);
            var title = new Label("Connect fragmented real-estate data.\nPredict failures before they spread.");
            title.style.color = Color.white; title.style.fontSize = 40; title.style.unityFontStyleAndWeight = FontStyle.Bold; title.style.whiteSpace = WhiteSpace.Normal; title.style.marginTop = 10;
            hero.Add(title);
            var sub = new Label("A live digital twin that fuses blueprints, building-system ages, IoT telemetry, hazard maps and market comps inside SpacetimeDB — with a team of NVIDIA Nemotron agents that analyse, price and execute fixes.");
            sub.style.color = new Color(1, 1, 1, 0.92f); sub.style.fontSize = 16; sub.style.whiteSpace = WhiteSpace.Normal; sub.style.marginTop = 14; sub.style.marginBottom = 22;
            hero.Add(sub);
            foreach (var (code, head, body, col) in new[]
            {
                ("01", "Predict cascading failures", "Weibull lifecycle + noisy-OR failure graph: gutters → foundation → plumbing → mold.", "#FFD166"),
                ("02", "Quantify business impact", "Expected loss, price bridge, days-on-market and value unlocked — in every market.", "#FFFFFF"),
                ("03", "Execute before disruptions spread", "Agents dispatch work orders transactionally; the twin recomputes instantly for everyone.", "#B8FFF4"),
            })
            {
                var row = new VisualElement(); row.AddToClassList("row"); row.style.marginBottom = 12;
                var badge = new Label(code);
                badge.style.width = 40; badge.style.height = 40; badge.style.borderTopLeftRadius = badge.style.borderTopRightRadius = badge.style.borderBottomLeftRadius = badge.style.borderBottomRightRadius = 12;
                badge.style.backgroundColor = new Color(1, 1, 1, 0.22f); badge.style.color = Palette.Hex(col); badge.style.unityTextAlign = TextAnchor.MiddleCenter; badge.style.unityFontStyleAndWeight = FontStyle.Bold; badge.style.marginRight = 12;
                row.Add(badge);
                var txt = new VisualElement();
                var h = new Label(head); h.style.color = Color.white; h.style.unityFontStyleAndWeight = FontStyle.Bold; h.style.fontSize = 16;
                var bLbl = new Label(body); bLbl.style.color = new Color(1, 1, 1, 0.85f); bLbl.style.whiteSpace = WhiteSpace.Normal; bLbl.style.fontSize = 13;
                txt.Add(h); txt.Add(bLbl); txt.style.flexShrink = 1;
                row.Add(txt);
                hero.Add(row);
            }
            loginLayer.Add(hero);

            // Card
            var card = new VisualElement(); card.AddToClassList("login-card");
            var brand = new VisualElement(); brand.AddToClassList("brand-row");
            var logo = new Label("DT"); logo.AddToClassList("brand-logo");
            brand.Add(logo);
            var bt = new VisualElement();
            var t1 = new Label("CBRE Digital Twin"); t1.AddToClassList("brand-title");
            var t2 = new Label("Construction & Listing Intelligence"); t2.AddToClassList("brand-sub");
            bt.Add(t1); bt.Add(t2); brand.Add(bt);
            card.Add(brand);
            var tag = new Label("Sign in to open the agent workspace. Your SpacetimeDB identity is created automatically and remembered on this device.");
            tag.AddToClassList("login-tagline");
            card.Add(tag);

            card.Add(FieldLabel("YOUR NAME"));
            nameField = Input("e.g. Jordan Lee");
            card.Add(nameField);
            card.Add(FieldLabel("COMPANY"));
            companyField = Input("CBRE");
            card.Add(companyField);
            card.Add(FieldLabel("I AM A…"));
            var roles = new VisualElement(); roles.AddToClassList("role-row");
            roleCm = RoleCard("CM", "Construction Manager", "Generate blueprint prototypes, verify structural integrity, sequence construction risk.", "construction_manager");
            roleAgent = RoleCard("RE", "Real Estate Agent", "Price the home in context, find what sells, fix issues before the inspection.", "real_estate_agent");
            roleAgent.style.marginRight = 0;
            roles.Add(roleCm); roles.Add(roleAgent);
            card.Add(roles);
            SetRole(role);

            var adv = new VisualElement(); adv.style.display = DisplayStyle.None;
            var advToggle = new Button(() => adv.style.display = adv.style.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None) { text = "Server settings +" };
            advToggle.AddToClassList("btn"); advToggle.AddToClassList("btn-ghost"); advToggle.AddToClassList("advanced-toggle");
            advToggle.style.alignSelf = Align.FlexStart;
            card.Add(advToggle);
            adv.Add(FieldLabel("SPACETIMEDB SERVER"));
            serverField = Input("http://127.0.0.1:3000"); serverField.value = net != null ? net.ServerUrl : TwinConnection.DefaultServer;
            adv.Add(serverField);
            adv.Add(FieldLabel("DATABASE"));
            dbField = Input("cbre-twin"); dbField.value = net != null ? net.DbName : TwinConnection.DefaultDb;
            adv.Add(dbField);
            var hint = new Label("Local: http://127.0.0.1:3000 · Maincloud: https://maincloud.spacetimedb.com");
            hint.AddToClassList("tiny"); hint.AddToClassList("muted"); hint.style.marginTop = 4;
            adv.Add(hint);
            card.Add(adv);

            var enter = new Button(OnEnterClicked) { text = "Enter the digital twin  →" };
            enter.AddToClassList("btn"); enter.AddToClassList("btn-cta");
            card.Add(enter);
            nameField.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) OnEnterClicked(); }, TrickleDown.TrickleDown);

            loginStatus = new Label("Connecting…"); loginStatus.AddToClassList("status-line");
            card.Add(loginStatus);
            var foot = new VisualElement(); foot.AddToClassList("login-footer");
            foreach (var (txt, cls) in new[] { ("SpacetimeDB 2.14 · reducers, procedures, schedules, views", "pill-violet"), ("NVIDIA Nemotron 3 Super agents", "pill-ok"), ("IRC 2021 · NAHB · NOAA · USDA · Zillow", "pill-info") })
            {
                var c = new Label(txt); c.AddToClassList("chip"); c.AddToClassList(cls); foot.Add(c);
            }
            card.Add(foot);
            loginLayer.Add(card);
        }

        VisualElement RoleCard(string icon, string name, string desc, string key)
        {
            var c = new VisualElement(); c.AddToClassList("role-card");
            var i = new Label(icon); i.AddToClassList("role-icon");
            i.style.color = key == "construction_manager" ? Palette.Coral : Palette.Indigo; i.style.unityFontStyleAndWeight = FontStyle.Bold;
            var n = new Label(name); n.AddToClassList("role-name");
            var d = new Label(desc); d.AddToClassList("role-desc");
            c.Add(i); c.Add(n); c.Add(d);
            c.RegisterCallback<ClickEvent>(_ => SetRole(key));
            return c;
        }

        void SetRole(string r)
        {
            role = r == "construction_manager" ? r : "real_estate_agent";
            roleCm?.EnableInClassList("role-card--on", role == "construction_manager");
            roleAgent?.EnableInClassList("role-card--on", role == "real_estate_agent");
        }

        void OnEnterClicked()
        {
            var name = nameField.value?.Trim();
            if (string.IsNullOrEmpty(name)) { loginStatus.text = "Please enter your name."; loginStatus.AddToClassList("status-line--err"); return; }
            pendingName = name;
            waitingForProfile = true;
            if (serverField.value.Trim() != net.ServerUrl || dbField.value.Trim() != net.DbName || !net.Ready)
            {
                net.Connect(serverField.value, dbField.value);
                return; // OnNetReady → Register
            }
            Register();
        }

        void Register()
        {
            if (!net.Ready) return;
            loginStatus.text = "Creating your session…";
            net.Conn.Reducers.RegisterUser(pendingName, role, string.IsNullOrWhiteSpace(companyField.value) ? "CBRE" : companyField.value.Trim());
        }

        // ───────────── App shell ─────────────

        void BuildApp()
        {
            appLayer = new VisualElement(); appLayer.AddToClassList("fill");
            appLayer.pickingMode = PickingMode.Ignore;
            root.Add(appLayer);

            // Top bar
            var top = new VisualElement(); top.AddToClassList("topbar");
            var logo = new Label("DT"); logo.AddToClassList("brand-logo"); top.Add(logo);
            var tb = new VisualElement();
            var tt = new Label("CBRE Digital Twin"); tt.AddToClassList("top-title");
            var ts = new Label("SpacetimeDB live twin · NVIDIA Nemotron agent team"); ts.AddToClassList("top-sub");
            tb.Add(tt); tb.Add(ts); top.Add(tb);
            top.Add(Spacer());
            simBtn = Btn("Simulate time", () => net.Conn.Reducers.SetSimulation(!(net.Sim?.Running ?? false)), "btn-mint");
            top.Add(simBtn);
            top.Add(Btn("+6 months", () => { net.Conn.Reducers.StepSimulation(6); Toast("Fast-forwarding 6 months of wear, weather & sensor drift…"); }, null));
            simClock = new Label("Month 0"); simClock.AddToClassList("sim-clock"); simClock.AddToClassList("small"); simClock.AddToClassList("bold");
            top.Add(simClock);
            top.Add(Btn("Reset demo", () => { net.Conn.Reducers.ResetDemo(); Toast("Demo data reset"); }, "btn-ghost"));
            top.Add(Spacer());
            liveDot = new VisualElement(); liveDot.AddToClassList("live-dot"); top.Add(liveDot);
            liveLabel = new Label("Live"); liveLabel.AddToClassList("small"); liveLabel.AddToClassList("muted"); liveLabel.style.marginRight = 14;
            top.Add(liveLabel);
            avatars = new VisualElement(); avatars.AddToClassList("row"); avatars.style.marginRight = 14; top.Add(avatars);
            top.Add(Btn("Sign out", SignOut, "btn-ghost"));
            appLayer.Add(top);

            var body = new VisualElement(); body.AddToClassList("body"); body.pickingMode = PickingMode.Ignore;
            appLayer.Add(body);

            // Sidebar
            var side = new ScrollView(ScrollViewMode.Vertical); side.AddToClassList("sidebar");
            side.Add(Section("EXAMPLE BLUEPRINTS"));
            bpList = new VisualElement(); side.Add(bpList);
            side.Add(Section("VIEW"));
            side.Add(Segmented(new[] { "3D Orbit", "Walk", "Blueprint" }, 0, i => SetView((ViewMode)i)));
            side.Add(Section("LEVELS"));
            levelSeg = new VisualElement(); side.Add(levelSeg);
            side.Add(Section("LAYERS"));
            side.Add(LayerToggle("Structural heatmap", true, v => { builder.ShowStructural = v; builder.RefreshOverlays(); }));
            side.Add(LayerToggle("Building systems", true, v => { builder.ShowSystems = v; builder.RefreshOverlays(); RebuildMarkers(); }));
            side.Add(LayerToggle("Cascade predictions", true, v => { builder.ShowCascade = v; builder.RefreshOverlays(); }));
            side.Add(LayerToggle("Room labels", true, v => { showLabels = v; RebuildMarkers(); }));
            side.Add(LayerToggle("Teammates", true, v => showTeammates = v));
            side.Add(LayerToggle("Roof", false, v => { builder.ShowRoof = v; RebuildScene(false); }));
            side.Add(Section("LEGEND"));
            var legend = new VisualElement(); legend.AddToClassList("row"); legend.style.flexWrap = Wrap.Wrap;
            foreach (var (t, cls) in new[] { ("Sound", "pill-ok"), ("Watch", "pill-watch"), ("Critical", "pill-critical"), ("Low risk", "pill-info"), ("Severe risk", "pill-critical") })
            { var c = new Label(t); c.AddToClassList("chip"); c.AddToClassList(cls); legend.Add(c); }
            side.Add(legend);
            body.Add(side);

            // Center
            var center = new VisualElement(); center.AddToClassList("center"); center.pickingMode = PickingMode.Ignore;
            overlay = new VisualElement(); overlay.AddToClassList("viewport"); overlay.pickingMode = PickingMode.Ignore;
            labelLayer = new VisualElement(); labelLayer.AddToClassList("fill"); labelLayer.pickingMode = PickingMode.Ignore;
            markerLayer = new VisualElement(); markerLayer.AddToClassList("fill"); markerLayer.pickingMode = PickingMode.Ignore;
            overlay.Add(labelLayer); overlay.Add(markerLayer);

            var hud = new VisualElement(); hud.AddToClassList("hud"); hud.pickingMode = PickingMode.Ignore;
            var hudCard = new VisualElement(); hudCard.AddToClassList("hud-card");
            hudTitleBox = new VisualElement(); hudCard.Add(hudTitleBox);
            buildLabel = new Label(""); buildLabel.AddToClassList("tiny"); buildLabel.AddToClassList("muted"); buildLabel.style.marginTop = 6;
            hudCard.Add(buildLabel);
            var bar = new VisualElement(); bar.AddToClassList("build-bar");
            buildFill = new VisualElement(); buildFill.AddToClassList("build-fill"); bar.Add(buildFill);
            hudCard.Add(bar);
            hud.Add(hudCard);
            kpiRow = new VisualElement(); kpiRow.AddToClassList("kpi-row"); hud.Add(kpiRow);
            overlay.Add(hud);
            viewHint = new Label(""); viewHint.AddToClassList("viewhint"); viewHint.pickingMode = PickingMode.Ignore;
            overlay.Add(viewHint);
            popover = new VisualElement(); popover.AddToClassList("popover"); popover.style.display = DisplayStyle.None;
            popover.style.right = 14; popover.style.top = 56;
            overlay.Add(popover);
            center.Add(overlay);

            // Drawer
            drawer = new VisualElement(); drawer.AddToClassList("drawer");
            tabBar = new VisualElement(); tabBar.AddToClassList("tabs");
            drawer.Add(tabBar);
            drawerContent = new VisualElement(); drawerContent.AddToClassList("drawer-content");
            drawer.Add(drawerContent);
            center.Add(drawer);
            body.Add(center);

            // Agent panel
            var agent = new VisualElement(); agent.AddToClassList("agentbar");
            var head = new VisualElement(); head.AddToClassList("agent-head");
            var hr = new VisualElement(); hr.AddToClassList("row");
            var orb = new Label("N"); orb.AddToClassList("agent-orb"); hr.Add(orb);
            var ht = new VisualElement(); ht.style.flexGrow = 1;
            var h1 = new Label("Nemotron Agent Team"); h1.AddToClassList("h3");
            agentModel = new Label("Orchestrator + 3 specialists"); agentModel.AddToClassList("tiny"); agentModel.AddToClassList("muted");
            ht.Add(h1); ht.Add(agentModel); hr.Add(ht);
            head.Add(hr);
            var crew = new VisualElement(); crew.AddToClassList("row"); crew.style.flexWrap = Wrap.Wrap; crew.style.marginTop = 8;
            foreach (var (n, k) in new[] { ("Orchestrator", "orchestrator"), ("Structural", "structural"), ("Operations", "operations"), ("Market", "market") })
            {
                var c = new Label(n); c.AddToClassList("chip"); c.style.backgroundColor = Palette.Agent(k); c.style.color = Color.white; crew.Add(c);
            }
            head.Add(crew);
            var at = new VisualElement(); at.AddToClassList("seg"); at.style.marginTop = 6;
            head.Add(Segmented(new[] { "Conversation", "Agent trace (live)" }, 0, i => { agentTab = i == 0 ? "chat" : "trace"; RefreshAgent(); }));
            agent.Add(head);

            agentBody = new VisualElement(); agentBody.style.flexGrow = 1;
            chatScroll = new ScrollView(ScrollViewMode.Vertical); chatScroll.AddToClassList("chat"); chatScroll.style.flexGrow = 1;
            chatBox = chatScroll.contentContainer;
            traceScroll = new ScrollView(ScrollViewMode.Vertical); traceScroll.AddToClassList("chat"); traceScroll.style.flexGrow = 1;
            traceBox = traceScroll.contentContainer;
            agentBody.Add(chatScroll); agentBody.Add(traceScroll);
            agent.Add(agentBody);
            thinking = new VisualElement(); thinking.AddToClassList("thinking"); thinking.style.marginLeft = 12; thinking.style.marginRight = 12; thinking.style.display = DisplayStyle.None;
            agent.Add(thinking);
            quickRow = new VisualElement(); quickRow.AddToClassList("quick"); agent.Add(quickRow);
            var comp = new VisualElement(); comp.AddToClassList("composer");
            promptField = Input("Ask the agents… e.g. “What fails first and what will it cost?”");
            promptField.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Ask(promptField.value); e.StopPropagation(); } }, TrickleDown.TrickleDown);
            comp.Add(promptField);
            comp.Add(Btn("Send", () => Ask(promptField.value), "btn-primary"));
            agent.Add(comp);
            body.Add(agent);
        }

        VisualElement levelSeg;

        // ───────────── Enter / select ─────────────

        void EnterApp()
        {
            waitingForProfile = false;
            loginLayer.style.display = DisplayStyle.None;
            appLayer.style.display = DisplayStyle.Flex;
            var me = net.Me;
            role = me?.Role ?? role;
            RebuildBlueprintList();
            uint start = me != null && me.SelectedBlueprint != 0 ? me.SelectedBlueprint : (role == "construction_manager" ? 4u : 1u);
            SelectBlueprint(start);
            BuildQuickPrompts();
            Toast($"Welcome, {me?.Name} — you're live on SpacetimeDB");
        }

        void SignOut()
        {
            appLayer.style.display = DisplayStyle.None;
            loginLayer.style.display = DisplayStyle.Flex;
            builder.Clear();
            selectedBp = 0;
            loginStatus.text = net.Status;
        }

        void SelectBlueprint(uint id)
        {
            if (net.Blueprint(id) == null) id = net.Blueprints().FirstOrDefault()?.Id ?? 0;
            if (id == 0) return;
            selectedBp = id;
            net.Conn.Reducers.SelectBlueprint(id);
            builder.MaxFloor = 99;
            builder.SelectedComponent = 0;
            popover.style.display = DisplayStyle.None;
            RebuildScene(true);
            RebuildBlueprintList();
            RebuildLevels();
            lastMessageCount = -1;
            RefreshAll();
        }

        void RebuildScene(bool reframe)
        {
            builder.Build(selectedBp);
            if (reframe || rig.Mode == ViewMode.Plan) rig.SetMode(rig.Mode, builder.WorldBounds, 0);
            var cam = rig.Cam;
            cam.backgroundColor = builder.PlanMode ? Palette.BlueprintBg : Palette.Sky3D;
            RebuildMarkers();
        }

        void SetView(ViewMode m)
        {
            bool plan = m == ViewMode.Plan;
            if (builder.PlanMode != plan) { builder.PlanMode = plan; RebuildScene(false); }
            int floor = Mathf.Min(builder.MaxFloor, builder.FloorCount - 1);
            rig.SetMode(m, builder.WorldBounds, builder.LevelBaseFt(Mathf.Max(0, floor == 98 ? 0 : floor)) * BlueprintBuilder.FT);
            if (m == ViewMode.Walk && builder.MaxFloor >= builder.FloorCount) { builder.MaxFloor = 0; builder.ApplyFloorVisibility(); builder.RefreshOverlays(); RebuildMarkers(); RebuildLevels(); }
            RefreshHint();
        }

        void RefreshHint()
        {
            viewHint.text = rig.Mode switch
            {
                ViewMode.Walk => "WALK · WASD move · Shift run · drag to look · pick a level to change floors",
                ViewMode.Plan => "BLUEPRINT · drag to pan · scroll to zoom · click a room",
                _ => "ORBIT · drag to rotate · right-drag/Q/E · middle-drag pan · scroll zoom · click rooms & systems",
            };
        }

        void RebuildLevels()
        {
            levelSeg.Clear();
            var names = new List<string> { "All" };
            for (int i = 0; i < builder.FloorCount; i++) names.Add($"L{i + 1}");
            int on = builder.MaxFloor >= builder.FloorCount ? 0 : builder.MaxFloor + 1;
            levelSeg.Add(Segmented(names.ToArray(), on, i =>
            {
                builder.MaxFloor = i == 0 ? 99 : i - 1;
                builder.ApplyFloorVisibility();
                builder.RefreshOverlays();
                RebuildMarkers();
                if (rig.Mode == ViewMode.Walk) rig.SetWalkFloor(builder.LevelBaseFt(Mathf.Max(0, i - 1)) * BlueprintBuilder.FT);
            }));
        }

        void RebuildBlueprintList()
        {
            bpList.Clear();
            foreach (var b in net.Blueprints())
            {
                var loc = net.Location(b.LocationId);
                var card = new VisualElement(); card.AddToClassList("bp-card");
                card.EnableInClassList("bp-card--on", b.Id == selectedBp);
                var stripe = new VisualElement(); stripe.AddToClassList("bp-stripe"); stripe.style.backgroundColor = Palette.Hex(b.AccentColor);
                card.Add(stripe);
                var bb = new VisualElement(); bb.AddToClassList("bp-body");
                var n = new Label(b.Name); n.AddToClassList("bp-name"); bb.Add(n);
                var tg = new Label(b.Tagline); tg.AddToClassList("bp-tag"); bb.Add(tg);
                var stats = new VisualElement(); stats.AddToClassList("bp-stats");
                stats.Add(Chip($"{b.Sqft:#,0} sf", null));
                stats.Add(Chip($"{b.Beds} bd / {b.Baths:0.#} ba", null));
                stats.Add(Chip(b.Stage == "existing" ? $"Built {b.YearBuilt}" : "Under construction", b.Stage == "existing" ? "pill-info" : "pill-watch"));
                if (loc != null) stats.Add(Chip(loc.City, "pill-violet"));
                bb.Add(stats);
                card.Add(bb);
                var id = b.Id;
                card.RegisterCallback<ClickEvent>(_ => SelectBlueprint(id));
                bpList.Add(card);
            }
        }

        // ───────────── Update loop ─────────────

        void Update()
        {
            if (net == null) return;
            if (waitingForProfile && net.Ready)
            {
                var me = net.Me;
                if (me != null && me.Name == pendingName && me.Role == role) EnterApp();
            }
            if (toast != null && toast.style.display == DisplayStyle.Flex && Time.unscaledTime > toastUntil) toast.style.display = DisplayStyle.None;
            if (appLayer.style.display == DisplayStyle.None || selectedBp == 0) return;

            // Build progress HUD
            float prog = Mathf.Clamp01((Time.time - builder.BuildStartTime) / Mathf.Max(0.1f, builder.BuildDuration));
            buildFill.style.width = Length.Percent(prog * 100);
            buildLabel.text = prog < 1 ? $"Generating 3D prototype from blueprint rows… {prog * 100:0}%" : $"Prototype ready · {builder.RoomsById.Count} rooms · {builder.FloorCount} level(s) · {builder.ComponentWorld.Count} monitored systems";

            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.25f;
            var d = net.DirtyFlags;
            if (d == Dirty.None) return;
            net.DirtyFlags = Dirty.None;

            if ((d & (Dirty.Risk | Dirty.Structural)) != 0)
            {
                if (Time.unscaledTime - lastOverlayRefresh > 0.8f)
                {
                    lastOverlayRefresh = Time.unscaledTime;
                    builder.RefreshOverlays();
                    RebuildMarkers();
                }
                else net.DirtyFlags |= d & (Dirty.Risk | Dirty.Structural); // retry next tick
            }
            if ((d & Dirty.Geometry) != 0) RebuildBlueprintList();
            if ((d & (Dirty.Risk | Dirty.Structural | Dirty.Market)) != 0) RefreshKpis();
            if ((d & (Dirty.Risk | Dirty.Structural | Dirty.Market | Dirty.Ops)) != 0) RefreshDrawer();
            if ((d & Dirty.Agent) != 0) RefreshAgent();
            if ((d & Dirty.Users) != 0) RefreshAvatars();
            if ((d & Dirty.Sim) != 0) RefreshSim();
            if ((d & Dirty.Annotations) != 0) RebuildMarkers();
            if ((d & Dirty.Ops) != 0) CheckAlerts();
            if (builder.SelectedComponent != 0 && (d & Dirty.Risk) != 0) ShowComponentPopover(builder.SelectedComponent);
        }

        void RefreshAll()
        {
            RefreshKpis();
            RefreshDrawer();
            RefreshAgent();
            RefreshAvatars();
            RefreshSim();
            RefreshHint();
            lastAlertSeen = net.Alerts(selectedBp).FirstOrDefault()?.Id ?? 0;
        }

        void CheckAlerts()
        {
            var a = net.Alerts(selectedBp).FirstOrDefault();
            if (a == null || a.Id <= lastAlertSeen) return;
            lastAlertSeen = a.Id;
            if (a.Level != "info") Toast((a.Level == "critical" ? "CRITICAL · " : "Alert · ") + a.Message);
        }

        void RefreshSim()
        {
            var s = net.Sim;
            if (s == null) return;
            simBtn.text = s.Running ? "Pause simulation" : "Simulate time";
            simBtn.EnableInClassList("btn-coral", s.Running);
            simBtn.EnableInClassList("btn-mint", !s.Running);
            double y = s.BaseYear + s.SimMonths / 12.0;
            int month = (int)((y % 1) * 12);
            var names = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            simClock.text = $"{names[Mathf.Clamp(month, 0, 11)]} {(int)y} · +{s.SimMonths} mo";
        }

        void RefreshAvatars()
        {
            avatars.Clear();
            foreach (var u in net.OnlineUsers().Take(6))
            {
                var a = new Label(Initials(u.Name)); a.AddToClassList("avatar"); a.style.backgroundColor = Palette.Hex(u.Color);
                a.tooltip = $"{u.Name} · {RoleName(u.Role)}";
                avatars.Add(a);
            }
            var me = net.Me;
            if (me != null)
            {
                var who = new Label($"  {me.Name} · {RoleName(me.Role)}"); who.AddToClassList("small"); who.AddToClassList("bold");
                avatars.Add(who);
            }
            liveDot.EnableInClassList("live-dot--off", !net.Ready);
        }

        // ───────────── KPIs / HUD ─────────────

        void RefreshKpis()
        {
            var b = net.Blueprint(selectedBp);
            if (b == null) return;
            var loc = net.Location(b.LocationId);
            hudTitleBox.Clear();
            var r = new VisualElement(); r.AddToClassList("row");
            var dot = new VisualElement(); dot.style.width = 12; dot.style.height = 12; dot.style.borderTopLeftRadius = dot.style.borderTopRightRadius = dot.style.borderBottomLeftRadius = dot.style.borderBottomRightRadius = 6; dot.style.backgroundColor = Palette.Hex(b.AccentColor); dot.style.marginRight = 8;
            r.Add(dot);
            var n = new Label(b.Name); n.AddToClassList("h2"); r.Add(n);
            hudTitleBox.Add(r);
            var sub = new Label($"{b.Style} · {loc?.Name} · {b.Foundation}"); sub.AddToClassList("small"); sub.AddToClassList("muted");
            hudTitleBox.Add(sub);

            kpiRow.Clear();
            var s = net.Summary(selectedBp);
            var v = net.Valuation(selectedBp);
            if (s != null) kpiRow.Add(Kpi("STRUCTURAL INTEGRITY", $"{s.IntegrityScore:0}/100", s.IntegrityScore >= 80 ? Palette.Ok : s.IntegrityScore >= 55 ? Palette.Watch : Palette.Critical));
            if (v != null)
            {
                kpiRow.Add(Kpi("12-MO CASCADE RISK", Palette.Money(v.ExpectedLoss12Mo), Palette.Pink));
                kpiRow.Add(Kpi(b.Stage == "existing" ? "RECOMMENDED LIST" : "PROJECTED VALUE", Palette.Money(v.ListPrice), Palette.Indigo));
                kpiRow.Add(Kpi("DAYS ON MARKET", $"{v.DaysOnMarket:0}", Palette.Mint));
            }
        }

        VisualElement Kpi(string label, string value, Color c)
        {
            var k = new VisualElement(); k.AddToClassList("kpi");
            var l = new Label(label); l.AddToClassList("kpi-label");
            var v = new Label(value); v.AddToClassList("kpi-value"); v.style.color = c;
            k.Add(l); k.Add(v);
            return k;
        }

        // ───────────── Drawer ─────────────

        static readonly (string key, string title)[] Tabs =
        {
            ("structural", "Structural integrity"), ("forecast", "Failure forecast"), ("pricing", "Pricing & location"),
            ("features", "Selling points & ROI"), ("execute", "Execute · work orders"), ("sources", "Data & research"),
        };

        void RefreshDrawer()
        {
            tabBar.Clear();
            foreach (var (key, title) in Tabs)
            {
                var k = key;
                var t = new Button(() => { drawerTab = k; drawerOpen = true; RefreshDrawer(); }) { text = title };
                t.AddToClassList("tab");
                t.EnableInClassList("tab--on", drawerTab == key && drawerOpen);
                tabBar.Add(t);
            }
            tabBar.Add(Spacer());
            tabBar.Add(Btn(drawerOpen ? "Collapse" : "Expand", () => { drawerOpen = !drawerOpen; RefreshDrawer(); }, "btn-ghost"));
            drawer.EnableInClassList("drawer--collapsed", !drawerOpen);

            // Preserve scroll positions across live refreshes
            var offsets = drawerContent.Query<ScrollView>().ToList().Select(sv => sv.scrollOffset).ToList();
            drawerContent.Clear();
            drawerContent.style.display = drawerOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (!drawerOpen) return;
            switch (drawerTab)
            {
                case "structural": DrawStructural(); break;
                case "forecast": DrawForecast(); break;
                case "pricing": DrawPricing(); break;
                case "features": DrawFeatures(); break;
                case "execute": DrawExecute(); break;
                default: DrawSources(); break;
            }
            var svs = drawerContent.Query<ScrollView>().ToList();
            drawerContent.schedule.Execute(() =>
            {
                for (int i = 0; i < svs.Count && i < offsets.Count; i++) svs[i].scrollOffset = offsets[i];
            }).ExecuteLater(1);
        }

        VisualElement HRow()
        {
            var r = new VisualElement(); r.style.flexDirection = FlexDirection.Row; r.style.flexGrow = 1; r.style.alignItems = Align.Stretch;
            drawerContent.Add(r);
            return r;
        }

        ScrollView ListCard(VisualElement parent, string title, float grow, string tint = null)
        {
            var c = new VisualElement(); c.AddToClassList("card"); if (tint != null) c.AddToClassList(tint);
            c.style.flexGrow = grow; c.style.flexBasis = 0;
            var h = new Label(title); h.AddToClassList("h3"); h.style.marginBottom = 6; c.Add(h);
            var sv = new ScrollView(ScrollViewMode.Vertical); sv.style.flexGrow = 1;
            c.Add(sv);
            parent.Add(c);
            return sv;
        }

        VisualElement FixedCard(VisualElement parent, float width, string tint = null)
        {
            var c = new VisualElement(); c.AddToClassList("card"); if (tint != null) c.AddToClassList(tint);
            c.style.width = width; c.style.flexShrink = 0;
            parent.Add(c);
            return c;
        }

        void DrawStructural()
        {
            var row = HRow();
            var s = net.Summary(selectedBp);
            var b = net.Blueprint(selectedBp);
            var left = FixedCard(row, 270, "card-tint-violet");
            left.Add(Tiny("INTEGRITY SCORE"));
            var score = new Label(s != null ? $"{s.IntegrityScore:0}" : "–"); score.AddToClassList("big-number");
            score.style.color = s == null ? Palette.Muted : s.IntegrityScore >= 80 ? Palette.Ok : s.IntegrityScore >= 55 ? Palette.Watch : Palette.Critical;
            left.Add(score);
            left.Add(Bar(s != null ? (float)s.IntegrityScore / 100f : 0, score.style.color.value));
            var chips = new VisualElement(); chips.AddToClassList("row"); chips.style.flexWrap = Wrap.Wrap; chips.style.marginTop = 8;
            if (s != null)
            {
                chips.Add(Chip($"{s.CriticalCount} critical", "pill-critical"));
                chips.Add(Chip($"{s.WatchCount} watch", "pill-watch"));
                chips.Add(Chip($"{s.OkCount} sound", "pill-ok"));
            }
            left.Add(chips);
            if (b != null)
            {
                left.Add(Small($"Framing: {b.FloorJoist} floors · {b.CeilingJoist} ceilings/roof"));
                left.Add(Small($"Foundation: {b.Foundation}"));
                left.Add(Small($"Roof: {b.RoofType}"));
            }
            var list = ListCard(row, "Findings — IRC 2021 / AWC span tables / expansive-soil rules", 1);
            var findings = net.Findings(selectedBp).OrderByDescending(f => f.Severity == "critical" ? 2 : f.Severity == "watch" ? 1 : 0).ThenByDescending(f => f.FixCost).ToList();
            foreach (var f in findings)
            {
                var r = new VisualElement(); r.AddToClassList("list-row");
                var stripe = new VisualElement(); stripe.AddToClassList("sev-stripe"); stripe.style.backgroundColor = Palette.Severity(f.Severity); r.Add(stripe);
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                var t = new Label(f.Title); t.AddToClassList("bold"); col.Add(t);
                if (f.Severity != "ok") { var dd = new Label(f.Detail); dd.AddToClassList("small"); dd.AddToClassList("muted"); dd.AddToClassList("wrap"); col.Add(dd); }
                r.Add(col);
                var chipsCol = new VisualElement(); chipsCol.style.alignItems = Align.FlexEnd; chipsCol.style.width = 210;
                var cr = new VisualElement(); cr.AddToClassList("row"); cr.style.justifyContent = Justify.FlexEnd; cr.style.flexWrap = Wrap.Wrap;
                cr.Add(Chip(f.Severity.ToUpperInvariant(), f.Severity == "critical" ? "pill-critical" : f.Severity == "watch" ? "pill-watch" : "pill-ok"));
                if (f.FixCost > 0) cr.Add(Chip("Fix " + Palette.Money(f.FixCost), "pill-violet"));
                chipsCol.Add(cr);
                var code = new Label(f.CodeRef); code.AddToClassList("tiny"); code.AddToClassList("muted"); chipsCol.Add(code);
                var rid = f.RoomId;
                chipsCol.Add(Btn("Locate", () => FocusRoom(rid), "btn-small"));
                r.Add(chipsCol);
                list.Add(r);
            }
        }

        void DrawForecast()
        {
            var row = HRow();
            var list = ListCard(row, "Building systems — 12-month failure probability (standalone vs. with cascade)", 1.6f);
            var comps = net.Components(selectedBp).ToDictionary(c => c.Id);
            var openWo = new HashSet<uint>(net.WorkOrders(selectedBp).Where(w => w.Status != "done").Select(w => w.ComponentId));
            foreach (var r in net.Risks(selectedBp))
            {
                if (!comps.TryGetValue(r.ComponentId, out var c)) continue;
                var line = new VisualElement(); line.AddToClassList("list-row");
                var dot = new Label(Palette.KindIcon(c.Kind)); dot.style.width = 30; dot.style.height = 30; Round(dot, 15);
                dot.style.backgroundColor = Palette.Tier(r.Tier); dot.style.color = Color.white; dot.style.unityTextAlign = TextAnchor.MiddleCenter; dot.style.unityFontStyleAndWeight = FontStyle.Bold; dot.style.fontSize = 10; dot.style.marginRight = 10;
                line.Add(dot);
                var name = new VisualElement(); name.style.width = 210;
                var nl = new Label($"#{r.Rank} {c.Name}"); nl.AddToClassList("bold"); name.Add(nl);
                var ml = new Label(c.Material); ml.AddToClassList("tiny"); ml.AddToClassList("muted"); ml.AddToClassList("wrap"); name.Add(ml);
                line.Add(name);
                var bars = new VisualElement(); bars.style.flexGrow = 1; bars.style.marginRight = 12;
                bars.Add(LabeledBar("alone", (float)r.BaseProb, Palette.Hex("#B6C2DD")));
                bars.Add(LabeledBar("cascade", (float)r.CascadeProb, Palette.Tier(r.Tier)));
                line.Add(bars);
                var money = new VisualElement(); money.style.width = 150;
                money.Add(Small($"Exp. loss {Palette.Money(r.ExpectedLoss)}"));
                var pv = new Label($"Fix prevents {Palette.Money(r.PreventionValue)}"); pv.AddToClassList("small"); pv.AddToClassList("bold"); pv.style.color = Palette.Mint; money.Add(pv);
                line.Add(money);
                var sensor = new VisualElement(); sensor.style.width = 170;
                var sc = c.SensorValue >= c.SensorCritical ? Palette.Critical : c.SensorValue >= c.SensorWarn ? Palette.Watch : Palette.Ok;
                var s1 = new Label($"{c.SensorValue:0.#} {c.SensorUnit}"); s1.AddToClassList("bold"); s1.style.color = sc; sensor.Add(s1);
                var s2 = new Label(c.SensorLabel); s2.AddToClassList("tiny"); s2.AddToClassList("muted"); sensor.Add(s2);
                line.Add(sensor);
                var cid = c.Id;
                if (openWo.Contains(c.Id)) line.Add(Chip("Work order open", "pill-info"));
                else line.Add(Btn("Dispatch fix", () => { net.Conn.Reducers.CreateWorkOrder(cid, "Preventive repair / replacement"); Toast($"Work order dispatched for {c.Name}"); }, "btn-small btn-coral"));
                line.RegisterCallback<ClickEvent>(e => { if (e.target is Button) return; SelectComponent(cid); });
                list.Add(line);
            }
            var paths = ListCard(row, "Predicted cascade chains (noisy-OR failure graph)", 1, "card-tint-coral");
            int i = 0;
            foreach (var p in net.Paths(selectedBp))
            {
                var r = new VisualElement(); r.style.marginBottom = 10;
                var t = new Label($"{++i}. {p.Label}"); t.AddToClassList("bold"); t.AddToClassList("wrap"); t.style.color = i == 1 ? Palette.Hex("#E63950") : i == 2 ? Palette.Hex("#E8701A") : Palette.Ink;
                r.Add(t);
                var cr = new VisualElement(); cr.AddToClassList("row"); cr.style.marginTop = 3;
                cr.Add(Chip($"P {p.Probability * 100:0.0}%", "pill-critical"));
                cr.Add(Chip($"Downstream {Palette.Money(p.Loss)}", "pill-violet"));
                r.Add(cr);
                var m = new Label(p.Mechanism); m.AddToClassList("tiny"); m.AddToClassList("muted"); m.AddToClassList("wrap");
                r.Add(m);
                paths.Add(r);
            }
        }

        void DrawPricing()
        {
            var row = HRow();
            var v = net.Valuation(selectedBp);
            var b = net.Blueprint(selectedBp);
            if (v == null || b == null) return;
            var main = FixedCard(row, 270, "card-tint-sky");
            main.Add(Tiny(b.Stage == "existing" ? "RECOMMENDED LIST PRICE" : "PROJECTED COMPLETED VALUE"));
            var price = new Label(Palette.MoneyFull(v.ListPrice)); price.AddToClassList("big-number"); price.style.color = Palette.Indigo; main.Add(price);
            main.Add(Small($"Range {Palette.Money(v.LowPrice)} – {Palette.Money(v.HighPrice)}"));
            main.Add(Small($"{Palette.MoneyFull(v.Ppsf)}/sqft · confidence {Palette.Pct(v.Confidence)}"));
            main.Add(Small($"~{v.DaysOnMarket:0} days on market"));
            main.Add(Small($"Annual carry (tax + insurance + risk): {Palette.Money(v.AnnualCarryCost)}"));
            if (v.ValueUnlocked > 0)
            {
                var u = new Label($"Fixing {Palette.Money(v.PreListingRepairCost)} of issues pre-listing unlocks {Palette.Money(v.ValueUnlocked)} net");
                u.AddToClassList("small"); u.AddToClassList("bold"); u.AddToClassList("wrap"); u.style.color = Palette.Mint; u.style.marginTop = 8;
                main.Add(u);
            }

            var bridge = ListCard(row, "Price bridge — how the twin got there", 1);
            double adjBase = v.MarketBase * (1 + v.AgeAdj + v.LayoutAdj);
            double riskDrag = adjBase + v.FeaturePremium - v.ConditionDiscount - v.StructuralDiscount - v.ListPrice;
            var items = new List<(string, double, Color)>
            {
                ("Market base (sqft × local $/sqft)", v.MarketBase, Palette.Sky),
                ($"Age / new-build ({v.AgeAdj * 100:+0.0;-0.0}%)", v.MarketBase * v.AgeAdj, v.AgeAdj >= 0 ? Palette.Ok : Palette.Watch),
                ($"Bed/bath layout ({v.LayoutAdj * 100:+0.0;-0.0}%)", v.MarketBase * v.LayoutAdj, v.LayoutAdj >= 0 ? Palette.Ok : Palette.Watch),
                ("Feature premium (local demand-weighted)", v.FeaturePremium, Palette.Mint),
                ("Condition / inspection credits", -v.ConditionDiscount, Palette.Coral),
                ("Structural defect discount", -v.StructuralDiscount, Palette.Critical),
                ("Cascade-risk drag (12-mo expected loss)", -riskDrag, Palette.Pink),
                ("= Recommended list", v.ListPrice, Palette.Indigo),
            };
            double max = items.Max(x => Math.Abs(x.Item2));
            foreach (var (label, val, col) in items)
            {
                var r = new VisualElement(); r.AddToClassList("row"); r.style.marginBottom = 5;
                var l = new Label(label); l.AddToClassList("small"); l.style.width = 250; r.Add(l);
                var barBg = new VisualElement(); barBg.style.flexGrow = 1; barBg.style.height = 14;
                var fill = new VisualElement(); fill.style.height = 14; Round(fill, 4);
                fill.style.width = Length.Percent((float)(Math.Abs(val) / Math.Max(1, max) * 100)); fill.style.backgroundColor = col;
                barBg.Add(fill); r.Add(barBg);
                var vl = new Label((val >= 0 ? (label.StartsWith("=") || label.StartsWith("Market") ? "" : "+") : "") + Palette.Money(val)); vl.AddToClassList("small"); vl.AddToClassList("bold"); vl.style.width = 80; vl.style.unityTextAlign = TextAnchor.MiddleRight;
                r.Add(vl);
                bridge.Add(r);
            }

            var loc = ListCard(row, "Same model, different market", 1, "card-tint-mint");
            var lp = net.LocationPricing(selectedBp);
            double lmax = lp.Count > 0 ? lp.Max(x => x.ListPrice) : 1;
            foreach (var p in lp)
            {
                bool here = p.LocationId == b.LocationId;
                var r = new VisualElement(); r.style.marginBottom = 6;
                var top = new VisualElement(); top.AddToClassList("row");
                var n = new Label((here ? "> " : "") + p.LocationName); n.AddToClassList("small"); n.AddToClassList("bold"); n.style.flexGrow = 1; if (here) n.style.color = Palette.Indigo;
                top.Add(n);
                var pr = new Label(Palette.Money(p.ListPrice)); pr.AddToClassList("small"); pr.AddToClassList("bold"); top.Add(pr);
                r.Add(top);
                r.Add(Bar((float)(p.ListPrice / lmax), here ? Palette.Indigo : Palette.Mint));
                var meta = new Label($"{Palette.MoneyFull(p.Ppsf)}/sf · risk {Palette.Money(p.RiskLoss12Mo)}/yr · {p.DaysOnMarket:0} DOM · top hazard: {p.TopHazard}"); meta.AddToClassList("tiny"); meta.AddToClassList("muted");
                r.Add(meta);
                loc.Add(r);
            }
        }

        void DrawFeatures()
        {
            var row = HRow();
            var feats = ListCard(row, "What sells here — demand-weighted for the local buyer pool", 1.2f, "card-tint-sun");
            foreach (var f in net.Features(selectedBp))
            {
                var r = new VisualElement(); r.AddToClassList("list-row");
                var badge = new Label(f.Present ? "HAS" : "GAP"); badge.AddToClassList("chip"); badge.AddToClassList(f.Present ? "pill-ok" : "pill-watch"); badge.style.width = 44; badge.style.unityTextAlign = TextAnchor.MiddleCenter;
                r.Add(badge);
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                var n = new Label(f.Name); n.AddToClassList("bold"); col.Add(n);
                var pt = new Label(f.Pitch); pt.AddToClassList("tiny"); pt.AddToClassList("muted"); pt.AddToClassList("wrap"); col.Add(pt);
                r.Add(col);
                var right = new VisualElement(); right.style.width = 150;
                right.Add(LabeledBar("demand", (float)f.DemandScore, Palette.Sun));
                var vi = new Label((f.Present ? "+" : "potential +") + Palette.Money(f.ValueImpact)); vi.AddToClassList("small"); vi.AddToClassList("bold"); vi.style.color = f.Present ? Palette.Ok : Palette.Muted;
                right.Add(vi);
                r.Add(right);
                feats.Add(r);
            }
            var ups = ListCard(row, "Best ROI moves (repairs × Cost vs. Value 2024 × local demand)", 1);
            foreach (var u in net.Upgrades(selectedBp).Take(12))
            {
                var r = new VisualElement(); r.AddToClassList("list-row");
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                var n = new Label(u.Name); n.AddToClassList("bold"); col.Add(n);
                var ra = new Label(u.Rationale); ra.AddToClassList("tiny"); ra.AddToClassList("muted"); ra.AddToClassList("wrap"); col.Add(ra);
                r.Add(col);
                var right = new VisualElement(); right.style.width = 170; right.style.alignItems = Align.FlexEnd;
                var roi = new Label($"{u.Roi:0.00}× ROI"); roi.AddToClassList("bold"); roi.style.color = u.Roi >= 1 ? Palette.Ok : Palette.Watch; right.Add(roi);
                right.Add(Small($"Cost {Palette.Money(u.Cost)} → {Palette.Money(u.ValueAdd + u.RiskAvoided)}"));
                right.Add(Chip(u.Kind == "repair" ? "Prevent cascade" : "Upgrade", u.Kind == "repair" ? "pill-critical" : "pill-info"));
                r.Add(right);
                ups.Add(r);
            }
        }

        void DrawExecute()
        {
            var row = HRow();
            var wos = ListCard(row, "Work orders — executed transactionally in SpacetimeDB", 1.2f, "card-tint-mint");
            var comps = net.Components(selectedBp).ToDictionary(c => c.Id);
            var orders = net.WorkOrders(selectedBp);
            if (orders.Count == 0) wos.Add(Small("No work orders yet. Ask the agent to “fix the top risk” or press Dispatch in the Failure forecast tab."));
            foreach (var w in orders)
            {
                var r = new VisualElement(); r.AddToClassList("list-row");
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                var n = new Label($"#{w.Id} {w.Title}"); n.AddToClassList("bold"); n.AddToClassList("wrap"); col.Add(n);
                col.Add(Small($"{(w.Source == "agent" ? "Dispatched by " : "Created by ")}{w.CreatedByName} · budget {Palette.Money(w.Cost)} · prevents ≈ {Palette.Money(w.LossAvoided)}"));
                r.Add(col);
                var st = w.Status switch { "done" => ("Done", "pill-ok"), "in_progress" => ("In progress", "pill-watch"), _ => ("Dispatched", "pill-info") };
                r.Add(Chip(st.Item1, st.Item2));
                if (w.Status != "done")
                {
                    var id = w.Id;
                    r.Add(Btn(w.Status == "dispatched" ? "Start" : "Complete", () => net.Conn.Reducers.AdvanceWorkOrder(id), "btn-small btn-primary"));
                }
                wos.Add(r);
            }
            var al = ListCard(row, "Live alerts — sensor thresholds, NOAA hail, freeze events", 1, "card-tint-coral");
            var alerts = net.Alerts(selectedBp);
            if (alerts.Count == 0) al.Add(Small("Press “Simulate time” in the top bar to stream monthly wear, weather events and sensor drift."));
            foreach (var a in alerts.Take(30))
            {
                var r = new VisualElement(); r.AddToClassList("list-row");
                r.Add(Chip(a.Level.ToUpperInvariant(), a.Level == "critical" ? "pill-critical" : a.Level == "warn" ? "pill-watch" : "pill-ok"));
                var m = new Label(a.Message); m.AddToClassList("small"); m.AddToClassList("wrap"); m.style.flexShrink = 1; m.style.flexGrow = 1;
                r.Add(m);
                var mo = new Label($"+{a.SimMonth} mo"); mo.AddToClassList("tiny"); mo.AddToClassList("muted"); r.Add(mo);
                al.Add(r);
            }
        }

        void DrawSources()
        {
            var row = HRow();
            var src = ListCard(row, "Research-backed data sources fused in the twin", 1.5f);
            foreach (var d in net.Sources())
            {
                var r = new VisualElement(); r.AddToClassList("list-row");
                r.Add(Chip(d.Domain, d.Domain == "Market" ? "pill-ok" : d.Domain == "Structural" ? "pill-violet" : d.Domain == "Hazard" ? "pill-watch" : "pill-info"));
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                var n = new Label(d.Name); n.AddToClassList("bold"); n.AddToClassList("wrap"); col.Add(n);
                var u = new Label($"{d.Usage} · {d.Url}"); u.AddToClassList("tiny"); u.AddToClassList("muted"); u.AddToClassList("wrap"); col.Add(u);
                r.Add(col);
                src.Add(r);
            }
            var arch = FixedCard(row, 420, "card-tint-violet");
            var h = new Label("How the twin is wired"); h.AddToClassList("h3"); arch.Add(h);
            foreach (var line in new[]
            {
                "• Tables unify plans, systems, hazards & comps (one relational source of truth).",
                "• Reducers recompute structure, risk & price transactionally on every change.",
                "• Scheduled reducer simulates monthly wear, NOAA hail & freeze events.",
                "• Procedure runs the Nemotron agent loop with outbound HTTP — tool calls hit the same transactions.",
                "• Subscriptions stream every agent step, alert and teammate pose to all clients live.",
                "• NVIDIA_API_KEY lives in the database environment; clients never see it.",
            })
            {
                var l = new Label(line); l.AddToClassList("small"); l.AddToClassList("wrap"); l.style.marginTop = 4; arch.Add(l);
            }
        }

        // ───────────── Agent panel ─────────────

        void BuildQuickPrompts()
        {
            quickRow.Clear();
            var prompts = role == "construction_manager"
                ? new[] { "Full structural review", "What fails first during construction?", "Sequence the next 90 days to avoid weather damage", "Dispatch fixes for critical issues" }
                : new[] { "Price this home for listing", "What features should I lead with?", "What should the seller fix before listing?", "Fix the top risk now", "How would this price in Austin vs Plano?" };
            foreach (var p in prompts)
            {
                var text = p;
                var c = new Button(() => Ask(text)) { text = p };
                c.AddToClassList("chip"); c.AddToClassList("chip-btn"); c.AddToClassList("pill-violet");
                quickRow.Add(c);
            }
        }

        void Ask(string prompt)
        {
            prompt = prompt?.Trim();
            if (string.IsNullOrEmpty(prompt) || selectedBp == 0 || !net.Ready) return;
            promptField.value = "";
            agentBusy = true;
            RefreshAgent();
            net.Conn.Procedures.AskAgent(selectedBp, prompt, (ctx, result) =>
            {
                agentBusy = false;
                if (!result.IsSuccess) Toast("Agent error: " + result.Error?.Message);
                net.DirtyFlags |= Dirty.Agent;
            });
        }

        void RefreshAgent()
        {
            chatScroll.style.display = agentTab == "chat" ? DisplayStyle.Flex : DisplayStyle.None;
            traceScroll.style.display = agentTab == "trace" ? DisplayStyle.Flex : DisplayStyle.None;

            var run = net.LatestRun(selectedBp);
            agentModel.text = run == null
                ? "Orchestrator + Structural · Operations · Market specialists"
                : run.UsedLlm || run.Status == "running" ? $"Model: {run.Model}" : $"Mode: {run.Model}";

            var msgs = net.Messages(selectedBp);
            if (msgs.Count != lastMessageCount)
            {
                lastMessageCount = msgs.Count;
                chatBox.Clear();
                if (msgs.Count == 0)
                {
                    var hello = Bubble("Twin Agent", "orchestrator",
                        role == "construction_manager"
                            ? "Hi! I coordinate a Structural Engineer, an Operations/Failure-prediction agent and a Market agent over this live twin. Ask for a structural review, what will fail first, or tell me to dispatch fixes."
                            : "Hi! I coordinate Structural, Operations and Market agents over this live twin. Ask me to price the home, find the selling points, or fix issues before they reach the buyer's inspection.", false);
                    chatBox.Add(hello);
                }
                foreach (var m in msgs) chatBox.Add(Bubble(m.AuthorName, m.Agent, m.Content, m.Role == "user"));
                chatScroll.schedule.Execute(() => chatScroll.scrollOffset = new Vector2(0, chatScroll.contentContainer.layout.height)).ExecuteLater(30);
            }

            // Trace (live steps)
            traceBox.Clear();
            var runs = net.Ready ? net.Conn.Db.AgentRun.BlueprintId.Filter(selectedBp).OrderByDescending(r => r.Id).Take(3).ToList() : new List<AgentRun>();
            if (runs.Count == 0) traceBox.Add(Small("Each run's plan, hand-offs, tool calls and executed actions stream here live from the agent_step table."));
            foreach (var r in runs)
            {
                var hdr = new Label($"Run #{r.Id} · {r.RequesterName}: “{Truncate(r.Prompt, 70)}” · {r.Status}"); hdr.AddToClassList("small"); hdr.AddToClassList("bold"); hdr.AddToClassList("wrap"); hdr.style.marginBottom = 6; hdr.style.marginTop = 6;
                traceBox.Add(hdr);
                foreach (var s in net.Steps(r.Id))
                {
                    var st = new VisualElement(); st.AddToClassList("step");
                    var d = new Label(s.Kind switch { "plan" => "P", "handoff" => "H", "tool_call" => "T", "tool_result" => "R", "action" => "!", "final" => "F", "error" => "x", _ => "·" });
                    d.AddToClassList("step-dot"); d.style.backgroundColor = s.Kind == "action" ? Palette.Coral : s.Kind == "error" ? Palette.Critical : Palette.Agent(s.Agent);
                    st.Add(d);
                    var bd = new VisualElement(); bd.AddToClassList("step-body");
                    var t = new Label($"{s.Title}"); t.AddToClassList("step-title"); bd.Add(t);
                    if (!string.IsNullOrEmpty(s.Detail)) { var dt = new Label(Truncate(Clean(s.Detail), 420)); dt.AddToClassList("step-detail"); dt.enableRichText = false; bd.Add(dt); }
                    st.Add(bd);
                    traceBox.Add(st);
                }
            }

            // Thinking indicator
            bool running = agentBusy || (run != null && run.Status == "running");
            thinking.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            thinking.Clear();
            if (running)
            {
                var dot = new VisualElement(); dot.style.width = 10; dot.style.height = 10; Round(dot, 5); dot.style.backgroundColor = Palette.NvidiaGreen; dot.style.marginRight = 8;
                thinking.Add(dot);
                var last = run != null ? net.Steps(run.Id).LastOrDefault() : null;
                var l = new Label(last != null ? $"{AgentName(last.Agent)}: {last.Title}…" : "Orchestrator is planning…"); l.AddToClassList("small"); l.AddToClassList("bold"); l.style.color = Palette.Hex("#3F7D00");
                thinking.Add(l);
            }
        }

        VisualElement Bubble(string author, string agent, string text, bool user)
        {
            var b = new VisualElement(); b.AddToClassList("bubble"); b.AddToClassList(user ? "bubble-user" : "bubble-agent");
            var meta = new Label(user ? author : $"{author} · {AgentName(agent)}"); meta.AddToClassList("bubble-meta");
            if (!user) meta.style.color = Palette.Agent(agent);
            b.Add(meta);
            var t = new Label(Clean(text)); t.AddToClassList("bubble-text"); t.enableRichText = false;
            b.Add(t);
            return b;
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("**", "").Replace("__", "");
            s = Regex.Replace(s, @"^#{1,6}\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
            return s.Trim();
        }

        static string AgentName(string a) => a switch
        {
            "structural" => "Structural Engineer",
            "operations" => "Operations",
            "market" => "Market & Valuation",
            "user" => "You",
            _ => "Orchestrator",
        };

        // ───────────── 3D overlays: system pills, room labels, teammates, pins ─────────────

        void RebuildMarkers()
        {
            markerLayer.Clear();
            labelLayer.Clear();
            markerEls.Clear();
            roomLabels.Clear();
            nameTags.Clear();
            pinEls.Clear();
            if (selectedBp == 0 || !net.Ready) return;

            if (showLabels)
            {
                foreach (var r in builder.RoomsById.Values)
                {
                    if (r.Floor > builder.MaxFloor) continue;
                    var l = new Label(builder.PlanMode ? $"{r.Name.ToUpperInvariant()}\n{r.W:0}' × {r.D:0}'" : r.Name);
                    l.AddToClassList("room-label");
                    if (builder.PlanMode) l.AddToClassList("room-label--plan");
                    l.pickingMode = PickingMode.Ignore;
                    labelLayer.Add(l);
                    roomLabels.Add((l, r.Id));
                }
            }
            if (builder.ShowSystems)
            {
                foreach (var c in net.Components(selectedBp))
                {
                    if (!builder.RoomsById.TryGetValue(c.RoomId, out var room) || room.Floor > builder.MaxFloor) continue;
                    var risk = net.Risk(c.Id);
                    var col = Palette.Tier(risk?.Tier ?? "low");
                    var m = new VisualElement(); m.AddToClassList("marker"); m.style.borderTopColor = m.style.borderBottomColor = m.style.borderLeftColor = m.style.borderRightColor = col;
                    var dot = new Label(Palette.KindIcon(c.Kind)); dot.AddToClassList("marker-dot"); dot.style.backgroundColor = col; dot.style.width = 22; dot.style.height = 16; dot.style.fontSize = 8; dot.style.color = Color.white; dot.style.unityTextAlign = TextAnchor.MiddleCenter;
                    m.Add(dot);
                    m.Add(new Label($"{Palette.KindLabel(c.Kind)} {(risk != null ? risk.CascadeProb * 100 : 0):0}%"));
                    var id = c.Id;
                    m.RegisterCallback<ClickEvent>(_ => SelectComponent(id));
                    markerLayer.Add(m);
                    markerEls.Add((m, c.Id));
                }
            }
            foreach (var a in net.Annotations(selectedBp))
            {
                var p = new Label($"{a.AuthorName}: {a.Text}"); p.AddToClassList("name-tag"); p.style.backgroundColor = Palette.Hex(a.Color);
                var aid = a.Id;
                p.RegisterCallback<ClickEvent>(_ => net.Conn.Reducers.DeleteAnnotation(aid));
                p.tooltip = "Click to remove pin";
                markerLayer.Add(p);
                pinEls.Add((p, new Vector3(a.X, a.Y, a.Z)));
            }
        }

        void LateUpdate()
        {
            if (appLayer == null || appLayer.style.display == DisplayStyle.None || root.panel == null) return;
            var cam = rig.Cam;
            foreach (var (el, id) in markerEls)
                Place(el, builder.ComponentWorld.TryGetValue(id, out var w) ? w + Vector3.up * 0.35f : (Vector3?)null, cam);
            foreach (var (el, id) in roomLabels)
                Place(el, builder.RoomCenter.TryGetValue(id, out var w) ? w + Vector3.up * 0.2f : (Vector3?)null, cam);
            foreach (var (el, pos) in pinEls) Place(el, pos, cam);

            // Teammate name tags
            var seen = new HashSet<string>();
            if (showTeammates)
            {
                foreach (var kv in presence.Others)
                {
                    if (kv.Value.t == null) continue;
                    seen.Add(kv.Key);
                    kv.Value.t.gameObject.SetActive(true);
                    if (!nameTags.TryGetValue(kv.Key, out var tag))
                    {
                        tag = new Label(kv.Value.name); tag.AddToClassList("name-tag"); tag.style.backgroundColor = kv.Value.color; tag.pickingMode = PickingMode.Ignore;
                        markerLayer.Add(tag);
                        nameTags[kv.Key] = tag;
                    }
                    Place(tag, kv.Value.t.position + Vector3.up * 0.45f, cam);
                }
            }
            else foreach (var kv in presence.Others) if (kv.Value.t) kv.Value.t.gameObject.SetActive(false);
            foreach (var k in nameTags.Keys.Where(k => !seen.Contains(k)).ToList()) { nameTags[k].RemoveFromHierarchy(); nameTags.Remove(k); }
        }

        void Place(VisualElement el, Vector3? world, Camera cam)
        {
            if (world == null) { el.style.display = DisplayStyle.None; return; }
            var vp = cam.WorldToViewportPoint(world.Value);
            if (vp.z < 0.1f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { el.style.display = DisplayStyle.None; return; }
            el.style.display = DisplayStyle.Flex;
            var p = RuntimePanelUtils.CameraTransformWorldToPanel(root.panel, world.Value, cam);
            var origin = el.parent.worldBound.position;
            el.style.left = p.x - origin.x;
            el.style.top = p.y - origin.y;
        }

        // ───────────── Selection & popovers ─────────────

        void HandleWorldClick(Vector2 screen)
        {
            if (appLayer.style.display == DisplayStyle.None) return;
            var ray = rig.Cam.ScreenPointToRay(screen);
            if (Physics.Raycast(ray, out var hit, 500f))
            {
                var tag = hit.collider.GetComponent<RoomTag>();
                if (tag != null) { ShowRoomPopover(tag.RoomId, hit.point); return; }
            }
            popover.style.display = DisplayStyle.None;
            builder.SelectedComponent = 0;
        }

        void FocusRoom(uint roomId)
        {
            if (builder.RoomsById.TryGetValue(roomId, out var r) && r.Floor > builder.MaxFloor)
            {
                builder.MaxFloor = (int)r.Floor;
                builder.ApplyFloorVisibility();
                builder.RefreshOverlays();
                RebuildMarkers();
                RebuildLevels();
            }
            rig.FocusOn(builder.RoomFocusPoint(roomId), 9f);
            ShowRoomPopover(roomId, builder.RoomFocusPoint(roomId));
        }

        void SelectComponent(uint id)
        {
            builder.SelectedComponent = id;
            if (builder.ComponentWorld.TryGetValue(id, out var w)) rig.FocusOn(w, 8f);
            ShowComponentPopover(id);
        }

        void ShowComponentPopover(uint id)
        {
            var c = net.Conn.Db.Component.Id.Find(id);
            if (c == null) return;
            var r = net.Risk(id);
            popover.Clear();
            popover.style.display = DisplayStyle.Flex;
            var head = new VisualElement(); head.AddToClassList("row");
            var t = new Label(c.Name); t.AddToClassList("h3"); t.style.flexGrow = 1; head.Add(t);
            head.Add(Btn("×", () => { popover.style.display = DisplayStyle.None; builder.SelectedComponent = 0; }, "btn-small btn-ghost"));
            popover.Add(head);
            popover.Add(Small(c.Material));
            double age = (net.Sim?.BaseYear ?? 2026.8) + (net.Sim?.SimMonths ?? 0) / 12.0 - c.InstallYear;
            var chips = new VisualElement(); chips.AddToClassList("row"); chips.style.flexWrap = Wrap.Wrap; chips.style.marginTop = 6;
            chips.Add(Chip($"Age {Math.Max(0, age):0.#} / {c.ExpectedLife:0} yr", "pill-info"));
            chips.Add(Chip($"Health {c.Condition * 100:0}%", c.Condition > 0.6 ? "pill-ok" : c.Condition > 0.4 ? "pill-watch" : "pill-critical"));
            if (r != null) chips.Add(Chip(r.Tier.ToUpperInvariant() + " RISK", r.Tier == "low" ? "pill-ok" : r.Tier == "elevated" ? "pill-watch" : "pill-critical"));
            popover.Add(chips);
            var sc = c.SensorValue >= c.SensorCritical ? Palette.Critical : c.SensorValue >= c.SensorWarn ? Palette.Watch : Palette.Ok;
            var sensor = new Label($"{c.SensorLabel}: {c.SensorValue:0.#} {c.SensorUnit}  (warn {c.SensorWarn}, critical {c.SensorCritical})"); sensor.AddToClassList("small"); sensor.AddToClassList("bold"); sensor.AddToClassList("wrap"); sensor.style.color = sc;
            popover.Add(sensor);
            if (r != null)
            {
                popover.Add(LabeledBar("alone", (float)r.BaseProb, Palette.Hex("#B6C2DD")));
                popover.Add(LabeledBar("cascade", (float)r.CascadeProb, Palette.Tier(r.Tier)));
                popover.Add(Small($"Expected 12-mo loss {Palette.Money(r.ExpectedLoss)} · fixing now prevents {Palette.Money(r.PreventionValue)} across the graph"));
                var dr = new Label(r.Drivers); dr.AddToClassList("tiny"); dr.AddToClassList("muted"); dr.AddToClassList("wrap"); popover.Add(dr);
            }
            if (!string.IsNullOrEmpty(c.HazardFlag)) { var hz = new Label("Hazard: " + c.HazardFlag); hz.AddToClassList("small"); hz.AddToClassList("wrap"); hz.style.color = Palette.Critical; hz.style.marginTop = 4; popover.Add(hz); }
            var actions = new VisualElement(); actions.AddToClassList("row"); actions.style.marginTop = 10;
            bool open = net.WorkOrders(selectedBp).Any(w => w.ComponentId == id && w.Status != "done");
            if (!open) actions.Add(Btn($"Dispatch fix · {Palette.Money(c.ReplaceCost)}", () => { net.Conn.Reducers.CreateWorkOrder(id, "Preventive repair / replacement"); Toast("Work order dispatched"); }, "btn-coral"));
            else actions.Add(Chip("Work order open", "pill-info"));
            actions.Add(Btn("Ask agent", () => Ask($"Explain the risk on the {c.Name.ToLowerInvariant()} and what happens downstream if we wait 12 months."), "btn-primary"));
            popover.Add(actions);
        }

        void ShowRoomPopover(uint roomId, Vector3 point)
        {
            if (!builder.RoomsById.TryGetValue(roomId, out var r)) return;
            builder.SelectedRoom = roomId;
            popover.Clear();
            popover.style.display = DisplayStyle.Flex;
            var head = new VisualElement(); head.AddToClassList("row");
            var t = new Label(r.Name); t.AddToClassList("h3"); t.style.flexGrow = 1; head.Add(t);
            head.Add(Btn("×", () => popover.style.display = DisplayStyle.None, "btn-small btn-ghost"));
            popover.Add(head);
            popover.Add(Small($"Level {r.Floor + 1} · {r.W:0.#}' × {r.D:0.#}' · {r.W * r.D:#,0} sf · {r.Ceiling:0}' ceiling · glazing {r.Glazing * 100:0}%"));
            foreach (var f in net.Findings(selectedBp).Where(f => f.RoomId == roomId))
            {
                var row = new VisualElement(); row.AddToClassList("list-row");
                var stripe = new VisualElement(); stripe.AddToClassList("sev-stripe"); stripe.style.backgroundColor = Palette.Severity(f.Severity); row.Add(stripe);
                var col = new VisualElement(); col.style.flexShrink = 1;
                var ft = new Label(f.Title); ft.AddToClassList("small"); ft.AddToClassList("bold"); ft.AddToClassList("wrap"); col.Add(ft);
                var fd = new Label($"{f.Detail} ({f.CodeRef})"); fd.AddToClassList("tiny"); fd.AddToClassList("muted"); fd.AddToClassList("wrap"); col.Add(fd);
                row.Add(col);
                popover.Add(row);
            }
            foreach (var c in net.Components(selectedBp).Where(c => c.RoomId == roomId))
            {
                var id = c.Id;
                popover.Add(Btn($"{Palette.KindLabel(c.Kind)}: {c.Name}", () => SelectComponent(id), "btn-small"));
            }
            var pinRow = new VisualElement(); pinRow.AddToClassList("row"); pinRow.style.marginTop = 10;
            var pinField = Input("Drop a shared note pin here…"); pinField.style.flexGrow = 1; pinField.style.marginRight = 6;
            pinRow.Add(pinField);
            var p = point + Vector3.up * 1.2f;
            pinRow.Add(Btn("Pin", () =>
            {
                if (string.IsNullOrWhiteSpace(pinField.value)) return;
                net.Conn.Reducers.AddAnnotation(selectedBp, p.x, p.y, p.z, pinField.value.Trim());
                pinField.value = "";
                Toast("Pin shared with everyone on this blueprint");
            }, "btn-primary"));
            popover.Add(pinRow);
            popover.Add(Btn("Ask agent about this room", () => Ask($"Review the {r.Name} for structural issues and buyer appeal."), "btn-ghost"));
        }

        // ───────────── Small UI factory helpers ─────────────

        static VisualElement Spacer() { var s = new VisualElement(); s.style.flexGrow = 1; return s; }

        static Label Section(string t) { var l = new Label(t); l.AddToClassList("section-title"); return l; }
        static Label FieldLabel(string t) { var l = new Label(t); l.AddToClassList("field-label"); return l; }
        static Label Tiny(string t) { var l = new Label(t); l.AddToClassList("kpi-label"); return l; }
        static Label Small(string t) { var l = new Label(t); l.AddToClassList("small"); l.AddToClassList("wrap"); l.style.marginTop = 2; return l; }

        static Label Chip(string t, string cls)
        {
            var l = new Label(t); l.AddToClassList("chip"); if (cls != null) l.AddToClassList(cls); return l;
        }

        static Button Btn(string text, Action onClick, string classes)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("btn");
            if (!string.IsNullOrEmpty(classes)) foreach (var c in classes.Split(' ')) b.AddToClassList(c);
            return b;
        }

        static TextField Input(string placeholder)
        {
            var t = new TextField();
            t.AddToClassList("input");
            t.textEdition.placeholder = placeholder;
            t.textEdition.hidePlaceholderOnFocus = true;
            t.RegisterCallback<FocusInEvent>(_ => IsTyping = true);
            t.RegisterCallback<FocusOutEvent>(_ => IsTyping = false);
            return t;
        }

        static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r; e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        static VisualElement Bar(float v, Color c)
        {
            var bg = new VisualElement(); bg.AddToClassList("bar-bg"); bg.style.marginTop = 4; bg.style.flexGrow = 0;
            var f = new VisualElement(); f.AddToClassList("bar-fill"); f.style.backgroundColor = c; f.style.width = Length.Percent(Mathf.Clamp01(v) * 100);
            bg.Add(f);
            return bg;
        }

        static VisualElement LabeledBar(string label, float v, Color c)
        {
            var r = new VisualElement(); r.AddToClassList("row"); r.style.marginTop = 2;
            var l = new Label(label); l.AddToClassList("tiny"); l.AddToClassList("muted"); l.style.width = 52; r.Add(l);
            var b = Bar(v, c); b.style.flexGrow = 1; b.style.marginTop = 0; r.Add(b);
            var p = new Label($"{v * 100:0}%"); p.AddToClassList("tiny"); p.AddToClassList("bold"); p.style.width = 36; p.style.unityTextAlign = TextAnchor.MiddleRight; r.Add(p);
            return r;
        }

        VisualElement Segmented(string[] options, int selected, Action<int> onPick)
        {
            var seg = new VisualElement(); seg.AddToClassList("seg");
            var buttons = new List<Button>();
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var b = new Button { text = options[i] };
                b.AddToClassList("seg-btn");
                b.EnableInClassList("seg-btn--on", i == selected);
                b.clicked += () =>
                {
                    for (int k = 0; k < buttons.Count; k++) buttons[k].EnableInClassList("seg-btn--on", k == idx);
                    onPick(idx);
                };
                buttons.Add(b);
                seg.Add(b);
            }
            return seg;
        }

        VisualElement LayerToggle(string label, bool on, Action<bool> changed)
        {
            var r = new VisualElement(); r.AddToClassList("layer-row");
            var l = new Label(label); l.AddToClassList("small"); r.Add(l);
            var t = new Toggle { value = on };
            t.RegisterValueChangedCallback(e => changed(e.newValue));
            r.Add(t);
            return r;
        }

        void Toast(string msg)
        {
            if (toast == null) return;
            toast.text = msg;
            toast.style.display = DisplayStyle.Flex;
            toast.BringToFront();
            toastUntil = Time.unscaledTime + 3.5f;
        }

        static string Initials(string n)
        {
            var parts = (n ?? "?").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant() : (n ?? "?").Substring(0, Math.Min(2, (n ?? "?").Length)).ToUpperInvariant();
        }

        static string RoleName(string r) => r == "construction_manager" ? "Construction Manager" : "Real Estate Agent";
        static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";

        static Texture2D Gradient(Color[] stops)
        {
            const int W = 256, H = 144;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float t = Mathf.Clamp01((x / (float)W) * 0.7f + (1 - y / (float)H) * 0.3f);
                    float f = t * (stops.Length - 1);
                    int i = Mathf.Min(stops.Length - 2, Mathf.FloorToInt(f));
                    var c = Color.Lerp(stops[i], stops[i + 1], f - i);
                    // soft radial highlight
                    float dx = x / (float)W - 0.25f, dy = y / (float)H - 0.7f;
                    c = Color.Lerp(c, Color.white, Mathf.Clamp01(0.25f - (dx * dx + dy * dy)) * 0.9f);
                    px[y * W + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
