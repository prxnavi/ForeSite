using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Keystone.Voice;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Component = SpacetimeDB.Types.Component;

namespace CbreTwin
{
    /// <summary>
    /// Runtime UI Toolkit interface.
    /// Pages: Overview (3D model + simple panels), Walkthrough (immersive tour), Agent (ask about anything).
    /// Voice (Meta Voice SDK): hold V or tap the mic to speak a note, a command, or a question.
    /// </summary>
    public class TwinUI : MonoBehaviour
    {
        public static bool IsTyping;

        BlueprintBuilder builder;
        CameraRig rig;
        PresenceSync presence;
        TwinConnection net;

        UIDocument doc;
        VisualElement root, loginLayer, appLayer, tourLayer, overviewPage, agentPage;
        VisualElement markerLayer, labelLayer, popover, tiles, drawer, drawerContent, tabBar, propList, levelHost, avatars, navHost;
        Label loginStatus, loginCaption, propTitle, buildLine, toast, simLabel;
        TextField nameField, companyField, serverField, dbField;
        Button simBtn, topMic;
        VisualElement roleBuilder, roleBroker;
        VisualElement listening;
        Label listeningText;

        string role = "real_estate_agent";
        uint selectedBp;
        string page = "overview";
        string drawerTab = "issues";
        bool drawerOpen = true;
        bool waitingForProfile;
        string pendingName;
        bool showLabels = true, showTeammates = true, showcase;
        float nextRefresh, lastOverlayRefresh, toastUntil;
        ulong lastAlertSeen;
        bool agentBusy;
        readonly List<(VisualElement el, uint id)> markerEls = new();
        readonly List<(VisualElement el, uint id)> roomLabels = new();
        readonly Dictionary<string, VisualElement> nameTags = new();
        readonly List<(VisualElement el, Vector3 pos)> pinEls = new();

        public void Init(BlueprintBuilder b, CameraRig r, PresenceSync p) { builder = b; rig = r; presence = p; }

        // ───────────────────────── Setup ─────────────────────────

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
            var go = new GameObject("UI");
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
            BuildTour();
            appLayer.style.display = DisplayStyle.None;
            tourLayer.style.display = DisplayStyle.None;

            listening = new VisualElement(); listening.AddToClassList("listening"); listening.pickingMode = PickingMode.Ignore;
            var ld = new VisualElement(); ld.AddToClassList("listening-dot"); listening.Add(ld);
            listeningText = L("Listening…", "listening-text"); listening.Add(listeningText);
            listening.style.display = DisplayStyle.None;
            root.Add(listening);

            toast = new Label(); toast.AddToClassList("toast"); toast.style.display = DisplayStyle.None; toast.pickingMode = PickingMode.Ignore;
            root.Add(toast);
            fade = new VisualElement(); fade.AddToClassList("fill"); fade.pickingMode = PickingMode.Ignore;
            fade.style.backgroundColor = new Color(0.07f, 0.06f, 0.16f, 1f);
            fade.style.opacity = 0; fade.style.display = DisplayStyle.None;
            root.Add(fade);

            rig.IsOverUI = IsOverUI;
            rig.OnClick = HandleWorldClick;
            rig.OnGuidedArrive = OnTourArrive;
            rig.OnTeleportRequest = p => FadeThen(() => rig.Teleport(p));
            rig.OnReticleSelect = OnReticleSelect;
            rig.Cam.backgroundColor = Palette.Sky3D;

            net.OnStatus += s =>
            {
                if (loginStatus == null) return;
                loginStatus.text = s;
                loginStatus.EnableInClassList("login-status--err", s.StartsWith("Could not") || s.Contains("error"));
            };
            net.OnReady += OnNetReady;
            net.OnReducerError += Toast;

            SetupVoice();
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
            if (loginLayer.style.display == DisplayStyle.Flex && builder.BlueprintId == 0) StartShowcase();
            if (waitingForProfile && pendingName != null) Register();
        }

        void StartShowcase()
        {
            var bp = net.Blueprint(4) ?? net.Blueprints().FirstOrDefault();
            if (bp == null) return;
            showcase = true;
            builder.ShowSystems = builder.ShowCascade = builder.ShowStructural = false;
            builder.PlanMode = false;
            builder.Build(bp.Id);
            rig.SetMode(ViewMode.Orbit, builder.WorldBounds);
            var b = builder.WorldBounds;
            rig.Frame(new Bounds(b.center + Vector3.left * b.extents.x * 0.45f, b.size * 0.9f));
            rig.AutoRotate = true;
            var loc = net.Location(bp.LocationId);
            loginCaption.text = $"{bp.Name}  ·  {loc?.City}, {loc?.State}";
        }

        // ───────────────────────── Sign-in ─────────────────────────

        void BuildLogin()
        {
            loginLayer = new VisualElement(); loginLayer.AddToClassList("fill"); loginLayer.pickingMode = PickingMode.Ignore;
            root.Add(loginLayer);
            var panel = new VisualElement(); panel.AddToClassList("login-panel");
            var top = new VisualElement();
            top.Add(L("Keystone", "brand"));
            top.Add(L("Welcome", "login-title"));
            top.Add(L("See the house. Know the risks. Price it right.", "login-lede"));
            top.Add(L("Your name", "field-label"));
            nameField = Field("");
            top.Add(nameField);
            top.Add(L("Company", "field-label"));
            companyField = Field("");
            top.Add(companyField);
            top.Add(L("I'm a", "field-label"));
            var roles = new VisualElement(); roles.AddToClassList("role-row");
            roleBuilder = RoleCard("CM", "Builder", Palette.Amber, "construction_manager");
            roleBroker = RoleCard("RE", "Agent", Palette.Violet, "real_estate_agent");
            roleBroker.style.marginRight = 0;
            roles.Add(roleBuilder); roles.Add(roleBroker);
            top.Add(roles);
            SetRole(role);
            top.Add(Btn("Let's go", OnEnterClicked, "btn-primary btn-block"));
            nameField.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) OnEnterClicked(); }, TrickleDown.TrickleDown);
            loginStatus = L("Connecting…", "login-status");
            top.Add(loginStatus);
            panel.Add(top);

            var bottom = new VisualElement();
            var adv = new VisualElement { style = { display = DisplayStyle.None } };
            var advBtn = Btn("Server settings", () => adv.style.display = adv.style.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None, "btn-ghost btn-sm");
            advBtn.style.alignSelf = Align.FlexStart; advBtn.style.marginLeft = 0;
            bottom.Add(advBtn);
            adv.Add(L("Server", "field-label"));
            serverField = Field("http://127.0.0.1:3000"); serverField.value = net != null ? net.ServerUrl : TwinConnection.DefaultServer;
            adv.Add(serverField);
            adv.Add(L("Database", "field-label"));
            dbField = Field("cbre-twin"); dbField.value = net != null ? net.DbName : TwinConnection.DefaultDb;
            adv.Add(dbField);
            bottom.Add(adv);
            panel.Add(bottom);
            loginLayer.Add(panel);
            loginCaption = L("", "login-caption"); loginCaption.pickingMode = PickingMode.Ignore;
            loginLayer.Add(loginCaption);
        }

        VisualElement RoleCard(string badge, string name, Color c, string key)
        {
            var o = new VisualElement(); o.AddToClassList("role");
            var b = L(badge, "role-badge"); b.style.backgroundColor = c; o.Add(b);
            o.Add(L(name, "h3"));
            o.RegisterCallback<ClickEvent>(_ => SetRole(key));
            return o;
        }

        void SetRole(string r)
        {
            role = r == "construction_manager" ? r : "real_estate_agent";
            roleBuilder?.EnableInClassList("role--on", role == "construction_manager");
            roleBroker?.EnableInClassList("role--on", role == "real_estate_agent");
        }

        void OnEnterClicked()
        {
            var name = nameField.value?.Trim();
            if (string.IsNullOrEmpty(name)) { loginStatus.text = "Type your name first."; loginStatus.AddToClassList("login-status--err"); return; }
            pendingName = name;
            waitingForProfile = true;
            if (serverField.value.Trim() != net.ServerUrl || dbField.value.Trim() != net.DbName || !net.Ready) { net.Connect(serverField.value, dbField.value); return; }
            Register();
        }

        void Register()
        {
            if (!net.Ready) return;
            loginStatus.text = "Signing in…";
            net.Conn.Reducers.RegisterUser(pendingName, role, string.IsNullOrWhiteSpace(companyField.value) ? "Independent" : companyField.value.Trim());
        }

        // ───────────────────────── Shell ─────────────────────────

        void BuildApp()
        {
            appLayer = new VisualElement(); appLayer.AddToClassList("fill"); appLayer.pickingMode = PickingMode.Ignore;
            root.Add(appLayer);

            var top = new VisualElement(); top.AddToClassList("topbar");
            top.Add(L("Keystone", "brand"));
            navHost = new VisualElement(); navHost.AddToClassList("nav");
            top.Add(navHost);
            top.Add(Spacer());
            simLabel = L("", "soft"); simLabel.style.marginRight = 6;
            top.Add(simLabel);
            simBtn = Btn("Simulate", () => net.Conn.Reducers.SetSimulation(!(net.Sim?.Running ?? false)), "btn-teal");
            top.Add(simBtn);
            top.Add(Btn("+6 mo", () => net.Conn.Reducers.StepSimulation(6), null));
            topMic = new Button(() => ToggleVoice("auto")) { text = "MIC" };
            topMic.AddToClassList("mic");
            topMic.tooltip = "Speak a note, a command or a question (or hold V)";
            top.Add(topMic);
            var sp = new VisualElement(); sp.style.width = 20; top.Add(sp);
            avatars = new VisualElement(); avatars.AddToClassList("row");
            top.Add(avatars);
            top.Add(Btn("Sign out", SignOut, "btn-ghost btn-sm"));
            appLayer.Add(top);

            overviewPage = new VisualElement(); overviewPage.AddToClassList("page"); overviewPage.pickingMode = PickingMode.Ignore;
            appLayer.Add(overviewPage);
            BuildOverview();
            agentPage = new VisualElement(); agentPage.AddToClassList("agent-page");
            appLayer.Add(agentPage);
            BuildAgentPage();
        }

        void RebuildNav()
        {
            navHost.Clear();
            foreach (var (key, title) in new[] { ("overview", "Overview"), ("walk", "Walkthrough"), ("agent", "Ask the agent") })
            {
                var k = key;
                var b = new Button(() => GoTo(k)) { text = title };
                b.AddToClassList("nav-btn");
                b.EnableInClassList("nav-btn--on", page == key);
                navHost.Add(b);
            }
        }

        void GoTo(string p)
        {
            if (p == "walk") { EnterTour(0); return; }
            page = p;
            overviewPage.style.display = p == "overview" ? DisplayStyle.Flex : DisplayStyle.None;
            agentPage.style.display = p == "agent" ? DisplayStyle.Flex : DisplayStyle.None;
            RebuildNav();
            if (p == "agent") { RefreshAgentPage(true); }
        }

        // ───────────────────────── Overview ─────────────────────────

        void BuildOverview()
        {
            var rail = new ScrollView(ScrollViewMode.Vertical); rail.AddToClassList("rail");
            rail.Add(Lbl("HOMES", 12));
            propList = new VisualElement(); rail.Add(propList);
            var lv = Lbl("FLOOR", 10); lv.style.marginTop = 18; rail.Add(lv);
            levelHost = new VisualElement(); rail.Add(levelHost);
            var shw = Lbl("SHOW", 6); shw.style.marginTop = 18; rail.Add(shw);
            rail.Add(Switch("Problem areas", true, v => { builder.ShowStructural = v; builder.RefreshOverlays(); }));
            rail.Add(Switch("Systems", true, v => { builder.ShowSystems = v; builder.RefreshOverlays(); RebuildMarkers(); }));
            rail.Add(Switch("How damage spreads", true, v => { builder.ShowCascade = v; builder.RefreshOverlays(); }));
            rail.Add(Switch("Room names", true, v => { showLabels = v; RebuildMarkers(); }));
            rail.Add(Switch("Teammates", true, v => showTeammates = v));
            rail.Add(Switch("Roof", false, v => { builder.ShowRoof = v; RebuildScene(false); }));
            var vw = Lbl("VIEW", 10); vw.style.marginTop = 18; rail.Add(vw);
            rail.Add(Segmented(new[] { "3D", "Floor plan" }, 0, i =>
            {
                builder.PlanMode = i == 1;
                RebuildScene(false);
                rig.SetMode(i == 1 ? ViewMode.Plan : ViewMode.Orbit, builder.WorldBounds);
            }));
            overviewPage.Add(rail);

            var center = new VisualElement(); center.AddToClassList("center"); center.pickingMode = PickingMode.Ignore;
            var view = new VisualElement(); view.AddToClassList("viewport"); view.pickingMode = PickingMode.Ignore;
            labelLayer = new VisualElement(); labelLayer.AddToClassList("fill"); labelLayer.pickingMode = PickingMode.Ignore;
            markerLayer = new VisualElement(); markerLayer.AddToClassList("fill"); markerLayer.pickingMode = PickingMode.Ignore;
            view.Add(labelLayer); view.Add(markerLayer);
            tiles = new VisualElement(); tiles.AddToClassList("tiles"); tiles.pickingMode = PickingMode.Ignore;
            view.Add(tiles);
            propTitle = L("", "prop-title"); propTitle.pickingMode = PickingMode.Ignore; view.Add(propTitle);
            buildLine = L("", "build-line"); buildLine.pickingMode = PickingMode.Ignore; view.Add(buildLine);
            popover = new VisualElement(); popover.AddToClassList("popover"); popover.style.display = DisplayStyle.None;
            view.Add(popover);
            center.Add(view);
            drawer = new VisualElement(); drawer.AddToClassList("drawer");
            tabBar = new VisualElement(); tabBar.AddToClassList("tabs"); drawer.Add(tabBar);
            drawerContent = new VisualElement(); drawerContent.AddToClassList("drawer-content"); drawer.Add(drawerContent);
            center.Add(drawer);
            overviewPage.Add(center);
        }

        void EnterApp()
        {
            waitingForProfile = false;
            showcase = false;
            rig.AutoRotate = false;
            builder.ShowSystems = builder.ShowCascade = builder.ShowStructural = true;
            loginLayer.style.display = DisplayStyle.None;
            appLayer.style.display = DisplayStyle.Flex;
            var me = net.Me;
            role = me?.Role ?? role;
            uint start = me != null && me.SelectedBlueprint != 0 ? me.SelectedBlueprint : (role == "construction_manager" ? 4u : 1u);
            GoTo("overview");
            SelectBlueprint(start);
        }

        void SignOut()
        {
            if (rig.Mode == ViewMode.Tour) ExitTour();
            appLayer.style.display = DisplayStyle.None;
            loginLayer.style.display = DisplayStyle.Flex;
            selectedBp = 0;
            RebuildMarkers();
            builder.Clear();
            StartShowcase();
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
            builder.SelectedRoom = 0;
            popover.style.display = DisplayStyle.None;
            RebuildScene(true);
            RebuildProps();
            RebuildLevels();
            agentTopic = null; agentItem = null;
            RefreshAll();
        }

        void RebuildScene(bool reframe)
        {
            builder.Build(selectedBp);
            if (reframe || rig.Mode == ViewMode.Plan) rig.SetMode(rig.Mode == ViewMode.Tour ? ViewMode.Orbit : rig.Mode, builder.WorldBounds);
            rig.Cam.backgroundColor = builder.PlanMode ? Palette.PlanPaper : Palette.Sky3D;
            RebuildMarkers();
        }

        void RebuildLevels()
        {
            levelHost.Clear();
            var names = new List<string> { "All" };
            for (int i = 0; i < builder.FloorCount; i++) names.Add($"{i + 1}");
            levelHost.Add(Segmented(names.ToArray(), builder.MaxFloor >= builder.FloorCount ? 0 : builder.MaxFloor + 1, i =>
            {
                builder.MaxFloor = i == 0 ? 99 : i - 1;
                builder.ApplyFloorVisibility();
                builder.RefreshOverlays();
                RebuildMarkers();
            }));
        }

        void RebuildProps()
        {
            propList.Clear();
            foreach (var b in net.Blueprints())
            {
                var loc = net.Location(b.LocationId);
                var card = new VisualElement(); card.AddToClassList("prop");
                card.EnableInClassList("prop--on", b.Id == selectedBp);
                var stripe = new VisualElement(); stripe.AddToClassList("prop-color"); stripe.style.backgroundColor = Palette.Hex(b.AccentColor);
                card.Add(stripe);
                var body = new VisualElement(); body.AddToClassList("prop-body");
                body.Add(L(b.Name, "prop-name"));
                body.Add(L($"{loc?.City}  ·  {(b.Stage == "existing" ? b.YearBuilt.ToString() : "New build")}", "prop-meta"));
                body.Add(L("WALKTHROUGH READY", "walk-tag"));
                card.Add(body);
                var id = b.Id;
                card.RegisterCallback<ClickEvent>(_ => SelectBlueprint(id));
                propList.Add(card);
            }
        }

        // ───────────────────────── Update ─────────────────────────

        void Update()
        {
            if (net == null || toast == null) return;
            if (waitingForProfile && net.Ready)
            {
                var me = net.Me;
                if (me != null && me.Name == pendingName && me.Role == role) EnterApp();
            }
            if (toast.style.display == DisplayStyle.Flex && Time.unscaledTime > toastUntil) toast.style.display = DisplayStyle.None;
            UpdateFade();
            UpdateVoiceKeys();
            if (rig.Mode == ViewMode.Tour) UpdateTour();
            if (selectedBp == 0 || (appLayer.style.display == DisplayStyle.None && rig.Mode != ViewMode.Tour)) return;

            float prog = Mathf.Clamp01((Time.time - builder.BuildStartTime) / Mathf.Max(0.1f, builder.BuildDuration));
            buildLine.text = prog < 1 ? $"Building model  {prog * 100:0}%" : $"{builder.RoomsById.Count} rooms  ·  {builder.FloorCount} floor{(builder.FloorCount > 1 ? "s" : "")}";

            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.25f;
            var d = net.DirtyFlags;
            if (d == Dirty.None) return;
            net.DirtyFlags = Dirty.None;
            if ((d & (Dirty.Risk | Dirty.Structural)) != 0 && rig.Mode != ViewMode.Tour)
            {
                if (Time.unscaledTime - lastOverlayRefresh > 0.8f) { lastOverlayRefresh = Time.unscaledTime; builder.RefreshOverlays(); RebuildMarkers(); }
                else net.DirtyFlags |= d & (Dirty.Risk | Dirty.Structural);
            }
            if ((d & (Dirty.Risk | Dirty.Structural | Dirty.Market)) != 0) RefreshTiles();
            if ((d & (Dirty.Risk | Dirty.Structural | Dirty.Market | Dirty.Ops | Dirty.Annotations)) != 0) RefreshDrawer();
            if ((d & Dirty.Agent) != 0) { RefreshAgentPage(false); RefreshTourSide(); }
            if ((d & Dirty.Users) != 0) RefreshAvatars();
            if ((d & Dirty.Sim) != 0) RefreshSim();
            if ((d & Dirty.Annotations) != 0) { RebuildMarkers(); if (rig.Mode == ViewMode.Tour) BuildTourMarkers(); }
            if ((d & Dirty.Ops) != 0) CheckAlerts();
        }

        void RefreshAll()
        {
            RefreshTiles();
            RefreshDrawer();
            RefreshAvatars();
            RefreshSim();
            RefreshAgentPage(true);
            lastAlertSeen = net.Alerts(selectedBp).FirstOrDefault()?.Id ?? 0;
        }

        void CheckAlerts()
        {
            var a = net.Alerts(selectedBp).FirstOrDefault();
            if (a == null || a.Id <= lastAlertSeen) return;
            lastAlertSeen = a.Id;
            if (a.Level != "info") Toast(a.Message);
        }

        void RefreshSim()
        {
            var s = net.Sim;
            if (s == null) return;
            simBtn.text = s.Running ? "Pause" : "Simulate";
            simBtn.EnableInClassList("btn-coral", s.Running);
            simBtn.EnableInClassList("btn-teal", !s.Running);
            double y = s.BaseYear + s.SimMonths / 12.0;
            var names = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            simLabel.text = s.SimMonths == 0 ? "" : $"{names[Mathf.Clamp((int)((y % 1) * 12), 0, 11)]} {(int)y}";
        }

        void RefreshAvatars()
        {
            avatars.Clear();
            foreach (var u in net.OnlineUsers().Take(5))
            {
                var a = L(Initials(u.Name), "avatar"); a.style.backgroundColor = Palette.Hex(u.Color); a.tooltip = u.Name;
                avatars.Add(a);
            }
        }

        void RefreshTiles()
        {
            var b = net.Blueprint(selectedBp);
            if (b == null) return;
            propTitle.text = b.Name;
            tiles.Clear();
            var s = net.Summary(selectedBp);
            var v = net.Valuation(selectedBp);
            if (s != null) tiles.Add(Tile("Structure", $"{s.IntegrityScore:0}/100", s.IntegrityScore >= 80 ? Palette.Teal : s.IntegrityScore >= 55 ? Palette.Amber : Palette.Coral));
            if (v != null)
            {
                tiles.Add(Tile("Risk this year", Palette.Money(v.ExpectedLoss12Mo), Palette.Pink));
                tiles.Add(Tile(b.Stage == "existing" ? "List price" : "Value when done", Palette.Money(v.ListPrice), Palette.Violet));
                tiles.Add(Tile("Days to sell", $"{v.DaysOnMarket:0}", Palette.SkyBlue));
            }
        }

        VisualElement Tile(string label, string value, Color c)
        {
            var t = new VisualElement(); t.AddToClassList("tile"); t.style.backgroundColor = c; t.pickingMode = PickingMode.Ignore;
            t.Add(L(label, "tile-label"));
            t.Add(L(value, "tile-value"));
            return t;
        }

        // ───────────────────────── Drawer (short, scannable) ─────────────────────────

        static readonly (string key, string title)[] Tabs =
            { ("issues", "Issues"), ("systems", "Systems"), ("value", "Value"), ("tasks", "Tasks"), ("notes", "Notes") };

        void RefreshDrawer()
        {
            if (drawer == null) return;
            tabBar.Clear();
            foreach (var (key, title) in Tabs)
            {
                var k = key;
                var label = title;
                if (key == "notes") { var n = net.Notes(selectedBp).Count; if (n > 0) label += $"  {n}"; }
                var t = new Button(() => { drawerTab = k; drawerOpen = true; RefreshDrawer(); }) { text = label };
                t.AddToClassList("tab");
                t.EnableInClassList("tab--on", drawerTab == key && drawerOpen);
                tabBar.Add(t);
            }
            tabBar.Add(Spacer());
            tabBar.Add(Btn(drawerOpen ? "Hide" : "Show", () => { drawerOpen = !drawerOpen; RefreshDrawer(); }, "btn-ghost btn-sm"));
            drawer.EnableInClassList("drawer--collapsed", !drawerOpen);
            var offsets = drawerContent.Query<ScrollView>().ToList().Select(sv => sv.scrollOffset).ToList();
            drawerContent.Clear();
            drawerContent.style.display = drawerOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (!drawerOpen) return;
            switch (drawerTab)
            {
                case "issues": DrawIssues(); break;
                case "systems": DrawSystems(); break;
                case "value": DrawValue(); break;
                case "tasks": DrawTasks(); break;
                default: DrawNotes(); break;
            }
            var svs = drawerContent.Query<ScrollView>().ToList();
            drawerContent.schedule.Execute(() => { for (int i = 0; i < svs.Count && i < offsets.Count; i++) svs[i].scrollOffset = offsets[i]; }).ExecuteLater(1);
        }

        VisualElement Card(float width, float grow = 0)
        {
            var c = new VisualElement(); c.AddToClassList("card");
            if (width > 0) { c.style.width = width; c.style.flexShrink = 0; }
            if (grow > 0) { c.style.flexGrow = grow; c.style.flexBasis = 0; }
            drawerContent.Add(c);
            return c;
        }

        ScrollView CardList(VisualElement card, string title)
        {
            var h = L(title, "h3"); h.style.marginBottom = 6; card.Add(h);
            var sv = new ScrollView(ScrollViewMode.Vertical); sv.style.flexGrow = 1;
            card.Add(sv);
            return sv;
        }

        void DrawIssues()
        {
            var s = net.Summary(selectedBp);
            var c1 = Card(240);
            c1.Add(L("STRUCTURE SCORE", "label"));
            var n = L(s != null ? $"{s.IntegrityScore:0}" : "—", "huge");
            n.style.color = s == null ? Palette.Muted : s.IntegrityScore >= 80 ? Palette.Teal : s.IntegrityScore >= 55 ? Palette.Amber : Palette.Coral;
            c1.Add(n);
            if (s != null)
            {
                var r = new VisualElement(); r.AddToClassList("row"); r.style.flexWrap = Wrap.Wrap; r.style.marginTop = 6;
                if (s.CriticalCount > 0) r.Add(Pill($"{s.CriticalCount} urgent", "pill-coral"));
                if (s.WatchCount > 0) r.Add(Pill($"{s.WatchCount} to check", "pill-amber"));
                r.Add(Pill($"{s.OkCount} good", "pill-teal"));
                c1.Add(r);
            }
            var list = CardList(Card(0, 1), "What to look at");
            foreach (var f in net.Findings(selectedBp).Where(f => f.Severity != "ok").OrderByDescending(f => f.Severity == "critical" ? 1 : 0).ThenByDescending(f => f.FixCost))
            {
                var row = new VisualElement(); row.AddToClassList("line");
                row.Add(Dot(f.Severity == "critical" ? "dot-coral" : "dot-amber"));
                var t = L(f.Title, "text"); t.style.flexGrow = 1; t.style.flexShrink = 1; row.Add(t);
                if (f.FixCost > 0) row.Add(Pill(Palette.Money(f.FixCost), "pill-violet"));
                var rid = f.RoomId;
                row.RegisterCallback<ClickEvent>(_ => FocusRoom(rid));
                list.Add(row);
            }
        }

        void DrawSystems()
        {
            var comps = net.Components(selectedBp).ToDictionary(c => c.Id);
            var open = new HashSet<uint>(net.WorkOrders(selectedBp).Where(w => w.Status != "done").Select(w => w.ComponentId));
            var list = CardList(Card(0, 1.6f), "Chance of failing this year");
            foreach (var r in net.Risks(selectedBp))
            {
                if (!comps.TryGetValue(r.ComponentId, out var c)) continue;
                var row = new VisualElement(); row.AddToClassList("line");
                var pct = L($"{r.CascadeProb * 100:0}%", "pct"); pct.style.backgroundColor = Palette.Tier(r.Tier); row.Add(pct);
                var t = L(c.Name, "text"); t.style.flexGrow = 1; row.Add(t);
                var cost = L($"saves {Palette.Money(r.PreventionValue)}", "soft"); cost.style.width = 130; row.Add(cost);
                var cid = c.Id;
                if (open.Contains(c.Id)) row.Add(Pill("Booked", "pill-teal"));
                else row.Add(Btn("Fix", () => { net.Conn.Reducers.CreateWorkOrder(cid, "Preventive repair or replacement"); Toast($"Booked: {c.Name}"); }, "btn-coral btn-sm"));
                row.RegisterCallback<ClickEvent>(e => { if (e.target is Button) return; SelectComponent(cid); });
                list.Add(row);
            }
            var paths = CardList(Card(360), "How damage spreads");
            foreach (var p in net.Paths(selectedBp).Take(4))
            {
                var row = new VisualElement(); row.style.marginBottom = 12;
                row.Add(L(p.Label, "text"));
                row.Add(L($"{p.Probability * 100:0.0}% chance  ·  {Palette.Money(p.Loss)}", "soft"));
                paths.Add(row);
            }
        }

        void DrawValue()
        {
            var v = net.Valuation(selectedBp);
            var b = net.Blueprint(selectedBp);
            if (v == null || b == null) return;
            var c1 = Card(280);
            c1.Add(L(b.Stage == "existing" ? "LIST AT" : "WORTH WHEN DONE", "label"));
            var p = L(Palette.Money(v.ListPrice), "big"); p.style.color = Palette.Violet; c1.Add(p);
            c1.Add(L($"{Palette.Money(v.LowPrice)} – {Palette.Money(v.HighPrice)}", "soft"));
            if (v.ValueUnlocked > 0)
            {
                var u = L($"Fix first and gain {Palette.Money(v.ValueUnlocked)}", "h3"); u.style.color = Palette.Teal; u.style.marginTop = 14; u.AddToClassList("wrap");
                c1.Add(u);
            }
            var feats = CardList(Card(0, 1), "What sells here");
            var wrap = new VisualElement(); wrap.AddToClassList("row"); wrap.style.flexWrap = Wrap.Wrap;
            foreach (var f in net.Features(selectedBp).Take(10)) wrap.Add(Pill(f.Name, f.Present ? "pill-teal" : "pill-gray"));
            feats.Add(wrap);
            var up = L("Best upgrades", "h3"); up.style.marginTop = 10; up.style.marginBottom = 4; feats.Add(up);
            foreach (var u in net.Upgrades(selectedBp).Take(3))
            {
                var row = new VisualElement(); row.AddToClassList("line");
                var t = L(u.Name, "text"); t.style.flexGrow = 1; row.Add(t);
                row.Add(Pill($"{u.Roi:0.0}×", u.Roi >= 1 ? "pill-teal" : "pill-amber"));
                feats.Add(row);
            }
            var locs = CardList(Card(320), "Same house, other cities");
            var lp = net.LocationPricing(selectedBp);
            double max = lp.Count > 0 ? lp.Max(x => x.ListPrice) : 1;
            foreach (var x in lp)
            {
                bool here = x.LocationId == b.LocationId;
                var row = new VisualElement(); row.style.marginBottom = 8;
                var top = new VisualElement(); top.AddToClassList("row");
                var n = L(x.LocationName.Split('(')[0].Trim(), here ? "h3" : "text"); n.style.flexGrow = 1; top.Add(n);
                top.Add(L(Palette.Money(x.ListPrice), "h3"));
                row.Add(top);
                row.Add(Bar((float)(x.ListPrice / max), here ? Palette.Violet : Palette.SkyBlue));
                locs.Add(row);
            }
        }

        void DrawTasks()
        {
            var list = CardList(Card(0, 1.2f), "Work orders");
            var orders = net.WorkOrders(selectedBp);
            if (orders.Count == 0) list.Add(L("Nothing booked yet. Tap Fix on a system, or ask the agent.", "soft"));
            foreach (var w in orders)
            {
                var row = new VisualElement(); row.AddToClassList("line");
                row.Add(Dot(w.Status == "done" ? "dot-teal" : w.Status == "in_progress" ? "dot-amber" : "dot-gray"));
                var t = L(w.Title.Split(':')[0], "text"); t.style.flexGrow = 1; row.Add(t);
                if (w.Source == "agent") row.Add(Pill("by agent", "pill-violet"));
                if (w.Status != "done") { var id = w.Id; row.Add(Btn(w.Status == "dispatched" ? "Start" : "Done", () => net.Conn.Reducers.AdvanceWorkOrder(id), "btn-sm")); }
                else row.Add(Pill("Done", "pill-teal"));
                list.Add(row);
            }
            var al = CardList(Card(0, 1), "Alerts");
            var alerts = net.Alerts(selectedBp);
            if (alerts.Count == 0) al.Add(L("Press Simulate to watch the house age.", "soft"));
            foreach (var a in alerts.Take(20))
            {
                var row = new VisualElement(); row.AddToClassList("line");
                row.Add(Dot(a.Level == "critical" ? "dot-coral" : a.Level == "warn" ? "dot-amber" : "dot-teal"));
                var m = L(a.Message, "text"); m.style.flexShrink = 1; row.Add(m);
                al.Add(row);
            }
        }

        void DrawNotes()
        {
            var c = Card(0, 1);
            var head = new VisualElement(); head.AddToClassList("row"); head.style.marginBottom = 8;
            var h = L("Notes", "h3"); h.style.flexGrow = 1; head.Add(h);
            head.Add(L(VoiceHub.Ready ? "Hold V to speak a note" : "Type a note, or set up voice in Ask the agent", "soft"));
            c.Add(head);
            var add = new VisualElement(); add.AddToClassList("row"); add.style.marginBottom = 8;
            var f = Field("Write a note for this home"); f.style.flexGrow = 1; add.Add(f);
            add.Add(Btn("Add", () => { SaveNote(f.value, "text"); f.value = ""; }, "btn-primary"));
            var mic = new Button(() => ToggleVoice("note")) { text = "MIC" }; mic.AddToClassList("mic"); add.Add(mic);
            c.Add(add);
            var sv = new ScrollView(ScrollViewMode.Vertical); sv.style.flexGrow = 1; c.Add(sv);
            var notes = net.Notes(selectedBp);
            if (notes.Count == 0) sv.Add(L("No notes yet.", "soft"));
            foreach (var n in notes)
            {
                var row = new VisualElement(); row.AddToClassList("line");
                var who = L(Initials(n.AuthorName), "avatar"); who.style.backgroundColor = Palette.Hex(n.Color); who.style.marginLeft = 0; who.style.marginRight = 12; row.Add(who);
                var t = L(n.Text, "text"); t.style.flexGrow = 1; t.style.flexShrink = 1; row.Add(t);
                var room = builder.RoomsById.TryGetValue(n.RoomId, out var r) ? r.Name : "";
                if (room != "") row.Add(Pill(room, "pill-sky"));
                if (n.Source == "voice") row.Add(Pill("spoken", "pill-coral"));
                var id = n.Id;
                row.Add(Btn("Remove", () => net.Conn.Reducers.DeleteNote(id), "btn-ghost btn-sm"));
                sv.Add(row);
            }
        }

        // ───────────────────────── Agent page ─────────────────────────

        VisualElement topicGrid, itemHost, questionHost, chatBox, stepsBox, thinkingRow, voiceCard, sourcesBox;
        ScrollView chatScroll, stepsScroll;
        TextField askField, tokenField;
        Label agentStatus;
        string agentTopic, agentItem;
        int lastMsgCount = -1;

        static readonly (string key, string title, string color)[] Topics =
        {
            ("structure", "Structure", "#FF7A45"), ("systems", "What breaks", "#FF4FA3"), ("price", "Price", "#6D5DFC"),
            ("selling", "Selling points", "#12C2A4"), ("build", "Build plan", "#FFB020"), ("room", "A room", "#2D9CFF"),
            ("system", "A system", "#9B5DE5"), ("notes", "Our notes", "#F15BB5"),
        };

        void BuildAgentPage()
        {
            var left = new ScrollView(ScrollViewMode.Vertical); left.AddToClassList("agent-left");
            left.Add(L("Ask about", "h2"));
            var sub = L("Pick a topic, then a question.", "soft"); sub.style.marginBottom = 14; left.Add(sub);
            topicGrid = new VisualElement(); topicGrid.AddToClassList("topic-grid"); left.Add(topicGrid);
            itemHost = new VisualElement(); itemHost.AddToClassList("row"); itemHost.style.flexWrap = Wrap.Wrap; itemHost.style.marginTop = 6; left.Add(itemHost);
            questionHost = new VisualElement(); questionHost.style.marginTop = 8; left.Add(questionHost);
            agentPage.Add(left);

            var center = new VisualElement(); center.AddToClassList("agent-center");
            var head = new VisualElement(); head.AddToClassList("chat-head");
            head.Add(L("Keystone agent", "h2"));
            agentStatus = L("Nemotron team: structure, operations and market", "soft"); head.Add(agentStatus);
            center.Add(head);
            chatScroll = new ScrollView(ScrollViewMode.Vertical); chatScroll.AddToClassList("chat"); chatBox = chatScroll.contentContainer;
            center.Add(chatScroll);
            thinkingRow = new VisualElement(); thinkingRow.AddToClassList("thinking"); thinkingRow.style.display = DisplayStyle.None;
            center.Add(thinkingRow);
            var comp = new VisualElement(); comp.AddToClassList("composer");
            askField = Field("Ask anything about this home");
            askField.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Ask(askField.value); askField.value = ""; e.StopPropagation(); } }, TrickleDown.TrickleDown);
            comp.Add(askField);
            comp.Add(Btn("Send", () => { Ask(askField.value); askField.value = ""; }, "btn-primary"));
            var mic = new Button(() => ToggleVoice("ask")) { text = "MIC" }; mic.AddToClassList("mic"); mic.tooltip = "Ask out loud";
            comp.Add(mic);
            center.Add(comp);
            agentPage.Add(center);

            var right = new ScrollView(ScrollViewMode.Vertical); right.AddToClassList("agent-right");
            var stepsCard = new VisualElement(); stepsCard.AddToClassList("card"); stepsCard.style.marginRight = 0;
            stepsCard.Add(L("What the agents did", "h3"));
            stepsScroll = new ScrollView(ScrollViewMode.Vertical); stepsScroll.style.maxHeight = 380; stepsScroll.style.marginTop = 10;
            stepsBox = stepsScroll.contentContainer;
            stepsCard.Add(stepsScroll);
            right.Add(stepsCard);
            voiceCard = new VisualElement(); voiceCard.AddToClassList("card"); voiceCard.style.marginRight = 0;
            right.Add(voiceCard);
            var srcCard = new VisualElement(); srcCard.AddToClassList("card"); srcCard.style.marginRight = 0;
            var srcHead = new VisualElement(); srcHead.AddToClassList("row");
            var sh = L("Data sources", "h3"); sh.style.flexGrow = 1; srcHead.Add(sh);
            sourcesBox = new VisualElement { style = { display = DisplayStyle.None } };
            srcHead.Add(Btn("Show", () => sourcesBox.style.display = sourcesBox.style.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None, "btn-ghost btn-sm"));
            srcCard.Add(srcHead);
            srcCard.Add(sourcesBox);
            right.Add(srcCard);
            agentPage.Add(right);
            agentPage.style.display = DisplayStyle.None;
        }

        void RefreshAgentPage(bool full)
        {
            if (agentPage == null || selectedBp == 0) return;
            if (full)
            {
                topicGrid.Clear();
                foreach (var (key, title, color) in Topics)
                {
                    if (key == "build" && role != "construction_manager" && net.Blueprint(selectedBp)?.Stage == "existing") continue;
                    var k = key;
                    var b = new Button(() => { agentTopic = k; agentItem = null; RefreshAgentPage(true); }) { text = title };
                    b.AddToClassList("topic");
                    b.style.backgroundColor = Palette.Hex(color);
                    b.EnableInClassList("topic--on", agentTopic == key);
                    topicGrid.Add(b);
                }
                itemHost.Clear();
                if (agentTopic == "room")
                    foreach (var r in builder.RoomsById.Values.Where(r => r.Kind != "closet" && r.Kind != "pool").OrderBy(r => r.Floor))
                    {
                        var n = r.Name;
                        var p = new Button(() => { agentItem = n; RefreshAgentPage(true); }) { text = n };
                        p.AddToClassList("pill"); p.AddToClassList("pill-btn"); p.AddToClassList(agentItem == n ? "pill--on" : "pill-sky");
                        itemHost.Add(p);
                    }
                if (agentTopic == "system")
                    foreach (var c in net.Components(selectedBp))
                    {
                        var n = c.Name;
                        var p = new Button(() => { agentItem = n; RefreshAgentPage(true); }) { text = n };
                        p.AddToClassList("pill"); p.AddToClassList("pill-btn"); p.AddToClassList(agentItem == n ? "pill--on" : "pill-violet");
                        itemHost.Add(p);
                    }
                questionHost.Clear();
                foreach (var q in QuestionsFor(agentTopic, agentItem))
                {
                    var text = q;
                    var b = Btn(q, () => Ask(text), null);
                    b.style.marginLeft = 0; b.style.marginBottom = 8; b.style.whiteSpace = WhiteSpace.Normal; b.style.unityTextAlign = TextAnchor.MiddleLeft;
                    questionHost.Add(b);
                }
                RefreshVoiceCard();
                sourcesBox.Clear();
                foreach (var d in net.Sources()) { var l = L($"{d.Domain}: {d.Name}", "soft"); l.style.marginTop = 6; sourcesBox.Add(l); }
                lastMsgCount = -1;
            }

            var run = net.LatestRun(selectedBp);
            if (run != null) agentStatus.text = run.Status == "running" ? "Working on it…" : run.UsedLlm ? $"Answered by {run.Model.Replace("nvidia/", "")}" : "Answered with built-in rules (add an NVIDIA key for Nemotron)";

            var msgs = net.Messages(selectedBp);
            if (msgs.Count != lastMsgCount)
            {
                lastMsgCount = msgs.Count;
                chatBox.Clear();
                if (msgs.Count == 0) chatBox.Add(Bubble("Agent", role == "construction_manager" ? "Hi! Ask me about structure, what might fail during the build, or what to do next. I can book work too." : "Hi! Ask me what this home should list for, what sells, or what to fix first. I can book work too.", false));
                foreach (var m in msgs) chatBox.Add(Bubble(m.Role == "user" ? m.AuthorName : "Agent", Clean(m.Content), m.Role == "user"));
                chatScroll.schedule.Execute(() => chatScroll.scrollOffset = new Vector2(0, chatScroll.contentContainer.layout.height)).ExecuteLater(30);
            }

            stepsBox.Clear();
            if (run == null) stepsBox.Add(L("Each step shows up here live.", "soft"));
            else
                foreach (var s in net.Steps(run.Id))
                {
                    var st = new VisualElement(); st.AddToClassList("step");
                    var d = new VisualElement(); d.AddToClassList("step-dot"); d.style.backgroundColor = s.Kind == "action" ? Palette.Coral : Palette.Agent(s.Agent); st.Add(d);
                    var bd = new VisualElement(); bd.style.flexShrink = 1;
                    bd.Add(L($"{AgentName(s.Agent)} {StepTitle(s)}", "step-title"));
                    if (s.Kind == "action" || s.Kind == "error") bd.Add(L(Truncate(s.Detail, 140), "step-detail"));
                    st.Add(bd);
                    stepsBox.Add(st);
                }

            bool running = agentBusy || (run != null && run.Status == "running");
            thinkingRow.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            thinkingRow.Clear();
            if (running)
            {
                thinkingRow.Add(Dot("dot-teal"));
                var last = run != null ? net.Steps(run.Id).LastOrDefault() : null;
                thinkingRow.Add(L(last != null ? $"{AgentName(last.Agent)} {StepTitle(last)}…" : "Thinking…", "soft"));
            }
        }

        IEnumerable<string> QuestionsFor(string topic, string item)
        {
            switch (topic)
            {
                case "structure": return new[] { "Is this home structurally sound?", "What's the most urgent structural fix?", "What will an inspector flag?" };
                case "systems": return new[] { "What will fail first?", "How would one failure spread through the house?", "What should we fix this month?" };
                case "price": return new[] { "What should this list for?", "What's hurting the price most?", "How would it price in other cities?" };
                case "selling": return new[] { "What should the listing lead with?", "Which upgrade pays back best?", "What do local buyers want that's missing?" };
                case "build": return new[] { "Plan the next 90 days around the weather.", "What could delay this build?", "Book fixes for the urgent items." };
                case "room": return item == null ? new[] { "Pick a room above." } : new[] { $"Tell me about the {item.ToLowerInvariant()}.", $"Any problems in the {item.ToLowerInvariant()}?", $"How will buyers see the {item.ToLowerInvariant()}?" };
                case "system": return item == null ? new[] { "Pick a system above." } : new[] { $"How risky is the {item.ToLowerInvariant()}?", $"What happens if we wait a year on the {item.ToLowerInvariant()}?", $"Book a fix for the {item.ToLowerInvariant()}." };
                case "notes": return new[] { "Summarize our notes.", "What should we do about our notes?", "Do our notes change the price?" };
                default: return new[] { "Give me the quick rundown on this home.", "What should I do first?" };
            }
        }

        VisualElement Bubble(string who, string text, bool me)
        {
            var b = new VisualElement(); b.AddToClassList("bubble"); b.AddToClassList(me ? "bubble-me" : "bubble-ai");
            b.Add(L(who, "bubble-who"));
            var t = L(text, "bubble-text"); t.enableRichText = false; b.Add(t);
            return b;
        }

        void Ask(string prompt)
        {
            prompt = prompt?.Trim();
            if (string.IsNullOrEmpty(prompt) || prompt.StartsWith("Pick a") || selectedBp == 0 || !net.Ready) return;
            agentBusy = true;
            RefreshAgentPage(false);
            RefreshTourSide();
            net.Conn.Procedures.AskAgent(selectedBp, prompt, (ctx, result) =>
            {
                agentBusy = false;
                if (!result.IsSuccess) Toast("The agent couldn't answer: " + result.Error?.Message);
                net.DirtyFlags |= Dirty.Agent;
            });
        }

        static string StepTitle(AgentStep s) => s.Kind switch
        {
            "handoff" => "took a look",
            "tool_result" => "reported back",
            "tool_call" => s.Title.StartsWith("consult") ? "asked a specialist" : s.Title.Replace("_", " "),
            "action" => "booked a work order",
            "final" => "answered",
            "error" => "fell back to built-in rules",
            "plan" => "started",
            _ => s.Title,
        };

        static string AgentName(string a) => a switch
        {
            "structural" => "Structure agent",
            "operations" => "Systems agent",
            "market" => "Market agent",
            _ => "Lead agent",
        };

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("**", "").Replace("__", "");
            s = Regex.Replace(s, @"^#{1,6}\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
            return s.Trim();
        }

        // ───────────────────────── Voice (Meta Voice SDK) ─────────────────────────

        string voiceMode = "auto";
        bool keyHeld;
        const string TokenKey = "keystone.wit_token";

        void SetupVoice()
        {
            VoiceHub.Partial += t => { if (listening != null) listeningText.text = t; };
            VoiceHub.Final += OnVoiceFinal;
            VoiceHub.Failed += e => { Toast("Voice: " + e); ShowListening(false); };
            VoiceHub.ListeningChanged += on => ShowListening(on);
            var token = PlayerPrefs.GetString(TokenKey, "");
            if (string.IsNullOrEmpty(token)) token = Environment.GetEnvironmentVariable("WIT_CLIENT_TOKEN") ?? "";
            if (!string.IsNullOrEmpty(token)) VoiceHub.Configure(token);
        }

        void RefreshVoiceCard()
        {
            voiceCard.Clear();
            var head = new VisualElement(); head.AddToClassList("row");
            var h = L("Voice", "h3"); h.style.flexGrow = 1; head.Add(h);
            head.Add(Pill(!VoiceHub.Available ? "SDK missing" : VoiceHub.Ready ? "Ready" : "Needs token", !VoiceHub.Available ? "pill-coral" : VoiceHub.Ready ? "pill-teal" : "pill-amber"));
            voiceCard.Add(head);
            var t = L(VoiceHub.Available
                ? "Meta Voice SDK (Wit.ai). Hold V anywhere, or tap MIC. Say a note, \"next room\", \"fix the roof\", or ask a question."
                : "Install com.meta.xr.sdk.voice (already in the package manifest) to enable voice.", "soft");
            t.style.marginTop = 6; voiceCard.Add(t);
            if (!VoiceHub.Available) return;
            tokenField = Field("Wit.ai client access token");
            tokenField.isPasswordField = true;
            tokenField.value = PlayerPrefs.GetString(TokenKey, "");
            tokenField.style.marginTop = 10;
            voiceCard.Add(tokenField);
            var save = Btn("Save", () =>
            {
                PlayerPrefs.SetString(TokenKey, tokenField.value.Trim());
                PlayerPrefs.Save();
                VoiceHub.Configure(tokenField.value);
                Toast(VoiceHub.Ready ? "Voice is ready. Hold V to talk." : "Token saved");
                RefreshVoiceCard();
            }, "btn-primary btn-sm");
            save.style.marginLeft = 0; save.style.marginTop = 8; save.style.alignSelf = Align.FlexStart;
            voiceCard.Add(save);
        }

        void UpdateVoiceKeys()
        {
            var kb = Keyboard.current;
            if (kb == null || IsTyping || appLayer.style.display == DisplayStyle.None && rig.Mode != ViewMode.Tour) return;
            if (kb.vKey.wasPressedThisFrame && !keyHeld) { keyHeld = true; voiceMode = page == "agent" ? "ask" : "auto"; StartVoice(); }
            if (kb.vKey.wasReleasedThisFrame && keyHeld) { keyHeld = false; VoiceHub.End(); }
        }

        void ToggleVoice(string mode)
        {
            if (VoiceHub.Listening) { VoiceHub.End(); return; }
            voiceMode = mode;
            StartVoice();
        }

        void StartVoice()
        {
            if (!VoiceHub.Ready)
            {
                Toast(VoiceHub.Available ? "Add your Wit.ai token in Ask the agent → Voice." : "Voice needs the Meta Voice SDK package.");
                return;
            }
            listeningText.text = voiceMode == "ask" ? "Ask your question…" : voiceMode == "note" ? "Say your note…" : "Listening…";
            ShowListening(true);
            VoiceHub.Begin();
        }

        void ShowListening(bool on)
        {
            if (listening == null) return;
            listening.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            listening.BringToFront();
            topMic?.EnableInClassList("mic--on", on);
        }

        void OnVoiceFinal(string text)
        {
            ShowListening(false);
            text = text.Trim();
            if (text.Length == 0 || selectedBp == 0) return;
            var lower = text.ToLowerInvariant().TrimEnd('.', '!', '?');

            if (voiceMode == "ask") { Ask(text); return; }
            if (voiceMode == "note") { SaveNote(text, "voice"); return; }

            // Commands
            if (rig.Mode == ViewMode.Tour)
            {
                if (Regex.IsMatch(lower, @"^(next( room)?|go on|keep going)$")) { GoToStop(stopIndex + 1, false); return; }
                if (Regex.IsMatch(lower, @"^(previous( room)?|go back|back)$")) { GoToStop(stopIndex - 1, true); return; }
                if (Regex.IsMatch(lower, @"^(exit|stop|leave)( (the )?(tour|walkthrough))?$")) { ExitTour(); return; }
            }
            else if (Regex.IsMatch(lower, @"^(start|begin)?\s*(the )?(tour|walk ?through)$")) { EnterTour(0); return; }

            var fix = Regex.Match(lower, @"^(fix|repair|book|schedule)( a fix for| a repair for)? (the )?(.+)$");
            if (fix.Success)
            {
                var want = fix.Groups[4].Value;
                var c = net.Components(selectedBp).FirstOrDefault(x => x.Name.ToLowerInvariant().Contains(want) || Palette.KindLabel(x.Kind).ToLowerInvariant().Contains(want) || want.Contains(Palette.KindLabel(x.Kind).ToLowerInvariant()));
                if (c != null) { net.Conn.Reducers.CreateWorkOrder(c.Id, "Repair requested by voice"); Toast($"Booked: {c.Name}"); return; }
            }
            var ask = Regex.Match(text, @"^(hey keystone|ask|agent)[,:]?\s+(.+)$", RegexOptions.IgnoreCase);
            if (ask.Success) { AskFromVoice(ask.Groups[2].Value); return; }
            if (Regex.IsMatch(lower, @"^(what|how|why|should|is|are|can|could|does|do|which|when|where)\b")) { AskFromVoice(text); return; }

            var note = Regex.Match(text, @"^(note|add a note|remember)[,:]?\s+(.+)$", RegexOptions.IgnoreCase);
            SaveNote(note.Success ? note.Groups[2].Value : text, "voice");
        }

        void AskFromVoice(string q)
        {
            if (rig.Mode == ViewMode.Tour) { tourMsgCount = net.Messages(selectedBp).Count; tourSide.style.display = DisplayStyle.Flex; }
            Ask(q);
            if (rig.Mode != ViewMode.Tour && page != "agent") Toast("Sent to the agent");
        }

        void SaveNote(string text, string source)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || selectedBp == 0) return;
            Vector3 pos; uint roomId;
            if (rig.Mode == ViewMode.Tour)
            {
                pos = rig.VisitorFeet + Vector3.up * 1.8f + Quaternion.Euler(0, rig.Yaw, 0) * Vector3.forward * 0.8f;
                roomId = RoomAt(rig.VisitorFeet);
            }
            else
            {
                roomId = builder.SelectedRoom != 0 ? builder.SelectedRoom : builder.RoomsById.Values.OrderByDescending(r => r.W * r.D).FirstOrDefault()?.Id ?? 0;
                pos = builder.RoomFocusPoint(roomId) + Vector3.up * 1.4f;
            }
            net.Conn.Reducers.AddNote(selectedBp, roomId, text, source, pos.x, pos.y, pos.z);
            var name = builder.RoomsById.TryGetValue(roomId, out var r) ? r.Name : "this home";
            Toast($"Note saved in {name}");
        }

        uint RoomAt(Vector3 feetMeters)
        {
            var ft = feetMeters / BlueprintBuilder.FT;
            uint best = 0; float bestDy = 999;
            foreach (var r in builder.RoomsById.Values)
            {
                if (ft.x < r.X || ft.x > r.X + r.W || ft.z < r.Z || ft.z > r.Z + r.D) continue;
                float dy = Mathf.Abs(ft.y - builder.LevelBaseFt((int)r.Floor));
                if (dy < bestDy) { bestDy = dy; best = r.Id; }
            }
            return best;
        }

        // ───────────────────────── 3D overlays ─────────────────────────

        void RebuildMarkers()
        {
            if (markerLayer == null) return;
            markerLayer.Clear(); labelLayer.Clear();
            markerEls.Clear(); roomLabels.Clear(); nameTags.Clear(); pinEls.Clear();
            if (selectedBp == 0 || !net.Ready || showcase) return;
            if (showLabels)
                foreach (var r in builder.RoomsById.Values)
                {
                    if (r.Floor > builder.MaxFloor) continue;
                    var l = L(builder.PlanMode ? $"{r.Name}\n{r.W:0}' × {r.D:0}'" : r.Name, "room-label");
                    if (builder.PlanMode) l.AddToClassList("room-label--plan");
                    l.pickingMode = PickingMode.Ignore;
                    labelLayer.Add(l);
                    roomLabels.Add((l, r.Id));
                }
            if (builder.ShowSystems)
                foreach (var c in net.Components(selectedBp))
                {
                    if (!builder.RoomsById.TryGetValue(c.RoomId, out var room) || room.Floor > builder.MaxFloor) continue;
                    var risk = net.Risk(c.Id);
                    var m = new VisualElement(); m.AddToClassList("marker");
                    var d = new VisualElement(); d.AddToClassList("dot"); d.style.backgroundColor = Palette.Tier(risk?.Tier ?? "low"); m.Add(d);
                    m.Add(new Label($"{Palette.KindLabel(c.Kind)} {(risk != null ? risk.CascadeProb * 100 : 0):0}%"));
                    var id = c.Id;
                    m.RegisterCallback<ClickEvent>(_ => SelectComponent(id));
                    markerLayer.Add(m);
                    markerEls.Add((m, c.Id));
                }
            foreach (var n in net.Notes(selectedBp))
            {
                var p = L((n.Source == "voice" ? "“" + n.Text + "”" : n.Text), "pin");
                p.style.borderLeftColor = Palette.Hex(n.Color);
                p.tooltip = $"{n.AuthorName}";
                markerLayer.Add(p);
                pinEls.Add((p, new Vector3(n.X, n.Y, n.Z)));
            }
        }

        void LateUpdate()
        {
            if (root?.panel == null || rig == null) return;
            var cam = rig.Cam;
            if (rig.Mode == ViewMode.Tour) { LateUpdateTour(cam); return; }
            if (appLayer.style.display == DisplayStyle.None || page != "overview") return;
            foreach (var (el, id) in markerEls) Place(el, builder.ComponentWorld.TryGetValue(id, out var w) ? w + Vector3.up * 0.3f : (Vector3?)null, cam);
            foreach (var (el, id) in roomLabels) Place(el, builder.RoomCenter.TryGetValue(id, out var w) ? w + Vector3.up * 0.15f : (Vector3?)null, cam);
            foreach (var (el, pos) in pinEls) Place(el, pos, cam);
            PlaceTeammates(markerLayer, cam);
        }

        void PlaceTeammates(VisualElement layer, Camera cam)
        {
            var seen = new HashSet<string>();
            if (showTeammates)
                foreach (var kv in presence.Others)
                {
                    if (kv.Value.t == null) continue;
                    seen.Add(kv.Key);
                    kv.Value.t.gameObject.SetActive(true);
                    if (!nameTags.TryGetValue(kv.Key, out var tag) || tag.parent != layer)
                    {
                        tag?.RemoveFromHierarchy();
                        tag = L(kv.Value.name, "name-tag"); tag.style.backgroundColor = kv.Value.color; tag.pickingMode = PickingMode.Ignore;
                        layer.Add(tag);
                        nameTags[kv.Key] = tag;
                    }
                    Place(tag, kv.Value.t.position + Vector3.up * 0.45f, cam);
                }
            else foreach (var kv in presence.Others) if (kv.Value.t) kv.Value.t.gameObject.SetActive(false);
            foreach (var k in nameTags.Keys.Where(k => !seen.Contains(k)).ToList()) { nameTags[k].RemoveFromHierarchy(); nameTags.Remove(k); }
        }

        void Place(VisualElement el, Vector3? world, Camera cam, float maxDist = 999f)
        {
            if (world == null || el.parent == null) { el.style.display = DisplayStyle.None; return; }
            var vp = cam.WorldToViewportPoint(world.Value);
            if (vp.z < 0.1f || vp.z > maxDist || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { el.style.display = DisplayStyle.None; return; }
            el.style.display = DisplayStyle.Flex;
            var p = RuntimePanelUtils.CameraTransformWorldToPanel(root.panel, world.Value, cam);
            var origin = el.parent.worldBound.position;
            el.style.left = p.x - origin.x;
            el.style.top = p.y - origin.y;
        }

        // ───────────────────────── Selection ─────────────────────────

        void HandleWorldClick(Vector2 screen)
        {
            if (rig.Mode == ViewMode.Tour) { UnityEngine.Cursor.lockState = CursorLockMode.Locked; UnityEngine.Cursor.visible = false; return; }
            if (appLayer.style.display == DisplayStyle.None || page != "overview") return;
            var ray = rig.Cam.ScreenPointToRay(screen);
            if (Physics.Raycast(ray, out var hit, 500f))
            {
                var tag = hit.collider.GetComponent<RoomTag>();
                if (tag != null) { ShowRoomPopover(tag.RoomId); return; }
            }
            popover.style.display = DisplayStyle.None;
            builder.SelectedComponent = 0;
        }

        void FocusRoom(uint roomId)
        {
            if (builder.RoomsById.TryGetValue(roomId, out var r) && r.Floor > builder.MaxFloor)
            {
                builder.MaxFloor = (int)r.Floor;
                builder.ApplyFloorVisibility(); builder.RefreshOverlays(); RebuildMarkers(); RebuildLevels();
            }
            rig.FocusOn(builder.RoomFocusPoint(roomId), 9f);
            ShowRoomPopover(roomId);
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
            var t = L(c.Name, "h2"); t.style.flexGrow = 1; t.AddToClassList("wrap"); head.Add(t);
            head.Add(Btn("Close", () => { popover.style.display = DisplayStyle.None; builder.SelectedComponent = 0; }, "btn-ghost btn-sm"));
            popover.Add(head);
            if (r != null)
            {
                var big = L($"{r.CascadeProb * 100:0}%", "big"); big.style.color = Palette.Tier(r.Tier); popover.Add(big);
                popover.Add(L("chance it fails this year", "soft"));
            }
            var chips = new VisualElement(); chips.AddToClassList("row"); chips.style.flexWrap = Wrap.Wrap; chips.style.marginTop = 10;
            double age = (net.Sim?.BaseYear ?? 2026.8) + (net.Sim?.SimMonths ?? 0) / 12.0 - c.InstallYear;
            chips.Add(Pill($"{Math.Max(0, age):0} of {c.ExpectedLife:0} yrs", "pill-sky"));
            chips.Add(Pill($"{c.SensorValue:0.#} {c.SensorUnit}", c.SensorValue >= c.SensorCritical ? "pill-coral" : c.SensorValue >= c.SensorWarn ? "pill-amber" : "pill-teal"));
            if (r != null) chips.Add(Pill($"fix saves {Palette.Money(r.PreventionValue)}", "pill-violet"));
            popover.Add(chips);
            var actions = new VisualElement(); actions.AddToClassList("row"); actions.style.marginTop = 8;
            bool open = net.WorkOrders(selectedBp).Any(w => w.ComponentId == id && w.Status != "done");
            if (!open) { var b = Btn($"Fix · {Palette.Money(c.ReplaceCost)}", () => { net.Conn.Reducers.CreateWorkOrder(id, "Preventive repair or replacement"); Toast("Booked"); }, "btn-coral"); b.style.marginLeft = 0; actions.Add(b); }
            else actions.Add(Pill("Booked", "pill-teal"));
            actions.Add(Btn("Ask agent", () => { GoTo("agent"); Ask($"How risky is the {c.Name.ToLowerInvariant()}, and what happens if we wait a year?"); }, null));
            popover.Add(actions);
        }

        void ShowRoomPopover(uint roomId)
        {
            if (!builder.RoomsById.TryGetValue(roomId, out var r)) return;
            builder.SelectedRoom = roomId;
            popover.Clear();
            popover.style.display = DisplayStyle.Flex;
            var head = new VisualElement(); head.AddToClassList("row");
            var t = L(r.Name, "h2"); t.style.flexGrow = 1; head.Add(t);
            head.Add(Btn("Close", () => popover.style.display = DisplayStyle.None, "btn-ghost btn-sm"));
            popover.Add(head);
            popover.Add(L($"{r.W:0}' × {r.D:0}'  ·  {r.W * r.D:#,0} sq ft  ·  floor {r.Floor + 1}", "soft"));
            var issues = net.Findings(selectedBp).Where(f => f.RoomId == roomId && f.Severity != "ok").ToList();
            var chips = new VisualElement(); chips.AddToClassList("row"); chips.style.flexWrap = Wrap.Wrap; chips.style.marginTop = 10;
            if (issues.Count == 0) chips.Add(Pill("No issues", "pill-teal"));
            foreach (var f in issues) chips.Add(Pill(f.Title.Replace(r.Name + ": ", ""), f.Severity == "critical" ? "pill-coral" : "pill-amber"));
            foreach (var c in net.Components(selectedBp).Where(c => c.RoomId == roomId))
            {
                var id = c.Id;
                var b = new Button(() => SelectComponent(id)) { text = c.Name };
                b.AddToClassList("pill"); b.AddToClassList("pill-btn"); b.AddToClassList("pill-violet");
                chips.Add(b);
            }
            popover.Add(chips);
            var actions = new VisualElement(); actions.AddToClassList("row"); actions.style.marginTop = 8;
            var walk = Btn("Walk in", () => EnterTour(roomId), "btn-primary"); walk.style.marginLeft = 0; actions.Add(walk);
            actions.Add(Btn("Note", () => { drawerTab = "notes"; drawerOpen = true; RefreshDrawer(); }, null));
            var mic = new Button(() => ToggleVoice("note")) { text = "MIC" }; mic.AddToClassList("mic"); actions.Add(mic);
            popover.Add(actions);
        }

        // ───────────────────────── Walkthrough ─────────────────────────

        VisualElement vignette, fade, reticle, gaze, gazeFill, capture, tourSideBody, tourProgress, tourMarkers, tourSide;
        Label tourRoom, tourSub, tourSay, tourStep, gazeTitle, gazeSub;
        Button autoBtn;
        List<TourStop> stops = new();
        int stopIndex;
        bool autoTour;
        float autoNextAt = -1, fadeAlpha, fadeTarget;
        Action fadeAction;
        uint gazeId;
        float gazeDwell;
        bool prevRoof;
        int prevMaxFloor;
        readonly List<(VisualElement el, uint id)> tourMarkerEls = new();
        readonly List<(VisualElement el, Vector3 pos)> tourPins = new();
        int tourMsgCount = -1;

        void BuildTour()
        {
            tourLayer = new VisualElement(); tourLayer.AddToClassList("fill"); tourLayer.pickingMode = PickingMode.Ignore;
            root.Add(tourLayer);
            vignette = new VisualElement(); vignette.AddToClassList("fill"); vignette.pickingMode = PickingMode.Ignore;
            vignette.style.backgroundImage = new StyleBackground(VignetteTexture());
            vignette.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Cover));
            vignette.style.opacity = 0;
            tourLayer.Add(vignette);
            tourMarkers = new VisualElement(); tourMarkers.AddToClassList("fill"); tourMarkers.pickingMode = PickingMode.Ignore;
            tourLayer.Add(tourMarkers);
            reticle = new VisualElement(); reticle.AddToClassList("reticle"); reticle.pickingMode = PickingMode.Ignore; tourLayer.Add(reticle);
            var core = new VisualElement(); core.AddToClassList("reticle-core"); core.pickingMode = PickingMode.Ignore; tourLayer.Add(core);

            gaze = new VisualElement(); gaze.AddToClassList("gaze"); gaze.AddToClassList("glass"); gaze.pickingMode = PickingMode.Ignore;
            gazeTitle = L("", "h3 on-dark"); gazeSub = L("", "soft on-dark-2");
            gazeFill = new VisualElement(); gazeFill.AddToClassList("gaze-fill");
            gaze.Add(gazeTitle); gaze.Add(gazeSub); gaze.Add(gazeFill);
            gaze.style.display = DisplayStyle.None;
            tourLayer.Add(gaze);
            capture = L("Click to look around", "capture glass"); capture.pickingMode = PickingMode.Ignore; tourLayer.Add(capture);

            var top = new VisualElement(); top.AddToClassList("tour-top"); top.AddToClassList("glass");
            tourStep = L("", "label"); tourRoom = L("", "tour-room"); tourSub = L("", "tour-sub");
            tourProgress = new VisualElement(); tourProgress.AddToClassList("tour-progress");
            top.Add(tourStep); top.Add(tourRoom); top.Add(tourSub); top.Add(tourProgress);
            tourLayer.Add(top);

            var keys = new VisualElement(); keys.AddToClassList("tour-keys"); keys.AddToClassList("glass");
            foreach (var (k, t) in new[] { ("WASD", "walk"), ("Mouse", "look"), ("Q E", "turn"), ("T", "jump to spot"), ("N", "next room"), ("V", "hold to talk"), ("Esc", "free cursor"), ("X", "exit") })
            {
                var r = new VisualElement(); r.AddToClassList("row"); r.style.marginTop = 4;
                r.Add(L(k, "kbd")); r.Add(L(t, "key-line"));
                keys.Add(r);
            }
            tourLayer.Add(keys);

            var say = new VisualElement(); say.AddToClassList("tour-say"); say.AddToClassList("glass");
            say.Add(L("THIS ROOM", "label"));
            tourSay = L("", "tour-say-text"); tourSay.style.marginTop = 6; tourSay.enableRichText = false;
            say.Add(tourSay);
            tourLayer.Add(say);

            var bar = new VisualElement(); bar.AddToClassList("tour-bar"); bar.AddToClassList("glass");
            bar.Add(TourBtn("Exit", ExitTour, null));
            bar.Add(TourBtn("Back", () => GoToStop(stopIndex - 1, true), null));
            autoBtn = TourBtn("Auto", ToggleAuto, null);
            bar.Add(autoBtn);
            bar.Add(TourBtn("Next room", () => GoToStop(stopIndex + 1, false), "tour-btn--go"));
            bar.Add(TourBtn("Talk", () => ToggleVoice("auto"), "tour-btn--mic"));
            bar.Add(TourBtn("Ask", AskAboutRoom, null));
            tourLayer.Add(bar);

            tourSide = new VisualElement(); tourSide.AddToClassList("tour-side"); tourSide.AddToClassList("glass");
            tourSide.Add(L("AGENT", "label"));
            var sv = new ScrollView(ScrollViewMode.Vertical); sv.style.maxHeight = 320; sv.style.marginTop = 6;
            tourSideBody = sv.contentContainer;
            tourSide.Add(sv);
            tourSide.style.display = DisplayStyle.None;
            tourLayer.Add(tourSide);
        }

        Button TourBtn(string text, Action a, string extra)
        {
            var b = new Button(a) { text = text };
            b.AddToClassList("tour-btn");
            if (extra != null) b.AddToClassList(extra);
            return b;
        }

        void EnterTour(uint fromRoom)
        {
            if (selectedBp == 0) return;
            popover.style.display = DisplayStyle.None;
            prevRoof = builder.ShowRoof;
            prevMaxFloor = builder.MaxFloor;
            builder.PlanMode = false;
            builder.ShowRoof = true;
            builder.MaxFloor = 99;
            builder.Build(selectedBp, animate: false);
            rig.Cam.backgroundColor = Palette.Sky3D;
            stops = TourPlanner.Plan(builder, net, selectedBp);
            if (stops.Count == 0) { Toast("No walkable rooms in this plan."); RebuildScene(true); return; }
            stopIndex = Mathf.Max(0, stops.FindIndex(s => s.RoomId == fromRoom));
            var s0 = stops[stopIndex];
            appLayer.style.display = DisplayStyle.None;
            tourLayer.style.display = DisplayStyle.Flex;
            toast.AddToClassList("toast--tour");
            fadeAlpha = 1; fade.style.opacity = 1; fade.style.display = DisplayStyle.Flex; fadeTarget = 0;
            rig.EnterTour(s0.Stand, YawTo(s0.Stand, s0.LookAt));
            autoTour = false; UpdateAutoBtn();
            BuildTourMarkers();
            ShowStop(s0);
            tourMsgCount = net.Messages(selectedBp).Count;
            tourSide.style.display = DisplayStyle.None;
        }

        void ExitTour()
        {
            rig.ExitTour();
            rig.Mode = ViewMode.Orbit;
            builder.ShowRoof = prevRoof;
            builder.MaxFloor = prevMaxFloor;
            tourLayer.style.display = DisplayStyle.None;
            appLayer.style.display = DisplayStyle.Flex;
            toast.RemoveFromClassList("toast--tour");
            gazeId = 0;
            GoTo("overview");
            RebuildScene(true);
            RebuildLevels();
        }

        static float YawTo(Vector3 from, Vector3 to)
        {
            var d = to - from; d.y = 0;
            return d.sqrMagnitude < 0.0001f ? 0 : Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        void GoToStop(int i, bool jump)
        {
            if (stops.Count == 0) return;
            i = Mathf.Clamp(i, 0, stops.Count - 1);
            if (i == stopIndex && !jump) { if (i == stops.Count - 1) { Toast("That's the whole house!"); autoTour = false; UpdateAutoBtn(); } return; }
            stopIndex = i;
            var s = stops[i];
            ShowStop(s);
            if (jump || s.Teleport) FadeThen(() => { rig.Teleport(s.Stand); rig.FaceToward(s.LookAt); OnTourArrive(); });
            else rig.Guide(s.Route, s.LookAt);
        }

        void ShowStop(TourStop s)
        {
            tourStep.text = $"ROOM {stopIndex + 1} OF {stops.Count}";
            tourRoom.text = s.Title;
            var r = builder.RoomsById.TryGetValue(s.RoomId, out var room) ? room : null;
            tourSub.text = r != null ? $"{r.W:0}' × {r.D:0}'  ·  floor {r.Floor + 1}" : "";
            tourSay.text = Shorten(s.Narration);
            tourProgress.Clear();
            for (int k = 0; k < stops.Count; k++)
            {
                var t = new VisualElement(); t.AddToClassList("tick");
                if (k < stopIndex) t.AddToClassList("tick--done");
                if (k == stopIndex) t.AddToClassList("tick--now");
                if (k != stopIndex && stops[k].Severity == "critical") t.AddToClassList("tick--alert");
                tourProgress.Add(t);
            }
        }

        static string Shorten(string narration)
        {
            if (string.IsNullOrEmpty(narration)) return "";
            var parts = narration.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            var first = parts[0];
            var dot = first.IndexOf(". ", StringComparison.Ordinal);
            var lead = dot > 0 && first.Length > 140 ? first.Substring(0, dot + 1) : first;
            return parts.Length > 1 ? lead + "\n\n" + Regex.Replace(parts[1], @";.*$", ".") : lead;
        }

        void OnTourArrive() { if (autoTour) autoNextAt = Time.unscaledTime + 6f; }

        void ToggleAuto()
        {
            autoTour = !autoTour;
            UpdateAutoBtn();
            if (autoTour) GoToStop(stopIndex + 1, false);
        }

        void UpdateAutoBtn() => autoBtn.text = autoTour ? "Pause" : "Auto";

        void AskAboutRoom()
        {
            if (stops.Count == 0) return;
            tourSide.style.display = DisplayStyle.Flex;
            tourMsgCount = net.Messages(selectedBp).Count;
            Ask($"I'm standing in the {stops[stopIndex].Title.ToLowerInvariant()}. What should I know about this room?");
        }

        void RefreshTourSide()
        {
            if (rig == null || rig.Mode != ViewMode.Tour || tourSide.style.display == DisplayStyle.None || tourMsgCount < 0) return;
            var answer = net.Messages(selectedBp).Skip(tourMsgCount).LastOrDefault(m => m.Role != "user");
            var run = net.LatestRun(selectedBp);
            if (answer == null && !agentBusy && (run == null || run.Status != "running")) return;
            tourSideBody.Clear();
            if (answer == null)
            {
                var last = run != null ? net.Steps(run.Id).LastOrDefault() : null;
                tourSideBody.Add(L(last != null ? $"{AgentName(last.Agent)} {StepTitle(last)}…" : "Thinking…", "soft on-dark-2"));
            }
            else { var t = L(Clean(answer.Content), "tour-say-text"); t.enableRichText = false; tourSideBody.Add(t); }
            var close = TourBtn("Close", () => tourSide.style.display = DisplayStyle.None, null);
            close.style.alignSelf = Align.FlexEnd; close.style.marginTop = 6;
            tourSideBody.Add(close);
        }

        void BuildTourMarkers()
        {
            tourMarkers.Clear(); tourMarkerEls.Clear(); tourPins.Clear();
            foreach (var c in net.Components(selectedBp))
            {
                var risk = net.Risk(c.Id);
                var m = new VisualElement(); m.AddToClassList("marker"); m.pickingMode = PickingMode.Ignore;
                var d = new VisualElement(); d.AddToClassList("dot"); d.style.backgroundColor = Palette.Tier(risk?.Tier ?? "low"); m.Add(d);
                m.Add(new Label(Palette.KindLabel(c.Kind)));
                tourMarkers.Add(m);
                tourMarkerEls.Add((m, c.Id));
            }
            foreach (var n in net.Notes(selectedBp))
            {
                var p = L(n.Text, "pin"); p.style.borderLeftColor = Palette.Hex(n.Color); p.pickingMode = PickingMode.Ignore;
                tourMarkers.Add(p);
                tourPins.Add((p, new Vector3(n.X, n.Y, n.Z)));
            }
        }

        void UpdateTour()
        {
            var kb = Keyboard.current;
            if (kb != null && !IsTyping)
            {
                if (kb.nKey.wasPressedThisFrame) GoToStop(stopIndex + 1, false);
                if (kb.pKey.wasPressedThisFrame) GoToStop(stopIndex - 1, true);
                if (kb.spaceKey.wasPressedThisFrame) ToggleAuto();
                if (kb.xKey.wasPressedThisFrame) { ExitTour(); return; }
            }
            if (autoTour && autoNextAt > 0 && Time.unscaledTime >= autoNextAt)
            {
                autoNextAt = -1;
                if (stopIndex < stops.Count - 1) GoToStop(stopIndex + 1, false);
                else { autoTour = false; UpdateAutoBtn(); }
            }
            float v = Mathf.Clamp01(rig.CurrentSpeed / 1.6f) * 0.65f;
            vignette.style.opacity = Mathf.Lerp(vignette.resolvedStyle.opacity, v, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
            capture.style.display = rig.PointerLocked || rig.Guided ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void LateUpdateTour(Camera cam)
        {
            foreach (var (el, id) in tourMarkerEls) Place(el, builder.ComponentWorld.TryGetValue(id, out var w) ? w : (Vector3?)null, cam, 6f);
            foreach (var (el, pos) in tourPins) Place(el, pos, cam, 8f);
            PlaceTeammates(tourMarkers, cam);

            uint best = 0; float bestD = 80f;
            var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            foreach (var kv in builder.ComponentWorld)
            {
                var sp = cam.WorldToScreenPoint(kv.Value);
                if (sp.z < 0.2f || sp.z > 6f) continue;
                var px = Vector2.Distance(new Vector2(sp.x, sp.y), center) * (1080f / Mathf.Max(1, Screen.height));
                if (px < bestD) { bestD = px; best = kv.Key; }
            }
            if (best != gazeId) { gazeId = best; gazeDwell = 0; } else if (gazeId != 0) gazeDwell += Time.unscaledDeltaTime;
            reticle.EnableInClassList("reticle--hot", gazeId != 0);
            if (gazeId == 0) { gaze.style.display = DisplayStyle.None; return; }
            var c = net.Conn.Db.Component.Id.Find(gazeId);
            if (c == null) return;
            var r = net.Risk(gazeId);
            gaze.style.display = DisplayStyle.Flex;
            gazeTitle.text = c.Name;
            gazeSub.text = gazeDwell > 0.6f && r != null ? $"{r.CascadeProb * 100:0}% chance it fails this year  ·  click for more" : Palette.KindLabel(c.Kind);
            gazeFill.style.width = Length.Percent(Mathf.Clamp01(gazeDwell / 0.6f) * 100);
        }

        void OnReticleSelect()
        {
            if (gazeId == 0) return;
            var c = net.Conn.Db.Component.Id.Find(gazeId);
            if (c == null) return;
            var r = net.Risk(gazeId);
            tourMsgCount = -1;
            tourSide.style.display = DisplayStyle.Flex;
            tourSideBody.Clear();
            tourSideBody.Add(L(c.Name, "h3 on-dark"));
            if (r != null) { var big = L($"{r.CascadeProb * 100:0}%", "big"); big.style.color = Palette.Tier(r.Tier); tourSideBody.Add(big); }
            tourSideBody.Add(L($"{c.SensorLabel}: {c.SensorValue:0.#} {c.SensorUnit}", "soft on-dark-2"));
            if (r != null) tourSideBody.Add(L($"Fixing now saves about {Palette.Money(r.PreventionValue)}", "soft on-dark-2"));
            var row = new VisualElement(); row.AddToClassList("row"); row.style.marginTop = 10;
            var id = c.Id;
            if (!net.WorkOrders(selectedBp).Any(w => w.ComponentId == id && w.Status != "done"))
                row.Add(TourBtn("Fix it", () => { net.Conn.Reducers.CreateWorkOrder(id, "Preventive repair or replacement"); Toast("Booked"); }, "tour-btn--go"));
            row.Add(TourBtn("Ask", () => { tourMsgCount = net.Messages(selectedBp).Count; Ask($"How risky is the {c.Name.ToLowerInvariant()}, and what happens if we wait a year?"); }, null));
            row.Add(TourBtn("Close", () => tourSide.style.display = DisplayStyle.None, null));
            tourSideBody.Add(row);
            tourSideBody.Add(L("Press Esc to use the buttons, or just say \"fix the " + Palette.KindLabel(c.Kind).ToLowerInvariant() + "\".", "soft on-dark-2"));
        }

        void FadeThen(Action a) { fadeAction = a; fadeTarget = 1; }

        void UpdateFade()
        {
            if (fade == null) return;
            fadeAlpha = Mathf.MoveTowards(fadeAlpha, fadeTarget, Time.unscaledDeltaTime / 0.18f);
            fade.style.opacity = fadeAlpha;
            fade.style.display = fadeAlpha > 0.001f ? DisplayStyle.Flex : DisplayStyle.None;
            if (fadeTarget >= 1f && fadeAlpha >= 1f) { var a = fadeAction; fadeAction = null; a?.Invoke(); fadeTarget = 0; }
        }

        // ───────────────────────── Factories ─────────────────────────

        static Label L(string text, string classes)
        {
            var l = new Label(text);
            if (!string.IsNullOrEmpty(classes)) foreach (var c in classes.Split(' ')) l.AddToClassList(c);
            return l;
        }

        static Label Lbl(string t, float marginBottom) { var l = L(t, "label"); l.style.marginBottom = marginBottom; return l; }
        static Label Pill(string t, string cls) => L(t, "pill " + cls);
        static VisualElement Spacer() { var s = new VisualElement(); s.style.flexGrow = 1; return s; }
        static VisualElement Dot(string cls) { var d = new VisualElement(); d.AddToClassList("dot"); d.AddToClassList(cls); return d; }

        static Button Btn(string text, Action onClick, string classes)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("btn");
            if (!string.IsNullOrEmpty(classes)) foreach (var c in classes.Split(' ')) b.AddToClassList(c);
            return b;
        }

        static TextField Field(string placeholder)
        {
            var t = new TextField();
            t.AddToClassList("field");
            t.textEdition.placeholder = placeholder;
            t.textEdition.hidePlaceholderOnFocus = true;
            t.RegisterCallback<FocusInEvent>(_ => IsTyping = true);
            t.RegisterCallback<FocusOutEvent>(_ => IsTyping = false);
            return t;
        }

        static VisualElement Bar(float v, Color c)
        {
            var bg = new VisualElement(); bg.AddToClassList("bar-track"); bg.style.marginTop = 6;
            var f = new VisualElement(); f.AddToClassList("bar-fill"); f.style.backgroundColor = c; f.style.width = Length.Percent(Mathf.Clamp01(v) * 100);
            bg.Add(f);
            return bg;
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
                b.clicked += () => { for (int k = 0; k < buttons.Count; k++) buttons[k].EnableInClassList("seg-btn--on", k == idx); onPick(idx); };
                buttons.Add(b);
                seg.Add(b);
            }
            return seg;
        }

        VisualElement Switch(string label, bool on, Action<bool> changed)
        {
            var r = new VisualElement(); r.AddToClassList("switch-row");
            r.Add(L(label, "text"));
            var t = new Toggle { value = on };
            t.RegisterValueChangedCallback(e => changed(e.newValue));
            r.Add(t);
            return r;
        }

        void Toast(string msg)
        {
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

        static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";

        static Texture2D VignetteTexture()
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f) / S - 0.5f, dy = (y + 0.5f) / S - 0.5f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 0.7071f;
                    px[y * S + x] = new Color(0.07f, 0.06f, 0.16f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1f, r)));
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
