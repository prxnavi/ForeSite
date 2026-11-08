using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Component = SpacetimeDB.Types.Component;

namespace CbreTwin
{
    /// <summary>Runtime UI Toolkit interface: sign-in over a live model, workspace, immersive tour.</summary>
    public class TwinUI : MonoBehaviour
    {
        public static bool IsTyping;

        BlueprintBuilder builder;
        CameraRig rig;
        PresenceSync presence;
        TwinConnection net;

        UIDocument doc;
        VisualElement root, loginLayer, appLayer, tourLayer;
        VisualElement markerLayer, labelLayer, popover, hudBox, metricsRow, drawer, drawerContent, tabBar;
        VisualElement propertyList, levelHost, avatars, viewSegHost, thread, steps, working, suggestions;
        ScrollView threadScroll, stepsScroll;
        Label loginStatus, loginCaption, crumb, simClock, buildLine, viewHint, advisorMode, toast;
        TextField nameField, companyField, serverField, dbField, promptField;
        Button simBtn;
        VisualElement roleBuilder, roleBroker;

        string role = "real_estate_agent";
        uint selectedBp;
        string drawerTab = "structure";
        bool showReasoning;
        bool drawerOpen = true;
        bool waitingForProfile;
        string pendingName;
        bool showLabels = true, showTeammates = true;
        bool showcase;
        float nextRefresh, lastOverlayRefresh, toastUntil;
        int lastMessageCount = -1;
        ulong lastAlertSeen;
        bool agentBusy;
        readonly List<(VisualElement el, uint id)> markerEls = new();
        readonly List<(VisualElement el, uint id)> roomLabels = new();
        readonly Dictionary<string, VisualElement> nameTags = new();
        readonly List<(VisualElement el, Vector3 pos)> pinEls = new();

        public void Init(BlueprintBuilder b, CameraRig r, PresenceSync p)
        {
            builder = b;
            rig = r;
            presence = p;
        }

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

            toast = new Label();
            toast.AddToClassList("toast");
            toast.style.display = DisplayStyle.None;
            toast.pickingMode = PickingMode.Ignore;
            root.Add(toast);

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
            net.OnReducerError += msg => Toast(msg);

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
            builder.ShowSystems = false;
            builder.ShowCascade = false;
            builder.ShowStructural = false;
            builder.PlanMode = false;
            builder.Build(bp.Id);
            rig.SetMode(ViewMode.Orbit, builder.WorldBounds);
            var b = builder.WorldBounds;
            rig.Frame(new Bounds(b.center + Vector3.left * b.extents.x * 0.35f, b.size * 0.9f));
            rig.AutoRotate = true;
            var loc = net.Location(bp.LocationId);
            loginCaption.text = $"{bp.Name}\n{loc?.City}, {loc?.State}  ·  generated from plan data in the database";
        }

        // ───────────────────────── Sign-in ─────────────────────────

        void BuildLogin()
        {
            loginLayer = new VisualElement();
            loginLayer.AddToClassList("fill");
            loginLayer.pickingMode = PickingMode.Ignore;
            root.Add(loginLayer);

            var panel = new VisualElement(); panel.AddToClassList("login-panel");
            var top = new VisualElement();
            top.Add(L("KEYSTONE", "wordmark"));
            top.Add(L("Property intelligence for builders and brokers", "wordmark-sub"));
            top.Add(L("Sign in", "login-title"));
            top.Add(L("Use your name and role. Your session is tied to this device.", "login-lede"));

            top.Add(L("Name", "field-label"));
            nameField = Field("");
            top.Add(nameField);
            top.Add(L("Organization", "field-label"));
            companyField = Field("");
            top.Add(companyField);
            top.Add(L("Role", "field-label"));
            roleBuilder = RoleOption("Construction manager", "Structural review, sequencing and build risk.", "construction_manager");
            roleBroker = RoleOption("Real estate agent", "Pricing, positioning and pre-listing repairs.", "real_estate_agent");
            top.Add(roleBuilder);
            top.Add(roleBroker);
            SetRole(role);

            top.Add(Btn("Continue", OnEnterClicked, "btn-solid btn-wide"));
            nameField.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) OnEnterClicked(); }, TrickleDown.TrickleDown);
            loginStatus = L("Connecting…", "login-status");
            top.Add(loginStatus);
            panel.Add(top);

            var bottom = new VisualElement();
            var adv = new VisualElement { style = { display = DisplayStyle.None } };
            var advBtn = Btn("Connection settings", () => adv.style.display = adv.style.display == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None, "btn-text");
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

            loginCaption = L("", "login-caption");
            loginCaption.pickingMode = PickingMode.Ignore;
            loginLayer.Add(loginCaption);
        }

        VisualElement RoleOption(string name, string desc, string key)
        {
            var o = new VisualElement(); o.AddToClassList("role-option");
            var radio = new VisualElement(); radio.AddToClassList("radio");
            var inner = new VisualElement(); inner.AddToClassList("radio-inner"); radio.Add(inner);
            o.Add(radio);
            var t = new VisualElement(); t.style.flexShrink = 1;
            t.Add(L(name, "h3"));
            t.Add(L(desc, "tiny"));
            o.Add(t);
            o.RegisterCallback<ClickEvent>(_ => SetRole(key));
            return o;
        }

        void SetRole(string r)
        {
            role = r == "construction_manager" ? r : "real_estate_agent";
            roleBuilder?.EnableInClassList("role-option--on", role == "construction_manager");
            roleBroker?.EnableInClassList("role-option--on", role == "real_estate_agent");
        }

        void OnEnterClicked()
        {
            var name = nameField.value?.Trim();
            if (string.IsNullOrEmpty(name)) { loginStatus.text = "Enter your name to continue."; loginStatus.AddToClassList("login-status--err"); return; }
            pendingName = name;
            waitingForProfile = true;
            if (serverField.value.Trim() != net.ServerUrl || dbField.value.Trim() != net.DbName || !net.Ready)
            {
                net.Connect(serverField.value, dbField.value);
                return;
            }
            Register();
        }

        void Register()
        {
            if (!net.Ready) return;
            loginStatus.text = "Signing in…";
            net.Conn.Reducers.RegisterUser(pendingName, role, string.IsNullOrWhiteSpace(companyField.value) ? "Independent" : companyField.value.Trim());
        }

        // ───────────────────────── Workspace ─────────────────────────

        void BuildApp()
        {
            appLayer = new VisualElement(); appLayer.AddToClassList("fill");
            appLayer.pickingMode = PickingMode.Ignore;
            root.Add(appLayer);

            var top = new VisualElement(); top.AddToClassList("topbar");
            top.Add(L("KEYSTONE", "wordmark"));
            crumb = L("", "crumb");
            top.Add(crumb);
            top.Add(Spacer());
            viewSegHost = new VisualElement();
            top.Add(viewSegHost);
            top.Add(Spacer());
            simClock = L("", "sim-label");
            top.Add(simClock);
            simBtn = Btn("Run simulation", () => net.Conn.Reducers.SetSimulation(!(net.Sim?.Running ?? false)), "btn-sm");
            top.Add(simBtn);
            top.Add(Btn("Advance 6 months", () => net.Conn.Reducers.StepSimulation(6), "btn-sm"));
            top.Add(Btn("Reset", () => net.Conn.Reducers.ResetDemo(), "btn-text btn-sm"));
            var sep = new VisualElement(); sep.style.width = 1; sep.style.height = 22; sep.style.backgroundColor = Palette.Line; sep.style.marginLeft = 14; sep.style.marginRight = 14;
            top.Add(sep);
            avatars = new VisualElement(); avatars.AddToClassList("row");
            top.Add(avatars);
            top.Add(Btn("Sign out", SignOut, "btn-text btn-sm"));
            appLayer.Add(top);

            var body = new VisualElement(); body.AddToClassList("body"); body.pickingMode = PickingMode.Ignore;
            appLayer.Add(body);

            var side = new ScrollView(ScrollViewMode.Vertical); side.AddToClassList("sidebar");
            var s1 = new VisualElement(); s1.AddToClassList("side-section-first");
            s1.Add(Eyebrow("PROPERTIES", 10));
            propertyList = new VisualElement(); s1.Add(propertyList);
            side.Add(s1);
            var s2 = new VisualElement(); s2.AddToClassList("side-section");
            s2.Add(Eyebrow("LEVEL", 8));
            levelHost = new VisualElement(); s2.Add(levelHost);
            side.Add(s2);
            var s3 = new VisualElement(); s3.AddToClassList("side-section");
            s3.Add(Eyebrow("DISPLAY", 6));
            s3.Add(Switch("Structural overlay", true, v => { builder.ShowStructural = v; builder.RefreshOverlays(); }));
            s3.Add(Switch("Building systems", true, v => { builder.ShowSystems = v; builder.RefreshOverlays(); RebuildMarkers(); }));
            s3.Add(Switch("Failure paths", true, v => { builder.ShowCascade = v; builder.RefreshOverlays(); }));
            s3.Add(Switch("Room labels", true, v => { showLabels = v; RebuildMarkers(); }));
            s3.Add(Switch("Teammates", true, v => showTeammates = v));
            s3.Add(Switch("Roof", false, v => { builder.ShowRoof = v; RebuildScene(false); }));
            side.Add(s3);
            var s4 = new VisualElement(); s4.AddToClassList("side-section");
            s4.Add(Eyebrow("KEY", 6));
            foreach (var (t, cls) in new[] { ("Sound / low risk", "dot-ok"), ("Needs review", "dot-watch"), ("Critical", "dot-critical") })
            {
                var r = new VisualElement(); r.AddToClassList("row"); r.style.marginBottom = 4;
                r.Add(Dot(cls)); r.Add(L(t, "small"));
                s4.Add(r);
            }
            side.Add(s4);
            body.Add(side);

            var center = new VisualElement(); center.AddToClassList("center"); center.pickingMode = PickingMode.Ignore;
            var overlay = new VisualElement(); overlay.AddToClassList("viewport"); overlay.pickingMode = PickingMode.Ignore;
            labelLayer = new VisualElement(); labelLayer.AddToClassList("fill"); labelLayer.pickingMode = PickingMode.Ignore;
            markerLayer = new VisualElement(); markerLayer.AddToClassList("fill"); markerLayer.pickingMode = PickingMode.Ignore;
            overlay.Add(labelLayer);
            overlay.Add(markerLayer);
            var hud = new VisualElement(); hud.AddToClassList("hud"); hud.pickingMode = PickingMode.Ignore;
            hudBox = new VisualElement(); hudBox.pickingMode = PickingMode.Ignore; hud.Add(hudBox);
            metricsRow = new VisualElement(); metricsRow.AddToClassList("metrics"); hud.Add(metricsRow);
            buildLine = L("", "build-line"); buildLine.pickingMode = PickingMode.Ignore; hud.Add(buildLine);
            overlay.Add(hud);
            viewHint = L("", "viewhint"); viewHint.pickingMode = PickingMode.Ignore;
            overlay.Add(viewHint);
            popover = new VisualElement(); popover.AddToClassList("popover"); popover.style.display = DisplayStyle.None;
            overlay.Add(popover);
            center.Add(overlay);

            drawer = new VisualElement(); drawer.AddToClassList("drawer");
            tabBar = new VisualElement(); tabBar.AddToClassList("tabs"); drawer.Add(tabBar);
            drawerContent = new VisualElement(); drawerContent.AddToClassList("drawer-content"); drawer.Add(drawerContent);
            center.Add(drawer);
            body.Add(center);

            var adv = new VisualElement(); adv.AddToClassList("agentbar");
            var head = new VisualElement(); head.AddToClassList("advisor-head");
            head.Add(Eyebrow("ADVISOR", 6));
            head.Add(L("Ask about this property", "h2"));
            advisorMode = L("Nemotron orchestrator with structural, operations and market agents", "tiny");
            advisorMode.style.marginTop = 3; advisorMode.style.marginBottom = 12;
            head.Add(advisorMode);
            head.Add(Segmented(new[] { "Conversation", "Reasoning" }, 0, i => { showReasoning = i == 1; RefreshAdvisor(); }));
            adv.Add(head);

            var advBody = new VisualElement(); advBody.AddToClassList("advisor-body");
            threadScroll = new ScrollView(ScrollViewMode.Vertical); threadScroll.AddToClassList("thread");
            thread = threadScroll.contentContainer;
            stepsScroll = new ScrollView(ScrollViewMode.Vertical); stepsScroll.AddToClassList("thread");
            steps = stepsScroll.contentContainer;
            advBody.Add(threadScroll);
            advBody.Add(stepsScroll);
            adv.Add(advBody);
            working = new VisualElement(); working.AddToClassList("working"); working.style.display = DisplayStyle.None;
            adv.Add(working);
            suggestions = new VisualElement(); suggestions.AddToClassList("suggestions");
            adv.Add(suggestions);
            var comp = new VisualElement(); comp.AddToClassList("composer");
            promptField = Field("Ask a question");
            promptField.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Ask(promptField.value); e.StopPropagation(); } }, TrickleDown.TrickleDown);
            comp.Add(promptField);
            comp.Add(Btn("Send", () => Ask(promptField.value), "btn-solid"));
            adv.Add(comp);
            body.Add(adv);
        }

        // ───────────────────────── Enter / select ─────────────────────────

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
            RebuildViewSeg();
            RebuildProperties();
            uint start = me != null && me.SelectedBlueprint != 0 ? me.SelectedBlueprint : (role == "construction_manager" ? 4u : 1u);
            SelectBlueprint(start);
            BuildSuggestions();
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
            popover.style.display = DisplayStyle.None;
            RebuildScene(true);
            RebuildProperties();
            RebuildLevels();
            RebuildViewSeg();
            lastMessageCount = -1;
            RefreshAll();
        }

        void RebuildScene(bool reframe)
        {
            builder.Build(selectedBp);
            if (reframe || rig.Mode == ViewMode.Plan) rig.SetMode(rig.Mode == ViewMode.Tour ? ViewMode.Orbit : rig.Mode, builder.WorldBounds);
            rig.Cam.backgroundColor = builder.PlanMode ? Palette.PlanPaper : Palette.Sky3D;
            RebuildMarkers();
        }

        void RebuildViewSeg()
        {
            viewSegHost.Clear();
            int on = rig.Mode == ViewMode.Plan ? 1 : 0;
            viewSegHost.Add(Segmented(new[] { "Model", "Plan", "Walk through" }, on, i =>
            {
                if (i == 2) { EnterTour(); return; }
                var m = i == 1 ? ViewMode.Plan : ViewMode.Orbit;
                bool plan = m == ViewMode.Plan;
                if (builder.PlanMode != plan) { builder.PlanMode = plan; RebuildScene(false); }
                rig.SetMode(m, builder.WorldBounds);
                RefreshHint();
            }));
        }

        void RefreshHint()
        {
            viewHint.text = rig.Mode == ViewMode.Plan
                ? "Drag to pan  ·  scroll to zoom  ·  click a room"
                : "Drag to orbit  ·  middle-drag to pan  ·  scroll to zoom  ·  click rooms and systems";
        }

        void RebuildLevels()
        {
            levelHost.Clear();
            var names = new List<string> { "All" };
            for (int i = 0; i < builder.FloorCount; i++) names.Add($"{i + 1}");
            int on = builder.MaxFloor >= builder.FloorCount ? 0 : builder.MaxFloor + 1;
            levelHost.Add(Segmented(names.ToArray(), on, i =>
            {
                builder.MaxFloor = i == 0 ? 99 : i - 1;
                builder.ApplyFloorVisibility();
                builder.RefreshOverlays();
                RebuildMarkers();
            }));
        }

        void RebuildProperties()
        {
            propertyList.Clear();
            foreach (var b in net.Blueprints())
            {
                var loc = net.Location(b.LocationId);
                var s = net.Summary(b.Id);
                var item = new VisualElement(); item.AddToClassList("property");
                item.EnableInClassList("property--on", b.Id == selectedBp);
                var r = new VisualElement(); r.AddToClassList("row");
                r.Add(Dot(s == null ? "dot-muted" : s.CriticalCount > 0 ? "dot-critical" : s.WatchCount > 2 ? "dot-watch" : "dot-ok"));
                r.Add(L(b.Name, "property-name"));
                item.Add(r);
                var meta = $"{loc?.City}, {loc?.State}  ·  {(b.Stage == "existing" ? b.YearBuilt.ToString() : "In construction")}  ·  {b.Sqft:#,0} sq ft";
                var m = L(meta, "property-meta"); m.style.marginLeft = 15;
                item.Add(m);
                var id = b.Id;
                item.RegisterCallback<ClickEvent>(_ => SelectBlueprint(id));
                propertyList.Add(item);
            }
        }

        // ───────────────────────── Update loop ─────────────────────────

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
            if (rig.Mode == ViewMode.Tour) UpdateTour();
            if (selectedBp == 0 || (appLayer.style.display == DisplayStyle.None && rig.Mode != ViewMode.Tour)) return;

            float prog = Mathf.Clamp01((Time.time - builder.BuildStartTime) / Mathf.Max(0.1f, builder.BuildDuration));
            buildLine.text = prog < 1
                ? $"Assembling model from plan data  {prog * 100:0}%"
                : $"{builder.RoomsById.Count} rooms  ·  {builder.FloorCount} level{(builder.FloorCount > 1 ? "s" : "")}  ·  {builder.ComponentWorld.Count} monitored systems";

            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.25f;
            var d = net.DirtyFlags;
            if (d == Dirty.None) return;
            net.DirtyFlags = Dirty.None;

            if ((d & (Dirty.Risk | Dirty.Structural)) != 0 && rig.Mode != ViewMode.Tour)
            {
                if (Time.unscaledTime - lastOverlayRefresh > 0.8f)
                {
                    lastOverlayRefresh = Time.unscaledTime;
                    builder.RefreshOverlays();
                    RebuildMarkers();
                }
                else net.DirtyFlags |= d & (Dirty.Risk | Dirty.Structural);
            }
            if ((d & (Dirty.Geometry | Dirty.Structural)) != 0) RebuildProperties();
            if ((d & (Dirty.Risk | Dirty.Structural | Dirty.Market)) != 0) RefreshHud();
            if ((d & (Dirty.Risk | Dirty.Structural | Dirty.Market | Dirty.Ops)) != 0) RefreshDrawer();
            if ((d & Dirty.Agent) != 0) { RefreshAdvisor(); RefreshTourAdvisor(); }
            if ((d & Dirty.Users) != 0) RefreshAvatars();
            if ((d & Dirty.Sim) != 0) RefreshSim();
            if ((d & Dirty.Annotations) != 0) RebuildMarkers();
            if ((d & Dirty.Ops) != 0) CheckAlerts();
            if (builder.SelectedComponent != 0 && (d & Dirty.Risk) != 0 && popover.style.display == DisplayStyle.Flex) ShowComponentPopover(builder.SelectedComponent);
        }

        void RefreshAll()
        {
            RefreshHud();
            RefreshDrawer();
            RefreshAdvisor();
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
            if (a.Level != "info") Toast(a.Message);
        }

        void RefreshSim()
        {
            var s = net.Sim;
            if (s == null) return;
            simBtn.text = s.Running ? "Pause simulation" : "Run simulation";
            double y = s.BaseYear + s.SimMonths / 12.0;
            int month = Mathf.Clamp((int)((y % 1) * 12), 0, 11);
            var names = new[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
            simClock.text = s.SimMonths == 0 ? "Today" : $"{names[month]} {(int)y}  (+{s.SimMonths} mo)";
        }

        void RefreshAvatars()
        {
            avatars.Clear();
            foreach (var u in net.OnlineUsers().Take(5))
            {
                var a = L(Initials(u.Name), "avatar");
                a.style.backgroundColor = Palette.Hex(u.Color);
                a.tooltip = $"{u.Name} · {RoleName(u.Role)}";
                avatars.Add(a);
            }
            var me = net.Me;
            if (me != null)
            {
                var who = L(me.Name, "small"); who.style.marginLeft = 10; who.style.color = Palette.Ink;
                avatars.Add(who);
            }
        }

        void RefreshHud()
        {
            var b = net.Blueprint(selectedBp);
            if (b == null) return;
            var loc = net.Location(b.LocationId);
            crumb.text = $"Portfolio   /   {b.Name}";
            hudBox.Clear();
            hudBox.Add(L(b.Name, "hud-title"));
            hudBox.Add(L($"{b.Style}  ·  {loc?.City}, {loc?.State}  ·  {b.Beds} bed, {b.Baths:0.#} bath  ·  {b.Sqft:#,0} sq ft", "hud-sub"));

            metricsRow.Clear();
            var s = net.Summary(selectedBp);
            var v = net.Valuation(selectedBp);
            var items = new List<(string, string)>();
            if (s != null) items.Add(("STRUCTURE", $"{s.IntegrityScore:0} / 100"));
            if (v != null)
            {
                items.Add(("12-MONTH RISK", Palette.Money(v.ExpectedLoss12Mo)));
                items.Add((b.Stage == "existing" ? "LIST PRICE" : "VALUE AT COMPLETION", Palette.Money(v.ListPrice)));
                items.Add(("DAYS ON MARKET", $"{v.DaysOnMarket:0}"));
            }
            for (int i = 0; i < items.Count; i++)
            {
                var m = new VisualElement(); m.AddToClassList("metric");
                if (i == items.Count - 1) m.AddToClassList("metric-last");
                m.Add(L(items[i].Item1, "eyebrow"));
                m.Add(L(items[i].Item2, "metric-value"));
                metricsRow.Add(m);
            }
        }

        // ───────────────────────── Drawer ─────────────────────────

        static readonly (string key, string title)[] Tabs =
        {
            ("structure", "Structure"), ("systems", "Systems"), ("valuation", "Valuation"),
            ("fit", "Market fit"), ("orders", "Work orders"), ("sources", "Sources"),
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
            tabBar.Add(Btn(drawerOpen ? "Hide" : "Show", () => { drawerOpen = !drawerOpen; RefreshDrawer(); }, "btn-text btn-sm"));
            drawer.EnableInClassList("drawer--collapsed", !drawerOpen);

            var offsets = drawerContent.Query<ScrollView>().ToList().Select(sv => sv.scrollOffset).ToList();
            drawerContent.Clear();
            drawerContent.style.display = drawerOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (!drawerOpen) return;
            switch (drawerTab)
            {
                case "structure": DrawStructure(); break;
                case "systems": DrawSystems(); break;
                case "valuation": DrawValuation(); break;
                case "fit": DrawFit(); break;
                case "orders": DrawOrders(); break;
                default: DrawSources(); break;
            }
            var svs = drawerContent.Query<ScrollView>().ToList();
            drawerContent.schedule.Execute(() =>
            {
                for (int i = 0; i < svs.Count && i < offsets.Count; i++) svs[i].scrollOffset = offsets[i];
            }).ExecuteLater(1);
        }

        VisualElement Column(float width, bool last = false)
        {
            var c = new VisualElement(); c.AddToClassList("col"); if (last) c.AddToClassList("col-last");
            if (width > 0) { c.style.width = width; c.style.flexShrink = 0; } else { c.style.flexGrow = 1; c.style.flexBasis = 0; }
            drawerContent.Add(c);
            return c;
        }

        ScrollView ColumnList(VisualElement col, string title, string aside = null)
        {
            var h = new VisualElement(); h.AddToClassList("col-head");
            h.Add(L(title, "h3"));
            if (aside != null) h.Add(L(aside, "tiny"));
            col.Add(h);
            var sv = new ScrollView(ScrollViewMode.Vertical); sv.style.flexGrow = 1;
            col.Add(sv);
            return sv;
        }

        void DrawStructure()
        {
            var s = net.Summary(selectedBp);
            var b = net.Blueprint(selectedBp);
            var left = Column(230);
            left.Add(Eyebrow("INTEGRITY", 4));
            left.Add(L(s != null ? $"{s.IntegrityScore:0}" : "—", "num-lg"));
            left.Add(Bar(s != null ? (float)s.IntegrityScore / 100f : 0, s == null ? Palette.Muted : s.IntegrityScore >= 80 ? Palette.Ok : s.IntegrityScore >= 55 ? Palette.Watch : Palette.Critical));
            if (s != null)
            {
                var t = L($"{s.CriticalCount} critical  ·  {s.WatchCount} to review  ·  {s.OkCount} sound", "small");
                t.style.marginTop = 10; left.Add(t);
            }
            if (b != null)
            {
                var f = L($"Floors {b.FloorJoist}, ceilings {b.CeilingJoist}. {b.Foundation}. {b.RoofType}.", "tiny");
                f.style.marginTop = 12; left.Add(f);
            }

            var list = ColumnList(Column(0, true), "Findings", "IRC 2021  ·  AWC span tables");
            foreach (var f in net.Findings(selectedBp).OrderByDescending(f => f.Severity == "critical" ? 2 : f.Severity == "watch" ? 1 : 0).ThenByDescending(f => f.FixCost))
            {
                var r = new VisualElement(); r.AddToClassList("line"); r.style.alignItems = Align.FlexStart;
                var dot = Dot("dot-" + f.Severity); dot.style.marginTop = 5; r.Add(dot);
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                col.Add(L(f.Title, "strong"));
                if (f.Severity != "ok") col.Add(L(f.Detail, "small"));
                r.Add(col);
                var right = new VisualElement(); right.style.width = 200; right.style.alignItems = Align.FlexEnd;
                right.Add(L(f.CodeRef, "tiny"));
                if (f.FixCost > 0) right.Add(L($"Remedy approx. {Palette.Money(f.FixCost)}", "small"));
                var rid = f.RoomId;
                right.Add(Btn("Show", () => FocusRoom(rid), "btn-text btn-sm"));
                r.Add(right);
                list.Add(r);
            }
        }

        void DrawSystems()
        {
            var comps = net.Components(selectedBp).ToDictionary(c => c.Id);
            var open = new HashSet<uint>(net.WorkOrders(selectedBp).Where(w => w.Status != "done").Select(w => w.ComponentId));
            var list = ColumnList(Column(0), "Building systems", "Chance of failure within 12 months, alone and with upstream effects");
            foreach (var r in net.Risks(selectedBp))
            {
                if (!comps.TryGetValue(r.ComponentId, out var c)) continue;
                var line = new VisualElement(); line.AddToClassList("line");
                line.Add(Dot(TierDot(r.Tier)));
                var name = new VisualElement(); name.style.width = 220;
                name.Add(L(c.Name, "strong"));
                name.Add(L(c.Material, "tiny"));
                line.Add(name);
                var bars = new VisualElement(); bars.style.flexGrow = 1; bars.style.marginRight = 16;
                bars.Add(BarRow("alone", (float)r.BaseProb, Palette.Hex("#C9C5BC")));
                bars.Add(BarRow("with cascade", (float)r.CascadeProb, Palette.Tier(r.Tier)));
                line.Add(bars);
                var money = new VisualElement(); money.style.width = 150;
                money.Add(L($"Expected loss {Palette.Money(r.ExpectedLoss)}", "small"));
                money.Add(L($"Fixing avoids {Palette.Money(r.PreventionValue)}", "tiny"));
                line.Add(money);
                var sensor = new VisualElement(); sensor.style.width = 150;
                var sv = L($"{c.SensorValue:0.#} {c.SensorUnit}", "strong");
                sv.style.color = c.SensorValue >= c.SensorCritical ? Palette.Critical : c.SensorValue >= c.SensorWarn ? Palette.Watch : Palette.Ink;
                sensor.Add(sv);
                sensor.Add(L(c.SensorLabel, "tiny"));
                line.Add(sensor);
                var cid = c.Id;
                if (open.Contains(c.Id)) { var o = L("Scheduled", "tiny"); o.style.width = 80; line.Add(o); }
                else line.Add(Btn("Schedule", () => { net.Conn.Reducers.CreateWorkOrder(cid, "Preventive repair or replacement"); Toast($"Work order created for {c.Name.ToLowerInvariant()}"); }, "btn-sm"));
                line.RegisterCallback<ClickEvent>(e => { if (e.target is Button) return; SelectComponent(cid); });
                list.Add(line);
            }

            var paths = ColumnList(Column(340, true), "How a failure would spread");
            foreach (var p in net.Paths(selectedBp))
            {
                var r = new VisualElement(); r.style.marginBottom = 12;
                var t = L(p.Label, "strong"); t.AddToClassList("wrap"); r.Add(t);
                r.Add(L($"{p.Probability * 100:0.0}% likelihood  ·  {Palette.Money(p.Loss)} downstream", "small"));
                r.Add(L(p.Mechanism, "tiny"));
                paths.Add(r);
            }
        }

        void DrawValuation()
        {
            var v = net.Valuation(selectedBp);
            var b = net.Blueprint(selectedBp);
            if (v == null || b == null) return;
            var left = Column(240);
            left.Add(Eyebrow(b.Stage == "existing" ? "RECOMMENDED LIST PRICE" : "VALUE AT COMPLETION", 4));
            left.Add(L(Palette.MoneyFull(v.ListPrice), "num"));
            left.Add(L($"{Palette.Money(v.LowPrice)} – {Palette.Money(v.HighPrice)}  ·  {Palette.MoneyFull(v.Ppsf)} per sq ft", "small"));
            var m = L($"About {v.DaysOnMarket:0} days on market. Carrying cost {Palette.Money(v.AnnualCarryCost)} a year.", "small"); m.style.marginTop = 10; left.Add(m);
            if (v.ValueUnlocked > 0)
            {
                var u = L($"Completing {Palette.Money(v.PreListingRepairCost)} of repairs before listing recovers roughly {Palette.Money(v.ValueUnlocked)} more than it costs.", "small");
                u.style.marginTop = 10; u.style.color = Palette.Accent; left.Add(u);
            }

            var bridge = ColumnList(Column(0), "Price build-up");
            double adjBase = v.MarketBase * (1 + v.AgeAdj + v.LayoutAdj);
            double riskDrag = adjBase + v.FeaturePremium - v.ConditionDiscount - v.StructuralDiscount - v.ListPrice;
            var rows = new List<(string, double)>
            {
                ("Local market (size × median price per sq ft)", v.MarketBase),
                ("Age / new construction", v.MarketBase * v.AgeAdj),
                ("Bedroom and bath count", v.MarketBase * v.LayoutAdj),
                ("Features, weighted by local demand", v.FeaturePremium),
                ("Expected inspection credits", -v.ConditionDiscount),
                ("Structural defects", -v.StructuralDiscount),
                ("Near-term failure risk", -riskDrag),
            };
            double max = rows.Max(x => Math.Abs(x.Item2));
            for (int i = 0; i < rows.Count; i++)
            {
                var (label, val) = rows[i];
                var r = new VisualElement(); r.AddToClassList("line");
                var l = L(label, "small"); l.style.width = 260; r.Add(l);
                var track = new VisualElement(); track.style.flexGrow = 1; track.style.height = 10;
                var fill = new VisualElement(); fill.style.height = 10; Round(fill, 2);
                fill.style.width = Length.Percent((float)(Math.Abs(val) / Math.Max(1, max) * 100));
                fill.style.backgroundColor = val >= 0 ? Palette.Hex("#9DB5AA") : Palette.Hex("#D7A99A");
                track.Add(fill); r.Add(track);
                var vl = L((val > 0 && i > 0 ? "+" : "") + Palette.Money(val), "small"); vl.AddToClassList("right"); vl.style.width = 80; vl.style.color = Palette.Ink;
                r.Add(vl);
                bridge.Add(r);
            }
            var total = new VisualElement(); total.AddToClassList("line");
            var tl = L("Recommended", "strong"); tl.style.flexGrow = 1; total.Add(tl);
            total.Add(L(Palette.Money(v.ListPrice), "strong"));
            bridge.Add(total);

            var locs = ColumnList(Column(320, true), "The same house elsewhere");
            foreach (var p in net.LocationPricing(selectedBp))
            {
                bool here = p.LocationId == b.LocationId;
                var r = new VisualElement(); r.AddToClassList("line"); r.style.flexDirection = FlexDirection.Column; r.style.alignItems = Align.Stretch;
                var t = new VisualElement(); t.AddToClassList("row");
                var n = L(p.LocationName + (here ? "  (this site)" : ""), here ? "strong" : "small"); n.style.flexGrow = 1; t.Add(n);
                t.Add(L(Palette.Money(p.ListPrice), "strong"));
                r.Add(t);
                r.Add(L($"{Palette.MoneyFull(p.Ppsf)}/sq ft  ·  {p.DaysOnMarket:0} days  ·  main hazard: {p.TopHazard.ToLowerInvariant()}", "tiny"));
                locs.Add(r);
            }
        }

        void DrawFit()
        {
            var feats = ColumnList(Column(0), "What buyers here look for", "Weighted by local buyer demand");
            foreach (var f in net.Features(selectedBp))
            {
                var r = new VisualElement(); r.AddToClassList("line"); r.style.alignItems = Align.FlexStart;
                var dot = Dot(f.Present ? "dot-ok" : "dot-muted"); dot.style.marginTop = 5; r.Add(dot);
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                col.Add(L(f.Name + (f.Present ? "" : "  — not present"), "strong"));
                col.Add(L(Regex.Replace(f.Pitch, @"^Missing — \d+% local demand\. ", ""), "tiny"));
                r.Add(col);
                var right = new VisualElement(); right.style.width = 150;
                right.Add(BarRow("demand", (float)f.DemandScore, Palette.Sand));
                var vi = L((f.Present ? "Adds " : "Could add ") + Palette.Money(f.ValueImpact), "tiny"); vi.AddToClassList("right");
                right.Add(vi);
                r.Add(right);
                feats.Add(r);
            }
            var ups = ColumnList(Column(0, true), "Best return on spend", "Repairs, plus Cost vs. Value 2024 upgrades");
            foreach (var u in net.Upgrades(selectedBp).Take(12))
            {
                var r = new VisualElement(); r.AddToClassList("line"); r.style.alignItems = Align.FlexStart;
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                col.Add(L(u.Name, "strong"));
                col.Add(L(u.Rationale, "tiny"));
                r.Add(col);
                var right = new VisualElement(); right.style.width = 150; right.style.alignItems = Align.FlexEnd;
                var roi = L($"{u.Roi:0.0}× return", "strong"); roi.style.color = u.Roi >= 1 ? Palette.Accent : Palette.Ink2; right.Add(roi);
                right.Add(L($"{Palette.Money(u.Cost)} → {Palette.Money(u.ValueAdd + u.RiskAvoided)}", "tiny"));
                r.Add(right);
                ups.Add(r);
            }
        }

        void DrawOrders()
        {
            var list = ColumnList(Column(0), "Work orders");
            var orders = net.WorkOrders(selectedBp);
            if (orders.Count == 0) list.Add(L("None yet. Schedule one from Systems, or ask the advisor to take care of the top risk.", "small"));
            foreach (var w in orders)
            {
                var r = new VisualElement(); r.AddToClassList("line");
                r.Add(Dot(w.Status == "done" ? "dot-ok" : w.Status == "in_progress" ? "dot-watch" : "dot-muted"));
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                col.Add(L(w.Title, "strong"));
                var by = w.Source == "agent" ? "Raised by the advisor" : $"Raised by {w.CreatedByName}";
                col.Add(L($"{by}  ·  budget {Palette.Money(w.Cost)}  ·  avoids about {Palette.Money(w.LossAvoided)}", "tiny"));
                r.Add(col);
                var st = L(w.Status == "done" ? "Complete" : w.Status == "in_progress" ? "In progress" : "Scheduled", "small"); st.style.width = 90; r.Add(st);
                if (w.Status != "done")
                {
                    var id = w.Id;
                    r.Add(Btn(w.Status == "dispatched" ? "Start" : "Mark complete", () => net.Conn.Reducers.AdvanceWorkOrder(id), "btn-sm"));
                }
                list.Add(r);
            }
            var al = ColumnList(Column(420, true), "Alerts");
            var alerts = net.Alerts(selectedBp);
            if (alerts.Count == 0) al.Add(L("Run the simulation to see monthly wear, weather events and sensor readings come through.", "small"));
            foreach (var a in alerts.Take(30))
            {
                var r = new VisualElement(); r.AddToClassList("line"); r.style.alignItems = Align.FlexStart;
                var dot = Dot(a.Level == "critical" ? "dot-critical" : a.Level == "warn" ? "dot-watch" : "dot-ok"); dot.style.marginTop = 5; r.Add(dot);
                var m = L(a.Message, "small"); m.style.flexGrow = 1; m.style.flexShrink = 1; r.Add(m);
                r.Add(L($"+{a.SimMonth} mo", "tiny"));
                al.Add(r);
            }
        }

        void DrawSources()
        {
            var src = ColumnList(Column(0), "Data sources");
            foreach (var d in net.Sources())
            {
                var r = new VisualElement(); r.AddToClassList("line"); r.style.alignItems = Align.FlexStart;
                var dom = L(d.Domain, "tiny"); dom.style.width = 80; r.Add(dom);
                var col = new VisualElement(); col.style.flexGrow = 1; col.style.flexShrink = 1;
                var n = L(d.Name, "small"); n.style.color = Palette.Ink; col.Add(n);
                col.Add(L($"{d.Usage}  ·  {d.Url}", "tiny"));
                r.Add(col);
                src.Add(r);
            }
            var how = Column(380, true);
            var h = new VisualElement(); h.AddToClassList("col-head"); h.Add(L("How it fits together", "h3")); how.Add(h);
            foreach (var line in new[]
            {
                "Plans, system ages, hazard layers and comparable sales live in one SpacetimeDB database.",
                "Every change re-scores structure, failure risk and price inside the same transaction.",
                "A scheduled job advances time: wear, hail, freezes and sensor drift.",
                "The advisor runs inside the database as a procedure that calls NVIDIA Nemotron; its tools read and write the same tables.",
                "Everyone connected sees agent steps, alerts, work orders and each other in real time.",
            })
            {
                var l = L(line, "small"); l.style.marginBottom = 8; how.Add(l);
            }
        }

        // ───────────────────────── Advisor ─────────────────────────

        void BuildSuggestions()
        {
            suggestions.Clear();
            var prompts = role == "construction_manager"
                ? new[] { "Give me a structural review", "What is most likely to fail during the build?", "Plan the next 90 days around weather risk", "Schedule fixes for the critical items" }
                : new[] { "What should this list for?", "What should the listing lead with?", "What should the seller fix first?", "Take care of the top risk" };
            foreach (var p in prompts)
            {
                var text = p;
                var b = new Button(() => Ask(text)) { text = p };
                b.AddToClassList("suggestion");
                suggestions.Add(b);
            }
        }

        void Ask(string prompt)
        {
            prompt = prompt?.Trim();
            if (string.IsNullOrEmpty(prompt) || selectedBp == 0 || !net.Ready) return;
            promptField.value = "";
            agentBusy = true;
            RefreshAdvisor();
            RefreshTourAdvisor();
            net.Conn.Procedures.AskAgent(selectedBp, prompt, (ctx, result) =>
            {
                agentBusy = false;
                if (!result.IsSuccess) Toast("The advisor could not answer: " + result.Error?.Message);
                net.DirtyFlags |= Dirty.Agent;
            });
        }

        void RefreshAdvisor()
        {
            threadScroll.style.display = showReasoning ? DisplayStyle.None : DisplayStyle.Flex;
            stepsScroll.style.display = showReasoning ? DisplayStyle.Flex : DisplayStyle.None;
            var run = net.LatestRun(selectedBp);
            if (run != null)
                advisorMode.text = run.Status == "running" ? $"Working  ·  {run.Model.Replace("nvidia/", "")}"
                    : run.UsedLlm ? $"Answered by {run.Model.Replace("nvidia/", "")}"
                    : "Answered with built-in rules (no NVIDIA key configured)";

            var msgs = net.Messages(selectedBp);
            if (msgs.Count != lastMessageCount)
            {
                lastMessageCount = msgs.Count;
                thread.Clear();
                if (msgs.Count == 0)
                {
                    var m = new VisualElement(); m.AddToClassList("msg");
                    m.Add(L("ADVISOR", "msg-author"));
                    m.Add(L(role == "construction_manager"
                        ? "Ask about structure, what is likely to fail during the build, or how to sequence the work. Answers come from this property's live data, and I can schedule work orders."
                        : "Ask what the house should list for, what to lead the listing with, or what to fix before an inspection. Answers come from this property's live data, and I can schedule work orders.", "msg-text"));
                    thread.Add(m);
                }
                foreach (var m in msgs) thread.Add(Message(m));
                threadScroll.schedule.Execute(() => threadScroll.scrollOffset = new Vector2(0, threadScroll.contentContainer.layout.height)).ExecuteLater(30);
            }

            steps.Clear();
            var runs = net.Ready ? net.Conn.Db.AgentRun.BlueprintId.Filter(selectedBp).OrderByDescending(r => r.Id).Take(3).ToList() : new List<AgentRun>();
            if (runs.Count == 0) steps.Add(L("Each question's plan, hand-offs between agents, tool calls and actions appear here as they happen.", "small"));
            foreach (var r in runs)
            {
                var hdr = L($"“{Truncate(r.Prompt, 80)}”", "strong"); hdr.AddToClassList("wrap"); hdr.style.marginTop = 6; hdr.style.marginBottom = 8;
                steps.Add(hdr);
                var list = net.Steps(r.Id);
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    var st = new VisualElement(); st.AddToClassList("step");
                    var rail = new VisualElement(); rail.AddToClassList("step-rail");
                    var dot = Dot("dot-muted"); dot.style.backgroundColor = s.Kind == "action" ? Palette.Clay : s.Kind == "error" ? Palette.Critical : Palette.Agent(s.Agent);
                    rail.Add(dot);
                    if (i < list.Count - 1) { var ln = new VisualElement(); ln.AddToClassList("step-line"); rail.Add(ln); }
                    st.Add(rail);
                    var bd = new VisualElement(); bd.style.flexGrow = 1; bd.style.flexShrink = 1;
                    bd.Add(L($"{AgentName(s.Agent)}  ·  {StepTitle(s)}", "step-title"));
                    if (!string.IsNullOrEmpty(s.Detail) && s.Kind != "final") { var dt = L(Truncate(Clean(s.Detail), 360), "step-detail"); dt.enableRichText = false; bd.Add(dt); }
                    st.Add(bd);
                    steps.Add(st);
                }
            }

            bool running = agentBusy || (run != null && run.Status == "running");
            working.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            working.Clear();
            if (running)
            {
                var d = Dot("dot-ok"); d.style.backgroundColor = Palette.Accent; working.Add(d);
                var last = run != null ? net.Steps(run.Id).LastOrDefault() : null;
                working.Add(L(last != null ? $"{AgentName(last.Agent)}: {StepTitle(last)}" : "Planning…", "small"));
            }
        }

        static string StepTitle(AgentStep s) => s.Kind switch
        {
            "handoff" => "asked for a specialist view",
            "tool_result" => s.Title.Replace(" brief", " reported back"),
            "tool_call" => $"called {s.Title.Replace("_", " ")}",
            "action" => "scheduled a work order",
            "final" => "answered",
            "error" => "model unavailable, used built-in rules",
            "plan" => "started",
            _ => s.Title,
        };

        VisualElement Message(AgentMessage m)
        {
            var el = new VisualElement(); el.AddToClassList("msg");
            if (m.Role == "user") el.AddToClassList("msg-user");
            else el.Add(L("ADVISOR", "msg-author"));
            var t = L(Clean(m.Content), "msg-text"); t.enableRichText = false;
            el.Add(t);
            return el;
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
            "structural" => "Structural",
            "operations" => "Operations",
            "market" => "Market",
            "user" => "You",
            _ => "Orchestrator",
        };

        // ───────────────────────── Model overlays ─────────────────────────

        void RebuildMarkers()
        {
            markerLayer.Clear();
            labelLayer.Clear();
            markerEls.Clear();
            roomLabels.Clear();
            nameTags.Clear();
            pinEls.Clear();
            if (selectedBp == 0 || !net.Ready || showcase) return;

            if (showLabels)
                foreach (var r in builder.RoomsById.Values)
                {
                    if (r.Floor > builder.MaxFloor) continue;
                    var l = L(builder.PlanMode ? $"{r.Name.ToUpperInvariant()}\n{r.W:0}' × {r.D:0}'" : r.Name, "room-label");
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
                    m.Add(Dot(TierDot(risk?.Tier ?? "low")));
                    m.Add(new Label($"{Palette.KindLabel(c.Kind)}  {(risk != null ? risk.CascadeProb * 100 : 0):0}%"));
                    var id = c.Id;
                    m.RegisterCallback<ClickEvent>(_ => SelectComponent(id));
                    markerLayer.Add(m);
                    markerEls.Add((m, c.Id));
                }
            foreach (var a in net.Annotations(selectedBp))
            {
                var p = L($"{a.AuthorName}: {a.Text}", "pin");
                p.style.borderLeftColor = Palette.Hex(a.Color);
                var aid = a.Id;
                p.RegisterCallback<ClickEvent>(_ => net.Conn.Reducers.DeleteAnnotation(aid));
                p.tooltip = "Click to remove";
                markerLayer.Add(p);
                pinEls.Add((p, new Vector3(a.X, a.Y, a.Z)));
            }
        }

        void LateUpdate()
        {
            if (root?.panel == null || rig == null) return;
            var cam = rig.Cam;
            if (rig.Mode == ViewMode.Tour) { LateUpdateTour(cam); return; }
            if (appLayer.style.display == DisplayStyle.None) return;
            foreach (var (el, id) in markerEls)
                Place(el, builder.ComponentWorld.TryGetValue(id, out var w) ? w + Vector3.up * 0.3f : (Vector3?)null, cam);
            foreach (var (el, id) in roomLabels)
                Place(el, builder.RoomCenter.TryGetValue(id, out var w) ? w + Vector3.up * 0.15f : (Vector3?)null, cam);
            foreach (var (el, pos) in pinEls) Place(el, pos, cam);
            PlaceTeammates(markerLayer, cam);
        }

        void PlaceTeammates(VisualElement layer, Camera cam)
        {
            var seen = new HashSet<string>();
            if (showTeammates)
            {
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
            if (rig.Mode == ViewMode.Tour)
            {
                UnityEngine.Cursor.lockState = CursorLockMode.Locked;
                UnityEngine.Cursor.visible = false;
                return;
            }
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

        void ComponentDetail(VisualElement host, Component c, bool dark)
        {
            var r = net.Risk(c.Id);
            string sub = dark ? "on-dark-2 small" : "small";
            string tiny = dark ? "on-dark-3 tiny" : "tiny";
            double age = (net.Sim?.BaseYear ?? 2026.8) + (net.Sim?.SimMonths ?? 0) / 12.0 - c.InstallYear;
            host.Add(L(c.Material, sub));
            host.Add(L($"{Math.Max(0, age):0.#} of {c.ExpectedLife:0} years  ·  condition {c.Condition * 100:0}%", sub));
            var sv = L($"{c.SensorLabel}: {c.SensorValue:0.#} {c.SensorUnit}", sub);
            if (c.SensorValue >= c.SensorWarn) sv.style.color = c.SensorValue >= c.SensorCritical ? (dark ? Palette.Hex("#E38A80") : Palette.Critical) : (dark ? Palette.Hex("#D9B060") : Palette.Watch);
            host.Add(sv);
            if (r != null)
            {
                var b = BarRow("12-mo risk", (float)r.CascadeProb, Palette.Tier(r.Tier), dark); b.style.marginTop = 8; host.Add(b);
                host.Add(L($"Expected loss {Palette.Money(r.ExpectedLoss)}. Fixing now avoids about {Palette.Money(r.PreventionValue)} across connected systems.", sub));
            }
            if (!string.IsNullOrEmpty(c.HazardFlag)) { var hz = L(c.HazardFlag, tiny); hz.style.marginTop = 6; host.Add(hz); }
        }

        void ShowComponentPopover(uint id)
        {
            var c = net.Conn.Db.Component.Id.Find(id);
            if (c == null) return;
            popover.Clear();
            popover.style.display = DisplayStyle.Flex;
            var head = new VisualElement(); head.AddToClassList("row");
            var t = L(c.Name, "h2"); t.style.flexGrow = 1; head.Add(t);
            head.Add(Btn("Close", () => { popover.style.display = DisplayStyle.None; builder.SelectedComponent = 0; }, "btn-text btn-sm"));
            popover.Add(head);
            ComponentDetail(popover, c, false);
            var actions = new VisualElement(); actions.AddToClassList("row"); actions.style.marginTop = 12;
            bool open = net.WorkOrders(selectedBp).Any(w => w.ComponentId == id && w.Status != "done");
            if (!open)
            {
                var sb = Btn($"Schedule repair  ·  {Palette.Money(c.ReplaceCost)}", () => { net.Conn.Reducers.CreateWorkOrder(id, "Preventive repair or replacement"); Toast("Work order created"); }, "btn-solid");
                sb.style.marginLeft = 0; actions.Add(sb);
            }
            else actions.Add(L("A work order is open", "small"));
            actions.Add(Btn("Ask advisor", () => Ask($"Explain the risk on the {c.Name.ToLowerInvariant()} and what happens downstream if we wait 12 months."), null));
            popover.Add(actions);
        }

        void ShowRoomPopover(uint roomId, Vector3 point)
        {
            if (!builder.RoomsById.TryGetValue(roomId, out var r)) return;
            builder.SelectedRoom = roomId;
            popover.Clear();
            popover.style.display = DisplayStyle.Flex;
            var head = new VisualElement(); head.AddToClassList("row");
            var t = L(r.Name, "h2"); t.style.flexGrow = 1; head.Add(t);
            head.Add(Btn("Close", () => popover.style.display = DisplayStyle.None, "btn-text btn-sm"));
            popover.Add(head);
            popover.Add(L($"Level {r.Floor + 1}  ·  {r.W:0.#}' × {r.D:0.#}'  ·  {r.W * r.D:#,0} sq ft  ·  {r.Ceiling:0}' ceiling", "small"));
            foreach (var f in net.Findings(selectedBp).Where(f => f.RoomId == roomId))
            {
                var row = new VisualElement(); row.AddToClassList("line"); row.style.alignItems = Align.FlexStart;
                var d = Dot("dot-" + f.Severity); d.style.marginTop = 5; row.Add(d);
                var col = new VisualElement(); col.style.flexShrink = 1;
                col.Add(L(f.Title, "strong"));
                if (f.Severity != "ok") col.Add(L($"{f.Detail} {f.CodeRef}.", "tiny"));
                row.Add(col);
                popover.Add(row);
            }
            foreach (var c in net.Components(selectedBp).Where(c => c.RoomId == roomId))
            {
                var id = c.Id;
                var b = Btn(c.Name, () => SelectComponent(id), "btn-text btn-sm"); b.style.alignSelf = Align.FlexStart; b.style.marginLeft = 0;
                popover.Add(b);
            }
            var pinRow = new VisualElement(); pinRow.AddToClassList("row"); pinRow.style.marginTop = 12;
            var pinField = Field("Leave a note for the team"); pinField.style.flexGrow = 1;
            pinRow.Add(pinField);
            var p = point + Vector3.up * 1.2f;
            pinRow.Add(Btn("Pin", () =>
            {
                if (string.IsNullOrWhiteSpace(pinField.value)) return;
                net.Conn.Reducers.AddAnnotation(selectedBp, p.x, p.y, p.z, pinField.value.Trim());
                pinField.value = "";
            }, null));
            popover.Add(pinRow);
            var tour = Btn("Walk through from here", () => EnterTour(roomId), "btn-solid"); tour.style.marginLeft = 0; tour.style.marginTop = 10;
            popover.Add(tour);
        }

        // ───────────────────────── Immersive tour ─────────────────────────

        VisualElement vignette, fade, reticle, gazeCard, gazeFill, captureHint, tourAdvisorBody, tourProgress, tourMarkers, tourAdvisor;
        Label tourRoom, tourSub, tourNarrText, tourStep, gazeTitle, gazeSub;
        Button autoBtn;
        List<TourStop> stops = new();
        int stopIndex;
        bool autoTour;
        float autoNextAt = -1;
        float fadeAlpha, fadeTarget;
        Action fadeAction;
        uint gazeId;
        float gazeDwell;
        bool prevRoof;
        int prevMaxFloor;
        readonly List<(VisualElement el, uint id)> tourMarkerEls = new();
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

            reticle = new VisualElement(); reticle.AddToClassList("reticle"); reticle.pickingMode = PickingMode.Ignore;
            tourLayer.Add(reticle);
            var core = new VisualElement(); core.AddToClassList("reticle-core"); core.pickingMode = PickingMode.Ignore;
            tourLayer.Add(core);

            gazeCard = new VisualElement(); gazeCard.AddToClassList("gaze-card"); gazeCard.AddToClassList("glass"); gazeCard.pickingMode = PickingMode.Ignore;
            gazeTitle = L("", "h3 on-dark");
            gazeSub = L("", "small on-dark-2");
            gazeFill = new VisualElement(); gazeFill.AddToClassList("gaze-fill");
            gazeCard.Add(gazeTitle); gazeCard.Add(gazeSub); gazeCard.Add(gazeFill);
            gazeCard.style.display = DisplayStyle.None;
            tourLayer.Add(gazeCard);

            captureHint = L("Click to look around", "capture-hint glass"); captureHint.pickingMode = PickingMode.Ignore;
            tourLayer.Add(captureHint);

            var tourTop = new VisualElement(); tourTop.AddToClassList("tour-top"); tourTop.AddToClassList("glass");
            tourStep = L("", "eyebrow");
            tourRoom = L("", "tour-room");
            tourSub = L("", "tour-sub");
            tourProgress = new VisualElement(); tourProgress.AddToClassList("tour-progress");
            tourTop.Add(tourStep); tourTop.Add(tourRoom); tourTop.Add(tourSub); tourTop.Add(tourProgress);
            tourLayer.Add(tourTop);

            var hint = new VisualElement(); hint.AddToClassList("tour-hint"); hint.AddToClassList("glass");
            hint.Add(L("CONTROLS", "eyebrow"));
            foreach (var (k, t) in new[] { ("W A S D", "walk"), ("Mouse", "look (click to capture, Esc to release)"), ("Q / E", "turn 30°"), ("T", "teleport to where you're looking"), ("N / P", "next / previous room"), ("Space", "auto tour"), ("Click", "inspect what you're looking at"), ("X", "leave the walkthrough") })
            {
                var r = new VisualElement(); r.AddToClassList("row"); r.style.marginTop = 4;
                r.Add(L(k, "kbd")); r.Add(L(t, "tour-hint-line"));
                hint.Add(r);
            }
            tourLayer.Add(hint);

            var narr = new VisualElement(); narr.AddToClassList("tour-narration"); narr.AddToClassList("glass");
            narr.Add(L("ABOUT THIS ROOM", "eyebrow"));
            tourNarrText = L("", "tour-narration-text"); tourNarrText.style.marginTop = 6; tourNarrText.enableRichText = false;
            narr.Add(tourNarrText);
            tourLayer.Add(narr);

            var bar = new VisualElement(); bar.AddToClassList("tour-bar"); bar.AddToClassList("glass");
            bar.Add(TourBtn("Exit", ExitTour));
            bar.Add(Sep());
            bar.Add(TourBtn("Previous", () => GoToStop(stopIndex - 1, true)));
            autoBtn = TourBtn("Auto tour", ToggleAuto);
            bar.Add(autoBtn);
            var next = TourBtn("Next room", () => GoToStop(stopIndex + 1, false)); next.AddToClassList("tour-btn--primary");
            bar.Add(next);
            bar.Add(Sep());
            bar.Add(TourBtn("Ask about this room", AskAboutRoom));
            tourLayer.Add(bar);

            tourAdvisor = new VisualElement(); tourAdvisor.AddToClassList("tour-advisor"); tourAdvisor.AddToClassList("glass");
            tourAdvisor.Add(L("ADVISOR", "eyebrow"));
            var sv = new ScrollView(ScrollViewMode.Vertical); sv.style.maxHeight = 300; sv.style.marginTop = 6;
            tourAdvisorBody = sv.contentContainer;
            tourAdvisor.Add(sv);
            tourAdvisor.style.display = DisplayStyle.None;
            tourLayer.Add(tourAdvisor);

            fade = new VisualElement(); fade.AddToClassList("fill"); fade.pickingMode = PickingMode.Ignore;
            fade.style.backgroundColor = new Color(0.08f, 0.08f, 0.09f, 1f);
            fade.style.opacity = 0;
            root.Add(fade);
        }

        Button TourBtn(string text, Action a)
        {
            var b = new Button(a) { text = text };
            b.AddToClassList("tour-btn");
            return b;
        }

        static VisualElement Sep() { var s = new VisualElement(); s.AddToClassList("tour-sep"); return s; }

        void EnterTour() => EnterTour(0);

        void EnterTour(uint fromRoom)
        {
            if (selectedBp == 0) return;
            popover.style.display = DisplayStyle.None;
            prevRoof = builder.ShowRoof;
            prevMaxFloor = builder.MaxFloor;
            builder.PlanMode = false;
            builder.ShowRoof = true;          // ceilings make the interior read as a real space
            builder.MaxFloor = 99;
            builder.Build(selectedBp, animate: false);
            rig.Cam.backgroundColor = Palette.Sky3D;
            stops = TourPlanner.Plan(builder, net, selectedBp);
            if (stops.Count == 0) { Toast("This plan has no walkable rooms yet."); RebuildScene(true); return; }
            stopIndex = Mathf.Max(0, stops.FindIndex(s => s.RoomId == fromRoom));
            var s0 = stops[stopIndex];
            appLayer.style.display = DisplayStyle.None;
            tourLayer.style.display = DisplayStyle.Flex;
            toast.AddToClassList("toast--tour");
            fadeAlpha = 1; fade.style.opacity = 1; fadeTarget = 0;
            rig.EnterTour(s0.Stand, YawTo(s0.Stand, s0.LookAt));
            autoTour = false;
            UpdateAutoBtn();
            BuildTourMarkers();
            ShowStop(s0);
            tourMsgCount = net.Messages(selectedBp).Count;
            tourAdvisor.style.display = DisplayStyle.None;
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
            RebuildScene(true);
            RebuildViewSeg();
            RebuildLevels();
            RefreshHint();
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
            if (i == stopIndex && !jump)
            {
                if (i == stops.Count - 1) { Toast("That's the last room on the walkthrough."); autoTour = false; UpdateAutoBtn(); }
                return;
            }
            stopIndex = i;
            var s = stops[i];
            ShowStop(s);
            if (jump || s.Teleport)
                FadeThen(() => { rig.Teleport(s.Stand); rig.FaceToward(s.LookAt); OnTourArrive(); });
            else
                rig.Guide(s.Route, s.LookAt);
        }

        void ShowStop(TourStop s)
        {
            tourStep.text = $"ROOM {stopIndex + 1} OF {stops.Count}";
            tourRoom.text = s.Title;
            tourSub.text = s.Subtitle;
            tourNarrText.text = s.Narration;
            tourProgress.Clear();
            for (int k = 0; k < stops.Count; k++)
            {
                var t = new VisualElement(); t.AddToClassList("tour-tick");
                if (k < stopIndex) t.AddToClassList("tour-tick--done");
                if (k == stopIndex) t.AddToClassList("tour-tick--now");
                if (k != stopIndex && stops[k].Severity == "critical") t.AddToClassList("tour-tick--critical");
                tourProgress.Add(t);
            }
        }

        void OnTourArrive()
        {
            if (autoTour) autoNextAt = Time.unscaledTime + 7f;
        }

        void ToggleAuto()
        {
            autoTour = !autoTour;
            UpdateAutoBtn();
            if (autoTour) GoToStop(stopIndex + 1, false);
        }

        void UpdateAutoBtn() => autoBtn.text = autoTour ? "Pause tour" : "Auto tour";

        void AskAboutRoom()
        {
            if (stops.Count == 0) return;
            var s = stops[stopIndex];
            tourAdvisor.style.display = DisplayStyle.Flex;
            tourMsgCount = net.Messages(selectedBp).Count;
            Ask($"I'm standing in the {s.Title.ToLowerInvariant()}. What should I know about this room: structure, systems, and how buyers will see it?");
        }

        void RefreshTourAdvisor()
        {
            if (rig.Mode != ViewMode.Tour || tourAdvisor.style.display == DisplayStyle.None || tourMsgCount < 0) return;
            var msgs = net.Messages(selectedBp);
            var answer = msgs.Skip(tourMsgCount).LastOrDefault(m => m.Role != "user");
            var run = net.LatestRun(selectedBp);
            if (answer == null && !agentBusy && (run == null || run.Status != "running")) return; // showing a system card
            tourAdvisorBody.Clear();
            if (answer == null)
            {
                var last = run != null ? net.Steps(run.Id).LastOrDefault() : null;
                tourAdvisorBody.Add(L(last != null ? $"{AgentName(last.Agent)}: {StepTitle(last)}…" : "Thinking…", "small on-dark-2"));
            }
            else
            {
                var t = L(Clean(answer.Content), "tour-narration-text"); t.enableRichText = false;
                tourAdvisorBody.Add(t);
            }
            var close = TourBtn("Close", () => tourAdvisor.style.display = DisplayStyle.None);
            close.style.alignSelf = Align.FlexEnd; close.style.marginTop = 6;
            tourAdvisorBody.Add(close);
        }

        void BuildTourMarkers()
        {
            tourMarkers.Clear();
            tourMarkerEls.Clear();
            foreach (var c in net.Components(selectedBp))
            {
                var risk = net.Risk(c.Id);
                var m = new VisualElement(); m.AddToClassList("marker"); m.pickingMode = PickingMode.Ignore;
                m.Add(Dot(TierDot(risk?.Tier ?? "low")));
                m.Add(new Label(Palette.KindLabel(c.Kind)));
                tourMarkers.Add(m);
                tourMarkerEls.Add((m, c.Id));
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
            // Comfort vignette narrows the view while moving
            float v = Mathf.Clamp01(rig.CurrentSpeed / 1.6f) * 0.7f;
            vignette.style.opacity = Mathf.Lerp(vignette.resolvedStyle.opacity, v, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
            captureHint.style.display = rig.PointerLocked || rig.Guided ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void LateUpdateTour(Camera cam)
        {
            foreach (var (el, id) in tourMarkerEls)
                Place(el, builder.ComponentWorld.TryGetValue(id, out var w) ? w : (Vector3?)null, cam, 6f);
            PlaceTeammates(tourMarkers, cam);

            // Gaze selection: the system nearest the reticle, within arm's reach of the room
            uint best = 0; float bestD = 80f;
            var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            foreach (var kv in builder.ComponentWorld)
            {
                var sp = cam.WorldToScreenPoint(kv.Value);
                if (sp.z < 0.2f || sp.z > 6f) continue;
                var px = Vector2.Distance(new Vector2(sp.x, sp.y), center) * (1080f / Mathf.Max(1, Screen.height));
                if (px < bestD) { bestD = px; best = kv.Key; }
            }
            if (best != gazeId) { gazeId = best; gazeDwell = 0; }
            else if (gazeId != 0) gazeDwell += Time.unscaledDeltaTime;
            reticle.EnableInClassList("reticle--hot", gazeId != 0);
            if (gazeId == 0) { gazeCard.style.display = DisplayStyle.None; return; }
            var c = net.Conn.Db.Component.Id.Find(gazeId);
            if (c == null) return;
            var r = net.Risk(gazeId);
            gazeCard.style.display = DisplayStyle.Flex;
            gazeTitle.text = c.Name;
            gazeSub.text = gazeDwell > 0.6f && r != null
                ? $"{c.SensorLabel} {c.SensorValue:0.#} {c.SensorUnit}  ·  {r.CascadeProb * 100:0}% risk in 12 months  ·  fixing avoids {Palette.Money(r.PreventionValue)}"
                : $"{Palette.KindLabel(c.Kind)}  ·  keep looking for details, click to open";
            gazeFill.style.width = Length.Percent(Mathf.Clamp01(gazeDwell / 0.6f) * 100);
        }

        void OnReticleSelect()
        {
            if (gazeId == 0) return;
            var c = net.Conn.Db.Component.Id.Find(gazeId);
            if (c == null) return;
            tourMsgCount = -1; // card, not an advisor answer
            tourAdvisor.style.display = DisplayStyle.Flex;
            tourAdvisorBody.Clear();
            tourAdvisorBody.Add(L(c.Name, "h3 on-dark"));
            ComponentDetail(tourAdvisorBody, c, true);
            var row = new VisualElement(); row.AddToClassList("row"); row.style.marginTop = 10;
            var id = c.Id;
            bool open = net.WorkOrders(selectedBp).Any(w => w.ComponentId == id && w.Status != "done");
            if (!open) row.Add(TourBtn("Schedule repair", () => { net.Conn.Reducers.CreateWorkOrder(id, "Preventive repair or replacement"); Toast("Work order created"); }));
            row.Add(TourBtn("Ask advisor", () => { tourMsgCount = net.Messages(selectedBp).Count; Ask($"Explain the risk on the {c.Name.ToLowerInvariant()} and what happens downstream if we wait 12 months."); }));
            row.Add(TourBtn("Close", () => tourAdvisor.style.display = DisplayStyle.None));
            tourAdvisorBody.Add(row);
            tourAdvisorBody.Add(L("Press Esc to free the cursor and use these buttons.", "tiny on-dark-3"));
        }

        void FadeThen(Action a)
        {
            fadeAction = a;
            fadeTarget = 1;
        }

        void UpdateFade()
        {
            if (fade == null) return;
            fadeAlpha = Mathf.MoveTowards(fadeAlpha, fadeTarget, Time.unscaledDeltaTime / 0.18f);
            fade.style.opacity = fadeAlpha;
            fade.style.display = fadeAlpha > 0.001f ? DisplayStyle.Flex : DisplayStyle.None;
            if (fadeTarget >= 1f && fadeAlpha >= 1f)
            {
                var a = fadeAction; fadeAction = null;
                a?.Invoke();
                fadeTarget = 0;
            }
        }

        // ───────────────────────── Factories ─────────────────────────

        static Label L(string text, string classes)
        {
            var l = new Label(text);
            if (!string.IsNullOrEmpty(classes)) foreach (var c in classes.Split(' ')) l.AddToClassList(c);
            return l;
        }

        static Label Eyebrow(string t, float marginBottom)
        {
            var l = L(t, "eyebrow"); l.style.marginBottom = marginBottom; return l;
        }

        static VisualElement Spacer() { var s = new VisualElement(); s.style.flexGrow = 1; return s; }

        static VisualElement Dot(string cls)
        {
            var d = new VisualElement(); d.AddToClassList("dot"); d.AddToClassList(cls); return d;
        }

        static string TierDot(string tier) => tier switch { "severe" => "dot-critical", "high" => "dot-critical", "elevated" => "dot-watch", _ => "dot-ok" };

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

        static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r; e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        static VisualElement Bar(float v, Color c)
        {
            var bg = new VisualElement(); bg.AddToClassList("bar-track"); bg.style.marginTop = 8;
            var f = new VisualElement(); f.AddToClassList("bar-fill"); f.style.backgroundColor = c; f.style.width = Length.Percent(Mathf.Clamp01(v) * 100);
            bg.Add(f);
            return bg;
        }

        static VisualElement BarRow(string label, float v, Color c, bool dark = false)
        {
            var r = new VisualElement(); r.AddToClassList("row"); r.style.marginTop = 3;
            var l = L(label, dark ? "on-dark-3" : "tiny"); l.style.width = 76; l.style.fontSize = 10; r.Add(l);
            var b = Bar(v, c); b.style.flexGrow = 1; b.style.marginTop = 0;
            if (dark) b.style.backgroundColor = new Color(1, 1, 1, 0.15f);
            r.Add(b);
            var p = L($"{v * 100:0}%", dark ? "on-dark-2" : "tiny"); p.style.width = 34; p.AddToClassList("right"); p.style.fontSize = 10; r.Add(p);
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

        VisualElement Switch(string label, bool on, Action<bool> changed)
        {
            var r = new VisualElement(); r.AddToClassList("switch-row");
            r.Add(L(label, "small"));
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
            toastUntil = Time.unscaledTime + 4f;
        }

        static string Initials(string n)
        {
            var parts = (n ?? "?").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant() : (n ?? "?").Substring(0, Math.Min(2, (n ?? "?").Length)).ToUpperInvariant();
        }

        static string RoleName(string r) => r == "construction_manager" ? "Construction manager" : "Real estate agent";
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
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1f, r));
                    px[y * S + x] = new Color(0.05f, 0.05f, 0.06f, a);
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
