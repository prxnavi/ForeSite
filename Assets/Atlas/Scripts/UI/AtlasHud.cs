using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Atlas
{
    /// <summary>
    /// Floating panels around the table:
    ///   mission bar + role picker (behind the map), guide panel (left),
    ///   agent reasoning panel (right), and a node card above the selection.
    /// Everything shown here comes from backend rows; buttons call reducers.
    /// </summary>
    public class AtlasHud : MonoBehaviour
    {
        /// Raised while the talk button is held. Wire speech-to-text here.
        public event Action<bool> PushToTalk;

        /// Raised by the Recenter button (only shown when ShowRecenter is set before Bind).
        public event Action RecenterRequested;
        public bool ShowRecenter;

        IAtlasBackend backend;
        AtlasMapView map;
        Camera cam;
        Transform viewer;
        string userName;
        Role role = Role.Engineer;
        string selected;
        readonly List<Transform> facing = new();

        // mission bar
        TextMeshProUGUI missionCaps, missionTitle;
        readonly Button[] roleButtons = new Button[3];
        readonly TextMeshProUGUI[] roleLabels = new TextMeshProUGUI[3];

        // guide
        TextMeshProUGUI guideTitle, guideBody, guideStatus, guideQuote, micLabel;
        readonly TextMeshProUGUI[] askLabels = new TextMeshProUGUI[4];
        readonly string[] askQuestions = new string[4];

        // reasoning
        TextMeshProUGUI reasonQuestion, reasonPill;
        Image reasonPillBg;
        RectTransform stepsRoot;
        readonly List<StepRow> rows = new();
        ulong currentTask;

        // node card
        RectTransform cardCanvas;
        CanvasGroup cardGroup;
        TextMeshProUGUI cardCode, cardFile, cardTitle, cardStatus, cardBody;
        Image cardStatusBg;

        class StepRow
        {
            public Image Dot;
            public TextMeshProUGUI Phase, Text;
            public bool Done;
        }

        static readonly string[] RoleTabs = { "Engineer", "PM", "Architect" };

        public void Bind(IAtlasBackend b, AtlasMapView m, Camera c, Transform v, string displayName)
        {
            backend = b; map = m; cam = c; viewer = v; userName = displayName;

            BuildMissionBar();
            BuildGuide();
            BuildReasoning();
            BuildCard();

            b.ParticipantUpdated += OnParticipant;
            b.TaskUpdated += OnTask;
            b.StepInserted += OnStepInserted;
            b.StepUpdated += OnStepUpdated;
            m.CommandApplied += _ => RefreshGuide();

            RefreshRole();
            RefreshGuide();
        }

        // ================= build =================

        RectTransform PanelCanvas(string name, Vector3 localPos, Vector2 size)
        {
            var canvas = UiKit.WorldCanvas(name, transform, size, cam);
            canvas.transform.localPosition = localPos;
            facing.Add(canvas.transform);
            return UiKit.Panel(canvas.transform, "Panel");
        }

        void BuildMissionBar()
        {
            float width = ShowRecenter ? 940 : 820;
            var p = PanelCanvas("MissionBar", new Vector3(0, 0.30f, 0.40f), new Vector2(width, 84));
            missionCaps = UiKit.Caps(p, "", AtlasStyle.AccentSoft, 11).At(24, 18, 440, 16);
            missionTitle = UiKit.Text(p, "", 21, AtlasStyle.TextHi, FontStyles.Bold).At(24, 38, 450, 30);
            missionTitle.textWrappingMode = TextWrappingModes.NoWrap;

            float segX = width - 3 - 20 - 308;
            if (ShowRecenter)
                UiKit.Button(p, "Recenter", false, () => RecenterRequested?.Invoke(), out _, fontSize: 15)
                    .At(segX - 12 - 112, 20, 112, 44);
            var seg = UiKit.Box(p, "Roles", AtlasStyle.Surface, 0.5f).At(segX, 20, 308, 44);
            for (int i = 0; i < 3; i++)
            {
                var r = (Role)i;
                roleButtons[i] = UiKit.Button(seg.transform, RoleTabs[i], false, () => backend.SetRole(r), out roleLabels[i], fontSize: 15)
                    .At(4 + i * 100, 4, 100, 36);
            }
        }

        void BuildGuide()
        {
            var p = PanelCanvas("GuidePanel", new Vector3(-0.68f, 0.22f, 0.10f), new Vector2(440, 620));
            const float x = 28, w = 381;
            UiKit.Caps(p, "Atlas guide", AtlasStyle.AccentSoft).At(x, 26, w, 16);
            guideTitle = UiKit.Text(p, "", 28, AtlasStyle.TextHi, FontStyles.Bold).At(x, 50, w, 38);
            guideTitle.textWrappingMode = TextWrappingModes.NoWrap;
            guideBody = UiKit.Text(p, "", 16, AtlasStyle.TextMid).At(x, 94, w, 72);

            UiKit.Caps(p, "Ask Atlas", AtlasStyle.TextLo).At(x, 180, w, 16);
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                UiKit.Button(p, "", false, () => backend.AskQuestion(askQuestions[idx]), out askLabels[i],
                    TextAlignmentOptions.MidlineLeft, 15).At(x, 204 + i * 52, w, 44);
                askLabels[i].fontStyle = FontStyles.Normal;
            }

            UiKit.Box(p, "Divider", AtlasStyle.PanelEdge, 0.05f).At(x, 420, w, 1.5f);
            guideStatus = UiKit.Caps(p, "Guide · listening", AtlasStyle.AccentSoft, 11).At(x, 440, w, 16);
            guideQuote = UiKit.Text(p, $"\"Hi {userName}. Start with the payment path, and I'll explain one piece at a time.\"",
                16, AtlasStyle.TextHi, FontStyles.Italic).At(x, 464, w, 74);

            var mic = UiKit.Button(p, "Hold to talk", true, null, out micLabel).At(x, 548, w, 44);
            var hold = mic.gameObject.AddComponent<UiKit.Hold>();
            hold.Changed = down =>
            {
                micLabel.text = down ? "Listening…" : "Hold to talk";
                PushToTalk?.Invoke(down);
            };
        }

        void BuildReasoning()
        {
            var p = PanelCanvas("ReasoningPanel", new Vector3(0.68f, 0.22f, 0.10f), new Vector2(440, 620));
            const float x = 28, w = 381;
            UiKit.Caps(p, "Agent reasoning", AtlasStyle.AccentSoft).At(x, 26, 250, 16);
            reasonPillBg = UiKit.Box(p, "Pill", AtlasStyle.Surface, 0.5f).At(x + w - 104, 20, 104, 26);
            reasonPill = UiKit.Caps(reasonPillBg.transform, "Idle", AtlasStyle.TextLo, 10).Stretch();
            reasonPill.alignment = TextAlignmentOptions.Center;
            reasonPill.characterSpacing = 4f;

            reasonQuestion = UiKit.Text(p, "Ask a question to watch Atlas work through the code, one step at a time.",
                18, AtlasStyle.TextLo).At(x, 56, w, 56);
            UiKit.Box(p, "Divider", AtlasStyle.PanelEdge, 0.05f).At(x, 124, w, 1.5f);
            stepsRoot = UiKit.Rect(p, "Steps").At(x, 142, w, 420);
            UiKit.Caps(p, $"{backend.AgentLabel} · five-step loop", AtlasStyle.TextLo, 10).At(x, 586, w, 14);
        }

        void BuildCard()
        {
            var canvas = UiKit.WorldCanvas("NodeCard", transform, new Vector2(360, 290), cam);
            cardCanvas = (RectTransform)canvas.transform;
            cardCanvas.pivot = new Vector2(0.5f, 0f);
            cardGroup = canvas.gameObject.AddComponent<CanvasGroup>();
            cardGroup.alpha = 0f;
            var p = UiKit.Panel(canvas.transform, "Panel");

            cardCode = UiKit.Caps(p, "", AtlasStyle.AccentSoft, 11).At(22, 20, 150, 16);
            cardFile = UiKit.Text(p, "", 12, AtlasStyle.TextLo, FontStyles.Normal, TextAlignmentOptions.TopRight).At(151, 19, 150, 16);
            UiKit.Button(p, "×", false, () => backend.SelectNode(null), out _, fontSize: 18).At(357 - 22 - 26, 12, 26, 26)
                .GetComponentInChildren<TextMeshProUGUI>().rectTransform.Stretch();
            cardTitle = UiKit.Text(p, "", 24, AtlasStyle.TextHi, FontStyles.Bold).At(22, 42, 313, 32);
            cardTitle.textWrappingMode = TextWrappingModes.NoWrap;
            cardStatusBg = UiKit.Box(p, "Status", AtlasStyle.Surface, 0.5f).At(22, 80, 120, 24);
            cardStatus = UiKit.Text(cardStatusBg.transform, "", 12, AtlasStyle.TextHi, FontStyles.Bold, TextAlignmentOptions.Center).Stretch();
            cardBody = UiKit.Text(p, "", 14.5f, AtlasStyle.TextMid).At(22, 114, 313, 112);

            UiKit.Button(p, "What breaks?", false, () => AskAboutSelection("What breaks if we change {0}?"), out _, fontSize: 14)
                .At(22, 236, 150, 36);
            UiKit.Button(p, "Tell me more", true, () => AskAboutSelection("Tell me more about {0}"), out _, fontSize: 14)
                .At(185, 236, 150, 36);
        }

        // ================= backend events =================

        void OnParticipant(Participant p)
        {
            if (p.Identity != backend.LocalIdentity) return;
            role = p.Role;
            selected = p.SelectedNode;
            RefreshRole();
            RefreshGuide();
            RefreshCard();
        }

        void OnTask(AgentTask t)
        {
            if (t.Id != currentTask)
            {
                currentTask = t.Id;
                foreach (Transform child in stepsRoot) Destroy(child.gameObject);
                rows.Clear();
            }
            bool thinking = t.State != "done";
            reasonQuestion.text = $"\"{t.Question}\"";
            reasonQuestion.color = AtlasStyle.TextHi;
            reasonQuestion.fontStyle = FontStyles.Bold;
            reasonPill.text = thinking ? "THINKING" : "DONE";
            reasonPill.color = thinking ? AtlasStyle.Warn : AtlasStyle.Good;
            reasonPillBg.color = (thinking ? AtlasStyle.Warn : AtlasStyle.Good).A(0.14f);
            guideStatus.text = thinking ? "GUIDE · THINKING" : "GUIDE · LISTENING";
            if (!thinking && !string.IsNullOrEmpty(t.Answer)) guideQuote.text = $"\"{t.Answer}\"";
        }

        void OnStepInserted(AgentStep s)
        {
            if (s.TaskId != currentTask || rows.Count >= 6) return;
            const float w = 381;
            var root = UiKit.Rect(stepsRoot, "Step").At(0, rows.Count * 66, w, 62);
            var row = new StepRow
            {
                Dot = UiKit.Box(root, "Dot", AtlasStyle.PhaseColor(s.Phase), 0.25f).At(0, 2, 12, 12),
                Phase = UiKit.Caps(root, s.Phase.ToString(), AtlasStyle.PhaseColor(s.Phase), 10).At(24, 0, w - 24, 14),
                Text = UiKit.Text(root, s.Text, 15, AtlasStyle.TextHi).At(24, 17, w - 24, 38),
                Done = s.Done,
            };
            rows.Add(row);
        }

        void OnStepUpdated(AgentStep s)
        {
            if (s.TaskId != currentTask || s.Index >= rows.Count) return;
            rows[s.Index].Done = s.Done;
        }

        // ================= refresh =================

        void RefreshRole()
        {
            missionCaps.text = $"ATLAS · {backend.DisplayName.ToUpperInvariant()}";
            missionTitle.text = role switch
            {
                Role.ProductManager => "Learn what this system does for customers",
                Role.Architect => "Find the riskiest coupling point",
                _ => "Trace how a payment gets processed",
            };
            for (int i = 0; i < 3; i++)
            {
                bool on = i == (int)role;
                UiKit.SetButtonColors(roleButtons[i], on ? AtlasStyle.Accent : new Color(1, 1, 1, 0f));
                roleLabels[i].color = on ? Color.white : AtlasStyle.TextLo;
            }
        }

        void RefreshGuide()
        {
            var node = SelectedNode();
            if (node == null)
            {
                guideTitle.text = "Select a module";
                guideBody.text = "Point at any block and pinch to see what it does for your role. Grab one and pull it out to see what depends on it.";
            }
            else
            {
                guideTitle.text = node.Title;
                guideBody.text = $"{node.Id} · {node.Feature}. Ask below, or pull it out of the map to see what breaks.";
            }

            string target = node?.Title ?? "Post payment";
            string layerQ = map.Layer == "business" ? "Back to the code view" : "Show me the business view";
            string[] qs = role switch
            {
                Role.ProductManager => new[] { "What does this system do for customers?", "How does a payment get processed?", $"What breaks if we change {target}?", "Reset the view" },
                Role.Architect => new[] { $"What breaks if we change {target}?", "How does a payment get processed?", layerQ, "Reset the view" },
                _ => new[] { "How does a payment get processed?", $"What breaks if we change {target}?", layerQ, "Reset the view" },
            };
            for (int i = 0; i < 4; i++)
            {
                askQuestions[i] = qs[i];
                askLabels[i].text = qs[i];
            }
        }

        void RefreshCard()
        {
            var n = SelectedNode();
            if (n == null) return;
            cardCode.text = n.Id;
            cardFile.text = n.File;
            cardTitle.text = n.Title;
            cardStatus.text = $"{AtlasStyle.StatusLabel(n.Status)} · {n.Tag}";
            var tint = AtlasStyle.Fill(n.Status);
            cardStatus.color = tint;
            cardStatusBg.color = tint.A(0.14f);
            ((RectTransform)cardStatusBg.transform).sizeDelta = new Vector2(cardStatus.GetPreferredValues(cardStatus.text).x + 22f, 24f);
            cardBody.text = n.SummaryFor(role);
        }

        ArchNode SelectedNode() => selected != null && map.Blocks.TryGetValue(selected, out var b) ? b.Data : null;

        void AskAboutSelection(string format)
        {
            var n = SelectedNode();
            if (n != null) backend.AskQuestion(string.Format(format, n.Title));
        }

        // ================= per frame =================

        void LateUpdate()
        {
            if (!viewer) return;
            float k = 1f - Mathf.Exp(-Time.deltaTime * 6f);

            foreach (var t in facing)
            {
                var dir = t.position - viewer.position;
                if (dir.sqrMagnitude > 1e-4f) t.rotation = Quaternion.Slerp(t.rotation, Quaternion.LookRotation(dir, Vector3.up), k);
            }

            // card follows the selected block
            bool show = selected != null && map.Blocks.TryGetValue(selected, out var block);
            cardGroup.alpha = Mathf.MoveTowards(cardGroup.alpha, show ? 1f : 0f, Time.deltaTime * 6f);
            cardGroup.interactable = cardGroup.blocksRaycasts = cardGroup.alpha > 0.5f;
            if (show)
            {
                block = map.Blocks[selected];
                var target = block.transform.position + map.transform.up * 0.075f;
                cardCanvas.position = cardGroup.alpha < 0.05f ? target : Vector3.Lerp(cardCanvas.position, target, k * 2f);
                var dir = cardCanvas.position - viewer.position;
                cardCanvas.rotation = Quaternion.LookRotation(dir, Vector3.up);
            }

            // pulse the dot of the step in progress
            for (int i = 0; i < rows.Count; i++)
            {
                var c = rows[i].Dot.color;
                c.a = rows[i].Done ? 1f : 0.35f + 0.65f * (0.5f + 0.5f * Mathf.Sin(Time.time * 8f));
                rows[i].Dot.color = c;
            }
        }
    }
}
