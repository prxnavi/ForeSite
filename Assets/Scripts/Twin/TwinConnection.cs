using System;
using System.Collections.Generic;
using System.Linq;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;
using Component = SpacetimeDB.Types.Component;

namespace CbreTwin
{
    [Flags]
    public enum Dirty
    {
        None = 0,
        Geometry = 1,
        Structural = 2,
        Risk = 4,
        Market = 8,
        Agent = 16,
        Ops = 32,
        Users = 64,
        Sim = 128,
        Annotations = 256,
        All = 0xFFFF,
    }

    /// <summary>
    /// Owns the SpacetimeDB connection. All app state is the client cache of
    /// subscribed tables — there is no separate API server.
    /// </summary>
    public class TwinConnection : MonoBehaviour
    {
        public static TwinConnection I { get; private set; }

        public DbConnection Conn { get; private set; }
        public Identity? MyIdentity { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsSubscribed { get; private set; }
        public string Status { get; private set; } = "Not connected";
        public Dirty DirtyFlags;

        public event Action OnReady;
        public event Action<string> OnStatus;
        public event Action<string> OnReducerError;

        public const string DefaultServer = "http://127.0.0.1:3000";
        public const string DefaultDb = "cbre-twin";

        public string ServerUrl
        {
            get => PlayerPrefs.GetString("twin.server", DefaultServer);
            set => PlayerPrefs.SetString("twin.server", value);
        }

        public string DbName
        {
            get => PlayerPrefs.GetString("twin.db", DefaultDb);
            set => PlayerPrefs.SetString("twin.db", value);
        }

        string TokenKey => $"twin.token::{ServerUrl}::{DbName}";
        bool retriedWithoutToken;

        void Awake()
        {
            I = this;
            if (GetComponent<SpacetimeDBNetworkManager>() == null)
                gameObject.AddComponent<SpacetimeDBNetworkManager>(); // drives FrameTick()
        }

        public void Connect(string server, string db)
        {
            if (Conn != null && Conn.IsActive)
            {
                if (server == ServerUrl && db == DbName) { if (IsSubscribed) OnReady?.Invoke(); return; }
                Conn.Disconnect();
            }
            ServerUrl = server.Trim();
            DbName = db.Trim();
            IsConnected = false;
            IsSubscribed = false;
            SetStatus($"Connecting to {DbName} @ {ServerUrl}…");

            var token = PlayerPrefs.GetString(TokenKey, "");
            Conn = DbConnection.Builder()
                .WithUri(ServerUrl)
                .WithDatabaseName(DbName)
                .WithToken(string.IsNullOrEmpty(token) ? null : token)
                .OnConnect(HandleConnect)
                .OnConnectError(HandleConnectError)
                .OnDisconnect((c, e) =>
                {
                    IsConnected = false;
                    IsSubscribed = false;
                    SetStatus(e == null ? "Disconnected" : $"Disconnected: {e.Message}");
                    DirtyFlags |= Dirty.Users;
                })
                .Build();
        }

        void HandleConnect(DbConnection conn, Identity identity, string token)
        {
            MyIdentity = identity;
            IsConnected = true;
            PlayerPrefs.SetString(TokenKey, token);
            PlayerPrefs.Save();
            SetStatus("Connected — syncing digital twin…");
            HookTables(conn);
            conn.OnUnhandledReducerError += (ctx, ex) => OnReducerError?.Invoke(ex.Message);
            conn.SubscriptionBuilder()
                .OnApplied(_ =>
                {
                    IsSubscribed = true;
                    SetStatus("Live · SpacetimeDB");
                    DirtyFlags = Dirty.All;
                    OnReady?.Invoke();
                })
                .OnError((_, ex) => SetStatus($"Subscription error: {ex.Message}"))
                .SubscribeToAllTables();
        }

        void HandleConnectError(Exception e)
        {
            // A token minted by a different server instance is rejected; retry anonymously once.
            if (!retriedWithoutToken && PlayerPrefs.HasKey(TokenKey))
            {
                retriedWithoutToken = true;
                PlayerPrefs.DeleteKey(TokenKey);
                Connect(ServerUrl, DbName);
                return;
            }
            SetStatus($"Could not reach SpacetimeDB at {ServerUrl} ({e.Message}). Is `spacetime start` running and the module published?");
        }

        void SetStatus(string s)
        {
            Status = s;
            OnStatus?.Invoke(s);
        }

        void HookTables(DbConnection c)
        {
            var db = c.Db;
            void H<TRow>(RemoteTableHandle<EventContext, TRow> t, Dirty flag) where TRow : class, SpacetimeDB.BSATN.IStructuralReadWrite, new()
            {
                t.OnInsert += (_, _) => DirtyFlags |= flag;
                t.OnDelete += (_, _) => DirtyFlags |= flag;
            }
            void U<TRow>(RemoteTableHandle<EventContext, TRow> t, Dirty flag) where TRow : class, SpacetimeDB.BSATN.IStructuralReadWrite, new()
            {
                H(t, flag);
                t.OnUpdate += (_, _, _) => DirtyFlags |= flag;
            }

            U(db.Blueprint, Dirty.Geometry);
            U(db.Room, Dirty.Geometry);
            U(db.Component, Dirty.Risk);
            U(db.ComponentRisk, Dirty.Risk);
            H(db.CascadePath, Dirty.Risk);
            H(db.StructuralFinding, Dirty.Structural);
            U(db.StructuralSummary, Dirty.Structural);
            U(db.Valuation, Dirty.Market);
            H(db.LocationPricing, Dirty.Market);
            H(db.SellingFeature, Dirty.Market);
            H(db.UpgradeRec, Dirty.Market);
            U(db.WorkOrder, Dirty.Ops);
            H(db.Alert, Dirty.Ops);
            U(db.AgentRun, Dirty.Agent);
            H(db.AgentStep, Dirty.Agent);
            H(db.AgentMessage, Dirty.Agent);
            U(db.UserProfile, Dirty.Users);
            U(db.SimulationState, Dirty.Sim);
            H(db.Annotation, Dirty.Annotations);
        }

        // ───────────── Query helpers over the client cache ─────────────

        public bool Ready => Conn != null && IsSubscribed;

        public UserProfile Me =>
            Ready && MyIdentity.HasValue ? Conn.Db.UserProfile.Identity.Find(MyIdentity.Value) : null;

        public List<Blueprint> Blueprints() =>
            Ready ? Conn.Db.Blueprint.Iter().OrderBy(b => b.Id).ToList() : new List<Blueprint>();

        public Blueprint Blueprint(uint id) => Ready ? Conn.Db.Blueprint.Id.Find(id) : null;
        public Location Location(uint id) => Ready ? Conn.Db.Location.Id.Find(id) : null;
        public List<Room> Rooms(uint bp) => Ready ? Conn.Db.Room.BlueprintId.Filter(bp).ToList() : new List<Room>();
        public List<Component> Components(uint bp) => Ready ? Conn.Db.Component.BlueprintId.Filter(bp).ToList() : new List<Component>();
        public ComponentRisk Risk(uint componentId) => Ready ? Conn.Db.ComponentRisk.ComponentId.Find(componentId) : null;
        public List<ComponentRisk> Risks(uint bp) => Ready ? Conn.Db.ComponentRisk.BlueprintId.Filter(bp).OrderBy(r => r.Rank).ToList() : new List<ComponentRisk>();
        public List<CascadePath> Paths(uint bp) => Ready ? Conn.Db.CascadePath.BlueprintId.Filter(bp).OrderByDescending(p => p.Loss).ToList() : new List<CascadePath>();
        public List<StructuralFinding> Findings(uint bp) => Ready ? Conn.Db.StructuralFinding.BlueprintId.Filter(bp).ToList() : new List<StructuralFinding>();
        public StructuralSummary Summary(uint bp) => Ready ? Conn.Db.StructuralSummary.BlueprintId.Find(bp) : null;
        public Valuation Valuation(uint bp) => Ready ? Conn.Db.Valuation.BlueprintId.Find(bp) : null;
        public List<LocationPricing> LocationPricing(uint bp) => Ready ? Conn.Db.LocationPricing.BlueprintId.Filter(bp).OrderByDescending(p => p.ListPrice).ToList() : new List<LocationPricing>();
        public List<SellingFeature> Features(uint bp) => Ready ? Conn.Db.SellingFeature.BlueprintId.Filter(bp).OrderByDescending(f => f.Present).ThenByDescending(f => f.ValueImpact).ToList() : new List<SellingFeature>();
        public List<UpgradeRec> Upgrades(uint bp) => Ready ? Conn.Db.UpgradeRec.BlueprintId.Filter(bp).OrderByDescending(u => u.Roi).ToList() : new List<UpgradeRec>();
        public List<WorkOrder> WorkOrders(uint bp) => Ready ? Conn.Db.WorkOrder.BlueprintId.Filter(bp).OrderByDescending(w => w.Id).ToList() : new List<WorkOrder>();
        public List<Alert> Alerts(uint bp) => Ready ? Conn.Db.Alert.BlueprintId.Filter(bp).OrderByDescending(a => a.Id).ToList() : new List<Alert>();
        public List<AgentMessage> Messages(uint bp) => Ready ? Conn.Db.AgentMessage.BlueprintId.Filter(bp).OrderBy(m => m.Id).ToList() : new List<AgentMessage>();
        public List<AgentStep> Steps(ulong runId) => Ready ? Conn.Db.AgentStep.RunId.Filter(runId).OrderBy(s => s.Id).ToList() : new List<AgentStep>();
        public AgentRun LatestRun(uint bp) => Ready ? Conn.Db.AgentRun.BlueprintId.Filter(bp).OrderByDescending(r => r.Id).FirstOrDefault() : null;
        public List<Presence> Presences(uint bp) => Ready ? Conn.Db.Presence.BlueprintId.Filter(bp).ToList() : new List<Presence>();
        public List<Annotation> Annotations(uint bp) => Ready ? Conn.Db.Annotation.BlueprintId.Filter(bp).ToList() : new List<Annotation>();
        public List<UserProfile> OnlineUsers() => Ready ? Conn.Db.UserProfile.Iter().Where(u => u.Online).ToList() : new List<UserProfile>();
        public SimulationState Sim => Ready ? Conn.Db.SimulationState.Id.Find(1) : null;
        public List<DataSource> Sources() => Ready ? Conn.Db.DataSource.Iter().OrderBy(d => d.Id).ToList() : new List<DataSource>();

        void OnApplicationQuit()
        {
            if (Conn != null && Conn.IsActive) Conn.Disconnect();
        }
    }
}
