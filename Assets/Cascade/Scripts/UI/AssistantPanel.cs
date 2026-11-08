using System;
using System.Collections.Generic;
using System.Linq;
using Atlas;
using Cascade.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Cascade
{
    /// <summary>
    /// The one place you talk to Cascade. Bright frosted panel that follows you.
    /// Left: talk (hold-to-talk + suggestions) and watch the Nemotron agent team work.
    /// Right: what they found, the next 24 hours, and the one-tap plan approval.
    /// Below: a small dock for mode, role and view toggles.
    /// </summary>
    public class AssistantPanel : MonoBehaviour
    {
        public event Action<bool> OverviewChanged;
        public event Action<bool> XrayChanged;
        public event Action RecenterRequested;
        public event Action<string> TakeMeThere;

        public float DisplayHour { get; private set; }
        public string DisplayLabel { get; private set; } = "do_nothing";
        public bool Overview { get; private set; }
        public Transform Left { get; private set; }
        public Transform Right { get; private set; }
        public Transform Dock { get; private set; }

        CascadeNet net; TwinView twin; Camera cam; VoiceInput voice;
        const float Scale = 0.0007f;
        const float W = 1060, H = 700;

        // header + talk
        TextMeshProUGUI statusPill; Image statusBg; TextMeshProUGUI subline;
        Button micBtn; Image micRing; TextMeshProUGUI micIcon, micHint, transcript;
        readonly List<Button> chips = new();
        // team
        TextMeshProUGUI teamCaps; Image nvBadge; TextMeshProUGUI nvText;
        readonly Dictionary<string, (Image ring, Image fill, TextMeshProUGUI initials, TextMeshProUGUI name)> avatars = new();
        TextMeshProUGUI nowText, nowResult, logCaption;
        readonly List<TextMeshProUGUI> logRows = new();
        // right column
        TextMeshProUGUI foundCaps, foundText; Button speakBtn;
        readonly List<(TextMeshProUGUI text, Image fill)> hyps = new();
        GameObject futureRoot; Button noneBtn, planBtn; TextMeshProUGUI noneText, planText;
        Slider slider; TextMeshProUGUI clockText, playText; bool dragging, playing, settingSlider; float localHour, sendAt;
        readonly TextMeshProUGUI[] counters = new TextMeshProUGUI[4];
        RawImage cloud; Texture2D cloudTex; ulong cloudFor = ulong.MaxValue; string cloudLabel; RectTransform nowLine;
        GameObject actRoot; TextMeshProUGUI actCaps, actSummary; Button approveBtn, goBtn; TextMeshProUGUI approveText, goText;
        readonly List<(Button b, TextMeshProUGUI t)> actRows = new();
        readonly List<ulong> actIds = new();
        // dock
        Button walkBtn, mapBtn, techBtn, mgrBtn, xrayBtn; bool xray;
        // card
        Canvas card; TextMeshProUGUI cardSub, cardTitle, cardBody; Button cardSend, cardGo; TextMeshProUGUI cardSendText; string cardId; string cardKind = "asset";
        IssueView issues; GameObject findRoot, issuesRoot; TextMeshProUGUI issuesCaps;
        readonly List<(Button b, TextMeshProUGUI t)> issueRows = new(); readonly List<string> issueIds = new();

        int seenVersion = -1;
        string spokenKey = "";
        readonly HashSet<ulong> spokenCritiques = new();

        public void Bind(CascadeNet n, TwinView t, Camera c, VoiceInput v, bool showRecenter)
        {
            net = n; twin = t; cam = c; voice = v;
            // Two cards (lower-left: talk + team, lower-right: findings + plan) keep the
            // middle of your view free for the building.
            var lc = UiKit.WorldCanvas("AssistantLeft", transform, new Vector2(540, H), cam);
            lc.transform.localScale = Vector3.one * Scale;
            BuildLeft(CUi.Panel(lc.transform, "Panel"));
            var rc = UiKit.WorldCanvas("AssistantRight", transform, new Vector2(540, H), cam);
            rc.transform.localScale = Vector3.one * Scale;
            var shift = UiKit.Rect(CUi.Panel(rc.transform, "Panel"), "Shift");
            shift.anchorMin = shift.anchorMax = new Vector2(0, 1); shift.pivot = new Vector2(0, 1);
            shift.anchoredPosition = new Vector2(-522, 0); shift.sizeDelta = new Vector2(W, H);
            findRoot = Container(shift, "Findings");
            BuildRight(findRoot.transform);
            issuesRoot = Container(shift, "Issues");
            BuildIssues(issuesRoot.transform);
            Left = lc.transform; Right = rc.transform;
            ((RectTransform)Left).pivot = new Vector2(0.5f, 1f);   // poses refer to the top edge
            ((RectTransform)Right).pivot = new Vector2(0.5f, 1f);
            BuildDock(showRecenter);
            BuildCard();
            voice.Transcribed += OnVoice;
            twin.Picked += (id, isAsset) => { cardId = id; cardKind = isAsset ? "asset" : "room"; RefreshCard(); };
        }

        // ================================================================ build

        void BuildLeft(Transform p)
        {
            CUi.Text(p, "<b>Cascade</b>", 30, CUi.Ink, 28, 20, 200, 40);
            (statusBg, statusPill) = CUi.Pill(p, "All normal", CUi.GoodSoft, CUi.Good, 330, 28, 180, 28, 13);
            subline = CUi.Text(p, "Harbor Point · Floor 3", 14, CUi.InkMid, 28, 58, 480, 20);

            // hold to talk
            var talk = CUi.Card(p, CUi.AccentSoft, 24, 90, 492, 108, 0.6f);
            micRing = UiKit.Box(talk.transform, "Ring", CUi.Accent.A(0.25f), 2.2f).At(12, 8, 92, 92);
            (micBtn, micIcon) = CUi.Button(talk.transform, "MIC", CUi.Style.Primary, null, 20, 16, 76, 76, 15);
            ((RectTransform)micBtn.transform).GetComponent<Image>().pixelsPerUnitMultiplier = 1f / 1.8f;
            var hold = micBtn.gameObject.AddComponent<UiKit.Hold>();
            hold.Changed += down => { if (down) voice.Begin(); else voice.End(); };
            micHint = CUi.Text(talk.transform, "Hold the button and speak", 13, CUi.Accent, 120, 16, 360, 18, FontStyles.Bold);
            transcript = CUi.Text(talk.transform, "“Anything I should worry about?”", 18, CUi.Ink, 120, 38, 360, 62);

            string[] q = { "Anything to worry about?", "If Chiller 2 fails?", "Who's at risk?" };
            string[] full = { "Anything I should worry about?", "What happens if Chiller 2 fails tonight?", "Which tenants are at risk tonight?" };
            float x = 24;
            for (int i = 0; i < q.Length; i++)
            {
                float w = i == 0 ? 210 : 138;
                int k = i;
                var (b, _) = CUi.Button(p, q[i], CUi.Style.Secondary, () => Ask(full[k]), x, 208, w, 36, 13);
                chips.Add(b);
                x += w + 6;
            }

            // agent team
            teamCaps = CUi.Caps(p, "Agent team", CUi.InkMid, 28, 262, 300);
            nvBadge = UiKit.Box(p, "NV", CUi.Surface, 0.5f).At(316, 256, 200, 24);
            nvText = UiKit.Text(nvBadge.transform, "", 11, CUi.InkMid, FontStyles.Bold, TextAlignmentOptions.Center).Stretch();
            for (int i = 0; i < CascadeStyle.Agents.Length; i++)
            {
                var a = CascadeStyle.Agents[i];
                float ax = 26 + i * 70;
                var ring = UiKit.Box(p, "Ring", CascadeStyle.AgentColor(a), 1.3f).At(ax + 6, 290, 54, 54);
                var fill = UiKit.Box(ring.transform, "Fill", Color.white, 1.25f).At(4, 4, 46, 46);
                var ini = UiKit.Text(fill.transform, Initials(a), 15, CUi.Ink, FontStyles.Bold, TextAlignmentOptions.Center).Stretch();
                var name = CUi.Text(p, ShortName(a), 10, CUi.InkMid, ax - 4, 348, 74, 14, FontStyles.Bold, TextAlignmentOptions.Top);
                avatars[a] = (ring, fill, ini, name);
            }
            var now = CUi.Card(p, CUi.Surface, 24, 372, 492, 72, 0.5f);
            nowText = CUi.Text(now.transform, "", 15, CUi.Ink, 14, 10, 466, 22, FontStyles.Bold);
            nowText.textWrappingMode = TextWrappingModes.NoWrap;
            nowResult = CUi.Text(now.transform, "", 13, CUi.InkMid, 14, 36, 466, 34);

            logCaption = CUi.Caps(p, "What the team did", CUi.InkLo, 28, 456, 300, 10);
            for (int i = 0; i < 9; i++)
            {
                var r = CUi.Text(p, "", 12, CUi.InkMid, 28, 476 + i * 23, 488, 20);
                r.textWrappingMode = TextWrappingModes.NoWrap;
                logRows.Add(r);
            }
        }

        void BuildRight(Transform p)
        {
            const float X = 544, CW = 492;
            foundCaps = CUi.Caps(p, "What Cascade found", CUi.Accent, X + 4, 24, 300);
            (speakBtn, _) = CUi.Button(p, "Speak", CUi.Style.Ghost, () => Speech.Say(foundText.text), X + CW - 80, 16, 80, 28, 12);
            foundText = CUi.Text(p, "", 18, CUi.Ink, X + 4, 48, CW - 8, 118);
            foundText.overflowMode = TextOverflowModes.Ellipsis;

            for (int i = 0; i < 3; i++)
            {
                float y = 172 + i * 26;
                var txt = CUi.Text(p, "", 12, CUi.InkMid, X + 4, y, CW - 8, 16);
                txt.textWrappingMode = TextWrappingModes.NoWrap;
                UiKit.Box(p, "Track", CUi.Surface, 0.15f).At(X + 4, y + 17, CW - 8, 5);
                var fill = UiKit.Box(p, "Fill", CUi.Accent, 0.15f).At(X + 4, y + 17, 0, 5);
                hyps.Add((txt, fill));
            }

            // the next 24 hours
            futureRoot = CUi.Card(p, CUi.Surface, X, 254, CW, 236, 0.5f).gameObject;
            var f = futureRoot.transform;
            CUi.Caps(f, "The next 24 hours", CUi.InkMid, 14, 12, 200, 10);
            (noneBtn, noneText) = CUi.Button(f, "If we do nothing", CUi.Style.Secondary, () => PickLabel("do_nothing"), 190, 6, 140, 30, 12);
            (planBtn, planText) = CUi.Button(f, "With the plan", CUi.Style.Secondary, () => PickLabel("with_plan"), 336, 6, 142, 30, 12);

            var (play, pt) = CUi.Button(f, "►", CUi.Style.Primary, () => { playing = !playing; if (playing && localHour >= 23.9f) localHour = 0; }, 12, 46, 40, 36, 16);
            playText = pt;
            slider = MakeSlider(f, 62, 46, 330, 36);
            clockText = CUi.Text(f, "", 15, CUi.Ink, 400, 52, 86, 24, FontStyles.Bold, TextAlignmentOptions.TopRight);

            string[] caps = { "lost", "tenants hit", "breaches", "hours down" };
            for (int i = 0; i < 4; i++)
            {
                var box = CUi.Card(f, Color.white, 12 + i * 118, 92, 112, 62, 0.4f);
                counters[i] = CUi.Text(box.transform, "–", 24, CUi.Ink, 10, 6, 96, 30, FontStyles.Bold);
                CUi.Text(box.transform, caps[i], 11, CUi.InkMid, 10, 38, 96, 16);
            }
            var chart = UiKit.Box(f, "Chart", Color.white, 0.3f).At(12, 162, 466, 64);
            cloudTex = new Texture2D(466, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            cloud = UiKit.Rect(chart.transform, "Cloud").gameObject.AddComponent<RawImage>();
            cloud.texture = cloudTex; cloud.raycastTarget = false;
            cloud.rectTransform.anchorMin = Vector2.zero; cloud.rectTransform.anchorMax = Vector2.one; cloud.rectTransform.sizeDelta = Vector2.zero;
            CUi.Text(chart.transform, "server room °F · 50 futures", 10, CUi.InkLo, 8, 4, 220, 14);
            nowLine = UiKit.Box(chart.transform, "Now", CUi.Ink.A(0.7f), 0.1f).At(0, 0, 2, 64).rectTransform;
            ClearCloud();

            // actions
            actRoot = new GameObject("Actions", typeof(RectTransform)).GetComponent<RectTransform>().gameObject;
            actRoot.transform.SetParent(p, false);
            var ar = (RectTransform)actRoot.transform; ar.anchorMin = ar.anchorMax = new Vector2(0, 1); ar.pivot = new Vector2(0, 1);
            ar.anchoredPosition = new Vector2(X, -500); ar.sizeDelta = new Vector2(CW, 190);
            actCaps = CUi.Caps(actRoot.transform, "The plan", CUi.InkMid, 4, 0, 300);
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                var row = CUi.Button(actRoot.transform, "", CUi.Style.Secondary, () => ToggleAction(k), 0, 20 + i * 30, CW, 27, 12);
                row.t.alignment = TextAlignmentOptions.Left;
                actRows.Add(row);
            }
            actSummary = CUi.Text(actRoot.transform, "", 13, CUi.InkMid, 4, 22, CW - 8, 110);
            (approveBtn, approveText) = CUi.Button(actRoot.transform, "Approve plan", CUi.Style.Primary, ApprovePlan, 0, 144, CW / 2 - 4, 44, 16);
            (goBtn, goText) = CUi.Button(actRoot.transform, "Take me there", CUi.Style.Secondary, () => TakeMeThere?.Invoke(RootAsset()), CW / 2 + 4, 144, CW / 2 - 4, 44, 16);
        }

        Slider MakeSlider(Transform parent, float x, float y, float w, float h)
        {
            var root = UiKit.Rect(parent, "Slider");
            root.anchorMin = root.anchorMax = new Vector2(0, 1); root.pivot = new Vector2(0, 1);
            root.anchoredPosition = new Vector2(x, -y); root.sizeDelta = new Vector2(w, h);
            var bg = UiKit.Box(root, "Track", CUi.SurfaceHi, 0.3f, raycast: true);
            bg.rectTransform.anchorMin = new Vector2(0, 0.5f); bg.rectTransform.anchorMax = new Vector2(1, 0.5f); bg.rectTransform.sizeDelta = new Vector2(0, 10);
            var fa = UiKit.Rect(root, "FillArea"); fa.anchorMin = new Vector2(0, 0.5f); fa.anchorMax = new Vector2(1, 0.5f); fa.sizeDelta = new Vector2(0, 10);
            var fill = UiKit.Box(fa, "Fill", CUi.Accent, 0.3f); fill.rectTransform.sizeDelta = Vector2.zero;
            var ha = UiKit.Rect(root, "HandleArea"); ha.anchorMin = Vector2.zero; ha.anchorMax = Vector2.one; ha.sizeDelta = Vector2.zero;
            var handle = UiKit.Box(ha, "Handle", Color.white, 0.8f, raycast: true); handle.rectTransform.sizeDelta = new Vector2(28, 0);
            var outline = handle.gameObject.AddComponent<Outline>(); outline.effectColor = CUi.Accent; outline.effectDistance = new Vector2(2, -2);
            var s = root.gameObject.AddComponent<Slider>();
            s.fillRect = fill.rectTransform; s.handleRect = handle.rectTransform; s.targetGraphic = handle;
            s.minValue = 0; s.maxValue = 24;
            s.onValueChanged.AddListener(v => { if (settingSlider) return; dragging = true; playing = false; localHour = v; sendAt = 0; });
            var hold = root.gameObject.AddComponent<UiKit.Hold>();
            hold.Changed += down => { dragging = down; if (!down) net.SetTime(localHour, DisplayLabel); };
            return s;
        }

        static GameObject Container(Transform parent, string name)
        {
            var r = UiKit.Rect(parent, name);
            r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1);
            r.anchoredPosition = Vector2.zero; r.sizeDelta = new Vector2(W, H);
            return r.gameObject;
        }

        public void BindIssues(IssueView v)
        {
            issues = v;
            issues.Picked += id => { cardId = id; cardKind = "issue"; RefreshCard(); };
        }

        void BuildIssues(Transform p)
        {
            const float X = 544, CW = 492;
            issuesCaps = CUi.Caps(p, "Issues in this building", CUi.Accent, X + 4, 24, 400);
            CUi.Text(p, "Found by inspections, sensors and tickets. Tap one to walk to it.", 13, CUi.InkMid, X + 4, 46, CW, 20);
            for (int i = 0; i < 6; i++)
            {
                int k = i;
                var row = CUi.Button(p, "", CUi.Style.Secondary, () => { if (k < issueIds.Count) { cardId = issueIds[k]; cardKind = "issue"; if (issues) issues.Selected = cardId; RefreshCard(); TakeMeThere?.Invoke(cardId); } },
                    X, 76 + i * 58, CW, 52, 13);
                row.t.alignment = TextAlignmentOptions.Left;
                row.t.textWrappingMode = TextWrappingModes.Normal;
                issueRows.Add(row);
            }
        }

        void RefreshIssues()
        {
            var rows = net.Db.Issue.Iter().OrderBy(i => i.Status == "open" ? 0 : 1).ThenBy(i => SevRank(i.Severity)).ToList();
            issueIds.Clear();
            int open = rows.Count(r => r.Status == "open");
            issuesCaps.text = $"ISSUES IN THIS BUILDING · {open} OPEN";
            for (int i = 0; i < issueRows.Count; i++)
            {
                var (b, t) = issueRows[i];
                b.gameObject.SetActive(i < rows.Count);
                if (i >= rows.Count) continue;
                var r = rows[i];
                issueIds.Add(r.Id);
                var col = r.Status == "open" ? IssueView.Severity(r.Severity) : CUi.Good;
                string room = twin.RoomRow(r.RoomId)?.Name ?? r.RoomId;
                t.text = $"  <color=#{CUi.Hex(col)}>●</color>  <b>{r.Title}</b>\n      <color=#{CUi.Hex(CUi.InkMid)}>{room} · {IssueView.KindLabel(r.Kind)} · {(r.Status == "open" ? r.Severity : "repair scheduled")}</color>";
                t.color = CUi.Ink;
            }
        }

        static int SevRank(string s) => s switch { "critical" => 0, "high" => 1, "medium" => 2, _ => 3 };

        void BuildDock(bool showRecenter)
        {
            var c = UiKit.WorldCanvas("Dock", transform, new Vector2(W, 64), cam);
            c.transform.localScale = Vector3.one * Scale;
            Dock = c.transform;
            var p = CUi.Panel(c.transform, "Dock", 0.8f);
            CUi.Caps(p, "View", CUi.InkLo, 18, 23, 50, 10);
            walkBtn = CUi.Button(p, "Walk in", CUi.Style.Secondary, () => SetOverview(false), 62, 10, 110, 44, 14).b;
            mapBtn = CUi.Button(p, "Overview", CUi.Style.Secondary, () => SetOverview(true), 176, 10, 110, 44, 14).b;
            CUi.Caps(p, "I am", CUi.InkLo, 306, 23, 40, 10);
            techBtn = CUi.Button(p, "Technician", CUi.Style.Secondary, () => net.SetRole("technician"), 346, 10, 128, 44, 14).b;
            mgrBtn = CUi.Button(p, "Manager", CUi.Style.Secondary, () => net.SetRole("manager"), 478, 10, 110, 44, 14).b;
            xrayBtn = CUi.Button(p, "X-ray", CUi.Style.Secondary, () => { xray = !xray; XrayChanged?.Invoke(xray); }, 612, 10, 96, 44, 14).b;
            float x = 712;
            if (showRecenter) { CUi.Button(p, "Recenter", CUi.Style.Secondary, () => RecenterRequested?.Invoke(), x, 10, 112, 44, 14); x += 116; }
            CUi.Button(p, "Start over", CUi.Style.Ghost, () => { net.ResetDemo(); playing = false; cardId = null; }, x, 10, 120, 44, 14);
        }

        void BuildCard()
        {
            card = UiKit.WorldCanvas("Card", null, new Vector2(440, 300), cam);
            var p = CUi.Panel(card.transform, "Card");
            cardSub = CUi.Caps(p, "", CUi.Accent, 22, 18, 340);
            cardTitle = CUi.Text(p, "", 24, CUi.Ink, 22, 38, 360, 32, FontStyles.Bold);
            CUi.Button(p, "×", CUi.Style.Ghost, () => { twin.Selected = null; cardId = null; RefreshCard(); }, 388, 12, 40, 40, 22);
            cardBody = CUi.Text(p, "", 15, CUi.InkMid, 22, 76, 396, 150);
            (cardGo, _) = CUi.Button(p, "Take me there", CUi.Style.Primary, () => { if (cardId != null) TakeMeThere?.Invoke(cardId); }, 22, 238, 190, 44, 14);
            (cardSend, cardSendText) = CUi.Button(p, "Send technician", CUi.Style.Secondary, () =>
            {
                if (cardId == null) return;
                if (cardKind == "issue") { var i = net.Db.Issue.Id.Find(cardId); if (i != null) Ask($"What should we do about the {i.Title.ToLowerInvariant()}?"); }
                else net.SendTech(cardId);
            }, 220, 238, 198, 44, 14);
            card.gameObject.SetActive(false);
        }

        // ================================================================ actions

        void Ask(string text) { net.Ask(text); transcript.text = $"“{text}”"; }
        void SetOverview(bool on) { Overview = on; OverviewChanged?.Invoke(on); }
        /// Keep the dock in sync when the app changes mode itself (e.g. Take me there).
        public void SyncOverview(bool on) => Overview = on;

        void PickLabel(string label)
        {
            if (label == "with_plan" && net.ScenarioFor("with_plan") == null) return;
            DisplayLabel = label;
            net.SetTime(DisplayHour, label);
        }

        void ToggleAction(int i)
        {
            if (i >= actIds.Count || net.MyRole != "manager") return;
            var a = net.Db.ActionCandidate.Id.Find(actIds[i]);
            if (a == null || a.Status == "done") return;
            net.Approve(a.Id, a.Status != "approved");
        }

        void ApprovePlan()
        {
            var inv = net.Investigation;
            if (inv == null || net.MyRole != "manager") return;
            var acts = net.Db.ActionCandidate.Iter().Where(a => a.InvId == inv.Id && a.Status != "done").ToList();
            bool anyApproved = acts.Any(a => a.Status == "approved");
            if (!anyApproved) foreach (var a in acts.Where(a => a.Recommended)) net.Approve(a.Id, true);
            net.Execute();
        }

        string RootAsset()
        {
            var inv = net.Investigation;
            var top = inv == null ? null : net.Db.Hypothesis.Iter().Where(h => h.InvId == inv.Id).OrderByDescending(h => h.Likelihood).FirstOrDefault();
            return top != null && twin.Has(top.AssetId) ? top.AssetId : "CH-2";
        }

        /// Voice: a few local commands, everything else goes to the agent team.
        void OnVoice(string text)
        {
            var t = text.ToLowerInvariant();
            transcript.text = $"“{text}”";
            if (t.Contains("approve") || t.Contains("go ahead") || t.Contains("execute")) { ApprovePlan(); return; }
            if (t.Contains("take me") || t.Contains("show me the way") || t.Contains("where do i go")) { TakeMeThere?.Invoke(RootAsset()); return; }
            if (t.Contains("overview") || t.Contains("show the map")) { SetOverview(true); return; }
            if (t.Contains("walk in") || t.Contains("life size")) { SetOverview(false); return; }
            if (t.Contains("x-ray") || t.Contains("x ray") || t.Contains("see through")) { xray = !xray; XrayChanged?.Invoke(xray); return; }
            if (t.Contains("start over") || t.Contains("reset")) { net.ResetDemo(); return; }
            net.Ask(text);
        }

        // ================================================================ update

        void Update()
        {
            if (net == null) return;
            UpdateVoice();
            if (!net.Ready)
            {
                SetStatus(net.Status == "offline" ? "Offline" : "Connecting…", CUi.WarnSoft, CUi.Warn);
                return;
            }
            UpdateTime();
            bool changed = net.Version != seenVersion;
            seenVersion = net.Version;
            if (changed) Refresh();
            UpdateFuture();
            UpdateDock();
            if (cardId != null) PlaceCard(changed || Time.frameCount % 20 == 0);
        }

        void UpdateVoice()
        {
            var s = voice.Current;
            micHint.text = s switch
            {
                VoiceInput.State.Loading => "Loading on-device speech…",
                VoiceInput.State.Listening => "Listening… release to send",
                VoiceInput.State.Transcribing => "Understanding…",
                VoiceInput.State.Unavailable => "Voice unavailable · use the buttons below",
                _ => "Hold the button and speak",
            };
            micIcon.text = s == VoiceInput.State.Listening ? "●" : s == VoiceInput.State.Transcribing ? "…" : "MIC";
            CUi.Restyle(micBtn, micIcon, s == VoiceInput.State.Listening ? CUi.Style.Danger : CUi.Style.Primary);
            float ring = s == VoiceInput.State.Listening ? 1f + voice.Level * 0.25f : 1f;
            micRing.rectTransform.localScale = Vector3.one * Mathf.Lerp(micRing.rectTransform.localScale.x, ring, Time.deltaTime * 12f);
            micRing.color = (s == VoiceInput.State.Listening ? CUi.Danger : CUi.Accent).A(0.25f);
            if (Input.GetKeyDown(KeyCode.V)) voice.Begin();
            if (Input.GetKeyUp(KeyCode.V)) voice.End();
        }

        void SetStatus(string text, Color bg, Color fg) { statusPill.text = text; statusBg.color = bg; statusPill.color = fg; }

        void Refresh()
        {
            var inv = net.Investigation;
            // idle: one compact card; the findings card appears once there is something to show
            ((RectTransform)Left).sizeDelta = new Vector2(540, inv == null ? 456 : H);
            ((RectTransform)Right).sizeDelta = new Vector2(540, inv == null ? 76 + 58 * Mathf.Max(1, net.Db.Issue.Count) + 20 : H);
            findRoot.SetActive(inv != null);
            issuesRoot.SetActive(inv == null);
            if (inv == null) RefreshIssues();
            logCaption.gameObject.SetActive(inv != null);
            var ai = net.Db.AiStatus.Id.Find(0);
            bool nemotron = ai != null && ai.Enabled;
            teamCaps.text = "AGENT TEAM";
            nvBadge.color = nemotron ? CUi.Nvidia : CUi.Surface;
            nvText.color = nemotron ? Color.black : CUi.InkMid;
            nvText.text = nemotron ? $"NEMOTRON · {Short(ai.ModelLarge)}" : "SCRIPTED · add NVIDIA key";

            // status
            if (inv == null)
            {
                var risky = net.Db.Asset.Iter().Where(a => a.Health != "ok").ToList();
                if (risky.Count > 0) SetStatus("Something's off", CUi.WarnSoft, CUi.Warn); else SetStatus("All normal", CUi.GoodSoft, CUi.Good);
            }
            else if (inv.Status == "investigating") SetStatus("Investigating", CUi.AccentSoft, CUi.Accent);
            else if (inv.Status == "awaiting_approval") SetStatus("Action needed", CUi.DangerSoft, CUi.Danger);
            else if (inv.Status == "resolved") SetStatus("Plan running", CUi.GoodSoft, CUi.Good);
            subline.text = inv == null ? "Harbor Point · Floor 3 · Tue 14:00 · 7 agents watching 6 assets"
                : (inv.Source == "watchdog" ? "The watchdog noticed something: " : "You asked: ") + inv.Trigger;

            // team
            var steps = inv == null ? new List<Net.AgentStep>() : net.Db.AgentStep.Iter().Where(s => s.InvId == inv.Id).OrderBy(s => s.Seq).ToList();
            foreach (var a in CascadeStyle.Agents)
            {
                var (ring, fill, ini, name) = avatars[a];
                var last = steps.LastOrDefault(s => s.Agent == a);
                bool active = last != null && (last.Status == "running" || last.Status == "thinking");
                bool revise = last != null && last.Status == "revise";
                var col = CascadeStyle.AgentColor(a);
                if (a == "critic") col = CUi.InkMid;
                ring.color = revise ? CUi.Danger : last == null ? CUi.SurfaceHi : col;
                fill.color = active ? col : revise ? CUi.DangerSoft : Color.white;
                ini.color = active ? Color.white : last == null ? CUi.InkLo : CUi.Ink;
                name.color = last == null ? CUi.InkLo : CUi.Ink;
            }
            var cur = steps.LastOrDefault(s => s.Status is "running" or "thinking") ?? steps.LastOrDefault();
            if (cur == null)
            {
                nowText.text = inv == null ? "Ready when you are." : "Starting…";
                nowResult.text = inv == null ? "Ask by voice or tap a question. A watchdog also opens investigations on its own." : "";
            }
            else
            {
                string who = CascadeStyle.AgentName(cur.Agent);
                string c = CUi.Hex(cur.Agent == "critic" ? CUi.InkMid : CascadeStyle.AgentColor(cur.Agent));
                nowText.text = cur.Status == "thinking" ? $"<color=#{c}>{who}</color> is thinking…"
                    : cur.Status == "running" ? $"<color=#{c}>{who}</color> · {cur.Why}"
                    : inv.Status == "investigating" ? $"<color=#{c}>{who}</color> · {cur.Why}" : "The team is done.";
                nowResult.text = cur.Status == "thinking" ? (string.IsNullOrEmpty(cur.Model) ? "" : $"Nemotron · {Short(cur.Model)}")
                    : cur.Tool == "reasoning" ? $"<i>{cur.Why} {cur.Result}</i>"
                    : string.IsNullOrEmpty(cur.Result) ? cur.Tool.Replace('_', ' ') + "…" : cur.Result;
                if (inv.Status != "investigating") nowResult.text = $"{steps.Count(s => s.Status is "done" or "revise")} steps · {net.Db.Evidence.Iter().Count(e => e.InvId == inv.Id)} pieces of evidence · critic {(net.Db.Critique.Iter().Any(x => x.InvId == inv.Id && x.Verdict == "revise") ? "pushed back, then approved" : "approved")}";
            }
            var shown = steps.Where(s => s.Status is "done" or "revise" or "error").ToList();
            shown = shown.Skip(Math.Max(0, shown.Count - logRows.Count)).ToList();
            for (int i = 0; i < logRows.Count; i++)
            {
                if (i >= shown.Count) { logRows[i].text = ""; continue; }
                var s = shown[i];
                var col = s.Status == "revise" || s.Status == "error" ? CUi.Danger : s.Agent == "critic" ? CUi.InkMid : CascadeStyle.AgentColor(s.Agent);
                string body = s.Tool == "reasoning" ? $"<i>thinking: {s.Why}</i>" : $"{s.Why} <color=#{CUi.Hex(CUi.InkLo)}>→ {s.Result}</color>";
                logRows[i].text = $"<color=#{CUi.Hex(col)}><b>{ShortName(s.Agent)}</b></color>  {body}";
                if (s.Status == "revise" && spokenCritiques.Add(s.Id)) Speech.Say("The critic pushed back: " + s.Result.Replace("REVISE: ", ""));
            }

            // found
            var role = net.MyRole;
            var answers = inv == null ? new List<Answer>() : net.Db.Answer.Iter().Where(a => a.InvId == inv.Id).ToList();
            var mine = answers.FirstOrDefault(a => a.Role == role) ?? answers.FirstOrDefault();
            foundCaps.text = mine == null ? "WHAT CASCADE FOUND" : $"FOR YOU · {(role == "technician" ? "TECHNICIAN" : "MANAGER")}{(mine.ByNemotron ? " · NEMOTRON" : "")}";
            foundText.text = mine?.Text ?? (inv == null
                ? "Ask anything about the building. Seven AI agents fuse sensor data, work orders, the asset registry and leases, then simulate the next 24 hours."
                : "The team is investigating. Watch the orbs fly to what they are checking.");
            speakBtn.gameObject.SetActive(mine != null);
            if (mine != null)
            {
                var key = mine.Id + mine.Text;
                if (key != spokenKey) { spokenKey = key; Speech.Stop(); Speech.Say(mine.Text); }
            }

            var hs = inv == null ? new List<Hypothesis>() : net.Db.Hypothesis.Iter().Where(h => h.InvId == inv.Id).OrderByDescending(h => h.Likelihood).ToList();
            for (int i = 0; i < hyps.Count; i++)
            {
                var (text, fill) = hyps[i];
                if (i >= hs.Count) { text.text = ""; fill.rectTransform.sizeDelta = new Vector2(0, 5); continue; }
                text.text = $"<b>{hs[i].Likelihood * 100:0}%</b>  {hs[i].Text}";
                fill.rectTransform.sizeDelta = new Vector2(484 * hs[i].Likelihood, 5);
                fill.color = i == 0 ? CUi.Accent : CUi.InkLo;
            }

            // plan
            var acts = inv == null ? new List<ActionCandidate>() : net.Db.ActionCandidate.Iter().Where(a => a.InvId == inv.Id).OrderBy(a => a.Rank == 0 ? 99 : a.Rank).ThenBy(a => a.Id).ToList();
            bool manager = role == "manager";
            bool done = acts.Count > 0 && acts.Any(a => a.Status == "done");
            futureRoot.SetActive(net.ScenarioFor("do_nothing") != null);
            actRoot.SetActive(acts.Count > 0);
            actIds.Clear();
            bool showRows = !done && manager;
            for (int i = 0; i < actRows.Count; i++)
            {
                var (b, t) = actRows[i];
                b.gameObject.SetActive(showRows && i < acts.Count);
                if (i >= acts.Count) continue;
                var a = acts[i];
                actIds.Add(a.Id);
                bool on = a.Status == "approved" || (a.Recommended && !acts.Any(x => x.Status == "approved"));
                string box = a.Status == "approved" ? "[x]" : "[ ]";
                t.text = $"  {box}  <b>{a.Title}</b>  <color=#{CUi.Hex(CUi.InkMid)}>{(a.Cost > 0 ? CUi.Money(a.Cost) : "free")}{(a.Saved > 0 ? $" · saves {CUi.Money(a.Saved)}" : "")}{(a.Recommended ? " · recommended" : "")}</color>";
                CUi.Restyle(b, t, CUi.Style.Secondary);
                if (a.Status == "approved") { var cb = b.colors; cb.normalColor = CUi.GoodSoft; b.colors = cb; }
                t.color = on || a.Status == "approved" ? CUi.Ink : CUi.InkMid;
            }
            actSummary.gameObject.SetActive(!showRows);
            var created = net.Db.WorkOrder.Iter().Where(w => w.CreatedBy == "cascade").Select(w => $"• {w.Id} {w.Title}")
                .Concat(net.Db.Notice.Iter().Select(n => "• Tenant notice sent to Northwind Analytics")).ToList();
            actSummary.text = done
                ? $"<color=#{CUi.Hex(CUi.Good)}><b>Plan approved and running.</b></color>\n{string.Join("\n", created.Take(3))}"
                : !manager ? $"The manager approves the plan. {(acts.Count > 0 ? "Recommended: " + string.Join(", ", acts.Where(a => a.Recommended).Select(a => a.Title.ToLowerInvariant())) + "." : "")}" : "";
            actCaps.text = done ? "THE PLAN · DONE" : manager ? "THE PLAN · TAP TO CHANGE" : "THE PLAN";
            approveBtn.gameObject.SetActive(manager && !done && inv?.Status == "awaiting_approval");
            approveText.text = acts.Any(a => a.Status == "approved") ? "Run approved actions" : "Approve plan";
            goBtn.gameObject.SetActive(acts.Count > 0);
            goText.text = Overview ? "Show me in the building" : "Take me there";
            if (!approveBtn.gameObject.activeSelf) ((RectTransform)goBtn.transform).anchoredPosition = new Vector2(0, -144);
            else ((RectTransform)goBtn.transform).anchoredPosition = new Vector2(492 / 2f + 4, -144);
        }

        void UpdateTime()
        {
            var tl = net.Timeline;
            float shared = tl?.Hour ?? 0;
            string label = tl?.Label ?? "do_nothing";
            if (label == "with_plan" && net.ScenarioFor("with_plan") == null) label = "do_nothing";
            DisplayLabel = label;
            if (playing) { localHour = Mathf.Min(24, localHour + Time.deltaTime * 1.6f); if (localHour >= 24) playing = false; }
            if (playing || dragging)
            {
                sendAt -= Time.deltaTime;
                if (sendAt <= 0) { net.SetTime(localHour, DisplayLabel); sendAt = 0.15f; }
            }
            else localHour = Mathf.MoveTowards(localHour, shared, Mathf.Max(6f, Mathf.Abs(shared - localHour) * 4f) * Time.deltaTime);
            DisplayHour = localHour;
            settingSlider = true; slider.SetValueWithoutNotify(localHour); settingSlider = false;
            playText.text = playing ? "II" : "►";
            clockText.text = (14 + DisplayHour >= 24 ? "Wed " : "Tue ") + CUi.Clock(DisplayHour);
            nowLine.anchoredPosition = new Vector2(DisplayHour / 24f * 464f, 0);
        }

        void UpdateFuture()
        {
            var s = net.ScenarioFor(DisplayLabel);
            var f = net.Frames(s);
            bool hasPlan = net.ScenarioFor("with_plan") != null;
            CUi.Restyle(noneBtn, noneText, DisplayLabel == "do_nothing" ? CUi.Style.Danger : CUi.Style.Secondary);
            CUi.Restyle(planBtn, planText, DisplayLabel == "with_plan" ? CUi.Style.Good : CUi.Style.Secondary);
            planBtn.interactable = hasPlan;
            planText.text = net.ScenarioFor("with_plan")?.Verified == true ? "With the plan (verified)" : "With the plan";
            if (s == null || f == null) { foreach (var c in counters) c.text = "–"; return; }
            var fr = f.At(DisplayHour);
            counters[0].text = CUi.Money(fr.c);
            counters[1].text = fr.ta.ToString();
            counters[2].text = fr.b.ToString();
            counters[3].text = $"{fr.h:0.#}";
            counters[0].color = fr.c > 0 ? CUi.Danger : CUi.Good;
            if (cloudFor != s.Id || cloudLabel != s.Label) { DrawCloud(s, f); cloudFor = s.Id; cloudLabel = s.Label; }
        }

        void UpdateDock()
        {
            Seg(walkBtn, !Overview); Seg(mapBtn, Overview);
            Seg(techBtn, net.MyRole == "technician"); Seg(mgrBtn, net.MyRole == "manager");
            Seg(xrayBtn, xray);
        }

        static void Seg(Button b, bool on) => CUi.Restyle(b, b.GetComponentInChildren<TextMeshProUGUI>(), on ? CUi.Style.Primary : CUi.Style.Secondary);

        // ================================================================ card

        void PlaceCard(bool refresh)
        {
            var t = card.transform;
            bool life = !Overview;
            t.position = cardKind == "issue" && issues ? issues.WorldOf(cardId) + Vector3.up * (life ? 0.55f : 0.05f)
                : twin.WorldOf(cardId, life ? 1.9f : 3.4f) + Vector3.up * (life ? 0.25f : 0.12f);
            t.localScale = Vector3.one * (life ? 0.0012f : 0.0006f);
            var d = t.position - cam.transform.position;
            if (d.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(d, Vector3.up);
            if (refresh) RefreshCard();
        }

        void RefreshCard()
        {
            if (cardId == null || !net.Ready) { card.gameObject.SetActive(false); return; }
            card.gameObject.SetActive(true);
            cardSend.gameObject.SetActive(net.MyRole == "manager" || cardKind == "issue");
            cardSendText.text = cardKind == "issue" ? "Ask the team" : "Send technician";
            var frames = net.Frames(net.ScenarioFor(DisplayLabel));
            if (cardKind == "issue")
            {
                var i = net.Db.Issue.Id.Find(cardId);
                if (i == null) { card.gameObject.SetActive(false); return; }
                var col = i.Status == "open" ? IssueView.Severity(i.Severity) : CUi.Good;
                cardSub.text = $"{IssueView.KindLabel(i.Kind).ToUpperInvariant()} · {twin.RoomRow(i.RoomId)?.Name}";
                cardTitle.text = i.Title;
                cardBody.text = $"<color=#{CUi.Hex(col)}><b>{(i.Status == "open" ? i.Severity.ToUpperInvariant() + " · open" : "Repair scheduled")}</b></color>\n{i.Detail}\n<color=#{CUi.Hex(CUi.InkLo)}>Source: {i.Source}{(string.IsNullOrEmpty(i.AssetId) ? "" : " · linked to " + (twin.AssetRow(i.AssetId)?.Name ?? i.AssetId))}</color>";
                return;
            }
            if (cardKind == "asset")
            {
                var a = net.Db.Asset.Id.Find(cardId);
                if (a == null) { card.gameObject.SetActive(false); return; }
                int age = 2026 - (int)a.InstallYear;
                var wos = net.Db.WorkOrder.Iter().Where(w => w.AssetId == a.Id && w.Status != "closed").ToList();
                var feeds = net.Db.Dependency.Iter().Where(d => d.FromId == a.Id).Select(d => twin.AssetRow(d.ToId)?.Name ?? twin.RoomRow(d.ToId)?.Name ?? d.ToId).ToList();
                string state = twin.StateOf(a, frames);
                cardSub.text = $"{a.Kind.ToUpperInvariant()} · {twin.RoomRow(a.RoomId)?.Name}";
                cardTitle.text = a.Name;
                cardBody.text = $"<color=#{CUi.Hex(StateColor(state))}><b>{CascadeStyle.HealthLabel(state)}</b></color> at {CUi.Clock(DisplayHour)}\n" +
                                $"{a.ReadingLabel}: <b>{TwinView.Reading(a)}</b> (normal {a.Baseline:0.0})\n" +
                                $"{age} of {a.ExpectedLife} years old\n" +
                                (wos.Count == 0 ? "No open tickets" : $"<b>{wos.Count} open tickets</b>: {string.Join(", ", wos.Select(w => w.Title))}") +
                                (feeds.Count > 0 ? $"\nKeeps cool: {string.Join(", ", feeds)}" : "");
            }
            else
            {
                var r = net.Db.Room.Id.Find(cardId);
                if (r == null) { card.gameObject.SetActive(false); return; }
                var tn = string.IsNullOrEmpty(r.TenantId) ? null : net.Db.Tenant.Id.Find(r.TenantId);
                float temp = frames != null ? frames.Temp(r.Id, DisplayHour) : r.StartTemp;
                cardSub.text = r.Kind == "server" ? "SERVER ROOM" : r.Kind.ToUpperInvariant();
                cardTitle.text = r.Name;
                cardBody.text = (float.IsNaN(temp) || r.Demand <= 0 ? "" : $"<b>{temp:0}°F</b> at {CUi.Clock(DisplayHour)} · limit {r.LimitTemp:0}°F\n") +
                                (tn != null ? $"Tenant: <b>{tn.Name}</b>\nDowntime costs ${tn.CostPerHour:N0}/hour\n{tn.Clause}" : "Common area");
            }
        }

        static Color StateColor(string s) => s switch
        {
            "failed" or "critical" => CUi.Danger,
            "degraded" or "watch" or "starved" => CUi.Warn,
            "repair" => CUi.Accent,
            _ => CUi.Good,
        };

        // ================================================================ chart

        void ClearCloud() { cloudTex.SetPixels32(new Color32[cloudTex.width * cloudTex.height]); cloudTex.Apply(); }

        void DrawCloud(Scenario s, FrameSet f)
        {
            int Wt = cloudTex.width, Ht = cloudTex.height;
            var px = new Color[Wt * Ht];
            float Y(float temp) => Mathf.Clamp((temp - 62f) / 46f, 0, 1) * (Ht - 4) + 2;
            void Line(float x0, float y0, float x1, float y1, Color c)
            {
                int n = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0))) + 1;
                for (int i = 0; i <= n; i++)
                {
                    float u = i / (float)n;
                    int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, u)), y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, u));
                    for (int dy = 0; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (x < 0 || x >= Wt || yy < 0 || yy >= Ht) continue;
                        var o = px[yy * Wt + x];
                        px[yy * Wt + x] = new Color(Mathf.Lerp(o.r, c.r, c.a), Mathf.Lerp(o.g, c.g, c.a), Mathf.Lerp(o.b, c.b, c.a), Mathf.Min(1, o.a + c.a));
                    }
                }
            }
            int ly = Mathf.RoundToInt(Y(80));
            for (int x = 0; x + 1 < Wt; x += 2) if (x % 12 < 7) { px[ly * Wt + x] = CUi.Danger; px[ly * Wt + x + 1] = CUi.Danger; }
            var runC = (s.Label == "with_plan" ? CUi.Good : CUi.Danger).A(0.16f);
            if (f.risk != null)
                foreach (var run in f.risk)
                    for (int i = 0; i + 1 < run.Length; i++)
                        Line(i * (Wt - 1f) / (run.Length - 1), Y(run[i]), (i + 1) * (Wt - 1f) / (run.Length - 1), Y(run[i + 1]), runC);
            int si = Array.IndexOf(f.rooms, "SRV300");
            if (si >= 0)
                for (int i = 0; i + 1 < f.frames.Length; i++)
                    Line(i * (Wt - 1f) / (f.frames.Length - 1), Y(f.frames[i].t[si]), (i + 1) * (Wt - 1f) / (f.frames.Length - 1), Y(f.frames[i + 1].t[si]), CUi.Ink);
            cloudTex.SetPixels(px);
            cloudTex.Apply();
        }

        // ================================================================ helpers

        static string Initials(string a) => a switch
        {
            "planner" => "PL", "signal" => "SA", "historian" => "HI", "lease" => "LA", "simulation" => "SE", "critic" => "CR", "narrator" => "NA", _ => "?",
        };

        static string ShortName(string a) => a switch
        {
            "planner" => "Planner", "signal" => "Signals", "historian" => "Historian", "lease" => "Leases", "simulation" => "Simulator", "critic" => "Critic", "narrator" => "Narrator", _ => a,
        };

        static string Short(string model) => string.IsNullOrEmpty(model) ? "" : model.Split('/').Last().Replace("llama-3.3-", "").ToUpperInvariant();
    }
}
